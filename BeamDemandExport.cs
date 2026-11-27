using CSiAPIv1;
using System.Globalization;

namespace BeamSchedulePlugin;

internal enum BeamScope { All, Selected, Groups }

internal sealed record DemandReport(IReadOnlyList<object?[]> Rows, int BeamCount, int StationCount);

internal static class BeamDemandExport
{
    internal const string TorsionOnlyStatus = "TORSION EXCLUDED FROM PROPOSAL - ENGINEER REVIEW REQUIRED";
    internal const string TorsionWarning = "Torsion reinforcement demand is excluded from proposals and ratios; design separately.";

    internal static string ReviewStatus(string errors, string warnings, bool geometryIssues, bool hasTorsion) =>
        errors.Length > 0 ? "DESIGN ERROR - DO NOT USE"
        : warnings.Length > 0 || geometryIssues ? "WARNING - ENGINEER REVIEW REQUIRED"
        : hasTorsion ? TorsionOnlyStatus : "DRAFT - ENGINEER REVIEW REQUIRED";

    internal static readonly string[] Headers =
    {
        "Review status", "ETABS design errors", "ETABS design warnings", "Model path",
        "Design code", "Exported local time", "Story", "Beam label", "Unique ID",
        "Assigned section", "Design section", "Geometry warning",
        "I X (mm)", "I Y (mm)", "I Z (mm)", "J X (mm)", "J Y (mm)", "J Z (mm)",
        "Station from I (mm)", "Top flexural area (mm2)", "Top combination",
        "Bottom flexural area (mm2)", "Bottom combination",
        "Major shear area per length (mm2/m)", "Major shear combination",
        "Longitudinal torsion area (mm2)", "Longitudinal torsion combination",
        "Transverse torsion area per length (mm2/m)", "Transverse torsion combination"
    };

