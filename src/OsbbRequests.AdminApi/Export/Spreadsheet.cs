using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OsbbRequests.AdminApi.Export;

internal static class Spreadsheet
{
    public static byte[] Create(string title, IReadOnlyList<string> headings, IEnumerable<string[]> rows, CancellationToken ct)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{title}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs></styleSheet>
                """);
            using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            xml.WriteStartDocument(); xml.WriteStartElement("worksheet", ns);
            xml.WriteStartElement("sheetViews", ns); xml.WriteStartElement("sheetView", ns); xml.WriteAttributeString("workbookViewId", "0");
            xml.WriteStartElement("pane", ns); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen"); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
            xml.WriteStartElement("cols", ns); xml.WriteStartElement("col", ns); xml.WriteAttributeString("min", "1"); xml.WriteAttributeString("max", headings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)); xml.WriteAttributeString("width", "24"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); xml.WriteEndElement();
            xml.WriteStartElement("sheetData", ns);
            var rowNumber = 0;
            void WriteRow(IEnumerable<string> values, bool header)
            {
                ct.ThrowIfCancellationRequested();
                xml.WriteStartElement("row", ns);
                xml.WriteAttributeString("r", (++rowNumber).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var column = 0;
                foreach (var value in values)
                {
                    xml.WriteStartElement("c", ns);
                    var number = ++column;
                    var columnName = "";
                    while (number > 0) { number--; columnName = (char)('A' + number % 26) + columnName; number /= 26; }
                    xml.WriteAttributeString("r", columnName + rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("t", "inlineStr");
                    if (header) xml.WriteAttributeString("s", "1");
                    xml.WriteStartElement("is", ns); xml.WriteStartElement("t", ns); xml.WriteAttributeString("xml", "space", null, "preserve");
                    var text = new StringBuilder();
                    foreach (var rune in (value ?? "").EnumerateRunes())
                        if (rune.Value is 9 or 10 or 13 || rune.Value >= 32 && rune.Value is not (0xfffe or 0xffff)) text.Append(rune.ToString());
                    xml.WriteString(text.ToString()); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            WriteRow(headings, true);
            foreach (var row in rows) WriteRow(row, false);
            xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndDocument();
        }
        return output.ToArray();
    }
    private static void Add(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
