using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TableEdit.Model;

/// <summary>One record. Cells is observable so indexer bindings refresh when a value is replaced in code.</summary>
public sealed class Row
{
    public Row(IEnumerable<string> cells) => Cells = new ObservableCollection<string>(cells);

    public ObservableCollection<string> Cells { get; }

    /// <summary>Position in file order, used to undo sorting ("original order"). Inserted rows get in-between values.</summary>
    public double Order { get; set; }

    public string[] ToArray() => Cells.ToArray();

    public void SetAll(IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i < Cells.Count)
            {
                if (Cells[i] != values[i]) Cells[i] = values[i];
            }
            else
            {
                Cells.Add(values[i]);
            }
        }
        while (Cells.Count > values.Count) Cells.RemoveAt(Cells.Count - 1);
    }

    public void EnsureWidth(int width)
    {
        while (Cells.Count < width) Cells.Add("");
    }
}

/// <summary>
/// Copy of a table used for undo/redo of structural operations. Row identities are kept
/// (values are restored into the same Row objects) so finer-grained undo entries stay valid.
/// </summary>
public sealed record TableSnapshot(string[] Headers, Row[] Rows, string[][] Values, bool HasHeader);

public sealed class LoadedFile
{
    public required List<string[]> Records { get; init; }
    public required char Delimiter { get; init; }
    public required Encoding Encoding { get; init; }
    public required string NewLine { get; init; }
    public required bool HasHeader { get; init; }
}

public static class Csv
{
    /// <summary>In tie-break order: earlier entries win when two candidates score the same.</summary>
    public static readonly char[] CandidateDelimiters = { ',', ';', '\t', '|', ':' };

    /// <param name="forcedDelimiter">Skips detection, e.g. when the user picks a delimiter from the menu.</param>
    public static LoadedFile Load(string path, char? forcedDelimiter = null)
    {
        var bytes = File.ReadAllBytes(path);
        var (encoding, text) = Decode(bytes);

        // Excel writes a "sep=;" hint line when it exports with a non-default separator.
        char? hinted = null;
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4 && text[4] != '\r' && text[4] != '\n')
        {
            hinted = text[4];
            int eol = text.IndexOf('\n');
            text = eol < 0 ? "" : text[(eol + 1)..];
        }

