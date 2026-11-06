using System.Globalization;
using System.Text;

namespace TableEdit.Model;

/// <summary>
/// Pattern language for Auto-Fill. Literal text is copied as-is; tokens are wrapped in percent signs:
///   %n7%  7 digits          %c7% 7 lowercase letters   %C7% 7 uppercase letters
///   %a7%  7 lowercase letters/digits                     %A7% 7 uppercase letters/digits
///   %n18-65%  integer in a range (inclusive)
///   %fname% %lname% %name% %email% %phone% %phone10% %company% %date% %uuid% %bool%
///   %street% %apt% %city% %state% %statename% %zip% %country% %countrycode%
///   %cardtype% %cardnumber% %cardmonth% %cardyear% %cardyear4% %cvv%
///   %row%  row number (1-based)   %row:1000%  row number counting from 1000
///   %pick:a|b|c%  one of the listed values
///   %[Column]%  value of another column in the same row
///   %%  a literal percent sign
/// Within one row, every person, address and card token describes the same random person, even across columns
/// filled together, so a row's city, state, ZIP and phone area code match and the card holder is that person.
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
        ("Phone (555) 123-4567", "%phone%"),
        ("Phone 10 digits", "%phone10%"),
        ("Phone 7 digits", "%n7%"),
        ("Street address", "%street%"),
        ("Apt / unit (often blank)", "%apt%"),
        ("City", "%city%"),
        ("State code (NY)", "%state%"),
        ("ZIP / postal code", "%zip%"),
        ("Country (United States)", "%country%"),
        ("Country code (US)", "%countrycode%"),
        ("Card type", "%cardtype%"),
        ("Card number (test)", "%cardnumber%"),
        ("Card month 01–12", "%cardmonth%"),
        ("Card year (29)", "%cardyear%"),
        ("CVV", "%cvv%"),
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
        "%fname% %lname% %name% %email% %phone% %phone10% %company% %date% %uuid% %bool%\n" +
        "%street% %apt% %city% %state% %statename% %zip% %country% %countrycode%\n" +
        "%cardtype% %cardnumber% %cardmonth% %cardyear% %cardyear4% %cvv% (public test cards)\n" +
        "%row% row number · %row:1000% count from 1000 · %pick:a|b|c% choose one · %[Column]% other column · %% = %";

    /// <summary>
    /// A pattern that suits a column name (SHIPPING_ZIP → %zip%, First Name → %fname%, CARD_CVV → %cvv%), or null
    /// when the name gives no clue.
    /// </summary>
    public static string? Suggest(string header)
    {
        if (ProfileFill.Classify(header) is { } profile)
            return profile.Field switch
            {
                ProfileField.ProfileName or ProfileField.Email => "%email%",
                ProfileField.Phone => "%phone10%",
                ProfileField.FirstName => "%fname%",
                ProfileField.LastName => "%lname%",
                ProfileField.FullName or ProfileField.CardHolder => "%name%",
                ProfileField.Company => "%company%",
                ProfileField.Address1 => "%street%",
                ProfileField.Address2 => "%apt%",
                ProfileField.City => "%city%",
                ProfileField.Zip => "%zip%",
                ProfileField.State => "%state%",
                ProfileField.Country => "%countrycode%",
                ProfileField.SameAsShipping => "TRUE",
                ProfileField.CardType => "%cardtype%",
                ProfileField.CardNumber => "%cardnumber%",
                ProfileField.CardMonth => "%cardmonth%",
                ProfileField.CardYear => "%cardyear%",
                ProfileField.CardCvv => "%cvv%",
                ProfileField.OneCheckoutPerProfile => "FALSE",
                _ => null,
            };
        var key = new string(header.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return key switch
        {
            "ID" or "NO" or "NUM" or "NUMBER" or "INDEX" or "ROW" or "ROWNUMBER" => "%row%",
            "UUID" or "GUID" => "%uuid%",
            "AGE" => "%n18-65%",
            "DATE" or "CREATED" or "CREATEDAT" or "UPDATED" or "UPDATEDAT" or "BIRTHDAY" or "DOB" => "%date%",
            "ACTIVE" or "ENABLED" or "BOOL" => "%bool%",
            "PASSWORD" or "PASS" => "%A12%",
            _ => null,
        };
    }

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
        "fname", "lname", "name", "email", "phone", "phone10", "company", "date", "uuid", "bool",
        "street", "apt", "city", "state", "statename", "zip", "country", "countrycode",
        "cardtype", "cardnumber", "cardmonth", "cardyear", "cardyear4", "cvv",
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
    /// <param name="sample">This row's random person; pass the same one to every column filled in the row.</param>
    public string Generate(Random rng, long rowNumber, Func<string, string?> columnValue, ProfileFill.Sample? sample = null)
    {
        var row = new Lazy<ProfileFill.Sample>(() => sample ?? ProfileFill.Sample.Random(rng));
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
                case "fname": sb.Append(row.Value.Person.First); break;
                case "lname": sb.Append(row.Value.Person.Last); break;
                case "name": sb.Append(row.Value.Person.First).Append(' ').Append(row.Value.Person.Last); break;
                case "email": sb.Append(row.Value.Person.Email); break;
                case "phone":
                    var digits = row.Value.Address.Phone;
                    sb.Append('(').Append(digits[..3]).Append(") ").Append(digits[3..6]).Append('-').Append(digits[6..]);
                    break;
                case "phone10": sb.Append(row.Value.Address.Phone); break;
                case "company": sb.Append(ProfileFill.RandomCompany(rng)); break;
                case "street": sb.Append(row.Value.Address.Street); break;
                case "apt": sb.Append(row.Value.Address.Unit); break;
                case "city": sb.Append(row.Value.Address.City); break;
                case "state": sb.Append(row.Value.Address.StateCode); break;
                case "statename": sb.Append(row.Value.Address.StateName); break;
                case "zip": sb.Append(row.Value.Address.Zip); break;
                case "country": sb.Append(row.Value.CountryName); break;
                case "countrycode": sb.Append(row.Value.CountryCode); break;
                case "cardtype": sb.Append(row.Value.Card.Type); break;
                case "cardnumber": sb.Append(row.Value.Card.Number); break;
                case "cardmonth": sb.Append(row.Value.Card.Month.ToString("D2", CultureInfo.InvariantCulture)); break;
                case "cardyear": sb.Append(row.Value.Card.YearText(false)); break;
                case "cardyear4": sb.Append(row.Value.Card.YearText(true)); break;
                case "cvv": sb.Append(row.Value.Card.Cvv); break;
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

}
