using System.Globalization;
using System.Text;

namespace TableEdit.Model;

public enum ProfileField
{
    ProfileName, Email, Phone, FirstName, LastName, FullName, Company,
    Address1, Address2, City, Zip, State, Country, SameAsShipping,
    CardHolder, CardType, CardNumber, CardMonth, CardYear, CardCvv, OneCheckoutPerProfile,
}

/// <summary>A table column recognised as part of a profile. Billing is true for BILLING_* name/address columns.</summary>
public sealed record ProfileColumn(int Index, string Header, ProfileField Field, bool Billing);

/// <param name="Canada">Generate Canadian cities, provinces and postal codes instead of US ones.</param>
/// <param name="Codes">Write "US" / "NY" rather than "United States" / "New York".</param>
/// <param name="BillingSameAsShipping">Billing columns copy the shipping address; otherwise billing gets its own address.</param>
/// <param name="FourDigitYear">Card year as 2029 rather than 29.</param>
public sealed record ProfileOptions(bool Canada, bool Codes, bool BillingSameAsShipping, bool FourDigitYear);

/// <summary>
/// Recognises profile-style headers (PROFILE_NAME, SHIPPING_FIRST_NAME, Postal Code, CARD_CVV, …) and fills
/// them with random, consistent data: one person per row, with matching city, state and ZIP, and the
/// card holder named after that person. Card numbers are public test numbers, never real cards.
/// </summary>
public static class ProfileFill
{
    /// <summary>Works out which profile field a header holds, or null when it is not one we know.</summary>
    public static (ProfileField Field, bool Billing)? Classify(string header)
    {
        var key = new string(header.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (key.Length == 0) return null;

        if (key.Contains("SAMEAS")) return (ProfileField.SameAsShipping, false);
        if (key.Contains("ONECHECKOUT") || key.Contains("CHECKOUTPERPROFILE")) return (ProfileField.OneCheckoutPerProfile, false);
        if (key.Contains("CARDHOLDER") || key.Contains("NAMEONCARD") || key == "CARDNAME") return (ProfileField.CardHolder, false);
        if (key is "CARDTYPE" or "CARDBRAND" or "CCTYPE" or "CARDNETWORK") return (ProfileField.CardType, false);
        if (key is "CARDNUMBER" or "CARDNO" or "CARDNUM" or "CCNUMBER" or "CCNUM" or "CREDITCARD" or "CREDITCARDNUMBER" or "CARD")
            return (ProfileField.CardNumber, false);
        if (key is "CARDMONTH" or "CCMONTH" or "EXPMONTH" or "EXPIRYMONTH" or "EXPIRATIONMONTH" or "CARDEXPMONTH" or "CARDEXPIRYMONTH")
            return (ProfileField.CardMonth, false);
        if (key is "CARDYEAR" or "CCYEAR" or "EXPYEAR" or "EXPIRYYEAR" or "EXPIRATIONYEAR" or "CARDEXPYEAR" or "CARDEXPIRYYEAR")
            return (ProfileField.CardYear, false);
        if (key is "CVV" or "CVC" or "CVV2" or "CARDCVV" or "CARDCVC" or "CCCVV" or "SECURITYCODE" or "CARDCODE" or "CARDSECURITYCODE")
            return (ProfileField.CardCvv, false);
        if (key is "PROFILENAME" or "PROFILE" or "PROFILETITLE") return (ProfileField.ProfileName, false);

        bool billing = false;
        foreach (var prefix in new[] { "SHIPPING", "BILLING", "DELIVERY", "SHIP", "BILL", "CUSTOMER", "CONTACT" })
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length) continue;
            billing = prefix.StartsWith("BILL", StringComparison.Ordinal);
            key = key[prefix.Length..];
            break;
        }