        bool tabExtension = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tab", StringComparison.OrdinalIgnoreCase);
        var delimiter = forcedDelimiter ?? hinted ?? (tabExtension ? '\t' : DetectDelimiter(text));
        var records = Parse(text, delimiter);
        return new LoadedFile
        {
            Records = records,
            Delimiter = delimiter,
            Encoding = encoding,
            NewLine = text.Contains("\r\n") ? "\r\n" : "\n",
            HasHeader = DetectHeader(records),
        };
    }

    /// <summary>
    /// Guesses whether the first record is a header row. Strong signals: a column whose body is mostly
    /// numbers/dates but whose first cell is text (header) or whose first cell is itself a number (data).
    /// For all-text tables, a header is assumed when the first row is non-empty, unique, and its values
    /// do not reappear in the body.
    /// </summary>
    public static bool DetectHeader(List<string[]> records)
    {
        if (records.Count < 2) return records.Count == 1 && records[0].All(v => v.Trim().Length > 0 && !Values.IsTyped(v));
        var first = records[0];
        var body = records.Skip(1).Take(200).ToList();

        int forHeader = 0, againstHeader = 0;
        for (int c = 0; c < first.Length; c++)
        {
            var head = first[c].Trim();
            var cells = body.Select(r => c < r.Length ? r[c].Trim() : "").Where(v => v.Length > 0).ToList();
            if (cells.Count == 0) continue;
            double typedShare = (double)cells.Count(Values.IsTyped) / cells.Count;
            if (Values.IsTyped(head))
                againstHeader++;
            else if (typedShare >= 0.8 && head.Length > 0)
                forHeader++;
        }
        if (againstHeader > 0 && againstHeader >= forHeader) return false;
        if (forHeader > 0) return true;

        // All columns are text: rely on shape of the first row.
        var names = first.Select(v => v.Trim()).ToList();
        if (names.Any(n => n.Length == 0)) return false;
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) return false;
        int repeated = 0;
        for (int c = 0; c < names.Count; c++)
            if (body.Any(r => c < r.Length && string.Equals(r[c].Trim(), names[c], StringComparison.OrdinalIgnoreCase)))
                repeated++;
        return repeated <= names.Count / 4;
    }

    private static (Encoding, string) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (new UTF8Encoding(false), strict.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: fall back to Windows-1252-compatible Latin-1 so no bytes are lost.
            return (Encoding.Latin1, Encoding.Latin1.GetString(bytes));
        }
    }

    /// <summary>
    /// Parses a sample with every candidate and picks the one that splits rows into the most consistent
    /// number of fields (more than one). Uses the real parser so quoted delimiters and line breaks count correctly.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        const int sampleChars = 64 * 1024;
        var sample = text;
        if (sample.Length > sampleChars)
        {
            int cut = sample.LastIndexOf('\n', sampleChars);
            sample = sample[..(cut > 0 ? cut : sampleChars)];
        }

        char best = ',';
        double bestScore = 0;
        foreach (var d in CandidateDelimiters)
        {
            var widths = Parse(sample, d).Take(200).Where(r => r.Length > 1 || r[0].Length > 0).Select(r => r.Length).ToList();
            if (widths.Count == 0) continue;
            var mode = widths.GroupBy(w => w).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key < 2) continue;
            double consistency = (double)mode.Count() / widths.Count;
            double score = consistency * 100 + Math.Min(mode.Key, 30);
            // Colons also appear inside times and URLs, so only choose them on clearly better evidence.
            if (d == ':') score -= 15;
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }

    /// <summary>RFC 4180 parser: quoted fields, doubled quotes, and line breaks inside quotes.</summary>
    public static List<string[]> Parse(string text, char delimiter)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool fieldStarted = false;
        int i = 0;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            records.Add(fields.ToArray());
            fields.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
                i++;
                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c == '\r' || c == '\n')
            {
                EndRecord();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
            i++;
        }

        if (field.Length > 0 || fields.Count > 0 || fieldStarted)
            EndRecord();

        return records;
    }

    public static string Quote(string value, char delimiter)
    {
        bool needs = value.IndexOf(delimiter) >= 0 || value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0
                     || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return needs ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> records, char delimiter, string newLine)
    {
        var sb = new StringBuilder();
        foreach (var record in records)
        {
            for (int c = 0; c < record.Count; c++)
            {
                if (c > 0) sb.Append(delimiter);
                sb.Append(Quote(record[c], delimiter));
            }
            sb.Append(newLine);
        }
        return sb.ToString();
    }

    public static string DelimiterName(char d) => d switch
    {
        ',' => "Comma",
        '\t' => "Tab",
        ';' => "Semicolon",
        '|' => "Pipe",
        ':' => "Colon",
        _ => $"'{d}'",
    };
}

