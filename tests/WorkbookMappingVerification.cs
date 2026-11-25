using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BeamSchedulePlugin;

internal static class WorkbookMappingVerification
{
    internal static void Run(string etabsPath, string revitPath, string outputDirectory)
    {
        string etabsHash = Hash(etabsPath), revitHash = Hash(revitPath);
        var (summaryHeaders, summaryRows) = ReadSheet(etabsPath, "Beam summary");
        var (stationHeaders, stationRows) = ReadSheet(etabsPath, "Design stations");
        object?[] Reorder(object?[] row, string[] source, string[] target) => target.Select(header =>
        {
            int i = Array.IndexOf(source, header);
            return i < 0 ? null : row[i];
        }).ToArray();
        var summary = new SummaryTable(summaryRows.Select(r => Reorder(r, summaryHeaders, BeamScheduleSummary.OutputHeaders)).ToArray());
        var report = new DemandReport(stationRows.Select(r => Reorder(r, stationHeaders, BeamDemandExport.Headers)).ToArray(),
            summary.Rows.Count, stationRows.Count);
        var mapped = BeamScheduleSummary.WithRevitMapping(summary, RevitScheduleReader.Read(revitPath));
        var mapping = mapped.Mapping!;
        var parity = new[] { (160, 160), (43, 52), (666, 666), (188, 188), (27, 76), (230, 230), (6, 6), (14, 59), (26, 27) };
        bool matchesReference = mapping.PassTotals.Count == 9 && mapping.PassTotals.Select((p, i) =>
            p.AddedRevit == parity[i].Item1 && p.AddedEtabs == parity[i].Item2).All(b => b)
            && mapping.MappedRevitCount == 1360 && mapping.MappedEtabsCount == 1464
            && mapping.PassTotals[8].AddedGroups == 7 && mapping.PassTotals[8].AddedLinks == 46;
        if (mapping.Links.GroupBy(l => l.Etabs).Any(g => g.Select(l => (l.Pass, l.GroupId)).Distinct().Count() > 1)
            || mapping.Links.GroupBy(l => l.Revit).Any(g => g.Select(l => (l.Pass, l.GroupId)).Distinct().Count() > 1))
            throw new InvalidOperationException("A line was reassigned between mapping groups or passes.");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "revit-nine-pass-verification.xlsx");
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(etabsPath), StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(output).Equals(Path.GetFullPath(revitPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verification output must not overwrite an input workbook.");
        DemandScheduleWriter.Write(output, report, mapped);
        var rows = RevitMappingSheet.Rows(mapping);
        using (var csv = new StreamWriter(Path.Combine(outputDirectory, "revit-nine-pass-verification.csv"), false, new UTF8Encoding(true)))
            foreach (var row in rows)
                csv.WriteLine(string.Join(",", row.Select(value =>
                {
                    string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    if (value is string && text.TrimStart().Length > 0 && "=+-@".Contains(text.TrimStart()[0])) text = "'" + text;
                    return "\"" + text.Replace("\"", "\"\"") + "\"";
                })));
        if (etabsHash != Hash(etabsPath) || revitHash != Hash(revitPath))
            throw new InvalidOperationException("A source workbook changed during verification.");
        using (var zip = ZipFile.OpenRead(output))
        {
            using var stream = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
            XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var sheet = XDocument.Load(stream);
            int linkRows = sheet.Descendants(x + "row").Count(r => r.Elements(x + "c").FirstOrDefault()?.Value == "Segment link");
            if (linkRows != mapping.Links.Count) throw new InvalidOperationException("Persisted worksheet link count is incorrect.");
        }
        var result = new
        {
            EtabsValid = mapping.Etabs.Count, RevitValid = mapping.Revit.Beams.Count, TranslationMm = mapping.Translation,
            mapping.Alignment, mapping.PassTotals, mapping.MappedRevitCount, mapping.MappedEtabsCount,
            SegmentLinks = mapping.Links.Count, AmbiguityRecords = mapping.Ambiguities.Count,
            UnmatchedRevit = mapping.Revit.Beams.Count - mapping.MappedRevitCount,
            UnmatchedEtabs = mapping.Etabs.Count - mapping.MappedEtabsCount,
            SkippedRevit = mapping.Revit.Skipped, SkippedEtabs = mapping.SkippedEtabs,
            FullInputParityPassed = matchesReference, SourceHashesUnchanged = true,
            Note = "Offline workbook parity verification. Geometric matching, not beam design verification. ETABS UI not exercised."
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "revit-nine-pass-verification.json"), json);
        Console.WriteLine(json);
        if (!matchesReference) throw new InvalidOperationException("Full-input nine-pass results differ from the Grasshopper reference; see saved verification report.");
    }

    private static FileStream Open(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static string Hash(string path) { using var file = Open(path); return Convert.ToHexString(SHA256.HashData(file)); }

    private static (string[] Headers, List<object?[]> Rows) ReadSheet(string path, string name)
    {
        using var file = Open(path); using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace p = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument Part(string part) { using var s = zip.GetEntry(part)!.Open(); return XDocument.Load(s); }
        var relations = Part("xl/_rels/workbook.xml.rels").Descendants(p + "Relationship").ToArray();
        string Resolve(XElement rel) => new Uri(new Uri("http://xlsx.invalid/xl/workbook.xml"), (string)rel.Attribute("Target")!).AbsolutePath.TrimStart('/');
        var sheet = Part("xl/workbook.xml").Descendants(x + "sheet").Single(s => (string?)s.Attribute("name") == name);
        var relation = relations.Single(rel => (string?)rel.Attribute("Id") == (string?)sheet.Attribute(r + "id"));
        var stringsRel = relations.SingleOrDefault(rel => ((string?)rel.Attribute("Type"))?.EndsWith("/sharedStrings") == true);
        var strings = stringsRel == null ? Array.Empty<string>() : Part(Resolve(stringsRel)).Descendants(x + "si")
            .Select(si => string.Concat(si.Descendants(x + "t").Select(t => t.Value))).ToArray();
        object? Value(XElement c)
        {
            if (c.Element(x + "f") is { } formula) return new SpreadsheetFormula(formula.Value);
            string? value = (string?)c.Element(x + "v");
            return (string?)c.Attribute("t") switch
            {
                "s" => strings[int.Parse(value!, CultureInfo.InvariantCulture)],
                "inlineStr" => string.Concat(c.Descendants(x + "t").Select(t => t.Value)),
                "str" => value,
                "e" or "b" => null,
                _ => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : value
            };
        }
        int Column(XElement c) => ((string)c.Attribute("r")!).TakeWhile(char.IsAsciiLetter).Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1) - 1;
        var rows = Part(Resolve(relation)).Descendants(x + "row").ToArray();
        var headerCells = rows[0].Elements(x + "c").ToArray();
        var headers = new string[headerCells.Max(Column) + 1];
        foreach (var c in headerCells) headers[Column(c)] = Convert.ToString(Value(c), CultureInfo.InvariantCulture) ?? "";
        var data = rows.Skip(1).Select(row =>
        {
            var values = new object?[headers.Length];
            foreach (var c in row.Elements(x + "c"))
                if (Column(c) < values.Length) values[Column(c)] = Value(c);
            return values;
        }).Where(row => row.Any(v => v != null)).ToList();
        return (headers, data);
    }
}
