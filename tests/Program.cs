using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using BeamSchedulePlugin;
using CSiAPIv1;

const string Draft = "DRAFT - ENGINEER REVIEW REQUIRED";
var fractions = ZoneFractions.Create(.25, .5, .25);
var model = DispatchProxy.Create<cSapModel, ModelProxy>();
var frame = DispatchProxy.Create<cFrameObj, FrameProxy>();
((ModelProxy)(object)model).Frame = frame;
var properties = DispatchProxy.Create<cPropFrame, PropertyProxy>();
((ModelProxy)(object)model).Properties = properties;
var stories = DispatchProxy.Create<cStory, StoryProxy>();
((ModelProxy)(object)model).Stories = stories;
var frameProxy = (FrameProxy)(object)frame;

object?[] Station(string id, double? location, double top = 0, string status = Draft, double torsion = 0)
{
    var row = new object?[29];
    row[0] = status;
    row[6] = "Level 1";
    row[8] = id;
    row[10] = "RECT";
    for (int i = 12; i <= 17; i++) row[i] = 0d;
    row[15] = 1000d;
    row[18] = location;
    if (location != null)
    {
        row[19] = top; row[21] = top * 2; row[23] = top * 3;
        row[25] = torsion; row[27] = 0d;
    }
    return row;
}

SummaryTable Summarize(params object?[][] rows) =>
    BeamScheduleSummary.Create(model, new DemandReport(rows, rows.Select(r => r[8]).Distinct().Count(), rows.Count(r => r[18] != null)), fractions);

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

object? SummaryValue(object?[] row, string header) =>
    row[Array.IndexOf(BeamScheduleSummary.OutputHeaders, header)];

Check(BeamDemandExport.ReadStoryNames(model).SequenceEqual(new[] { "Level 1", "Level 2" }), "Story list incorrect.");
((StoryProxy)(object)stories).ReturnCode = 1;
try { BeamDemandExport.ReadStoryNames(model); throw new Exception("Story API errors must be reported."); }
catch (InvalidOperationException) { }
((StoryProxy)(object)stories).ReturnCode = 0;
var selected = new HashSet<string>(new[] { "A" }, StringComparer.OrdinalIgnoreCase);
Check(BeamDemandExport.MatchesScope("A", "Level 2", null, null), "All levels must preserve beams.");
Check(BeamDemandExport.MatchesScope("A", "LEVEL 2", selected, "Level 2"), "Story and scope should match together.");
Check(!BeamDemandExport.MatchesScope("A", "Level 1", selected, "Level 2"), "Other levels must be excluded.");
Check(!BeamDemandExport.MatchesScope("B", "Level 2", selected, "Level 2"), "Selected/group scope must not be bypassed.");
var when = new DateTime(2026, 11, 23, 17, 5, 6);
Check(BeamDemandExport.SuggestedFilename(when, BeamScope.All, null) == "261123_170506 ETABS beam schedule - All beams - All levels", "All-level filename incorrect.");
Check(BeamDemandExport.SuggestedFilename(when, BeamScope.Selected, "2/West").EndsWith("Selected beams - Level 2_West"), "Story filename must identify the scope and sanitize invalid characters.");

var stations = new[] { Station("A", 0, 1), Station("A", 300, 4), Station("A", 500, 3), Station("A", 700, 8), Station("A", 1000, 5), Station("B", null) };
var summary = Summarize(stations);
Check(summary.Rows.Count == 2, "Expected one summary row per beam.");
var a = summary.Rows[0];
Check(Equals(a[2], "Level 1"), "Story must appear in column C.");
Check((double)a[5]! == 4 && (double)a[6]! == 8 && (double)a[7]! == 8, "Boundary stations must count in both adjacent zones.");
Check((double)a[8]! == 8 && (double)a[11]! == 12, "Bottom and shear demands incorrect.");
Check(a[32] is SpreadsheetFormula f && f.Expression.Contains("F2/X2"), "Ratio formula references incorrect.");
Check(summary.Rows[1][5] == null && summary.Rows[1][32] == null, "Missing results must remain blank.");