public static class Values
{
    /// <summary>
    /// Parses plain numbers plus common spreadsheet spellings: "1,234.5", "1.234,5", "1,5", "$12", "€ 3", "45%", "-7".
    /// </summary>
    public static bool TryNumber(string s, out double value)
    {
        value = 0;
        var t = s.AsSpan().Trim();
        if (t.Length == 0 || t.Length > 40) return false;
        if (t[0] is '$' or '€' or '£' or '¥') t = t[1..].TrimStart();
        if (t.Length > 0 && t[^1] == '%') t = t[..^1].TrimEnd();
        if (t.Length == 0 || !(char.IsDigit(t[0]) || t[0] is '-' or '+' or '.' || (t.Length > 1 && char.IsDigit(t[1])))) return false;

        int comma = t.LastIndexOf(','), dot = t.LastIndexOf('.');
        string normalized;
        if (comma >= 0 && dot >= 0)
            normalized = comma > dot ? t.ToString().Replace(".", "").Replace(',', '.') : t.ToString().Replace(",", "");
        else if (comma >= 0)
            // "1,234" / "1,234,567" are thousands; "1,5" / "12,75" are decimal commas.
            normalized = t.Length - comma - 1 == 3 && t.IndexOf(',') != 0 ? t.ToString().Replace(",", "") : t.ToString().Replace(',', '.');
        else
            normalized = t.ToString();

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static bool TryDate(string s, out DateTime value)
    {
        value = default;
        var t = s.AsSpan().Trim();
        if (t.Length < 6 || t.Length > 40 || !char.IsAsciiDigit(t[0]) && !char.IsLetter(t[0])) return false;
        if (t.IndexOfAny('-', '/', '.') < 0 && t.IndexOf(' ') < 0) return false;
        int digits = 0;
        foreach (var ch in t) if (char.IsAsciiDigit(ch)) digits++;
        if (digits < 4) return false;
        return DateTime.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value)
               || DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    /// <summary>True when the value looks like data (number or date) rather than a column name.</summary>
    public static bool IsTyped(string s) => TryNumber(s, out _) || TryDate(s, out _);
}

/// <summary>
/// Fast "do what I mean" sort. Each cell is classified once (number, date, text, empty) and turned into a
/// comparable key, so 300k rows sort in well under a second instead of re-parsing on every comparison.
/// Order: numbers, then dates, then text (case-insensitive, natural digit runs), blanks always last.
/// The sort is stable, so sorting by one column and then another keeps ties in the earlier order.
/// </summary>
public static class Sorter
{
    private readonly struct Key
    {
        public readonly byte Kind;      // 0 number, 1 date, 2 text, 3 empty
        public readonly double Number;
        public readonly string Text;

        public Key(byte kind, double number, string text)
        {
            Kind = kind;
            Number = number;
            Text = text;
        }
    }

    private static Key MakeKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new Key(3, 0, "");
        if (Values.TryNumber(s, out var n)) return new Key(0, n, "");
        if (Values.TryDate(s, out var d)) return new Key(1, d.Ticks, "");
        return new Key(2, 0, s.Trim());
    }

    public static List<Row> Sort(IReadOnlyList<Row> rows, int column, bool ascending)
    {
        int n = rows.Count;
        var keys = new Key[n];
        Parallel.For(0, n, i => keys[i] = MakeKey(column < rows[i].Cells.Count ? rows[i].Cells[column] : ""));

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        int dir = ascending ? 1 : -1;

        Array.Sort(order, (a, b) =>
        {
            ref readonly var ka = ref keys[a];
            ref readonly var kb = ref keys[b];
            // Blanks stay at the bottom in both directions.
            if (ka.Kind == 3 || kb.Kind == 3)
                return ka.Kind == kb.Kind ? a.CompareTo(b) : ka.Kind == 3 ? 1 : -1;

            int c = ka.Kind != kb.Kind
                ? ka.Kind.CompareTo(kb.Kind)
                : ka.Kind == 2 ? NaturalCompare(ka.Text, kb.Text) : ka.Number.CompareTo(kb.Number);
            return c != 0 ? c * dir : a.CompareTo(b);
        });

        var result = new List<Row>(n);
        foreach (var i in order) result.Add(rows[i]);
        return result;
    }

