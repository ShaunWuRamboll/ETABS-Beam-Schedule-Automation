using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace BeamSchedulePlugin;

internal readonly record struct BeamPoint(double X, double Y, double Z)
{
    public static BeamPoint operator +(BeamPoint a, BeamPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static BeamPoint operator -(BeamPoint a, BeamPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static BeamPoint operator /(BeamPoint a, double n) => new(a.X / n, a.Y / n, a.Z / n);
    public static BeamPoint operator *(BeamPoint a, double n) => new(a.X * n, a.Y * n, a.Z * n);
    internal double Dot(BeamPoint b) => X * b.X + Y * b.Y + Z * b.Z;
    internal double SquaredLength => X * X + Y * Y + Z * Z;
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
}

internal sealed record MappingBeam(string Reference, BeamPoint Start, BeamPoint End, int? ExcelRow = null, string? Mark = null,
    string? EtabsLabel = null)
{
    internal BeamPoint Midpoint => Start / 2 + End / 2;
    internal BeamPoint Direction => End - Start;
    internal bool IsValid => Start.IsFinite && End.IsFinite && Direction.IsFinite
        && double.IsFinite(Direction.SquaredLength) && Direction.SquaredLength > 0;
}

internal sealed record SkippedMappingBeam(string Reference, int? ExcelRow, string Reason, string? Mark = null, string? EtabsLabel = null);
internal sealed record RevitScheduleData(string Filename, IReadOnlyList<MappingBeam> Beams,
    IReadOnlyList<SkippedMappingBeam> Skipped);
internal sealed record BeamMappingPair(MappingBeam Etabs, MappingBeam Revit, bool Reversed,
    double MaximumEndpointError, bool Ambiguous);
internal sealed record TranslationScore(BeamPoint Translation, int MatchCount, double SquaredEndpointError);

internal sealed record RevitMappingResult(RevitScheduleData Revit, IReadOnlyList<MappingBeam> Etabs,
    IReadOnlyList<SkippedMappingBeam> SkippedEtabs, BeamPoint? Translation,
    IReadOnlyList<BeamMappingPair> Pairs, IReadOnlyList<TranslationScore> CompetingTranslations)
{
    internal RevitMappingOptions Options { get; init; } = RevitMappingOptions.Default;
    internal IReadOnlyList<MappingLink> Links { get; init; } = Array.Empty<MappingLink>();
    internal IReadOnlyList<MappingAmbiguity> Ambiguities { get; init; } = Array.Empty<MappingAmbiguity>();
    internal IReadOnlyList<MappingPassTotal> PassTotals { get; init; } = Array.Empty<MappingPassTotal>();
    internal int MappedEtabsCount => Links.Select(p => p.Etabs).Distinct().Count();
    internal int MappedRevitCount => Links.Select(p => p.Revit).Distinct().Count();
    internal bool HasCompetingTranslations => CompetingTranslations.Count > 0;
    internal string Alignment => Translation == null ? "Translation not established - No match"
        : MappedEtabsCount == Etabs.Count && MappedRevitCount == Revit.Beams.Count
            && SkippedEtabs.Count == 0 && Revit.Skipped.Count == 0 ? "Full match" : "Partial alignment";
}

internal static class RevitScheduleReader
{
    internal static readonly string[] CoordinateHeaders =
        { "StartX_mm", "StartY_mm", "StartZ_mm", "EndX_mm", "EndY_mm", "EndZ_mm" };
    private static readonly string[] ElementIdHeaders =
        { "Element ID", "ElementId", "Element_ID", "Revit Element ID", "RevitElementId", "Revit ID" };
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static RevitScheduleData Read(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var book = ReadPart(zip, "xl/workbook.xml");
        var sheet = book.Descendants(X + "sheet").FirstOrDefault(s =>
            string.Equals((string?)s.Attribute("name"), "RC Beam Schedule", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Revit workbook is missing worksheet 'RC Beam Schedule'.");
        var relations = ReadPart(zip, "xl/_rels/workbook.xml.rels").Descendants(P + "Relationship").ToArray();
        string? id = (string?)sheet.Attribute(R + "id");
        var sheetRel = relations.SingleOrDefault(r => id != null && (string?)r.Attribute("Id") == id);
        var stringsRel = relations.FirstOrDefault(r =>
            ((string?)r.Attribute("Type"))?.EndsWith("/sharedStrings", StringComparison.Ordinal) == true);
        string[] strings = stringsRel == null ? Array.Empty<string>() :
            ReadPart(zip, PartPath(stringsRel)).Descendants(X + "si").Select(Text).ToArray();
        using var stream = OpenPart(zip, PartPath(sheetRel));
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        int[]? columns = null;
        int elementIdColumn = 0, markColumn = 0;
        var beams = new List<MappingBeam>();
        var skipped = new List<SkippedMappingBeam>();
        int previousRow = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row" || reader.NamespaceURI != X.NamespaceName)
                continue;
            using var subtree = reader.ReadSubtree();
            var row = XElement.Load(subtree);
            string? rowReference = (string?)row.Attribute("r");
            string? firstAddress = (string?)row.Elements(X + "c").FirstOrDefault()?.Attribute("r");
            rowReference ??= firstAddress == null ? null : new string(firstAddress.SkipWhile(char.IsAsciiLetter).ToArray());
            int rowNumber = rowReference == null ? previousRow + 1
                : int.TryParse(rowReference, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;
            if (rowNumber <= previousRow || rowNumber > 1_048_576)
                throw new InvalidDataException("Revit worksheet has invalid or out-of-order Excel row numbers.");
            previousRow = rowNumber;
            var cells = new Dictionary<int, XElement>();
            int nextColumn = 1;
            foreach (var cell in row.Elements(X + "c"))
            {
                string address = (string?)cell.Attribute("r") ?? "";
                int column = 0;
                foreach (char c in address.TakeWhile(char.IsAsciiLetter))
                {
                    column = column * 26 + char.ToUpperInvariant(c) - 'A' + 1;
                    if (column > 16_384) throw new InvalidDataException("Revit worksheet has an invalid Excel column address.");
                }
                if (address.Length == 0) column = nextColumn;
                if (column < 1 || column > 16_384)
                    throw new InvalidDataException("Revit worksheet has an invalid Excel cell address.");
                nextColumn = column + 1;
                if (!cells.TryAdd(column, cell))
                    throw new InvalidDataException($"Revit Excel row {rowNumber} contains duplicate cell addresses.");
            }
            if (!cells.Values.Any(c => c.Element(X + "v") != null || c.Element(X + "is") != null || c.Element(X + "f") != null))
                continue;
            if (columns == null)
            {
                var headers = cells.ToDictionary(c => c.Key, c => Value(c.Value, strings)?.Trim());
                columns = CoordinateHeaders.Select(header =>
                {
                    var found = headers.Where(c => string.Equals(c.Value, header, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (found.Length > 1) throw new InvalidDataException($"Revit worksheet has duplicate header '{header}'.");
                    return found.FirstOrDefault().Key;
                }).ToArray();
                var missing = CoordinateHeaders.Where((_, i) => columns[i] == 0).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException("Revit 'RC Beam Schedule' is missing required millimetre columns: " + string.Join(", ", missing));
                elementIdColumn = ElementIdHeaders.SelectMany(header => headers.Where(c =>
                    string.Equals(c.Value, header, StringComparison.OrdinalIgnoreCase))).Select(c => c.Key).FirstOrDefault();
                markColumn = headers.FirstOrDefault(c => string.Equals(c.Value, "Mark", StringComparison.OrdinalIgnoreCase)).Key;
                continue;
            }
            string? elementId = elementIdColumn != 0 && cells.TryGetValue(elementIdColumn, out var idCell)
                ? Value(idCell, strings)?.Trim() : null;
            string reference = string.IsNullOrEmpty(elementId) ? $"Excel row {rowNumber}" : $"Element ID {elementId}";
            string? mark = markColumn != 0 && cells.TryGetValue(markColumn, out var markCell)
                ? Value(markCell, strings)?.Trim() : null;
            var xyz = new double[6];
            bool valid = true;
            for (int i = 0; i < columns.Length; i++)
                valid &= cells.TryGetValue(columns[i], out var cell)
                    && double.TryParse(Value(cell, strings), NumberStyles.Float, CultureInfo.InvariantCulture, out xyz[i])
                    && double.IsFinite(xyz[i]);
            var beam = new MappingBeam(reference, new(xyz[0], xyz[1], xyz[2]), new(xyz[3], xyz[4], xyz[5]), rowNumber, mark);
            if (valid && beam.IsValid) beams.Add(beam);
            else skipped.Add(new(reference, rowNumber, valid ? "Coincident or invalid endpoints" : "Missing, non-numeric or non-finite endpoint coordinates", mark));
        }
        if (columns == null) throw new InvalidDataException("Revit 'RC Beam Schedule' contains no header row.");
        return new(Path.GetFileName(path), beams, skipped);
    }

    private static string Text(XElement cell) => string.Concat(cell.Descendants(X + "t").Select(t => t.Value));

    private static string? Value(XElement cell, string[] strings)
    {
        string? value = (string?)cell.Element(X + "v");
        switch ((string?)cell.Attribute("t"))
        {
            case "inlineStr": return Text(cell);
            case "s":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < strings.Length)
                    return strings[i];
                throw new InvalidDataException("Revit workbook refers to a missing Excel shared string.");
            case "b":
            case "e": return null;
            default: return value;
        }
    }

    private static string PartPath(XElement? relationship)
    {
        string? target = (string?)relationship?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || (string?)relationship?.Attribute("TargetMode") == "External")
            throw new InvalidDataException("Revit workbook has a missing or external worksheet relationship.");
        var uri = new Uri(new Uri("http://xlsx.invalid/xl/workbook.xml"), target);
        if (uri.Host != "xlsx.invalid") throw new InvalidDataException("Revit workbook has an external part target.");
        return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    }

    private static Stream OpenPart(ZipArchive zip, string path) =>
        (zip.GetEntry(path) ?? throw new InvalidDataException($"Revit workbook part '{path}' was not found.")).Open();

    private static XDocument ReadPart(ZipArchive zip, string path)
    {
        using var stream = OpenPart(zip, path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}

internal static class RevitBeamMatcher
{
    internal const int CandidateLimit = 24;
    private sealed record CandidatePair(int Source, int Target, double Error, bool Reversed, bool OrientationAmbiguous);
    private sealed record ScoredCandidate(BeamPoint Offset, IReadOnlyList<BeamMappingPair> Pairs, double Error);

    internal static RevitMappingResult Match(IReadOnlyList<MappingBeam> etabs, RevitScheduleData revit,
        IReadOnlyList<SkippedMappingBeam>? skippedEtabs = null, RevitMappingOptions? options = null)
    {
        options ??= RevitMappingOptions.Default;
        options.Validate();
        double tolerance = options.EndpointTolerance;
        if (etabs.Any(b => !b.IsValid) || revit.Beams.Any(b => !b.IsValid))
            throw new ArgumentException("Matcher input must contain valid, finite, non-coincident beam endpoints.");
        var votes = new Dictionary<(long X, long Y, long Z), (int Count, BeamPoint Sum)>();
        foreach (var a in etabs)
        foreach (var b in revit.Beams)
        {
            if ((a.Direction - b.Direction).SquaredLength > 4 * tolerance * tolerance
                && (a.Direction + b.Direction).SquaredLength > 4 * tolerance * tolerance) continue;
            var offset = b.Midpoint - a.Midpoint;
            var key = Cell(offset + new BeamPoint(tolerance / 2, tolerance / 2, tolerance / 2), tolerance);
            votes.TryGetValue(key, out var vote);
            votes[key] = (vote.Count + 1, vote.Sum + offset);
        }
        var targetCells = new Dictionary<(long X, long Y, long Z), List<int>>();
        for (int j = 0; j < revit.Beams.Count; j++)
        {
            var key = Cell(revit.Beams[j].Midpoint, tolerance);
            if (!targetCells.TryGetValue(key, out var indices)) targetCells[key] = indices = new();
            indices.Add(j);
        }
        // LINQ's stable ordering preserves prototype insertion order for equal votes/errors.
        var scores = votes.OrderByDescending(v => v.Value.Count).Take(CandidateLimit)
            .Select(v => v.Value.Sum / v.Value.Count).Distinct()
            .Select(offset => Score(etabs, revit.Beams, targetCells, offset, tolerance))
            .OrderByDescending(s => s.Pairs.Count).ThenBy(s => s.Error).ToArray();
        var best = scores.FirstOrDefault();
        bool matched = best != null && best.Pairs.Count > 0;
        var competitors = matched ? scores.Skip(1).Where(s => s.Pairs.Count == best!.Pairs.Count
                && (s.Offset - best.Offset).SquaredLength > tolerance * tolerance)
            .Select(s => new TranslationScore(s.Offset, s.Pairs.Count, s.Error)).ToArray() : Array.Empty<TranslationScore>();
        var result = new RevitMappingResult(revit, etabs, skippedEtabs ?? Array.Empty<SkippedMappingBeam>(),
            matched ? best!.Offset : null, matched ? best!.Pairs : Array.Empty<BeamMappingPair>(), competitors) { Options = options };
        return RevitMappingPasses.Apply(result);
    }

    private static ScoredCandidate Score(IReadOnlyList<MappingBeam> sources, IReadOnlyList<MappingBeam> targets,
        Dictionary<(long X, long Y, long Z), List<int>> cells, BeamPoint offset, double tolerance)
    {
        var candidates = new List<CandidatePair>();
        double limit = tolerance * tolerance;
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var (x, y, z) = Cell(source.Midpoint + offset, tolerance);
            var start = source.Start + offset;
            var end = source.End + offset;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!cells.TryGetValue((x + dx, y + dy, z + dz), out var indices)) continue;
                foreach (int j in indices)
                {
                    var target = targets[j];
                    double forward = Math.Max((start - target.Start).SquaredLength, (end - target.End).SquaredLength);
                    double reverse = Math.Max((start - target.End).SquaredLength, (end - target.Start).SquaredLength);
                    double error = Math.Min(forward, reverse);
                    if (error <= limit)
                        candidates.Add(new(i, j, error, reverse < forward, forward <= limit && reverse <= limit));
                }
            }
        }
        int[] sourceDegree = new int[sources.Count], targetDegree = new int[targets.Count];
        foreach (var pair in candidates) { sourceDegree[pair.Source]++; targetDegree[pair.Target]++; }
        var usedSources = new bool[sources.Count];
        var usedTargets = new bool[targets.Count];
        var selected = new List<CandidatePair>();
        foreach (var pair in candidates.OrderBy(p => p.Error))
        {
            if (usedSources[pair.Source] || usedTargets[pair.Target]) continue;
            usedSources[pair.Source] = true;
            usedTargets[pair.Target] = true;
            selected.Add(pair);
        }
        return new(offset, selected.Select(p => new BeamMappingPair(sources[p.Source], targets[p.Target],
            p.Reversed, Math.Sqrt(p.Error), p.OrientationAmbiguous || sourceDegree[p.Source] > 1 || targetDegree[p.Target] > 1)).ToArray(),
            selected.Sum(p => p.Error));
    }

    private static (long X, long Y, long Z) Cell(BeamPoint point, double tolerance) =>
        (Coordinate(point.X, tolerance), Coordinate(point.Y, tolerance), Coordinate(point.Z, tolerance));

    private static long Coordinate(double value, double tolerance)
    {
        double cell = Math.Floor(value / tolerance);
        if (!double.IsFinite(cell) || cell <= long.MinValue + 2048d || cell >= long.MaxValue - 2048d)
            throw new InvalidOperationException("Beam coordinates are outside the supported mapping range.");
        return (long)cell;
    }
}