var incomplete = Summarize(Station("A", 0, 1), Station("A", 1000, 2)).Rows[0];
Check(incomplete[6] == null && incomplete[32] == null && incomplete[41]!.ToString()!.Contains("INCOMPLETE"), "Missing zone must be flagged and ratios suppressed.");
foreach (string status in new[] { "DESIGN ERROR - DO NOT USE", "WARNING - ENGINEER REVIEW REQUIRED" })
{
    var flagged = Summarize(Station("A", 0, 1, status), Station("A", 500, 1, status), Station("A", 1000, 1, status)).Rows[0];
    Check(flagged[5] is double && flagged[32] == null, "Flagged result must not produce ratios.");
}
Check(BeamDemandExport.ReviewStatus("", "", false, true) == BeamDemandExport.TorsionOnlyStatus
    && BeamDemandExport.ReviewStatus("", "ETABS warning", false, true) != BeamDemandExport.TorsionOnlyStatus
    && BeamDemandExport.ReviewStatus("ETABS error", "", false, true) != BeamDemandExport.TorsionOnlyStatus
    && BeamDemandExport.ReviewStatus("", "", true, true) != BeamDemandExport.TorsionOnlyStatus,
    "Only torsion without other issues should be eligible for proposals.");
var torsionStations = new[] { Station("A", 0, 1, BeamDemandExport.TorsionOnlyStatus, 5),
    Station("A", 500, 1, BeamDemandExport.TorsionOnlyStatus),
    Station("A", 1000, 1, BeamDemandExport.TorsionOnlyStatus) };
foreach (var station in torsionStations) station[2] = BeamDemandExport.TorsionWarning;
var torsion = Summarize(torsionStations).Rows[0];
Check((double)SummaryValue(torsion, "Max longitudinal torsion area (mm2)")! == 5
    && torsion[32] is SpreadsheetFormula && Equals(SummaryValue(torsion, "ETABS design warnings"), BeamDemandExport.TorsionWarning)
    && Equals(SummaryValue(torsion, "Review status"), BeamDemandExport.TorsionOnlyStatus),
    "Torsion-only ratios must be allowed with an explicit warning and separate demand.");
var negative = Summarize(Station("A", 0, -1)).Rows[0];
Check(negative[5] == null && negative[41]!.ToString()!.Contains("INCOMPLETE"), "Negative demands must be flagged.");
var outside = Summarize(Station("A", 1001, 1)).Rows[0];
Check(outside[5] == null && SummaryValue(outside, "Zone sampling note")!.ToString()!.Contains("outside"), "Out-of-range stations must be flagged.");
var noGeometry = Station("A", 0, 1);
noGeometry[12] = null;
Check(Summarize(noGeometry).Rows[0][5] == null, "Missing geometry must suppress demands.");
frameProxy.OffsetJ = 1000;
Check(Summarize(Station("A", 0, 1)).Rows[0][5] == null, "Invalid offsets must suppress demands.");
frameProxy.OffsetJ = 100;
frameProxy.ReturnCode = 1;
Check(Summarize(Station("A", 0, 1)).Rows[0][5] == null, "Failed offset read must suppress demands.");
frameProxy.ReturnCode = 0;
try { ZoneFractions.Create(.25, .5, .3); throw new Exception("Invalid fractions accepted."); }
catch (InvalidOperationException) { }

