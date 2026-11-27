using CSiAPIv1;
using System.Globalization;

namespace BeamSchedulePlugin;

internal sealed record ZoneFractions(double I, double Middle, double J)
{
    internal static ZoneFractions Create(double i, double middle, double j)
    {
        if (!double.IsFinite(i) || !double.IsFinite(middle) || !double.IsFinite(j)
            || i <= 0 || middle <= 0 || j <= 0 || Math.Abs(i + middle + j - 1) > 1e-9)
            throw new InvalidOperationException("Zone fractions must be positive and sum to 1.");
        return new ZoneFractions(i, middle, j);
    }
}

internal sealed record SpreadsheetFormula(string Expression, string? CachedText = null);
internal sealed record SummaryTable(IReadOnlyList<object?[]> Rows, RevitMappingResult? Mapping = null)
{
    internal IReadOnlyList<string> ColumnHeaders => Mapping == null ? BeamScheduleSummary.OutputHeaders
        : BeamScheduleSummary.OutputHeaders.Concat(BeamScheduleSummary.MappingHeaders).ToArray();
}

internal static class BeamScheduleSummary
{
    private static readonly string[] DemandNames =
    {
        "I top", "Middle top", "J top", "I bottom", "Middle bottom", "J bottom",
        "I shear", "Middle shear", "J shear"
    };

    internal static readonly string[] Headers = CreateHeaders();
    private static readonly int[] SummaryMetadataIndices = Enumerable.Range(0, 29)
        .Where(index => index != 4 && index != 5 && index != 6 && index != 7 && index != 8 && index != 9
            && index != 24 && index != 25).ToArray();
    internal static readonly string[] OutputHeaders = CreateOutputHeaders();
    internal static readonly string[] MappingHeaders =
        { "Revit mapping status", "Linked Revit references", "Mapping group ID", "Mapping review flag",
            "Linked Revit Marks", "Mapping pass", "Max endpoint error (mm)" };
    private const int DemandStart = 29;

