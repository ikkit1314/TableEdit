using System.Globalization;
using System.Text;

namespace TableEdit.Model;

/// <summary>
/// Pattern language for Auto-Fill. Literal text is copied as-is; tokens are wrapped in percent signs:
///   %n7%  7 digits          %c7% 7 lowercase letters   %C7% 7 uppercase letters
///   %a7%  7 lowercase letters/digits                     %A7% 7 uppercase letters/digits
///   %n18-65%  integer in a range (inclusive)
///   %fname% %lname% %name% %email% %phone% %city% %country% %company% %date% %uuid% %bool%
///   %row%  row number (1-based)   %row:1000%  row number counting from 1000
///   %pick:a|b|c%  one of the listed values
///   %[Column]%  value of another column in the same row
///   %%  a literal percent sign
/// Within one row, name, email and similar tokens describe the same random person.
/// </summary>
public sealed class FillPattern
{
    private abstract record Part;
    private sealed record Literal(string Text) : Part;
    private sealed record Token(string Name, string Arg) : Part;

    private readonly List<Part> _parts;

    private FillPattern(List<Part> parts) => _parts = parts;

    public static readonly (string Label, string Pattern)[] Presets =
    {
        ("First name", "%fname%"),
        ("Last name", "%lname%"),
        ("Full name", "%name%"),
        ("Email", "%email%"),
        ("Phone (555) 123-4567", "(%n3%) %n3%-%n4%"),
        ("Phone 7 digits", "%n7%"),
        ("City", "%city%"),
        ("Country", "%country%"),
        ("Company", "%company%"),
        ("Date (yyyy-mm-dd)", "%date%"),
        ("Age 18–65", "%n18-65%"),
        ("Amount 1–9999", "%n1-9999%.%n2%"),
        ("ID like AB-12345", "%C2%-%n5%"),
        ("Code 8 letters/digits", "%A8%"),
        ("Row number", "%row%"),
        ("Sequence from 1000", "%row:1000%"),
        ("UUID", "%uuid%"),
        ("Yes / No", "%pick:Yes|No%"),
        ("Status", "%pick:open|pending|closed%"),
        ("Email from name columns", "%[First Name]%.%[Last Name]%@example.com"),
    };

    public static string Help =>
        "%n7% digits · %c7% / %C7% letters · %a7% / %A7% letters+digits · %n18-65% number range\n" +
        "%fname% %lname% %name% %email% %phone% %city% %country% %company% %date% %uuid% %bool%\n" +
        "%row% row number · %row:1000% count from 1000 · %pick:a|b|c% choose one · %[Column]% other column · %% = %";

