using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using BeamSchedulePlugin;

internal static class RevitMappingTests
{
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static MappingBeam Beam(string id, double start, double end, double y = 0, double z = 0) =>
        new(id, new(start, y, z), new(end, y, z));
    private static RevitScheduleData Data(params MappingBeam[] beams) => new("synthetic.xlsx",
        beams.Select((b, i) => b with { ExcelRow = i + 2, Mark = $"RB-{i + 1}" }).ToArray(), Array.Empty<SkippedMappingBeam>());

    // Isolate pass thresholds from translation estimation with an already established vector.
    private static RevitMappingResult Apply(MappingBeam[] sources, params MappingBeam[] targets) =>
        RevitMappingPasses.Apply(new(Data(targets), sources, Array.Empty<SkippedMappingBeam>(), default(BeamPoint),
            Array.Empty<BeamMappingPair>(), Array.Empty<TranslationScore>()));
    private static int Pass(MappingBeam source, MappingBeam target) =>
        Apply(new[] { source }, target).Links.SingleOrDefault()?.Pass ?? 0;

    internal static void Run(string directory, DemandReport report, SummaryTable summary)
    {
        TestTranslation();
        TestPasses();
        TestInputAndWorkbook(directory, report, summary);
        Console.WriteLine("Revit nine-pass mapping, reader and workbook tests passed.");
    }

    private static void TestTranslation()
    {
        var offset = new BeamPoint(-12345.25, 6789.5, 12000);
        var sources = new[] { Beam("A", 0, 1000),
            new MappingBeam("B", new(2345, 200, 300), new(2345, 1434, 300)),
            new MappingBeam("C", new(500, 3000, 600), new(500, 3000, 2167)) };
        var targets = sources.Select((b, i) => b with { Reference = $"R{i}",
            Start = (i == 1 ? b.End : b.Start) + offset, End = (i == 1 ? b.Start : b.End) + offset }).ToArray();
        var known = RevitBeamMatcher.Match(sources, Data(targets));
        Check(known.Translation == offset && known.Pairs.Count == 3 && known.Links.All(l => l.Pass == 1)
            && known.Links.Single(l => l.Etabs.Reference == "B").Reversed, "Known translation and reversed endpoint match failed.");
        var residual = new BeamPoint(6, 8, 0);
        var boundary = targets[0] with { Start = targets[0].Start - residual, End = targets[0].End + residual };
        var atLimit = RevitBeamMatcher.Match(new[] { sources[0] }, Data(boundary));
        Check(atLimit.Pairs.Count == 1 && Math.Abs(atLimit.Pairs[0].MaximumEndpointError - 10) < 1e-8, "10 mm endpoint boundary must be inclusive.");
        var outside = boundary with { Start = boundary.Start - new BeamPoint(.001, 0, 0), End = boundary.End + new BeamPoint(.001, 0, 0) };
        Check(RevitBeamMatcher.Match(new[] { sources[0] }, Data(outside)).Translation == null, "Endpoints outside 10 mm must not establish translation.");
        var duplicates = new[] { sources[0], sources[0] with { Reference = "duplicate" } };
        var duplicateResult = RevitBeamMatcher.Match(duplicates, Data(targets[0], targets[0] with { Reference = "other" }));
        Check(duplicateResult.Pairs.Count == 2 && duplicateResult.Pairs.All(p => p.Ambiguous)
            && duplicateResult.Ambiguities.Count > 0, "Prototype duplicate assignments must be deterministic and visibly flagged.");
        Check(RevitBeamMatcher.Match(duplicates, duplicateResult.Revit).Links.SequenceEqual(duplicateResult.Links), "Tie handling must be repeatable.");
        var competing = RevitBeamMatcher.Match(new[] { sources[0] }, Data(targets[0],
            targets[0] with { Reference = "translated", Start = targets[0].Start + new BeamPoint(100, 0, 0), End = targets[0].End + new BeamPoint(100, 0, 0) }));
        Check(competing.HasCompetingTranslations, "Competing translation must be reported.");
        Check(RevitBeamMatcher.Match(sources, Data()).Translation == null, "Empty Revit input cannot establish translation.");

        var segments = new[] { Beam("split1", 0, 400), Beam("split2", 400, 1000), sources[1] };
        var full = RevitBeamMatcher.Match(segments, Data(targets[0], targets[1]));
        Check(full.Translation == offset && full.Pairs.Count == 1 && full.Links.Count(l => l.Pass == 2) == 2,
            "End-to-end matcher must retain the anchor vector and map split segments without reassigning the anchor.");
    }