    internal static IReadOnlyList<string> ReadStoryNames(cSapModel model)
    {
        int count = 0;
        string[] names = Array.Empty<string>();
        Check(model.Story.GetNameList(ref count, ref names), "read story names");
        RequireLengths(count, names);
        if (names.Take(count).Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("ETABS returned an empty story name.");
        return names.Take(count).ToArray();
    }

    internal static bool MatchesScope(string name, string frameStory, HashSet<string>? included, string? story) =>
        (included == null || included.Contains(name))
        && (story == null || string.Equals(frameStory, story, StringComparison.OrdinalIgnoreCase));

    internal static string SuggestedFilename(DateTime now, BeamScope scope, string? story)
    {
        string scopeName = scope switch
        {
            BeamScope.All => "All beams",
            BeamScope.Selected => "Selected beams",
            BeamScope.Groups => "Group beams",
            _ => throw new InvalidOperationException("Unsupported beam scope.")
        };
        string location = story == null ? "All levels" : "Level " + SafeFilenamePart(story);
        return now.ToString("yyMMdd_HHmmss", CultureInfo.InvariantCulture)
            + " ETABS beam schedule - " + scopeName + " - " + location;
    }

    private static string SafeFilenamePart(string name)
    {
        string clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "_";
        return clean.Length > 80 ? clean.Substring(0, 80).TrimEnd(' ', '.') : clean;
    }

    internal static DemandReport Read(cSapModel model, BeamScope scope, IReadOnlyList<string> groups, string? story = null)
    {
        string code = "";
        Check(model.DesignConcrete.GetCode(ref code), "read concrete design code");
        if (code != "Eurocode 2-2004")
            throw new InvalidOperationException($"Only Eurocode 2-2004 is supported; the current code is '{code}'.");
        if (!model.DesignConcrete.GetResultsAvailable())
            throw new InvalidOperationException("Concrete design results are not available. Run and review design in ETABS first.");

        eForce force = default;
        eLength length = default;
        eTemperature temperature = default;
        Check(model.GetPresentUnits_2(ref force, ref length, ref temperature), "read present units");
        double mm = MillimetresPerUnit(length);
        string modelPath = model.GetModelFilename(true);
        string exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        HashSet<string>? included = GetScope(model, scope, groups);

        int frameCount = 0;
        string[] names = Array.Empty<string>(), labels = Array.Empty<string>(), stories = Array.Empty<string>();
        Check(model.FrameObj.GetLabelNameList(ref frameCount, ref names, ref labels, ref stories), "read beam labels");
        RequireLengths(frameCount, names, labels, stories);

        var stations = ReadStations(model, mm);
        var result = new List<object?[]>();
        int beams = 0, stationCount = 0;
        for (int i = 0; i < frameCount; i++)
        {
            string name = names[i];
            if (!MatchesScope(name, stories[i], included, story)) continue;
            eFrameDesignOrientation orientation = default;
            Check(model.FrameObj.GetDesignOrientation(name, ref orientation), $"read orientation for frame '{name}'");
            if (orientation != eFrameDesignOrientation.Beam) continue;
            beams++;

            string assigned = "", autoSelect = "", designed = "";
            var geometryIssues = new List<string>();
            int rc = model.FrameObj.GetSection(name, ref assigned, ref autoSelect);
            if (rc != 0) geometryIssues.Add($"Assigned section unavailable (ETABS code {rc})");
            rc = model.DesignConcrete.GetDesignSection(name, ref designed);
            if (rc != 0) geometryIssues.Add($"Design section unavailable (ETABS code {rc})");
            var coordinates = ReadEndpoints(model, name, mm, geometryIssues);

            if (!stations.TryGetValue(name, out var beamStations) || beamStations.Count == 0)
            {
                result.Add(CreateRow("MISSING DESIGN RESULTS - REVIEW", "", "", modelPath, code, exportedAt,
                    stories[i], labels[i], name, assigned, designed, string.Join("; ", geometryIssues), coordinates, null));
                continue;
            }

            string beamErrors = string.Join("; ", beamStations.Select(s => s.Error)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            string beamWarnings = string.Join("; ", beamStations.Select(s => s.Warning)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            bool hasTorsion = beamStations.Any(s => s.TorsionLong != 0 || s.TorsionTransverse != 0);
            string status = ReviewStatus(beamErrors, beamWarnings, geometryIssues.Count > 0, hasTorsion);
            if (hasTorsion)
                beamWarnings = string.Join("; ", new[] { beamWarnings, TorsionWarning }
                    .Where(s => s.Length > 0));
            foreach (var station in beamStations)
            {
                result.Add(CreateRow(status, beamErrors, beamWarnings, modelPath, code, exportedAt,
                    stories[i], labels[i], name, assigned, designed, string.Join("; ", geometryIssues), coordinates, station));
                stationCount++;
            }
        }

        if (beams == 0) throw new InvalidOperationException("The selected scope and level contain no beam-oriented frame objects.");
        return new DemandReport(result, beams, stationCount);
    }

    internal static RevitScheduleData ReadMappingGeometry(cSapModel model)
    {
        eForce force = default;
        eLength length = default;
        eTemperature temperature = default;
        Check(model.GetPresentUnits_2(ref force, ref length, ref temperature), "read present units");
        double mm = MillimetresPerUnit(length);
        int count = 0;
        string[] names = Array.Empty<string>(), labels = Array.Empty<string>(), stories = Array.Empty<string>();
        Check(model.FrameObj.GetLabelNameList(ref count, ref names, ref labels, ref stories), "read beam labels");
        RequireLengths(count, names, labels, stories);
        var beams = new List<MappingBeam>();
        var skipped = new List<SkippedMappingBeam>();
        for (int i = 0; i < count; i++)
        {
            string name = names[i];
            eFrameDesignOrientation orientation = default;
            Check(model.FrameObj.GetDesignOrientation(name, ref orientation), $"read orientation for frame '{name}'");
            if (orientation != eFrameDesignOrientation.Beam) continue;
            var issues = new List<string>();
            var xyz = ReadEndpoints(model, name, mm, issues);
            string? label = string.IsNullOrWhiteSpace(labels[i]) ? null : labels[i].Trim();
            if (xyz is [double x, double y, double z, double jx, double jy, double jz])
            {
                var beam = new MappingBeam(name, new(x, y, z), new(jx, jy, jz), EtabsLabel: label, Level: stories[i]);
                if (beam.IsValid) { beams.Add(beam); continue; }
            }
            skipped.Add(new(name, null, issues.Count == 0 ? "Invalid or coincident ETABS endpoints" : string.Join("; ", issues),
                EtabsLabel: label));
        }
        return new(model.GetModelFilename(true), beams, skipped);
    }

    private static object?[] ReadEndpoints(cSapModel model, string name, double mm, List<string> issues)
    {
        string pointI = "", pointJ = "";
        var coordinates = new object?[6];
        int rc = model.FrameObj.GetPoints(name, ref pointI, ref pointJ);
        if (rc != 0) issues.Add($"Endpoints unavailable (ETABS code {rc})");
        else
        {
            ReadPoint(model, pointI, mm, coordinates, 0, issues);
            ReadPoint(model, pointJ, mm, coordinates, 3, issues);
        }
        return coordinates;
    }

    private static HashSet<string>? GetScope(cSapModel model, BeamScope scope, IReadOnlyList<string> groups)
    {
        if (scope == BeamScope.All) return null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scope == BeamScope.Selected)
        {
            int count = 0;
            int[] types = Array.Empty<int>();
            string[] names = Array.Empty<string>();
            Check(model.SelectObj.GetSelected(ref count, ref types, ref names), "read selected objects");
            RequireLengths(count, types, names);
            for (int i = 0; i < count; i++) if (types[i] == 2) result.Add(names[i]);
        }
        else if (scope == BeamScope.Groups)
        {
            if (groups.Count == 0) throw new InvalidOperationException("Select at least one group.");
            int groupCount = 0;
            string[] available = Array.Empty<string>();
            Check(model.GroupDef.GetNameList(ref groupCount, ref available), "read group names");
            RequireLengths(groupCount, available);
            var groupLookup = available.Take(groupCount).ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
            foreach (string group in groups)
                if (!groupLookup.ContainsKey(group))
                    throw new InvalidOperationException($"ETABS group '{group}' does not exist.");
            if (groups.Any(group => string.Equals(group, "All", StringComparison.OrdinalIgnoreCase))) return null;
            foreach (string group in groups.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int count = 0;
                int[] types = Array.Empty<int>();
                string[] names = Array.Empty<string>();
                Check(model.GroupDef.GetAssignments(groupLookup[group], ref count, ref types, ref names), $"read group '{group}'");
                RequireLengths(count, types, names);
                for (int i = 0; i < count; i++) if (types[i] == 2) result.Add(names[i]);
            }
        }
        else throw new InvalidOperationException("Unsupported beam scope.");
        return result;
    }

    private sealed record Station(double Location, double Top, string TopCombo, double Bottom, string BottomCombo,
        double Shear, string ShearCombo, double TorsionLong, string TorsionLongCombo,
        double TorsionTransverse, string TorsionTransverseCombo, string Error, string Warning);

    private static Dictionary<string, List<Station>> ReadStations(cSapModel model, double mm)
    {
        int count = 0;
        string[] names = Array.Empty<string>(), topCombo = Array.Empty<string>(), bottomCombo = Array.Empty<string>(),
            shearCombo = Array.Empty<string>(), torsionLongCombo = Array.Empty<string>(), torsionTransCombo = Array.Empty<string>(),
            errors = Array.Empty<string>(), warnings = Array.Empty<string>();
        double[] locations = Array.Empty<double>(), top = Array.Empty<double>(), bottom = Array.Empty<double>(),
            shear = Array.Empty<double>(), torsionLong = Array.Empty<double>(), torsionTrans = Array.Empty<double>();
        Check(model.DesignConcrete.GetSummaryResultsBeam("All", ref count, ref names, ref locations,
            ref topCombo, ref top, ref bottomCombo, ref bottom, ref shearCombo, ref shear,
            ref torsionLongCombo, ref torsionLong, ref torsionTransCombo, ref torsionTrans,
            ref errors, ref warnings, eItemType.Group), "read concrete beam design results");
        RequireLengths(count, names, locations, topCombo, top, bottomCombo, bottom, shearCombo, shear,
            torsionLongCombo, torsionLong, torsionTransCombo, torsionTrans, errors, warnings);

        var result = new Dictionary<string, List<Station>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (!double.IsFinite(locations[i]) || !double.IsFinite(top[i]) || !double.IsFinite(bottom[i])
                || !double.IsFinite(shear[i]) || !double.IsFinite(torsionLong[i]) || !double.IsFinite(torsionTrans[i]))
                throw new InvalidOperationException($"ETABS returned a non-finite result for beam '{names[i]}'.");
            if (!result.TryGetValue(names[i], out var list)) result[names[i]] = list = new List<Station>();
            list.Add(new Station(locations[i] * mm, top[i] * mm * mm, topCombo[i], bottom[i] * mm * mm,
                bottomCombo[i], shear[i] * mm * 1000, shearCombo[i], torsionLong[i] * mm * mm,
                torsionLongCombo[i], torsionTrans[i] * mm * 1000, torsionTransCombo[i], errors[i], warnings[i]));
        }
        return result;
    }

    private static object?[] CreateRow(string status, string error, string warning, string modelPath, string code,
        string exportedAt, string story, string label, string name, string assigned, string designed,
        string geometryWarning, object?[] xyz, Station? station) => new object?[]
    {
        status, error, warning, modelPath, code, exportedAt, story, label, name, assigned, designed, geometryWarning,
        xyz[0], xyz[1], xyz[2], xyz[3], xyz[4], xyz[5],
        station?.Location, station?.Top, station?.TopCombo, station?.Bottom, station?.BottomCombo,
        station?.Shear, station?.ShearCombo, station?.TorsionLong, station?.TorsionLongCombo,
        station?.TorsionTransverse, station?.TorsionTransverseCombo
    };

    private static void ReadPoint(cSapModel model, string name, double mm, object?[] xyz, int offset, List<string> issues)
    {
        double x = 0, y = 0, z = 0;
        int rc = model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global");
        if (rc != 0) { issues.Add($"Point '{name}' coordinates unavailable (ETABS code {rc})"); return; }
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new InvalidOperationException($"ETABS returned invalid coordinates for point '{name}'.");
        xyz[offset] = x * mm;
        xyz[offset + 1] = y * mm;
        xyz[offset + 2] = z * mm;
    }

    internal static double MillimetresPerUnit(eLength unit) => unit switch
    {
        eLength.mm => 1,
        eLength.cm => 10,
        eLength.m => 1000,
        eLength.inch => 25.4,
        eLength.ft => 304.8,
        eLength.micron => 0.001,
        _ => throw new InvalidOperationException($"Unsupported ETABS length unit: {unit}.")
    };

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Could not {operation} (ETABS code {result}).");
    }

    private static void RequireLengths(int count, params Array[] arrays)
    {
        if (count < 0 || arrays.Any(array => array == null || array.Length < count))
            throw new InvalidOperationException("ETABS returned incomplete or inconsistent result arrays.");
    }
}
