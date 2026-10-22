using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace TableEdit.Model;

public sealed record ExportFormat(string Name, string Extension, Func<IReadOnlyList<string>, IReadOnlyList<string[]>, string, string> Render);

public static class Exporters
{
    public static readonly ExportFormat[] All =
    {
        new("JSON", "json", (h, r, _) => Json(h, r)),
        new("HTML Table", "html", (h, r, name) => Html(h, r, name)),
        new("XML", "xml", (h, r, _) => Xml(h, r)),
        new("Markdown Table", "md", (h, r, _) => Markdown(h, r)),
        new("SQL INSERT Statements", "sql", (h, r, name) => Sql(h, r, name)),
        new("Tab-Separated (TSV)", "tsv", (h, r, _) => Csv.Write(Prepend(h, r), '\t', "\n")),
        new("Comma-Separated (CSV)", "csv", (h, r, _) => Csv.Write(Prepend(h, r), ',', "\n")),
    };

    private static IEnumerable<IReadOnlyList<string>> Prepend(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        yield return headers;
        foreach (var r in rows) yield return r;
    }

    private static string Cell(string[] row, int i) => i < row.Length ? row[i] : "";

    public static string Json(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var keys = UniqueNames(headers);
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < keys.Count; i++)
                    w.WriteString(keys[i], Cell(row, i));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Html(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title>");
        sb.AppendLine("<style>body{font-family:-apple-system,system-ui,sans-serif}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}th{background:#f3f3f3}</style>");
        sb.AppendLine("</head><body><table>");
        sb.Append("<thead><tr>");
        foreach (var h in headers) sb.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            for (int i = 0; i < headers.Count; i++) sb.Append("<td>").Append(WebUtility.HtmlEncode(Cell(row, i))).Append("</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    public static string Xml(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var names = UniqueNames(headers).Select(XmlName).ToList();
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            w.WriteStartElement("rows");
            foreach (var row in rows)
            {
                w.WriteStartElement("row");
                for (int i = 0; i < names.Count; i++)
                    w.WriteElementString(names[i], Cell(row, i));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString();
    }

    public static string Markdown(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        static string Esc(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers.Select(Esc))).AppendLine(" |");
        sb.Append('|').Append(string.Concat(headers.Select(_ => " --- |"))).AppendLine();
        foreach (var row in rows)
            sb.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, headers.Count).Select(i => Esc(Cell(row, i))))).AppendLine(" |");
        return sb.ToString();
    }

    public static string Sql(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, string tableName)
    {
        static string Ident(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var cols = string.Join(", ", UniqueNames(headers).Select(Ident));
        var table = Ident(string.IsNullOrWhiteSpace(tableName) ? "data" : tableName);
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = Enumerable.Range(0, headers.Count).Select(i =>
            {
                var v = Cell(row, i);
                if (v.Length == 0) return "NULL";
                // Keep zero-padded codes such as ZIPs as text.
                bool leadingZero = v.Length > 1 && v[0] == '0' && v[1] != '.';
                if (!leadingZero && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return v;
                return "'" + v.Replace("'", "''") + "'";
            });
            sb.Append("INSERT INTO ").Append(table).Append(" (").Append(cols).Append(") VALUES (")
              .Append(string.Join(", ", values)).AppendLine(");");
        }
        return sb.ToString();
    }

    private static List<string> UniqueNames(IReadOnlyList<string> headers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        for (int i = 0; i < headers.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(headers[i]) ? $"Column{i + 1}" : headers[i].Trim();
            var candidate = name;
            for (int n = 2; !seen.Add(candidate); n++) candidate = $"{name}_{n}";
            result.Add(candidate);
        }
        return result;
    }

    private static string XmlName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
        var s = sb.ToString();
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) s = "_" + s;
        return s;
    }
}