var settings = new ReinforcementOptions(35, new[] { 25 }, 3, true, 25, 25, new[] { 10 }, new[] { 5 }, 300, 300, 50);
var proposal = ReinforcementSelection.Select(3911, 3911, 960, 1000, 600, settings)!;
Check(proposal.Top.Description == "8H25" && Math.Abs(proposal.Top.Area - 3926.9908) < .001, "Longitudinal area example incorrect.");
Check(proposal.Link.Description == "H10-300 (Link) + 3H10-300 (Hooks)" && Math.Abs(proposal.Link.Area - 1308.9969) < .001, "Link area example incorrect.");
Check(ReinforcementSelection.Select(3911, 3911, 960, 100, 600, settings) == null, "Bars must fit beam width.");
Check(ReinforcementSelection.Select(3911, 3911, 960, 1000, 100, settings) == null, "Top and bottom bars must fit beam height.");
Check(ReinforcementSelection.Select(3911, 3911, 100000, 1000, 600, settings) == null, "Unsatisfied shear must not produce a proposal.");
var layeredSettings = settings with { BarDiameters = new[] { 16, 25 }, LinkLegs = new[] { 2 } };
var layered = ReinforcementSelection.Select(3500, 3500, 500, 400, 600, layeredSettings);
Check(layered != null && layered.Top.Description == "6H25+4H16", "Single layers should be exhausted before adding a smaller-diameter layer.");
var automatic = BeamScheduleSummary.Create(model, new DemandReport(stations, 2, 5), fractions, settings).Rows[0];
Check(automatic[14] is string && automatic[23] is SpreadsheetFormula guard && guard.Expression.Contains("DETAIL CHANGED") && guard.Expression.Contains("O2"), "Automatic areas must be guarded against changed descriptions.");
Check(automatic[32] is SpreadsheetFormula, "Automatic ratio is missing.");
var torsionAutomatic = BeamScheduleSummary.Create(model, new DemandReport(torsionStations, 1, 3), fractions, settings).Rows[0];
Check(torsionAutomatic[14] is string && torsionAutomatic[23] is SpreadsheetFormula && torsionAutomatic[32] is SpreadsheetFormula,
    "Torsion-only beams must receive flexure/shear proposals and ratios.");
var torsionAndWarning = torsionStations.Select(station => (object?[])station.Clone()).ToArray();
foreach (var station in torsionAndWarning) { station[0] = BeamDemandExport.ReviewStatus("", "ETABS warning", false, true); station[2] = "ETABS warning; " + BeamDemandExport.TorsionWarning; }
var warnedAutomatic = BeamScheduleSummary.Create(model, new DemandReport(torsionAndWarning, 1, 3), fractions, settings).Rows[0];
Check(warnedAutomatic[14] == null && warnedAutomatic[32] == null, "ETABS warnings with torsion must still suppress proposals and ratios.");
var incompleteTorsion = BeamScheduleSummary.Create(model, new DemandReport(new[] { torsionStations[0], torsionStations[2] }, 1, 2), fractions, settings).Rows[0];
Check(incompleteTorsion[14] == null && incompleteTorsion[32] == null, "Missing zones with torsion must still suppress proposals.");