    private static void TestPasses()
    {
        var target = Beam("R", 0, 1000);
        Check(Pass(Beam("E", 0, 1000, 10), target) == 2 && Pass(Beam("E", 0, 1000, 10.001), target) == 3,
            "Strict-axis 10 mm boundary must precede offset review.");
        Check(Pass(Beam("E", 0, 1000, 400, 100), target) == 3, "400 lateral / 100 vertical offset boundary failed.");
        Check(Pass(Beam("E", 0, 1000, 100, 100.001), target) == 4
            && Pass(Beam("E", 0, 1000, 100, 650), target) == 4
            && Pass(Beam("E", 0, 1000, 100, 650.001), target) == 0, "Elevation boundaries and offset precedence failed.");
        Check(Pass(Beam("E", 0, 1000, 500, 100), target) == 7
            && Pass(Beam("E", 0, 1000, 500.001, 100), target) == 0, "Wide lateral boundary failed.");
        MappingBeam Angled(double angle) => new("angle", new(0, 100, 0),
            new(1000 * Math.Cos(angle * Math.PI / 180), 100 + 1000 * Math.Sin(angle * Math.PI / 180), 0));
        Check(Pass(Angled(.25), target) == 3 && Pass(Angled(.25001), target) == 7
            && Pass(Angled(.5), target) == 7 && Pass(Angled(.50001), target) == 0, "Standard and wide angle boundaries failed.");
        Check(Pass(Beam("E", 0, 950, 100), target) == 3
            && Pass(Beam("E", 0, 949.99, 100), target) == 6, "95% standard overlap boundary failed.");
        Check(Pass(Beam("E", 0, 850, 100), target) == 6 && Pass(Beam("E", 0, 849.99, 100), target) == 0,
            "85% longer-span extension boundary failed.");
        Check(Pass(Beam("E", -20, 980, 100), target) == 3, "Standard overlap must not be relabelled as extension.");
        Check(Pass(Beam("E", -20, 930, 100), target) == 0, "End extension must contain at least 98% of the shorter span.");
        Check(Pass(Beam("E", -20, 980, 100), Beam("R", 0, 1080)) == 6
            && Pass(Beam("E", -20.001, 979.999, 100), Beam("R", 0, 1080)) == 0,
            "The 98% shorter-span end-extension boundary must be inclusive.");
        Check(Pass(Beam("E", 0, 3980, 100), Beam("long", 0, 4580)) == 6
            && Pass(Beam("E", 0, 3980, 100), Beam("long", 0, 4580.001)) == 0, "600 mm end difference boundary failed.");
        Check(Pass(Beam("E", 0, 980, 450), target) == 7
            && Pass(Beam("E", 0, 979.99, 450), target) == 0, "Wide review requires 98% longer-span overlap.");
        var split = Apply(new[] { Beam("A", 0, 400), Beam("B", 400, 1000) }, target with { Start = target.End, End = target.Start });
        Check(split.Links.Count == 2 && split.Links.All(l => l.Pass == 2 && l.Reversed && !l.Review)
            && split.Links.Select(l => l.GroupId).Distinct().Count() == 1
            && split.PassTotals[1].AddedRevit == 1 && split.PassTotals[1].AddedEtabs == 2,
            "Strict split group must repeat the target ID, retain reversal, and count distinct beams.");
        Check(Apply(new[] { Beam("A", -40, 360), Beam("B", 360, 1000) }, target).Links.Count == 2
            && Apply(new[] { Beam("A", -40, 359.999), Beam("B", 360, 1000) }, target).Links.Count == 0,
            "Each split segment needs at least 90% containment, even if the group covers the Revit span.");
        Check(Apply(new[] { Beam("A", 0, 500), Beam("B", 450, 1000) }, target).Links.Count == 2
            && Apply(new[] { Beam("A", 0, 500), Beam("B", 449.999, 1000) }, target).Links.Count == 0,
            "Strict-axis internal projected overlap must not exceed 50 mm.");
        var offsetSplit = Apply(new[] { Beam("A", 0, 400, 100), Beam("B", 400, 1000, 100) }, target);
        Check(offsetSplit.Links.Count == 2 && offsetSplit.Links.All(l => l.Pass == 5 && l.Review), "Offset split pass failed.");
        Check(Apply(new[] { Beam("A", 0, 400, 100), Beam("B", 400, 1000, 150) }, target).Links.Count == 2
            && Apply(new[] { Beam("A", 0, 400, 100), Beam("B", 400, 1000, 150.001) }, target).Links.Count == 0,
            "A zero projected gap must not bypass the 50 mm 3D internal join test.");
        Check(Apply(new[] { Beam("A", 0, 400, 100), Beam("B", 450, 1000, 100) }, target).Links.Count == 2
            && Apply(new[] { Beam("A", 0, 400, 100), Beam("B", 450.001, 1000, 100) }, target).Links.Count == 0,
            "50 mm internal gap and 95% group coverage boundaries failed.");
        Check(Apply(new[] { Beam("A", 0, 500, 100), Beam("B", 500, 950, 100) }, target).Links.Count == 2
            && Apply(new[] { Beam("A", 0, 500, 100), Beam("B", 500, 949.99, 100) }, target).Links.Count == 0,
            "95% combined Revit coverage boundary failed.");
        var extendedTarget = Beam("R", 0, 10000);
        var extended = Apply(new[] { Beam("A", -350, 5000, 100), Beam("B", 5000, 10350, 100) }, extendedTarget);
        Check(extended.Links.Count == 2 && extended.Links.All(l => l.Pass == 8)
            && Apply(new[] { Beam("A", -350.001, 5000, 100), Beam("B", 5000, 10350, 100) }, extendedTarget).Links.Count == 0,
            "350 mm extended split overhang boundary failed.");
        Check(Apply(new[] { Beam("A", -50, 5000, 100), Beam("B", 5000, 10050, 100) }, extendedTarget).Links.All(l => l.Pass == 5),
            "50 mm outer overhang must be assigned in offset split, not extended split.");
        var standardBlocked = Apply(new[] { Beam("E", 0, 1000) }, Beam("low1", 0, 1000, 20),
            Beam("low2", 0, 1000, 30), Beam("high", 0, 1000, 0, 200));
        Check(standardBlocked.Links.Count == 0 && standardBlocked.Ambiguities.Any(a => a.Pass == 3),
            "Elevation fallback must keep better unresolved low-offset candidates in its ranking.");
        var ambiguous = Apply(new[] { Beam("E", 0, 1000) }, Beam("R1", 0, 1000, 100), Beam("R2", 0, 1000, 124.999));
        Check(ambiguous.Links.Count == 0 && ambiguous.Ambiguities.Any(a => a.Pass == 3),
            "Runner-up margin below 25 mm must be rejected, not forced.");
        Check(Apply(new[] { Beam("E", 0, 1000) }, Beam("R1", 0, 1000, 100), Beam("R2", 0, 1000, 125)).Links.Single().Revit.Reference == "R1",
            "Exactly 25 mm runner-up margin must be accepted.");
        var reverseTie = Apply(new[] { Beam("E1", 0, 1000, 100), Beam("E2", 0, 1000, 120) }, target);
        Check(reverseTie.Links.Count == 0 && reverseTie.Ambiguities.Count > 0, "The runner-up margin must also hold target-to-source.");
        var strictCompeting = Apply(new[] { Beam("E", 0, 1000) }, target, target with { Reference = "duplicate" });
        Check(strictCompeting.Links.Count == 0 && strictCompeting.Ambiguities.Any(a => a.Pass == 2),
            "Strict-axis competition must not silently assign a target.");

        var chainSources = new[] { Beam("E1", 0, 4000), Beam("E2", 4000, 10000) };
        var chainTargets = new[] { Beam("R1", 0, 6000, 100), Beam("R2", 6000, 10000, 100) };
        var chains = Apply(chainSources, chainTargets);
        Check(chains.Links.Count == 3 && chains.Links.All(l => l.Pass == 9 && l.Review)
            && chains.Links.Select(l => l.GroupId).Distinct().Count() == 1
            && chains.Links.All(l => l.Measurements.Overlap > 1)
            && chains.MappedEtabsCount == 2 && chains.MappedRevitCount == 2, "Staggered chain joins must emit only overlapping segment links in one group.");
        var competingChains = Apply(chainSources, chainTargets.Concat(new[] { Beam("R3", 0, 6000, 200), Beam("R4", 6000, 10000, 200) }).ToArray());
        Check(competingChains.Links.Count == 0 && competingChains.Ambiguities.Any(a => a.Pass == 9 && a.Reason.Contains("pairing")),
            "Competing chain pairings must be rejected.");
        var chainBoundary = Apply(new[] { Beam("E1", 0, 4000), Beam("E2", 4050, 10000) }, chainTargets);
        Check(chainBoundary.Links.Count == 3 && chainBoundary.Links.All(l => l.Measurements.MaximumJoin == 50), "Chain join limit must include 50 mm.");
        Check(Apply(new[] { Beam("E1", 0, 4000), Beam("E2", 4050.001, 10000) }, chainTargets).Links.All(l => l.Pass != 9),
            "Chain joins above 50 mm must not connect.");
        var linkBoundarySources = new[] { Beam("E1", 0, 4000), Beam("E2", 4000, 7000), Beam("E3", 7000, 10000) };
        var tinyLink = Apply(linkBoundarySources, Beam("R1", 0, 2000, 100), Beam("R2", 2000, 3999, 100),
            Beam("R3", 3999, 9000, 100), Beam("R4", 9000, 10000, 100));
        Check(tinyLink.Links.All(l => l.Pass == 9) && tinyLink.Links.Count == 5
            && !tinyLink.Links.Any(l => l.Etabs.Reference == "E1" && l.Revit.Reference == "R3"),
            "A chain segment overlap of exactly 1 mm must not emit a link.");
        var overOne = Apply(linkBoundarySources, Beam("R1", 0, 2000, 100), Beam("R2", 2000, 3998.999, 100),
            Beam("R3", 3998.999, 9000, 100), Beam("R4", 9000, 10000, 100));
        Check(overOne.Links.Count == 6, "A chain segment overlap greater than 1 mm must emit a link.");
        var endpointSource = Beam("E-anchor", 0, 1000);
        var endpointTarget = Data(target).Beams[0];
        var locked = RevitMappingPasses.Apply(new(Data(target, Beam("near", 0, 1000, 20)),
            new[] { endpointSource, Beam("other-segment", 0, 400) }, Array.Empty<SkippedMappingBeam>(), default(BeamPoint),
            new[] { new BeamMappingPair(endpointSource, endpointTarget, false, 0, false) }, Array.Empty<TranslationScore>()));
        Check(locked.Links.Count == 1 && locked.Links[0].Pass == 1,
            "Endpoint assignments must block both source and target from later split and offset passes.");
        foreach (var group in chains.Links.GroupBy(l => l.Etabs)) Check(group.Select(l => l.Pass).Distinct().Count() == 1, "Sources must not be reassigned.");
        foreach (var group in split.Links.GroupBy(l => l.Revit)) Check(group.Select(l => l.Pass).Distinct().Count() == 1, "Targets must not be reassigned.");
    }