    /// <summary>Parses a pattern; returns null and an error message when a token is unknown or malformed.</summary>
    public static FillPattern? TryParse(string pattern, out string? error)
    {
        error = null;
        var parts = new List<Part>();
        var literal = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char ch = pattern[i];
            if (ch != '%')
            {
                literal.Append(ch);
                i++;
                continue;
            }
            if (i + 1 < pattern.Length && pattern[i + 1] == '%')
            {
                literal.Append('%');
                i += 2;
                continue;
            }

            // Column references may contain '%'-free text including ':' and '|', so find the closing ']%'.
            int end = i + 1 < pattern.Length && pattern[i + 1] == '['
                ? pattern.IndexOf("]%", i + 2, StringComparison.Ordinal) + 1
                : pattern.IndexOf('%', i + 1);
            if (end <= i)
            {
                error = $"Missing closing % after position {i + 1}.";
                return null;
            }

            var body = pattern[(i + 1)..end];
            var token = ParseToken(body);
            if (token == null)
            {
                error = $"Unknown token %{body}%.";
                return null;
            }
            if (literal.Length > 0)
            {
                parts.Add(new Literal(literal.ToString()));
                literal.Clear();
            }
            parts.Add(token);
            i = end + 1;
        }
        if (literal.Length > 0) parts.Add(new Literal(literal.ToString()));
        return new FillPattern(parts);
    }

    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "fname", "lname", "name", "email", "phone", "city", "country", "company", "date", "uuid", "bool",
    };

    private static Token? ParseToken(string body)
    {
        if (body.Length == 0) return null;
        if (body.StartsWith('[') && body.EndsWith(']') && body.Length > 2) return new Token("col", body[1..^1]);
        if (body.StartsWith("pick:", StringComparison.OrdinalIgnoreCase) && body.Length > 5) return new Token("pick", body[5..]);
        if (body.Equals("row", StringComparison.OrdinalIgnoreCase)) return new Token("row", "1");
        if (body.StartsWith("row:", StringComparison.OrdinalIgnoreCase) && long.TryParse(body[4..], out _)) return new Token("row", body[4..]);
        if (Words.Contains(body)) return new Token(body.ToLowerInvariant(), "");

        // Character classes: n c C a A followed by a count, or n followed by a range.
        char kind = body[0];
        var rest = body[1..];
        if ("ncCaA".IndexOf(kind) < 0) return null;
        if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 1000)
            return new Token(kind.ToString(), rest);
        if (kind == 'n')
        {
            var range = rest.Split('-');
            if (range.Length == 2 && long.TryParse(range[0], out var lo) && long.TryParse(range[1], out var hi) && lo <= hi)
                return new Token("range", rest);
        }
        return null;
    }

    /// <summary>Column names referenced by %[Column]% tokens, for validation against the table.</summary>
    public IEnumerable<string> ReferencedColumns => _parts.OfType<Token>().Where(t => t.Name == "col").Select(t => t.Arg);

    /// <param name="rowNumber">1-based position of the row being filled.</param>
    /// <param name="columnValue">Looks up another column's value in this row by name; null when it does not exist.</param>
    public string Generate(Random rng, long rowNumber, Func<string, string?> columnValue)
    {
        var person = new Lazy<Person>(() => Person.Random(rng));
        var sb = new StringBuilder();
        foreach (var part in _parts)
        {
            if (part is Literal lit)
            {
                sb.Append(lit.Text);
                continue;
            }
            var t = (Token)part;
            switch (t.Name)
            {
                case "n": AppendRandom(sb, rng, "0123456789", int.Parse(t.Arg)); break;
                case "c": AppendRandom(sb, rng, "abcdefghijklmnopqrstuvwxyz", int.Parse(t.Arg)); break;
                case "C": AppendRandom(sb, rng, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", int.Parse(t.Arg)); break;
                case "a": AppendRandom(sb, rng, "abcdefghijklmnopqrstuvwxyz0123456789", int.Parse(t.Arg)); break;
                case "A": AppendRandom(sb, rng, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", int.Parse(t.Arg)); break;
                case "range":
                    var r = t.Arg.Split('-');
                    sb.Append(rng.NextInt64(long.Parse(r[0]), long.Parse(r[1]) + 1));
                    break;
                case "row": sb.Append(long.Parse(t.Arg) + rowNumber - 1); break;
                case "pick":
                    var options = t.Arg.Split('|');
                    sb.Append(options[rng.Next(options.Length)]);
                    break;
                case "col": sb.Append(columnValue(t.Arg) ?? ""); break;
                case "fname": sb.Append(person.Value.First); break;
                case "lname": sb.Append(person.Value.Last); break;
                case "name": sb.Append(person.Value.First).Append(' ').Append(person.Value.Last); break;
                case "email": sb.Append(person.Value.Email); break;
                case "phone": sb.Append(person.Value.Phone); break;
                case "city": sb.Append(Pick(rng, Data.Cities)); break;
                case "country": sb.Append(Pick(rng, Data.Countries)); break;
                case "company": sb.Append(Pick(rng, Data.CompanyFirst)).Append(' ').Append(Pick(rng, Data.CompanySuffix)); break;
                case "date": sb.Append(DateTime.Today.AddDays(-rng.Next(0, 365 * 5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
                case "uuid": sb.Append(Guid.NewGuid().ToString()); break;
                case "bool": sb.Append(rng.Next(2) == 0 ? "true" : "false"); break;
            }
        }
        return sb.ToString();
    }

    private static void AppendRandom(StringBuilder sb, Random rng, string alphabet, int count)
    {
        for (int i = 0; i < count; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
    }

    private static string Pick(Random rng, string[] values) => values[rng.Next(values.Length)];

    private sealed record Person(string First, string Last, string Email, string Phone)
    {
        public static Person Random(Random rng)
        {
            var first = Pick(rng, Data.FirstNames);
            var last = Pick(rng, Data.LastNames);
            var email = $"{Ascii(first)}.{Ascii(last)}{(rng.Next(3) == 0 ? rng.Next(1, 100).ToString(CultureInfo.InvariantCulture) : "")}@{Pick(rng, Data.EmailDomains)}";
            var phone = $"({rng.Next(200, 1000)}) {rng.Next(200, 1000)}-{rng.Next(0, 10000):D4}";
            return new Person(first, last, email, phone);
        }

        private static string Ascii(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
                if (ch < 128 && char.IsLetter(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
    }

    private static class Data
    {
        public static readonly string[] FirstNames =
        {
            "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth",
            "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen",
            "Daniel", "Nancy", "Matthew", "Lisa", "Anthony", "Betty", "Mark", "Margaret", "Steven", "Sandra",
            "Paul", "Ashley", "Andrew", "Emily", "Joshua", "Donna", "Kevin", "Michelle", "Brian", "Carol",
            "George", "Amanda", "Edward", "Melissa", "Ryan", "Deborah", "Jacob", "Stephanie", "Nicholas", "Rebecca",
            "Eric", "Laura", "Jonathan", "Sharon", "Justin", "Cynthia", "Brandon", "Kathleen", "Samuel", "Amy",
            "Wei", "Mei", "Hiroshi", "Yuki", "Min-jun", "Seo-yeon", "Arjun", "Priya", "Mohammed", "Fatima",
            "Lucas", "Sofia", "Mateo", "Valentina", "Noah", "Emma", "Liam", "Olivia", "Ethan", "Chloé",
            "Ivan", "Anna", "Luca", "Giulia", "Hugo", "Léa", "Omar", "Aisha", "Chen", "Lin",
        };

        public static readonly string[] LastNames =
        {
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin",
            "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
            "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores",
            "Green", "Adams", "Nelson", "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts",
            "Wang", "Li", "Zhang", "Liu", "Chen", "Tan", "Wong", "Kim", "Park", "Sato",
            "Suzuki", "Tanaka", "Patel", "Singh", "Khan", "Ali", "Müller", "Schmidt", "Dubois", "Rossi",
            "Silva", "Santos", "Novak", "Kowalski", "Ivanov", "O'Brien", "Murphy", "Kelly", "Cohen", "Haddad",
        };

        public static readonly string[] Cities =
        {
            "Toronto", "Vancouver", "Montreal", "New York", "Los Angeles", "Chicago", "London", "Paris", "Berlin", "Madrid",
            "Rome", "Amsterdam", "Tokyo", "Seoul", "Shanghai", "Beijing", "Hong Kong", "Singapore", "Kuala Lumpur", "Sydney",
            "Melbourne", "São Paulo", "Mexico City", "Dubai", "Mumbai", "Bangkok", "Istanbul", "Cairo", "Stockholm", "Dublin",
        };

        public static readonly string[] Countries =
        {
            "Canada", "United States", "United Kingdom", "France", "Germany", "Spain", "Italy", "Netherlands", "Japan", "South Korea",
            "China", "Singapore", "Malaysia", "Australia", "Brazil", "Mexico", "India", "Thailand", "Turkey", "Sweden",
        };

        public static readonly string[] CompanyFirst =
        {
            "Acme", "Globex", "Initech", "Northwind", "Contoso", "Fabrikam", "Umbrella", "Stark", "Wayne", "Pioneer",
            "Summit", "Bluewave", "Redwood", "Silverline", "Brightpath", "Ironclad", "Maple", "Harbor", "Nimbus", "Vertex",
        };

        public static readonly string[] CompanySuffix = { "Inc.", "LLC", "Ltd.", "Group", "Labs", "Corp.", "Holdings", "Systems", "Partners", "Co." };

        public static readonly string[] EmailDomains = { "example.com", "mail.com", "inbox.net", "corp.io", "test.org" };
    }
}