        ProfileField? field = key switch
        {
            "EMAIL" or "EMAILADDRESS" or "MAIL" or "EMAILADDR" => ProfileField.Email,
            "PHONE" or "PHONENUMBER" or "PHONENO" or "MOBILE" or "MOBILENUMBER" or "TEL" or "TELEPHONE" or "CELL" => ProfileField.Phone,
            "FIRSTNAME" or "FNAME" or "FIRST" or "GIVENNAME" => ProfileField.FirstName,
            "LASTNAME" or "LNAME" or "LAST" or "SURNAME" or "FAMILYNAME" => ProfileField.LastName,
            "NAME" or "FULLNAME" => ProfileField.FullName,
            "COMPANY" or "COMPANYNAME" => ProfileField.Company,
            "ADDRESS" or "ADDRESS1" or "ADDRESSLINE1" or "ADDRESSLINE" or "ADDR" or "ADDR1" or "STREET" or "STREETADDRESS" or "LINE1"
                => ProfileField.Address1,
            "ADDRESS2" or "ADDRESSLINE2" or "ADDR2" or "LINE2" or "APT" or "APARTMENT" or "UNIT" or "SUITE" => ProfileField.Address2,
            "CITY" or "TOWN" => ProfileField.City,
            "ZIP" or "ZIPCODE" or "POSTALCODE" or "POSTCODE" or "POSTAL" => ProfileField.Zip,
            "STATE" or "PROVINCE" or "REGION" or "STATECODE" or "PROVINCECODE" => ProfileField.State,
            "COUNTRY" or "COUNTRYCODE" => ProfileField.Country,
            _ => null,
        };
        if (field == null) return null;
        bool addressPart = field is ProfileField.FirstName or ProfileField.LastName or ProfileField.FullName or ProfileField.Address1
            or ProfileField.Address2 or ProfileField.City or ProfileField.Zip or ProfileField.State or ProfileField.Country
            or ProfileField.Phone or ProfileField.Company;
        return (field.Value, billing && addressPart);
    }

    public static List<ProfileColumn> Detect(IReadOnlyList<string> headers)
    {
        var columns = new List<ProfileColumn>();
        for (int i = 0; i < headers.Count; i++)
            if (Classify(headers[i]) is { } c) columns.Add(new ProfileColumn(i, headers[i], c.Field, c.Billing));
        return columns;
    }

    public static string Describe(ProfileField field) => field switch
    {
        ProfileField.ProfileName => "profile name (email, or the person's name)",
        ProfileField.Email => "email",
        ProfileField.Phone => "phone, 10 digits",
        ProfileField.FirstName => "first name",
        ProfileField.LastName => "last name",
        ProfileField.FullName => "full name",
        ProfileField.Company => "company",
        ProfileField.Address1 => "street address",
        ProfileField.Address2 => "apt / unit (often blank)",
        ProfileField.City => "city",
        ProfileField.Zip => "ZIP / postal code",
        ProfileField.State => "state / province",
        ProfileField.Country => "country",
        ProfileField.SameAsShipping => "TRUE / FALSE",
        ProfileField.CardHolder => "card holder = the person's name",
        ProfileField.CardType => "card type",
        ProfileField.CardNumber => "test card number",
        ProfileField.CardMonth => "expiry month 01–12",
        ProfileField.CardYear => "expiry year",
        ProfileField.CardCvv => "CVV",
        ProfileField.OneCheckoutPerProfile => "FALSE",
        _ => field.ToString(),
    };

    /// <summary>
    /// Values to write for one row, keyed by column index. Existing values in the row are kept when
    /// onlyEmpty is set, and the generated values are built around them (a typed-in first name still
    /// ends up in the email and card holder).
    /// </summary>
    public static Dictionary<int, string> Generate(Random rng, IReadOnlyList<ProfileColumn> columns,
        Func<int, string> existing, bool onlyEmpty, ProfileOptions options)
    {
        string? Existing(ProfileField field, bool billing)
        {
            if (!onlyEmpty) return null;
            foreach (var c in columns)
                if (c.Field == field && c.Billing == billing && !string.IsNullOrWhiteSpace(existing(c.Index)))
                    return existing(c.Index).Trim();
            return null;
        }

        var person = Person.Random(rng);
        var fullName = Existing(ProfileField.FullName, false)?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = Existing(ProfileField.FirstName, false) ?? fullName?[0];
        var last = Existing(ProfileField.LastName, false) ?? (fullName?.Length > 1 ? fullName[1] : null);
        if (first != null || last != null)
            person = person with { First = first ?? person.First, Last = last ?? person.Last, Email = MakeEmail(rng, first ?? person.First, last ?? person.Last) };
        var email = Existing(ProfileField.Email, false) ?? person.Email;

        var shipping = Address.Random(rng, options.Canada);
        var billing = options.BillingSameAsShipping ? shipping : Address.Random(rng, options.Canada);
        var card = Card.Random(rng);

        var values = new Dictionary<int, string>();
        foreach (var c in columns)
        {
            if (onlyEmpty && !string.IsNullOrWhiteSpace(existing(c.Index))) continue;
            var address = c.Billing ? billing : shipping;
            values[c.Index] = c.Field switch
            {
                ProfileField.ProfileName => email.Length > 0 ? email : $"{person.First} {person.Last}",
                ProfileField.Email => email,
                ProfileField.Phone => address.Phone,
                ProfileField.FirstName => person.First,
                ProfileField.LastName => person.Last,
                ProfileField.FullName or ProfileField.CardHolder => $"{person.First} {person.Last}",
                ProfileField.Company => RandomCompany(rng),
                ProfileField.Address1 => address.Street,
                ProfileField.Address2 => address.Unit,
                ProfileField.City => address.City,
                ProfileField.Zip => address.Zip,
                ProfileField.State => options.Codes ? address.StateCode : address.StateName,
                ProfileField.Country => options.Codes ? (options.Canada ? "CA" : "US") : (options.Canada ? "Canada" : "United States"),
                ProfileField.SameAsShipping => options.BillingSameAsShipping ? "TRUE" : "FALSE",
                ProfileField.CardType => card.Type,
                ProfileField.CardNumber => card.Number,
                ProfileField.CardMonth => card.Month.ToString("D2", CultureInfo.InvariantCulture),
                ProfileField.CardYear => card.YearText(options.FourDigitYear),
                ProfileField.CardCvv => card.Cvv,
                ProfileField.OneCheckoutPerProfile => "FALSE",
                _ => "",
            };
        }
        return values;
    }

    /// <summary>One made-up person with an address and a test card, shared by every column filled in a row.</summary>
    public sealed record Sample(Person Person, Address Address, Card Card, bool Canada)
    {
        public static Sample Random(Random rng, bool canada = false) =>
            new(Person.Random(rng), Address.Random(rng, canada), Card.Random(rng), canada);

        public string CountryCode => Canada ? "CA" : "US";
        public string CountryName => Canada ? "Canada" : "United States";
    }

    public static string RandomCompany(Random rng) => Pick(rng, Data.Companies);

    private static string Pick(Random rng, string[] values) => values[rng.Next(values.Length)];

    private static string MakeEmail(Random rng, string first, string last)
    {
        static string Ascii(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
                if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
        var separator = Pick(rng, new[] { ".", "", "_" });
        return $"{Ascii(first)}{separator}{Ascii(last)}{rng.Next(1, 1000)}@{Pick(rng, Data.EmailDomains)}";
    }

    public sealed record Person(string First, string Last, string Email)
    {
        public static Person Random(Random rng)
        {
            var first = Pick(rng, Data.FirstNames);
            var last = Pick(rng, Data.LastNames);
            return new Person(first, last, MakeEmail(rng, first, last));
        }
    }

    public sealed record Address(string Street, string Unit, string City, string StateCode, string StateName, string Zip, string Phone)
    {
        public static Address Random(Random rng, bool canada)
        {
            var street = $"{rng.Next(10, 9900)} {Pick(rng, Data.Streets)} {Pick(rng, Data.StreetTypes)}";
            var unit = rng.Next(4) == 0 ? Pick(rng, new[] { "Apt", "Unit", "Suite" }) + " " + rng.Next(1, 40) + (rng.Next(2) == 0 ? "" : ((char)('A' + rng.Next(6))).ToString()) : "";
            if (canada)
            {
                var c = Data.CanadaCities[rng.Next(Data.CanadaCities.Length)];
                const string letters = "ABCEGHJKLMNPRSTVWXYZ"; // letters Canada Post uses
                char L() => letters[rng.Next(letters.Length)];
                var postal = $"{c.Fsa}{rng.Next(10)}{L()} {rng.Next(10)}{L()}{rng.Next(10)}";
                return new Address(street, unit, c.City, c.Code, c.Name, postal, MakePhone(rng, c.AreaCode));
            }
            var u = Data.UsCities[rng.Next(Data.UsCities.Length)];
            return new Address(street, unit, u.City, u.Code, u.Name, u.Zip3 + rng.Next(1, 100).ToString("D2", CultureInfo.InvariantCulture), MakePhone(rng, u.AreaCode));
        }

        private static string MakePhone(Random rng, string areaCode) =>
            areaCode + rng.Next(200, 1000).ToString(CultureInfo.InvariantCulture) + rng.Next(0, 10000).ToString("D4", CultureInfo.InvariantCulture);
    }

    public sealed record Card(string Type, string Number, int Month, int Year, string Cvv)
    {
        // Published payment-processor test numbers: they pass format checks but are not real accounts.
        private static readonly (string Type, string Number)[] TestNumbers =
        {
            ("Visa", "4111111111111111"), ("Visa", "4242424242424242"), ("Visa", "4012888888881881"),
            ("Mastercard", "5555555555554444"), ("Mastercard", "5105105105105100"), ("Mastercard", "2223003122003222"),
            ("Amex", "378282246310005"), ("Amex", "371449635398431"),
            ("Discover", "6011111111111117"), ("Discover", "6011000990139424"),
        };

        public string YearText(bool fourDigits) => fourDigits
            ? Year.ToString(CultureInfo.InvariantCulture)
            : (Year % 100).ToString("D2", CultureInfo.InvariantCulture);

        public static Card Random(Random rng)
        {
            var (type, number) = TestNumbers[rng.Next(TestNumbers.Length)];
            int cvvLength = type == "Amex" ? 4 : 3;
            var cvv = string.Concat(Enumerable.Range(0, cvvLength).Select(_ => (char)('0' + rng.Next(10))));
            return new Card(type, number, rng.Next(1, 13), DateTime.Today.Year + rng.Next(1, 6), cvv);
        }
    }

    private static class Data
    {
        public static readonly string[] FirstNames =
        {
            "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth",
            "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen",
            "Daniel", "Nancy", "Matthew", "Lisa", "Anthony", "Emily", "Mark", "Megan", "Steven", "Rachel",
            "Paul", "Ashley", "Andrew", "Hannah", "Joshua", "Grace", "Kevin", "Michelle", "Brian", "Nicole",
            "Ryan", "Amanda", "Eric", "Stephanie", "Jason", "Lauren", "Justin", "Olivia", "Brandon", "Chloe",
            "Tyler", "Samantha", "Aaron", "Victoria", "Adam", "Natalie", "Nathan", "Julia", "Kyle", "Madison",
        };

        public static readonly string[] LastNames =
        {
            "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
            "Wilson", "Anderson", "Taylor", "Thomas", "Moore", "Jackson", "Martin", "Lee", "Thompson", "White",
            "Harris", "Clark", "Lewis", "Robinson", "Walker", "Young", "Allen", "King", "Wright", "Scott",
            "Hill", "Green", "Adams", "Nelson", "Baker", "Hall", "Campbell", "Mitchell", "Carter", "Roberts",
            "Nguyen", "Chen", "Wong", "Tan", "Kim", "Park", "Patel", "Singh", "Murphy", "Kelly",
        };

        public static readonly string[] EmailDomains = { "gmail.com", "yahoo.com", "outlook.com", "icloud.com", "hotmail.com" };

        public static readonly string[] Companies =
        {
            "Northwind Traders", "Bluewave Labs", "Redwood Partners", "Summit Group", "Harbor Systems", "Maple Supply Co.",
        };

        public static readonly string[] Streets =
        {
            "Main", "Oak", "Maple", "Pine", "Cedar", "Elm", "Washington", "Lake", "Hill", "Park",
            "Sunset", "Lincoln", "Jackson", "Church", "Highland", "Willow", "Meadow", "River", "Forest", "Spring",
            "Franklin", "Madison", "Chestnut", "Walnut", "Ridge", "Valley", "Birch", "Jefferson", "Adams", "Union",
        };

        public static readonly string[] StreetTypes = { "St", "Ave", "Rd", "Blvd", "Dr", "Ln", "Ct", "Way", "Pl" };

        public static readonly (string City, string Code, string Name, string Zip3, string AreaCode)[] UsCities =
        {
            ("New York", "NY", "New York", "100", "212"), ("Brooklyn", "NY", "New York", "112", "718"),
            ("Los Angeles", "CA", "California", "900", "213"), ("San Diego", "CA", "California", "921", "619"),
            ("San Francisco", "CA", "California", "941", "415"), ("Sacramento", "CA", "California", "958", "916"),
            ("Chicago", "IL", "Illinois", "606", "312"), ("Houston", "TX", "Texas", "770", "713"),
            ("Dallas", "TX", "Texas", "752", "214"), ("Austin", "TX", "Texas", "787", "512"),
            ("San Antonio", "TX", "Texas", "782", "210"), ("Phoenix", "AZ", "Arizona", "850", "602"),
            ("Philadelphia", "PA", "Pennsylvania", "191", "215"), ("Pittsburgh", "PA", "Pennsylvania", "152", "412"),
            ("Jacksonville", "FL", "Florida", "322", "904"), ("Miami", "FL", "Florida", "331", "305"),
            ("Orlando", "FL", "Florida", "328", "407"), ("Tampa", "FL", "Florida", "336", "813"),
            ("Columbus", "OH", "Ohio", "432", "614"), ("Cincinnati", "OH", "Ohio", "452", "513"),
            ("Charlotte", "NC", "North Carolina", "282", "704"), ("Raleigh", "NC", "North Carolina", "276", "919"),
            ("Indianapolis", "IN", "Indiana", "462", "317"), ("Seattle", "WA", "Washington", "981", "206"),
            ("Denver", "CO", "Colorado", "802", "303"), ("Boston", "MA", "Massachusetts", "021", "617"),
            ("Nashville", "TN", "Tennessee", "372", "615"), ("Portland", "OR", "Oregon", "972", "503"),
            ("Las Vegas", "NV", "Nevada", "891", "702"), ("Detroit", "MI", "Michigan", "482", "313"),
            ("Atlanta", "GA", "Georgia", "303", "404"), ("Minneapolis", "MN", "Minnesota", "554", "612"),
            ("Kansas City", "MO", "Missouri", "641", "816"), ("Salt Lake City", "UT", "Utah", "841", "801"),
            ("Baltimore", "MD", "Maryland", "212", "410"), ("Milwaukee", "WI", "Wisconsin", "532", "414"),
            ("Newark", "NJ", "New Jersey", "071", "973"), ("Richmond", "VA", "Virginia", "232", "804"),
        };

        public static readonly (string City, string Code, string Name, char Fsa, string AreaCode)[] CanadaCities =
        {
            ("Toronto", "ON", "Ontario", 'M', "416"), ("Mississauga", "ON", "Ontario", 'L', "905"),
            ("Ottawa", "ON", "Ontario", 'K', "613"), ("Hamilton", "ON", "Ontario", 'L', "905"),
            ("London", "ON", "Ontario", 'N', "519"), ("Montreal", "QC", "Quebec", 'H', "514"),
            ("Quebec City", "QC", "Quebec", 'G', "418"), ("Vancouver", "BC", "British Columbia", 'V', "604"),
            ("Victoria", "BC", "British Columbia", 'V', "250"), ("Calgary", "AB", "Alberta", 'T', "403"),
            ("Edmonton", "AB", "Alberta", 'T', "780"), ("Winnipeg", "MB", "Manitoba", 'R', "204"),
            ("Regina", "SK", "Saskatchewan", 'S', "306"), ("Saskatoon", "SK", "Saskatchewan", 'S', "306"),
            ("Halifax", "NS", "Nova Scotia", 'B', "902"), ("St. John's", "NL", "Newfoundland and Labrador", 'A', "709"),
        };
    }
}