string directory = Path.Combine(Path.GetTempPath(), "beam-summary-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string workbook = Path.Combine(directory, "test.xlsx");
    var workbookStations = stations.Select(station => (object?[])station.Clone()).ToArray();
    workbookStations[0][18] = 0.125;
    var report = new DemandReport(workbookStations, 2, 5);
    DemandScheduleWriter.Write(workbook, report, summary);
    using var zip = ZipFile.OpenRead(workbook);
    XDocument Read(string name) { using var stream = zip.GetEntry(name)!.Open(); return XDocument.Load(stream); }
    XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var book = Read("xl/workbook.xml");
    Check(book.Descendants(x + "sheet").Select(s => (string?)s.Attribute("name")).SequenceEqual(new[] { "Beam summary" })
        && zip.GetEntry("xl/worksheets/sheet2.xml") == null, "Excel must contain only the beam summary when mapping is disabled.");
    Check((string?)book.Descendants(x + "calcPr").Single().Attribute("fullCalcOnLoad") == "1", "Workbook must recalculate on open.");
    var sheet = Read("xl/worksheets/sheet1.xml");
    string[] summaryHeaders = sheet.Descendants(x + "row").First().Descendants(x + "t").Select(t => t.Value).ToArray();
    Check(summaryHeaders.Length == 63 && summaryHeaders.Count(h => h == "Beam label") == 1
        && summaryHeaders.Count(h => h == "Unique ID") == 1, "Summary must contain one label and ID column.");
    Check(summaryHeaders[2] == "Story" && summaryHeaders.Count(h => h == "Story") == 1, "Story must appear once in column C.");
    var filter = sheet.Root!.Element(x + "autoFilter")!;
    Check((string?)filter.Attribute("ref") == "A1:BK3", "Filter must cover the whole summary table.");
    var hiddenFilters = filter.Elements(x + "filterColumn")
        .Where(c => (string?)c.Attribute("hiddenButton") == "1")
        .Select(c => int.Parse((string)c.Attribute("colId")!)).ToHashSet();
    var filterNames = new[] { "Story", "Review status", "ETABS design errors", "ETABS design warnings",
        "Geometry warning", "Assigned section", "Design section" };
    Check(summaryHeaders.Where((header, index) => !hiddenFilters.Contains(index)).SequenceEqual(
        summaryHeaders.Where(header => filterNames.Contains(header)))
        && filterNames.All(summaryHeaders.Contains), "Only the requested columns should show filter dropdowns.");
    Check(!summaryHeaders.Intersect(new[] { "Model path", "Design code", "Exported local time" }).Any(), "Summary must omit requested metadata.");
    Check(!summaryHeaders.Intersect(new[] { "I zone boundary from I (mm)", "J zone boundary from I (mm)" }).Any(), "Summary must omit zone boundary columns.");
    Check(summaryHeaders.Contains("I top area ratio") && !summaryHeaders.Any(h => h.Contains("not a design check")), "Ratio headers should not include the disclaimer.");
    var headerCells = sheet.Descendants(x + "row").First().Elements(x + "c").ToArray();
    string[] headerStyles = headerCells.Select(c => (string)c.Attribute("s")!).ToArray();
    string topStyle = headerStyles[5], bottomStyle = headerStyles[8], shearStyle = headerStyles[11];
    Check(new[] { topStyle, bottomStyle, shearStyle }.Distinct().Count() == 3, "Top, bottom and shear header colours must differ.");
    for (int group = 0; group < 4; group++)
        for (int i = 0; i < 9; i++)
            Check(headerStyles[5 + group * 9 + i] == new[] { topStyle, bottomStyle, shearStyle }[i / 3], "Header colour must follow the reinforcement type.");
    var styles = Read("xl/styles.xml");
    var fills = styles.Root!.Element(x + "fills")!.Elements(x + "fill").ToArray();
    var xfs = styles.Root.Element(x + "cellXfs")!.Elements(x + "xf").ToArray();
    string FillColor(string style) => (string)fills[int.Parse((string)xfs[int.Parse(style)].Attribute("fillId")!)]
        .Element(x + "patternFill")!.Element(x + "fgColor")!.Attribute("rgb")!;
    Check(new[] { FillColor(topStyle), FillColor(bottomStyle), FillColor(shearStyle) }.Distinct().Count() == 3, "Header styles must have distinct fill colours.");
    Check(sheet.Descendants(x + "row").Count() == 3, "Summary row count incorrect.");
    Check(sheet.Descendants(x + "c").Single(c => (string?)c.Attribute("r") == "C2").Value == "Level 1", "Workbook level cell incorrect.");
    XElement Cell(XDocument worksheet, string address) =>
        worksheet.Descendants(x + "c").Single(c => (string?)c.Attribute("r") == address);
    string NumberFormat(XElement cell) =>
        (string)xfs[(int?)cell.Attribute("s") ?? 0].Attribute("numFmtId")!;
    var ratio = Cell(sheet, "AG2");
    Check(ratio.Element(x + "f")!.Value.Contains("F2/X2"), "Workbook formula references incorrect.");
    Check(NumberFormat(Cell(sheet, "F2")) == "1"
        && summaryHeaders.Select((header, index) => (header, index))
            .Where(column => column.header.EndsWith(" area ratio", StringComparison.Ordinal))
            .All(column => NumberFormat(Cell(sheet, DemandScheduleWriter.ColumnName(column.index) + "2")) == "2"),
        "Excel should display whole-number reinforcement areas and all area ratios to two decimal places.");
    Check(!sheet.Descendants(x + "c").Any(c => (string?)c.Attribute("r") == "X2"), "Manual input must initially be empty.");
    workbookStations[0][7] = "=unsafe";
    string csv = Path.Combine(directory, "test.csv");
    DemandScheduleWriter.Write(csv, report, null);
    Check(File.ReadAllText(csv).Contains("\"'=unsafe\""), "CSV formula injection not escaped.");
    Check(!File.ReadLines(csv).First().Contains("\"Model path\""), "CSV must omit requested metadata.");
    Check(File.ReadAllLines(csv).Length == 7 && File.ReadLines(csv).First().Contains("\"Station from I (mm)\"")
        && File.ReadAllText(csv).Contains("\"0.125\""), "CSV must retain station rows and their full precision.");
    RevitMappingTests.Run(directory, report, summary);
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
Console.WriteLine("Beam summary and workbook tests passed.");

if (args.Length == 4 && args[0] == "--mapping-workbooks")
    WorkbookMappingVerification.Run(args[1], args[2], args[3]);

if (args.Length == 2 && args[0] == "--fixture")
{
    var report = new DemandReport(stations, 2, 5);
    DemandScheduleWriter.Write(args[1], report, BeamScheduleSummary.Create(model, report, fractions, settings), overwrite: false);
}

if (args.Length == 2 && args[0] == "--fixture-torsion")
{
    var report = new DemandReport(torsionStations, 1, 3);
    DemandScheduleWriter.Write(args[1], report, BeamScheduleSummary.Create(model, report, fractions, settings), overwrite: false);
}

if (args.Length > 0 && args[0] == "--live")
{
    if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Supply an output path and optional story name for the live workbook.");
    cOAPI etabs = ((cHelper)new Helper()).GetObject("CSI.ETABS.API.ETABSObject")
        ?? throw new InvalidOperationException("No running ETABS instance is registered with the API.");
    cSapModel liveModel = etabs.SapModel;
    string? liveStory = args.Length == 3 ? args[2] : null;
    if (liveStory == "--verify-story")
    {
        DemandReport all = BeamDemandExport.Read(liveModel, BeamScope.All, Array.Empty<string>());
        liveStory = (string)all.Rows.First(r => r[6] is string value && value.Length > 0)[6]!;
        Check(BeamDemandExport.ReadStoryNames(liveModel).Contains(liveStory, StringComparer.OrdinalIgnoreCase), "Beam story is absent from story selector.");
        DemandReport filtered = BeamDemandExport.Read(liveModel, BeamScope.All, Array.Empty<string>(), liveStory);
        var expected = all.Rows.Where(r => string.Equals((string?)r[6], liveStory, StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(filtered.Rows.Select(r => (string)r[8]!).SequenceEqual(expected.Select(r => (string)r[8]!)), "Live level filter differs from unfiltered model results.");
        Console.WriteLine($"Verified level {liveStory}: {filtered.BeamCount} beams, {filtered.StationCount} stations.");
    }
    DemandReport liveReport = BeamDemandExport.Read(liveModel, BeamScope.All, Array.Empty<string>(), liveStory);
    if (liveStory != null)
        Check(liveReport.Rows.All(r => string.Equals((string?)r[6], liveStory, StringComparison.OrdinalIgnoreCase)), "Live export contains another story.");
    var liveOptions = new ReinforcementOptions(35, new[] { 16, 20, 25, 32, 40 }, 3, true, 25, 25,
        new[] { 10, 12, 16, 20 }, new[] { 2, 3, 4, 5 }, 100, 300, 50);
    SummaryTable liveSummary = BeamScheduleSummary.Create(liveModel, liveReport, fractions, liveOptions);
    Check(liveSummary.Rows.Count == liveReport.BeamCount, "Live summary does not contain one row per beam.");
    var byId = liveReport.Rows.GroupBy(r => (string)r[8]!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    foreach (var beam in liveSummary.Rows.Where(r => SummaryValue(r, "I end offset (mm)") is double
        && SummaryValue(r, "J end offset (mm)") is double && r[5] is double))
    {
        var beamStations = byId[(string)beam[1]!];
        double length = (double)SummaryValue(beam, "I-J length (mm)")!;
        double offsetI = (double)SummaryValue(beam, "I end offset (mm)")!;
        double offsetJ = (double)SummaryValue(beam, "J end offset (mm)")!;
        double clear = length - offsetI - offsetJ;
        double boundaryI = offsetI + clear * fractions.I;
        double boundaryJ = offsetI + clear * (fractions.I + fractions.Middle);
        double tolerance = Math.Max(1e-7, length * 1e-9);
        Check(Math.Abs((double)beam[5]! - beamStations.Where(r => (double)r[18]! <= boundaryI + tolerance).Max(r => (double)r[19]!)) < 1e-6, $"I-zone maximum incorrect for {beam[1]}.");
        if (beam[7] is double jTop)
            Check(Math.Abs(jTop - beamStations.Where(r => (double)r[18]! >= boundaryJ - tolerance).Max(r => (double)r[19]!)) < 1e-6, $"J-zone maximum incorrect for {beam[1]}.");
    }
    DemandScheduleWriter.Write(args[1], liveReport, liveSummary, overwrite: false);
    Console.WriteLine($"Live export: {liveReport.BeamCount} beams and {liveReport.StationCount} stations; model {liveReport.Rows[0][3]}.");
    Console.WriteLine($"Beams with at least one proposal: {liveSummary.Rows.Count(r => r.Skip(14).Take(9).Any(v => v is string))}.");
    foreach (var beam in liveSummary.Rows.Where(r => SummaryValue(r, "I end offset (mm)") is double i
        && SummaryValue(r, "J end offset (mm)") is double j && (i > 0 || j > 0)).Take(1))
        Console.WriteLine($"Example beam {beam[1]}: offsets {SummaryValue(beam, "I end offset (mm)")}/{SummaryValue(beam, "J end offset (mm)")} mm.");
}

public class ModelProxy : DispatchProxy
{
    public cFrameObj Frame { get; set; } = null!;
    public cPropFrame Properties { get; set; } = null!;
    public cStory Stories { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "get_FrameObj") return Frame;
        if (method.Name == "get_PropFrame") return Properties;
        if (method.Name == "get_Story") return Stories;
        if (method.Name == "GetPresentUnits_2") { args![1] = eLength.mm; return 0; }
        throw new NotSupportedException(method.Name);
    }
}

public class StoryProxy : DispatchProxy
{
    public int ReturnCode { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetNameList")
        {
            args![0] = 2;
            args[1] = new[] { "Level 1", "Level 2" };
            return ReturnCode;
        }
        throw new NotSupportedException(method.Name);
    }
}

public class PropertyProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetTypeOAPI") { args![1] = eFramePropType.Rectangular; return 0; }
        if (method.Name == "GetRectangle") { args![3] = 600d; args[4] = 1000d; return 0; }
        throw new NotSupportedException(method.Name);
    }
}

public class FrameProxy : DispatchProxy
{
    public double OffsetJ { get; set; } = 100;
    public int ReturnCode { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetEndLengthOffset")
        {
            args![2] = 100d;
            args[3] = OffsetJ;
            return ReturnCode;
        }
        throw new NotSupportedException(method.Name);
    }
}
