using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BeamSchedulePlugin;

internal static class DemandScheduleWriter
{
    private static readonly int[] StationOutputIndices = Enumerable.Range(0, BeamDemandExport.Headers.Length)
        .Where(index => index != 3 && index != 4 && index != 5).ToArray();
    private static readonly string[] StationOutputHeaders = StationOutputIndices
        .Select(index => BeamDemandExport.Headers[index]).ToArray();

    private static object?[] StationOutputRow(object?[] row) =>
        StationOutputIndices.Select(index => row[index]).ToArray();

    internal static void Write(string path, DemandReport report, SummaryTable? summary, bool overwrite = true,
        MappingReview? review = null)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an .xlsx or .csv output file.");
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Choose an output directory.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Output directory does not exist: {directory}");
        string temporary = Path.Combine(directory, $".beam-demands-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                    WriteCsv(stream, report);
                else
                    WriteWorkbook(stream, summary ?? throw new InvalidOperationException("Beam summary is required for Excel export."), review);
            }
            File.Move(temporary, fullPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteCsv(Stream stream, DemandReport report)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
        WriteCsvRow(writer, StationOutputHeaders);
        foreach (object?[] row in report.Rows) WriteCsvRow(writer, StationOutputRow(row));
    }

    private static void WriteCsvRow(StreamWriter writer, IReadOnlyList<object?> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) writer.Write(',');
            object? value = values[i];
            string text = value switch
            {
                null => "",
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };
            if (value is string && text.TrimStart().Length > 0
                && "=+-@".Contains(text.TrimStart()[0]))
                text = "'" + text;
            writer.Write('"');
            writer.Write(text.Replace("\"", "\"\""));
            writer.Write('"');
        }
        writer.WriteLine();
    }

    private static void WriteWorkbook(Stream stream, SummaryTable summary, MappingReview? review)
    {
        var mappingRows = summary.Mapping == null ? null : RevitMappingSheet.Rows(summary.Mapping);
        if (review != null && !ReferenceEquals(review.Mapping, summary.Mapping))
            throw new InvalidOperationException("Review exports must share the same mapping run.");
        var (visual, effective) = review == null ? (null, null) : MappingDecisionSheets.Rows(review);
        if (summary.Rows.Count + 1 > 1_048_576 || mappingRows?.Count > 1_048_576
            || visual?.Count > 1_048_576 || effective?.Count > 1_048_576)
            throw new InvalidOperationException("The schedule exceeds the Excel worksheet row limit. Export CSV instead.");
        const string worksheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        const string officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteXml(archive, "[Content_Types].xml", xml =>
        {
            xml.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Empty(xml, "Default", ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
            Empty(xml, "Default", ("Extension", "xml"), ("ContentType", "application/xml"));
            Empty(xml, "Override", ("PartName", "/xl/workbook.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
            Empty(xml, "Override", ("PartName", "/xl/worksheets/sheet1.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            if (mappingRows != null)
                Empty(xml, "Override", ("PartName", "/xl/worksheets/sheet2.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
            if (review != null)
                foreach (int i in new[] { 3, 4 })
                    Empty(xml, "Override", ("PartName", $"/xl/worksheets/sheet{i}.xml"), ("ContentType", worksheetType));
            Empty(xml, "Override", ("PartName", "/xl/styles.xml"), ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"));
            xml.WriteEndElement();
        });
        WriteXml(archive, "_rels\\.rels", xml =>
        {
            xml.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Empty(xml, "Relationship", ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), ("Target", "xl/workbook.xml"));
            xml.WriteEndElement();
        });
        WriteXml(archive, "xl\\workbook.xml", xml =>
        {
            xml.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            xml.WriteStartElement("sheets");
            xml.WriteStartElement("sheet");
            xml.WriteAttributeString("name", "Beam summary");
            xml.WriteAttributeString("sheetId", "1");
            xml.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId1");
            xml.WriteEndElement();
            if (mappingRows != null)
            {
                xml.WriteStartElement("sheet");
                xml.WriteAttributeString("name", "Revit mapping");
                xml.WriteAttributeString("sheetId", "2");
                xml.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId2");
                xml.WriteEndElement();
            }
            if (review != null)
                foreach (var (id, name) in new[] { (3, "Visual Review"), (4, "Effective Links") })
                {
                    xml.WriteStartElement("sheet");
                    xml.WriteAttributeString("name", name);
                    xml.WriteAttributeString("sheetId", id.ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("r", "id", officeRel, $"rId{id + 1}");
                    xml.WriteEndElement();
                }
            xml.WriteEndElement();
            Empty(xml, "calcPr", ("calcMode", "auto"), ("fullCalcOnLoad", "1"), ("forceFullCalc", "1"));
            xml.WriteEndElement();
        });
        WriteXml(archive, "xl\\_rels\\workbook.xml.rels", xml =>
        {
            xml.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Empty(xml, "Relationship", ("Id", "rId1"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", "worksheets/sheet1.xml"));
            Empty(xml, "Relationship", ("Id", "rId3"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), ("Target", "styles.xml"));
            if (mappingRows != null)
                Empty(xml, "Relationship", ("Id", "rId2"), ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), ("Target", "worksheets/sheet2.xml"));
            if (review != null)
                foreach (int i in new[] { 3, 4 })
                    Empty(xml, "Relationship", ("Id", $"rId{i + 1}"), ("Type", officeRel + "/worksheet"), ("Target", $"worksheets/sheet{i}.xml"));
            xml.WriteEndElement();
        });
        WriteStyles(archive, mapping: summary.Mapping != null);
        WriteXml(archive, "xl\\worksheets\\sheet1.xml", xml =>
        {
            xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            WriteSheetLayout(xml, true, mapping: summary.Mapping != null);
            xml.WriteStartElement("sheetData");
            WriteExcelRow(xml, 1, summary.ColumnHeaders, summary: true);
            for (int i = 0; i < summary.Rows.Count; i++) WriteExcelRow(xml, i + 2, summary.Rows[i], summary: true);
            xml.WriteEndElement();
            WriteSummaryFilters(xml, summary.Rows.Count + 1, summary.ColumnHeaders);
            xml.WriteEndElement();
        });
        if (mappingRows != null)
            WriteXml(archive, "xl\\worksheets\\sheet2.xml", xml =>
            {
                xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                WriteSheetLayout(xml, false);
                xml.WriteStartElement("sheetData");
                for (int i = 0; i < mappingRows.Count; i++)
                    WriteExcelRow(xml, i + 1, mappingRows[i], preciseNumbers: true);
                xml.WriteEndElement();
                WriteFilters(xml, mappingRows.Count, RevitMappingSheet.Headers,
                    new HashSet<string>(StringComparer.Ordinal) { "Revit Mark" });
                xml.WriteEndElement();
            });
        if (visual != null && effective != null)
        {
            WriteDecisionSheet(archive, 3, visual, MappingDecisionSheets.VisualHeaders, true);
            WriteDecisionSheet(archive, 4, effective, MappingDecisionSheets.EffectiveHeaders, false);
        }
    }

    private static void WriteDecisionSheet(ZipArchive archive, int sheet, List<object?[]> rows, string[] headers,
        bool visual)
    {
        WriteXml(archive, $"xl\\worksheets\\sheet{sheet}.xml", xml =>
        {
            xml.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            xml.WriteStartElement("cols");
            for (int i = 1; i <= headers.Length; i++)
            {
                string column = i.ToString(CultureInfo.InvariantCulture);
                string width = visual && i is 15 or 16 or 19 ? "38" : "19";
                Empty(xml, "col", ("min", column), ("max", column), ("width", width), ("customWidth", "1"));
            }
            xml.WriteEndElement();
            xml.WriteStartElement("sheetData");
            for (int i = 0; i < rows.Count; i++) WriteExcelRow(xml, i + 1, rows[i], preciseNumbers: true, visualReview: visual);
            xml.WriteEndElement();
            Empty(xml, "autoFilter", ("ref", $"A1:{ColumnName(headers.Length - 1)}{rows.Count}"));
            if (visual && rows.Count > 1)
            {
                xml.WriteStartElement("dataValidations");
                xml.WriteAttributeString("count", "1");
                xml.WriteStartElement("dataValidation");
                xml.WriteAttributeString("type", "list");
                xml.WriteAttributeString("allowBlank", "1");
                xml.WriteAttributeString("showErrorMessage", "1");
                xml.WriteAttributeString("sqref", $"Q2:Q{rows.Count}");
                xml.WriteElementString("formula1", "\"Accept,Reject,Defer\"");
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        });
    }

    private static void WriteSummaryFilters(XmlWriter xml, int lastRow, IReadOnlyList<string> headers)
    {
        var filterHeaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Story", "Review status", "ETABS design errors", "ETABS design warnings",
            "Geometry warning", "Assigned section", "Design section", "Revit mapping status"
        };
        WriteFilters(xml, lastRow, headers, filterHeaders);
    }

    private static void WriteFilters(XmlWriter xml, int lastRow, IReadOnlyList<string> headers, HashSet<string> filterHeaders)
    {
        xml.WriteStartElement("autoFilter");
        xml.WriteAttributeString("ref", $"A1:{ColumnName(headers.Count - 1)}{lastRow}");
        for (int i = 0; i < headers.Count; i++)
        {
            if (!filterHeaders.Contains(headers[i]))
                Empty(xml, "filterColumn", ("colId", i.ToString(CultureInfo.InvariantCulture)), ("hiddenButton", "1"));
        }
        xml.WriteEndElement();
    }

    private static void WriteSheetLayout(XmlWriter xml, bool summary, bool mapping = false)
    {
        xml.WriteStartElement("sheetViews");
        xml.WriteStartElement("sheetView");
        xml.WriteAttributeString("workbookViewId", "0");
        if (summary)
            Empty(xml, "pane", ("xSplit", "3"), ("ySplit", "1"), ("topLeftCell", "D2"), ("activePane", "bottomRight"), ("state", "frozen"));
        else
            Empty(xml, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("cols");
        if (summary)
        {
            Empty(xml, "col", ("min", "1"), ("max", "3"), ("width", "17"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "4"), ("max", "14"), ("width", "19"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "15"), ("max", "23"), ("width", "31"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "24"), ("max", "41"), ("width", "19"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "42"), ("max", "63"), ("width", "24"), ("customWidth", "1"));
            if (mapping)
            {
                Empty(xml, "col", ("min", "64"), ("max", "65"), ("width", "37"), ("customWidth", "1"));
                Empty(xml, "col", ("min", "66"), ("max", "66"), ("width", "24"), ("customWidth", "1"));
                Empty(xml, "col", ("min", "67"), ("max", "67"), ("width", "24"), ("customWidth", "1"));
                Empty(xml, "col", ("min", "68"), ("max", "68"), ("width", "65"), ("customWidth", "1"));
                Empty(xml, "col", ("min", "69"), ("max", "70"), ("width", "24"), ("customWidth", "1"));
            }
        }
        else if (mapping)
        {
            Empty(xml, "col", ("min", "1"), ("max", "1"), ("width", "24"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "2"), ("max", "2"), ("width", "48"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "3"), ("max", "3"), ("width", "70"), ("customWidth", "1"));
            Empty(xml, "col", ("min", "4"), ("max", "46"), ("width", "23"), ("customWidth", "1"));
        }
        else
            Empty(xml, "col", ("min", "1"), ("max", "26"), ("width", "21"), ("customWidth", "1"));
        xml.WriteEndElement();
    }

    private static void WriteStyles(ZipArchive archive, bool mapping)
    {
        WriteXml(archive, "xl\\styles.xml", xml =>
        {
            xml.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            xml.WriteStartElement("fonts"); xml.WriteAttributeString("count", "2");
            xml.WriteStartElement("font"); Empty(xml, "sz", ("val", "11")); Empty(xml, "name", ("val", "Calibri")); xml.WriteEndElement();
            xml.WriteStartElement("font"); Empty(xml, "b"); Empty(xml, "color", ("rgb", "FFFFFFFF")); Empty(xml, "sz", ("val", "11")); Empty(xml, "name", ("val", "Calibri")); xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("fills"); xml.WriteAttributeString("count", "5");
            xml.WriteStartElement("fill"); Empty(xml, "patternFill", ("patternType", "none")); xml.WriteEndElement();
            xml.WriteStartElement("fill"); Empty(xml, "patternFill", ("patternType", "gray125")); xml.WriteEndElement();
            foreach (string color in new[] { "FF155A80", "FF39705E", "FF775D83" })
            {
                xml.WriteStartElement("fill"); xml.WriteStartElement("patternFill"); xml.WriteAttributeString("patternType", "solid");
                Empty(xml, "fgColor", ("rgb", color)); Empty(xml, "bgColor", ("indexed", "64"));
                xml.WriteEndElement(); xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteStartElement("borders"); xml.WriteAttributeString("count", "1"); xml.WriteStartElement("border");
            Empty(xml, "left"); Empty(xml, "right"); Empty(xml, "top"); Empty(xml, "bottom"); Empty(xml, "diagonal");
            xml.WriteEndElement(); xml.WriteEndElement();
            xml.WriteStartElement("cellStyleXfs"); xml.WriteAttributeString("count", "1");
            Empty(xml, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"));
            xml.WriteEndElement();
            xml.WriteStartElement("cellXfs"); xml.WriteAttributeString("count", mapping ? "8" : "6");
            Empty(xml, "xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"));
            for (int fill = 2; fill <= 4; fill++)
            {
                xml.WriteStartElement("xf");
                xml.WriteAttributeString("numFmtId", "0"); xml.WriteAttributeString("fontId", "1");
                xml.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("borderId", "0"); xml.WriteAttributeString("xfId", "0");
                xml.WriteAttributeString("applyFont", "1"); xml.WriteAttributeString("applyFill", "1"); xml.WriteAttributeString("applyAlignment", "1");
                Empty(xml, "alignment", ("wrapText", "1"), ("vertical", "center"));
                xml.WriteEndElement();
            }
            Empty(xml, "xf", ("numFmtId", "1"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"),
                ("xfId", "0"), ("applyNumberFormat", "1"));
            Empty(xml, "xf", ("numFmtId", "2"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"),
                ("xfId", "0"), ("applyNumberFormat", "1"));
            if (mapping)
            {
                xml.WriteStartElement("xf");
                xml.WriteAttributeString("numFmtId", "0"); xml.WriteAttributeString("fontId", "0");
                xml.WriteAttributeString("fillId", "0"); xml.WriteAttributeString("borderId", "0");
                xml.WriteAttributeString("xfId", "0"); xml.WriteAttributeString("applyAlignment", "1");
                Empty(xml, "alignment", ("wrapText", "1"), ("vertical", "top"));
                xml.WriteEndElement();
                Empty(xml, "xf", ("numFmtId", "10"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"),
                    ("xfId", "0"), ("applyNumberFormat", "1"));
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
        });
    }

    private static void WriteExcelRow(XmlWriter xml, int rowNumber, IReadOnlyList<object?> values, bool summary = false,
        bool preciseNumbers = false, bool visualReview = false)
    {
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        if (rowNumber == 1) { xml.WriteAttributeString("ht", "48"); xml.WriteAttributeString("customHeight", "1"); }
        for (int i = 0; i < values.Count; i++)
        {
            object? value = values[i];
            if (value == null) continue;
            if (value is string text && text.Length > 32767)
                throw new InvalidOperationException($"Excel cell {ColumnName(i)}{rowNumber} exceeds the 32767-character limit. Narrow the export scope.");
            xml.WriteStartElement("c");
            xml.WriteAttributeString("r", ColumnName(i) + rowNumber.ToString(CultureInfo.InvariantCulture));
            if (rowNumber == 1)
            {
                int style = summary && i >= 5 && i < 41 ? 1 + (i - 5) % 9 / 3 : 1;
                xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            }
            else if (value is double or SpreadsheetFormula)
            {
                bool areaRatio = summary && (i >= BeamScheduleSummary.OutputHeaders.Length
                    || BeamScheduleSummary.OutputHeaders[i].EndsWith(" area ratio", StringComparison.Ordinal));
                xml.WriteAttributeString("s", visualReview && i is 7 or 8 ? "7"
                    : visualReview && i == 2 ? "4" : areaRatio || preciseNumbers ? "5" : "4");
            }
            else if (value is string && (preciseNumbers || summary && i >= BeamScheduleSummary.OutputHeaders.Length))
                xml.WriteAttributeString("s", "6");
            if (value is SpreadsheetFormula formula)
            {
                if (formula.CachedText != null) xml.WriteAttributeString("t", "str");
                xml.WriteElementString("f", formula.Expression);
                if (formula.CachedText != null) xml.WriteElementString("v", formula.CachedText);
            }
            else if (value is double number)
                xml.WriteElementString("v", number.ToString("R", CultureInfo.InvariantCulture));
            else
            {
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteStartElement("is");
                xml.WriteStartElement("t");
                xml.WriteAttributeString("xml", "space", null, "preserve");
                xml.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    internal static string ColumnName(int zeroBased)
    {
        string result = "";
        for (int index = zeroBased + 1; index > 0; index = (index - 1) / 26)
            result = (char)('A' + (index - 1) % 26) + result;
        return result;
    }

    private static void WriteXml(ZipArchive archive, string name, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartDocument();
        write(xml);
        xml.WriteEndDocument();
    }

    private static void Empty(XmlWriter xml, string name, params (string Name, string Value)[] attributes)
    {
        xml.WriteStartElement(name);
        foreach (var attribute in attributes) xml.WriteAttributeString(attribute.Name, attribute.Value);
        xml.WriteEndElement();
    }
}
