using System.IO.Compression;
using System.Text;
using IPScaner.Core.Storage;

namespace IPScaner.Core.Export;

/// <summary>
/// Result export helpers — CSV and real .xlsx.
/// </summary>
/// <remarks>
/// The original "导出到Excel" wrote a bare CSV with no encoding argument, which
/// produces UTF-8 <i>without</i> a BOM; Excel on a Chinese locale then renders the
/// Chinese column headers as mojibake. Here:
/// <list type="bullet">
/// <item><see cref="WriteCsv"/> writes UTF-8 <b>with</b> a BOM by default so the
/// file opens correctly on double-click, and quotes fields per RFC&nbsp;4180.</item>
/// <item><see cref="WriteXlsx"/> emits a genuine SpreadsheetML workbook with no
/// third-party dependency, so "导出到Excel" really produces an .xlsx.</item>
/// </list>
/// The original's file-naming convention is preserved.
/// </remarks>
public static class TableExporter
{
    /// <summary>Builds the original's timestamped file name, e.g. "IP批量扫描-20260812_174828.csv".</summary>
    public static string BuildFileName(string prefix, string extension, DateTime? timestamp = null)
    {
        var stamp = (timestamp ?? DateTime.Now).ToString("yyyyMMddHHmmss");
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}-{stamp}{ext}";
    }

    /// <summary>Writes a CSV file.</summary>
    public static void WriteCsv(
        string path,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> rows,
        bool withBom = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Escape)));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, sb.ToString(), withBom ? TextFileEncoding.Utf8Bom : TextFileEncoding.Utf8NoBom);
    }

    /// <summary>RFC 4180 field escaping: quote when the value contains , " or a newline.</summary>
    public static string Escape(string? value)
    {
        var v = value ?? string.Empty;
        if (v.IndexOfAny([',', '"', '\r', '\n']) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Writes a minimal but valid .xlsx workbook (one sheet, inline strings).
    /// </summary>
    public static void WriteXlsx(
        string path,
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> rows)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // A package must be created from scratch each time.
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);

        AddEntry(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        AddEntry(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="{Xml(SafeSheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);

        AddEntry(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        AddEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(headers, rows));
    }

    private static string BuildSheet(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        var rowIndex = 1;
        sb.Append($"<row r=\"{rowIndex}\">");
        for (var c = 0; c < headers.Count; c++)
        {
            sb.Append($"<c r=\"{Column(c)}{rowIndex}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(headers[c])}</t></is></c>");
        }
        sb.Append("</row>");

        foreach (var row in rows)
        {
            rowIndex++;
            sb.Append($"<row r=\"{rowIndex}\">");
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c] ?? string.Empty;
                // Numbers are written as numbers so Excel can sum/sort them.
                if (c > 0 && long.TryParse(value, out _))
                {
                    sb.Append($"<c r=\"{Column(c)}{rowIndex}\"><v>{value}</v></c>");
                }
                else
                {
                    sb.Append($"<c r=\"{Column(c)}{rowIndex}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(value)}</t></is></c>");
                }
            }
            sb.Append("</row>");
        }

        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    /// <summary>0 -> A, 25 -> Z, 26 -> AA ...</summary>
    public static string Column(int index)
    {
        var result = string.Empty;
        var i = index;
        do
        {
            result = (char)('A' + i % 26) + result;
            i = i / 26 - 1;
        } while (i >= 0);
        return result;
    }

    /// <summary>Excel forbids : \ / ? * [ ] in sheet names and caps them at 31 chars.</summary>
    public static string SafeSheetName(string? name)
    {
        var cleaned = new string((name ?? "Sheet1").Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string Xml(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