    /// <summary>Case-insensitive compare where digit runs compare by value: "Item 2" &lt; "Item 10".</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (si < a.Length && a[si] == '0') si++;
                while (sj < b.Length && b[sj] == '0') sj++;
                int ei = si, ej = sj;
                while (ei < a.Length && char.IsAsciiDigit(a[ei])) ei++;
                while (ej < b.Length && char.IsAsciiDigit(b[ej])) ej++;
                int lenCmp = (ei - si).CompareTo(ej - sj);
                if (lenCmp != 0) return lenCmp;
                int cmp = string.CompareOrdinal(a, si, b, sj, ei - si);
                if (cmp != 0) return cmp;
                i = ei;
                j = ej;
                continue;
            }
            char ca = char.ToUpperInvariant(a[i]), cb = char.ToUpperInvariant(b[j]);
            if (ca != cb)
            {
                // Letters with accents compare by culture so "é" sits next to "e".
                int c = string.Compare(a, i, b, j, 1, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                return c != 0 ? c : ca.CompareTo(cb);
            }
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>
/// Search box semantics (modelled on Ron's Data Edit quick filter):
/// - the query splits on whitespace into terms; "quoted text" stays one term;
/// - a plain term matches when a cell contains it; a term with * or ? is a wildcard pattern for the whole cell
///   (so "a*" = starts with a, "*.com" = ends with .com, "a?c" = a, any char, c);
/// - Match All: every term must be found somewhere in the row (terms may sit in different cells);
///   Match Any: at least one term found in any cell;
/// - Invert keeps the rows that do NOT match;
/// - without Match Case, comparison ignores case and accents ("cafe" finds "Café").
/// </summary>
public sealed class RowSearch
{
    private sealed record Term(string Text, Regex? Pattern);

    private readonly Term[] _terms;
    private readonly int _column;
    private readonly bool _matchCase;

    public RowSearch(string query, int column, bool matchCase, bool matchAll = true, bool invert = false)
    {
        _column = column;
        _matchCase = matchCase;
        MatchAll = matchAll;
        Invert = invert;
        _terms = SplitTerms(query).Select(t => MakeTerm(Fold(t, matchCase), matchCase)).ToArray();
    }

    public bool IsEmpty => _terms.Length == 0;
    public bool MatchAll { get; }
    public bool Invert { get; }

    private static Term MakeTerm(string text, bool matchCase)
    {
        if (text.IndexOfAny(new[] { '*', '?' }) < 0) return new Term(text, null);
        var pattern = "^" + Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        var options = RegexOptions.Singleline | RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Term(text, new Regex(pattern, options));
    }

    public static string[] SplitTerms(string query)
    {
        var terms = new List<string>();
        int i = 0;
        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i])) { i++; continue; }
            if (query[i] == '"')
            {
                int end = query.IndexOf('"', i + 1);
                if (end < 0) end = query.Length;
                if (end > i + 1) terms.Add(query[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            int start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
            terms.Add(query[start..i]);
        }
        return terms.ToArray();
    }

    /// <summary>Removes accents when case/accent-insensitive; ASCII text is returned untouched (fast path).</summary>
    private static string Fold(string s, bool matchCase)
    {
        if (matchCase) return s;
        bool ascii = true;
        foreach (var ch in s)
            if (ch > 127) { ascii = false; break; }
        if (ascii) return s;

        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private bool TermMatches(Term term, string foldedCell) =>
        term.Pattern?.IsMatch(foldedCell)
        ?? foldedCell.Contains(term.Text, _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private bool InScope(int column) => _column < 0 || column == _column;

    /// <summary>True when this cell contains at least one term (used for highlighting and Find Next).</summary>
    public bool IsCellMatch(string? cell, int column)
    {
        if (IsEmpty || Invert || cell == null || !InScope(column)) return false;
        var folded = Fold(cell, _matchCase);
        foreach (var t in _terms)
            if (TermMatches(t, folded)) return true;
        return false;
    }

    public bool Matches(Row row)
    {
        if (IsEmpty) return true;
        var cells = row.Cells;
        bool result;
        if (MatchAll)
        {
            Span<bool> found = _terms.Length <= 64 ? stackalloc bool[_terms.Length] : new bool[_terms.Length];
            int remaining = _terms.Length;
            for (int c = 0; c < cells.Count && remaining > 0; c++)
            {
                if (!InScope(c)) continue;
                var folded = Fold(cells[c], _matchCase);
                for (int t = 0; t < _terms.Length; t++)
                    if (!found[t] && TermMatches(_terms[t], folded)) { found[t] = true; remaining--; }
            }
            result = remaining == 0;
        }
        else
        {
            result = false;
            for (int c = 0; c < cells.Count && !result; c++)
            {
                if (!InScope(c)) continue;
                var folded = Fold(cells[c], _matchCase);
                foreach (var t in _terms)
                    if (TermMatches(t, folded)) { result = true; break; }
            }
        }
        return result != Invert;
    }

    /// <summary>Filters in parallel while keeping the original row order.</summary>
    public List<Row> Filter(IReadOnlyList<Row> rows, CancellationToken token)
    {
        if (IsEmpty) return rows.ToList();
        var hit = new bool[rows.Count];
        Parallel.For(0, rows.Count, new ParallelOptions { CancellationToken = token }, i => hit[i] = Matches(rows[i]));
        var result = new List<Row>();
        for (int i = 0; i < rows.Count; i++)
            if (hit[i]) result.Add(rows[i]);
        return result;
    }
}