    private static void TestInputAndWorkbook(string directory, DemandReport report, SummaryTable summary)
    {
        string input = Path.Combine(directory, "revit-reader.xlsx");
        WriteFixture(input, "RC Beam Schedule", new[] { "ElementId", "Mark" }.Concat(RevitScheduleReader.CoordinateHeaders).ToArray(),
            new[] { ("987654", "RB-01", new[] { "0", "0", "0", "1000", "0", "0" }),
                ("", "", new[] { "2000", "0", "0", "2000", "1200", "0" }),
                ("invalid", "RB-X", new[] { "NaN", "0", "0", "0", "0", "0" }),
                ("zero", "RB-Z", new[] { "0", "0", "0", "0", "0", "0" }) });
        byte[] source = File.ReadAllBytes(input);
        var imported = RevitScheduleReader.Read(input);
        Check(imported.Beams.Count == 2 && imported.Skipped.Count == 2 && imported.Beams[0].Reference == "Element ID 987654"
            && imported.Beams[0].Mark == "RB-01" && imported.Beams[1].Reference == "Excel row 5"
            && imported.Skipped[0].Mark == "RB-X", "Reader must retain ID, Mark and physical row fallback and separately skip invalid rows.");
        string bad = Path.Combine(directory, "revit-invalid.xlsx");
        foreach (var spec in new[] { ("wrong", RevitScheduleReader.CoordinateHeaders), ("RC Beam Schedule", RevitScheduleReader.CoordinateHeaders.Take(5).ToArray()) })
        {
            WriteFixture(bad, spec.Item1, spec.Item2, Array.Empty<(string, string, string[])>());
            Invalid(() => RevitScheduleReader.Read(bad));
        }
        File.WriteAllText(bad, "not a workbook"); Invalid(() => RevitScheduleReader.Read(bad));
        var mapped = BeamScheduleSummary.WithRevitMapping(summary, imported);
        Check(mapped.Rows.Zip(summary.Rows).All(p => p.First.Take(summary.ColumnHeaders.Count).SequenceEqual(p.Second)),
            "All original reinforcement values and formula objects must be preserved.");
        var scoped = BeamScheduleSummary.WithRevitMapping(new(new[] { summary.Rows[0] }), imported);
        Check(scoped.Mapping!.Etabs.Count == 1, "Mapping must not bypass export scope.");
        string plainFile = Path.Combine(directory, "off.xlsx"), mappedFile = Path.Combine(directory, "on.xlsx");
        DemandScheduleWriter.Write(plainFile, report, summary); DemandScheduleWriter.Write(mappedFile, report, mapped);
        using (var plain = ZipFile.OpenRead(plainFile))
        using (var enabled = ZipFile.OpenRead(mappedFile))
        {
            XDocument Read(ZipArchive zip, string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
            Check(Read(plain, "xl/workbook.xml").Descendants(X + "sheet").Count() == 2
                && Read(enabled, "xl/workbook.xml").Descendants(X + "sheet").Count() == 3, "Optional worksheet must only exist with mapping.");
            var original = Read(plain, "xl/worksheets/sheet1.xml"); var appended = Read(enabled, "xl/worksheets/sheet1.xml");
            foreach (var cell in original.Descendants(X + "c"))
                Check(XNode.DeepEquals(cell, appended.Descendants(X + "c").Single(c => (string?)c.Attribute("r") == (string?)cell.Attribute("r"))),
                    "Mapping must not change existing Excel cells/formulas.");
            Check(XNode.DeepEquals(Read(plain, "xl/worksheets/sheet2.xml"), Read(enabled, "xl/worksheets/sheet2.xml")), "Station output changed.");
            var audit = Read(enabled, "xl/worksheets/sheet3.xml");
            var types = audit.Descendants(X + "row").Select(r => r.Elements(X + "c").FirstOrDefault()?.Value).ToArray();
            Check(types.Count(t => t == "Pass total") == 9 && types.Count(t => t == "Segment link") == 1
                && types.Count(t => t == "Skipped Revit") == 2 && types.Contains("Unmatched Revit"), "Audit must separate links, pass counts, unmatched and invalid rows.");
            Check(audit.Descendants(X + "t").Any(t => t.Value == "RB-01") && appended.Descendants(X + "t").Any(t => t.Value.Contains("Mark: RB-01")),
                "Summary and link audit must preserve Marks.");
            Check(appended.Descendants(X + "row").First().Descendants(X + "t").Select(t => t.Value)
                .SequenceEqual(summary.ColumnHeaders.Concat(BeamScheduleSummary.MappingHeaders)), "Mapping headers must be appended.");
        }
        string csvOff = Path.Combine(directory, "off.csv"), csvOn = Path.Combine(directory, "on.csv");
        DemandScheduleWriter.Write(csvOff, report, summary); DemandScheduleWriter.Write(csvOn, report, mapped);
        Check(File.ReadAllBytes(csvOff).SequenceEqual(File.ReadAllBytes(csvOn)), "CSV must remain unchanged.");
        Check(File.ReadAllBytes(input).SequenceEqual(source), "Source Revit workbook was modified.");
        var none = BeamScheduleSummary.WithRevitMapping(summary, Data(new MappingBeam("vertical", new(0, 0, 0), new(0, 0, 2500))));
        Check(none.Mapping!.Translation == null && none.Mapping.Alignment.Contains("Translation not established")
            && none.Mapping.Links.Count == 0, "No translation must be explicitly reported, not a valid empty match.");
        DemandScheduleWriter.Write(Path.Combine(directory, "no-translation.xlsx"), report, none);
        using (var zip = ZipFile.OpenRead(Path.Combine(directory, "no-translation.xlsx")))
        {
            using var stream = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
            var sheet = XDocument.Load(stream);
            foreach (string component in new[] { "dX (mm)", "dY (mm)", "dZ (mm)" })
            {
                var row = sheet.Descendants(X + "row").Single(r => r.Elements(X + "c").ElementAtOrDefault(1)?.Value == component);
                Check(row.Elements(X + "c").Count() == 2, "Unavailable translation components must be blank in Excel.");
            }
        }
        var sourceBeams = new[] { Beam("E1", 0, 4000), Beam("E2", 4000, 10000),
            new MappingBeam("ANCHOR", new(20000, 20000, 0), new(20000, 20000, 1234)),
            new MappingBeam("ANCHOR2", new(30000, 30000, 0), new(30000, 30000, 1567)) };
        var groupedRows = sourceBeams.Select(b =>
        {
            var row = (object?[])summary.Rows[0].Clone(); row[1] = b.Reference;
            string[] names = { "I X (mm)", "I Y (mm)", "I Z (mm)", "J X (mm)", "J Y (mm)", "J Z (mm)" };
            double[] values = { b.Start.X, b.Start.Y, b.Start.Z, b.End.X, b.End.Y, b.End.Z };
            for (int i = 0; i < names.Length; i++) row[Array.IndexOf(BeamScheduleSummary.OutputHeaders, names[i])] = values[i];
            return row;
        }).ToArray();
        var groupSummary = BeamScheduleSummary.WithRevitMapping(new(groupedRows),
            Data(Beam("R1", 0, 6000, 100), Beam("R2", 6000, 10000, 100),
                sourceBeams[2] with { Reference = "R-ANCHOR" }, sourceBeams[3] with { Reference = "R-ANCHOR2" }));
        Check(groupSummary.Mapping!.PassTotals[8].AddedLinks == 3 && groupSummary.Mapping.PassTotals[8].AddedGroups == 1
            && groupSummary.Mapping.MappedEtabsCount == 4 && groupSummary.Mapping.MappedRevitCount == 4,
            "Summary integration must preserve many-to-many chain groups and count distinct model lines.");
        string groupFile = Path.Combine(directory, "grouped.xlsx");
        DemandScheduleWriter.Write(groupFile, report, groupSummary);
        using (var zip = ZipFile.OpenRead(groupFile))
        {
            using var stream = zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();
            var sheet = XDocument.Load(stream);
            string? Cell(XElement row, int column) => row.Elements(X + "c")
                .SingleOrDefault(c => new string(((string)c.Attribute("r")!).TakeWhile(char.IsAsciiLetter).ToArray())
                    == DemandScheduleWriter.ColumnName(column))?.Value;
            var chainRows = sheet.Descendants(X + "row").Where(r => Cell(r, 0) == "Segment link" && Cell(r, 5) == "9").ToArray();
            Check(chainRows.Length == 3 && chainRows.Select(r => Cell(r, 6)).Distinct().Count() == 1
                && chainRows.Select(r => Cell(r, 2)).Distinct().Count() == 2
                && chainRows.All(r => Cell(r, 7) == "REVIEW" && double.Parse(Cell(r, 15)!, CultureInfo.InvariantCulture) > 1),
                "Chain link workbook rows must repeat references, use the same group ID and expose review/overlap measurements.");
            var finalTotal = sheet.Descendants(X + "row").Single(r => Cell(r, 0) == "Pass total" && Cell(r, 5) == "9");
            Check(Cell(finalTotal, 36) == "2" && Cell(finalTotal, 37) == "2" && Cell(finalTotal, 38) == "3"
                && Math.Abs(double.Parse(Cell(finalTotal, 42)!, CultureInfo.InvariantCulture) - 50) < 1e-10,
                "Excel must distinguish added beams from link rows and record coverage gain in percentage points.");
        }
    }

    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("Invalid workbook accepted."); } catch (InvalidDataException) { }
    }

    private static void WriteFixture(string path, string sheet, string[] headers, (string Id, string Mark, string[] Coordinates)[] data)
    {
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Part(string name, XElement root) { using var stream = zip.CreateEntry(name).Open(); new XDocument(root).Save(stream); }
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace p = "http://schemas.openxmlformats.org/package/2006/relationships";
        Part("xl/workbook.xml", new(X + "workbook", new XElement(X + "sheets",
            new XElement(X + "sheet", new XAttribute("name", sheet), new XAttribute(r + "id", "rId1")))));
        Part("xl/_rels/workbook.xml.rels", new(p + "Relationships", new XElement(p + "Relationship",
            new XAttribute("Id", "rId1"), new XAttribute("Target", "/xl/worksheets/sheet1.xml"))));
        XElement Row(int number, string[] values) => new(X + "row", new XAttribute("r", number),
            values.Select((v, i) => new XElement(X + "c", new XAttribute("r", DemandScheduleWriter.ColumnName(i) + number),
                new XAttribute("t", "inlineStr"), new XElement(X + "is", new XElement(X + "t", v)))));
        Part("xl/worksheets/sheet1.xml", new(X + "worksheet", new XElement(X + "sheetData",
            Row(3, headers), data.Select((d, i) => Row(i + 4, new[] { d.Id, d.Mark }.Concat(d.Coordinates).ToArray())))));
    }
}