    internal static SummaryTable WithRevitMapping(SummaryTable summary, RevitScheduleData revit, RevitMappingOptions? options = null,
        RevitScheduleData? fullModel = null)
    {
        if (summary.Mapping != null) throw new InvalidOperationException("Revit mapping has already been applied.");
        int[] coordinates = new[] { "I X (mm)", "I Y (mm)", "I Z (mm)", "J X (mm)", "J Y (mm)", "J Z (mm)" }
            .Select(header => Array.IndexOf(OutputHeaders, header)).ToArray();
        var beams = new List<MappingBeam>();
        var skipped = new List<SkippedMappingBeam>();
        foreach (var row in summary.Rows)
        {
            string reference = (string)row[1]!;
            string? label = row[0] as string;
            var xyz = coordinates.Select(i => row[i]).ToArray();
            if (xyz.All(v => v is double d && double.IsFinite(d)))
            {
                var beam = new MappingBeam(reference, new((double)xyz[0]!, (double)xyz[1]!, (double)xyz[2]!),
                    new((double)xyz[3]!, (double)xyz[4]!, (double)xyz[5]!), EtabsLabel: string.IsNullOrWhiteSpace(label) ? null : label,
                    Level: row[2] as string);
                if (beam.IsValid) { beams.Add(beam); continue; }
            }
            skipped.Add(new(reference, null, "Missing, invalid or coincident ETABS endpoints", EtabsLabel: label));
        }
        var mapping = RevitBeamMatcher.Match(fullModel?.Beams ?? beams, revit, fullModel?.Skipped ?? skipped, options)
            with { EtabsFilename = fullModel?.Filename };
        var bySource = mapping.Links.GroupBy(p => p.Etabs.Reference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var invalid = skipped.Select(b => b.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = summary.Rows.Select(row =>
        {
            string reference = (string)row[1]!;
            var linked = bySource.GetValueOrDefault(reference) ?? Array.Empty<MappingLink>();
            bool ambiguous = mapping.Ambiguities.Any(p => string.Equals(p.Etabs?.Reference, reference, StringComparison.OrdinalIgnoreCase));
            string status = linked.Length > 0 ? string.Join("; ", linked.Select(p => p.Status).Distinct())
                : invalid.Contains(reference) ? "Invalid ETABS geometry - skipped"
                : mapping.Translation == null ? mapping.Alignment : ambiguous ? "Unmatched - ambiguous candidates" : "Unmatched";
            bool review = mapping.HasCompetingTranslations || ambiguous || linked.Any(p => p.Review);
            object?[] extra = { status, linked.Length == 0 ? null : string.Join("\n", linked.Select(p => p.Revit.Reference).Distinct()),
                linked.Length == 0 ? null : string.Join("\n", linked.Select(p => p.GroupId).Distinct()),
                review ? "REVIEW" : linked.Length > 0 ? "Strict geometric match - engineering review still required" : null,
                linked.Length == 0 ? null : string.Join("\n", linked.Select(p => $"{p.Revit.Reference} | Mark: {p.Revit.Mark ?? "(not provided)"}")),
                linked.Length == 0 ? null : string.Join(", ", linked.Select(p => p.PassName).Distinct()),
                linked.Select(p => p.Measurements.EndpointError).FirstOrDefault(v => v != null) };
            return row.Concat(extra).ToArray();
        }).ToArray();
        return new(rows, mapping);
    }

    internal static SummaryTable Create(cSapModel model, DemandReport report, ZoneFractions fractions,
        ReinforcementOptions? reinforcement = null)
    {
        eForce force = default;
        eLength lengthUnit = default;
        eTemperature temperature = default;
        int rc = model.GetPresentUnits_2(ref force, ref lengthUnit, ref temperature);
        if (rc != 0) throw new InvalidOperationException($"Could not read ETABS units (code {rc}).");
        double mm = BeamDemandExport.MillimetresPerUnit(lengthUnit);

        var result = new List<object?[]>(report.BeamCount);
        foreach (var group in report.Rows.GroupBy(row => (string)row[8]!, StringComparer.OrdinalIgnoreCase))
        {
            object?[] first = group.First();
            var row = new object?[Headers.Length];
            row[0] = first[0]; row[1] = first[1]; row[2] = first[2]; row[3] = first[11];
            row[4] = first[3]; row[5] = first[4]; row[6] = first[5]; row[7] = first[6];
            row[8] = first[7]; row[9] = first[8]; row[10] = first[9]; row[11] = first[10];
            Array.Copy(first, 12, row, 12, 6);
            row[21] = fractions.I; row[22] = fractions.Middle; row[23] = fractions.J;
            if (first[10] is string section && section.Length > 0)
            {
                eFramePropType type = default;
                rc = model.PropFrame.GetTypeOAPI(section, ref type);
                string filename = "", material = "", notes = "", guid = "";
                int color = 0;
                double height = 0, width = 0;
                if (rc == 0 && type == eFramePropType.Rectangular)
                    rc = model.PropFrame.GetRectangle(section, ref filename, ref material, ref height, ref width,
                        ref color, ref notes, ref guid);
                if (rc == 0 && type == eFramePropType.Rectangular
                    && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
                {
                    row[65] = width * mm;
                    row[66] = height * mm;
                }
            }

            var stations = group.Where(item => item[18] is double).ToArray();
            if (stations.Length == 0)
            {
                row[28] = "No design stations; demands and ratios unavailable.";
                result.Add(row);
                continue;
            }
            if (stations.Any(item => (double)item[19]! < 0 || (double)item[21]! < 0
                || (double)item[23]! < 0 || (double)item[25]! < 0 || (double)item[27]! < 0))
            {
                Flag(row, "Negative design demand returned by ETABS; zone demands are not calculated.");
                result.Add(row);
                continue;
            }

            if (stations.Any(item => (double)item[25]! != 0 || (double)item[27]! != 0))
            {
                row[26] = stations.Max(item => (double)item[25]!);
                row[27] = stations.Max(item => (double)item[27]!);
            }
            else
            {
                row[26] = 0d; row[27] = 0d;
            }

            if (Enumerable.Range(12, 6).Any(index => first[index] is not double))
            {
                Flag(row, "Coordinates unavailable; zone demands and ratios are not calculated.");
                result.Add(row);
                continue;
            }
            double dx = (double)first[15]! - (double)first[12]!;
            double dy = (double)first[16]! - (double)first[13]!;
            double dz = (double)first[17]! - (double)first[14]!;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            bool auto = false;
            double offsetI = 0, offsetJ = 0, rigid = 0;
            rc = model.FrameObj.GetEndLengthOffset(group.Key, ref auto, ref offsetI, ref offsetJ, ref rigid);
            offsetI *= mm;
            offsetJ *= mm;
            row[18] = length;
            if (rc != 0 || !double.IsFinite(length) || !double.IsFinite(offsetI) || !double.IsFinite(offsetJ)
                || offsetI < 0 || offsetJ < 0 || length <= offsetI + offsetJ)
            {
                Flag(row, $"Invalid or unavailable end offsets (ETABS code {rc}); zone demands are not calculated.");
                result.Add(row);
                continue;
            }
            row[19] = offsetI;
            row[20] = offsetJ;
            double clear = length - offsetI - offsetJ;
            double boundaryI = offsetI + clear * fractions.I;
            double boundaryJ = offsetI + clear * (fractions.I + fractions.Middle);
            row[24] = boundaryI;
            row[25] = boundaryJ;
            double tolerance = Math.Max(1e-7, length * 1e-9);
            if (stations.Any(item => (double)item[18]! < -tolerance || (double)item[18]! > length + tolerance))
            {
                Flag(row, "A design station lies outside the I-J length; zone demands are not calculated.");
                result.Add(row);
                continue;
            }

            object?[][][] zones =
            {
                stations.Where(item => (double)item[18]! <= boundaryI + tolerance).ToArray(),
                stations.Where(item => (double)item[18]! >= boundaryI - tolerance && (double)item[18]! <= boundaryJ + tolerance).ToArray(),
                stations.Where(item => (double)item[18]! >= boundaryJ - tolerance).ToArray()
            };
            bool complete = zones.All(zone => zone.Length > 0);
            bool eligible = Equals(first[0], "DRAFT - ENGINEER REVIEW REQUIRED")
                || Equals(first[0], BeamDemandExport.TorsionOnlyStatus);
            if (complete)
                row[28] = "Station maxima only; no interpolation or detailing checks.";
            else
                Flag(row, "At least one zone has no design stations; do not infer missing demands.");
            int rowNumber = result.Count + 2;
            for (int kind = 0; kind < 3; kind++)
            {
                int source = kind switch { 0 => 19, 1 => 21, _ => 23 };
                for (int zone = 0; zone < 3; zone++)
                {
                    if (zones[zone].Length == 0) continue;
                    int column = DemandStart + (kind * 3 + zone) * 4;
                    row[column] = zones[zone].Max(item => (double)item[source]!);
                    if (complete && eligible)
                    {
                        string required = DemandScheduleWriter.ColumnName(column) + rowNumber;
                        string provided = DemandScheduleWriter.ColumnName(column + 2) + rowNumber;
                        row[column + 3] = new SpreadsheetFormula(
                            $"IFERROR(IF(AND(ISNUMBER({required}),{required}>=0,ISNUMBER({provided}),{provided}>0),{required}/{provided},\"\"),\"\")");
                    }
                }
            }
            if (reinforcement != null && complete && eligible)
            {
                if (row[65] is not double width || row[66] is not double height)
                {
                    Flag(row, "Rectangular design-section dimensions unavailable; reinforcement proposals not generated.");
                    for (int i = 0; i < 9; i++) row[DemandStart + i * 4 + 3] = null;
                }
                else
                {
                    var failedZones = new List<string>();
                    for (int zone = 0; zone < 3; zone++)
                    {
                        int top = DemandStart + zone * 4;
                        int bottom = DemandStart + (3 + zone) * 4;
                        int shear = DemandStart + (6 + zone) * 4;
                        ZoneProposal? proposal = ReinforcementSelection.Select(
                            (double)row[top]!, (double)row[bottom]!, (double)row[shear]!, width, height, reinforcement);
                        if (proposal == null)
                        {
                            failedZones.Add(new[] { "I", "Middle", "J" }[zone]);
                            row[top + 3] = row[bottom + 3] = row[shear + 3] = null;
                            continue;
                        }
                        row[top + 1] = proposal.Top.Description; row[top + 2] = proposal.Top.Area;
                        row[bottom + 1] = proposal.Bottom.Description; row[bottom + 2] = proposal.Bottom.Area;
                        row[shear + 1] = proposal.Link.Description; row[shear + 2] = proposal.Link.Area;
                    }
                    if (failedZones.Count > 0)
                        Flag(row, $"No proposal found within the selected limits in zone(s): {string.Join(", ", failedZones)}.");
                }
            }
            result.Add(row);
        }
        return new SummaryTable(result.Select((row, index) => Reorder(row, index + 2, reinforcement)).ToList());
    }

    private static object?[] Reorder(object?[] row, int rowNumber, ReinforcementOptions? options)
    {
        var output = new object?[OutputHeaders.Length];
        output[0] = row[8]; output[1] = row[9]; output[2] = row[7]; output[3] = row[65]; output[4] = row[66];
        for (int i = 0; i < 9; i++)
        {
            int source = DemandStart + i * 4;
            output[5 + i] = row[source];
            output[14 + i] = row[source + 1];
            output[23 + i] = row[source + 2];
            if (options != null && row[source + 1] is string description && row[source + 2] is double area)
            {
                string detail = DemandScheduleWriter.ColumnName(14 + i) + rowNumber;
                output[23 + i] = new SpreadsheetFormula(
                    $"IF({detail}=\"{description.Replace("\"", "\"\"")}\",{area.ToString("R", CultureInfo.InvariantCulture)},\"DETAIL CHANGED - RE-EXPORT\")");
            }
            if (row[source + 3] is SpreadsheetFormula)
            {
                string required = DemandScheduleWriter.ColumnName(5 + i) + rowNumber;
                string provided = DemandScheduleWriter.ColumnName(23 + i) + rowNumber;
                output[32 + i] = new SpreadsheetFormula(
                    $"IFERROR(IF(AND(ISNUMBER({required}),{required}>=0,ISNUMBER({provided}),{provided}>0),{required}/{provided},\"\"),\"\")");
            }
        }
        for (int i = 0; i < SummaryMetadataIndices.Length; i++)
            output[41 + i] = row[SummaryMetadataIndices[i]];
        if (options != null)
            output[^1] = FormattableString.Invariant($"Cover {options.Cover} mm; bars {string.Join(",", options.BarDiameters)} mm; max layers {options.MaxLayers}; {(options.ClearSpacing ? "clear" : "centre")} horizontal spacing {options.HorizontalSpacing} mm; vertical clear spacing {options.VerticalSpacing} mm; links {string.Join(",", options.LinkDiameters)} mm; legs {string.Join(",", options.LinkLegs)}; spacing {options.MinimumLinkSpacing}-{options.MaximumLinkSpacing} step {options.LinkSpacingStep} mm");
        return output;
    }

    private static void Flag(object?[] row, string message)
    {
        row[0] = $"{row[0]}; SUMMARY INCOMPLETE - REVIEW";
        row[28] = message;
    }

    private static string[] CreateHeaders()
    {
        var headers = new List<string>
        {
            "Review status", "ETABS design errors", "ETABS design warnings", "Geometry warning",
            "Model path", "Design code", "Exported local time", "Story", "Beam label", "Unique ID",
            "Assigned section", "Design section", "I X (mm)", "I Y (mm)", "I Z (mm)",
            "J X (mm)", "J Y (mm)", "J Z (mm)", "I-J length (mm)",
            "I end offset (mm)", "J end offset (mm)", "I zone fraction", "Middle zone fraction", "J zone fraction",
            "I zone boundary from I (mm)", "J zone boundary from I (mm)",
            "Max longitudinal torsion area (mm2)", "Max transverse torsion area (mm2/m)", "Zone sampling note"
        };
        for (int i = 0; i < DemandNames.Length; i++)
        {
            string unit = i < 6 ? "mm2" : "mm2/m";
            headers.Add($"{DemandNames[i]} {(i < 6 ? "flexural" : "major shear")} demand ({unit})");
            headers.Add($"{DemandNames[i]} reinforcement description (manual)");
            headers.Add($"{DemandNames[i]} provided area ({unit}, manual)");
            headers.Add($"{DemandNames[i]} area ratio");
        }
        headers.Add("Section width (mm)");
        headers.Add("Section height (mm)");
        return headers.ToArray();
    }

    private static string[] CreateOutputHeaders()
    {
        var headers = new List<string> { "Beam label", "Unique ID", "Story", "Width (mm)", "Height (mm)" };
        for (int i = 0; i < DemandNames.Length; i++)
            headers.Add($"{DemandNames[i]} required {(i < 6 ? "flexural area (mm2)" : "shear area (mm2/m)")}");
        foreach (string name in DemandNames) headers.Add($"{name} reinforcement proposal / input");
        for (int i = 0; i < DemandNames.Length; i++)
            headers.Add($"{DemandNames[i]} provided area ({(i < 6 ? "mm2" : "mm2/m")})");
        foreach (string name in DemandNames) headers.Add($"{name} area ratio");
        headers.AddRange(SummaryMetadataIndices.Select(index => Headers[index]));
        headers.Add("Proposal settings");
        return headers.ToArray();
    }
}
