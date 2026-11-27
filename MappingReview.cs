namespace BeamSchedulePlugin;

internal sealed record VisualReviewPair(string Id, string Level, int Slide, MappingBeam Etabs, MappingBeam Revit,
    MappingMeasurements Metrics, string Reason);
internal sealed record MappingReviewLevel(string Name, IReadOnlyList<MappingBeam> Revit, IReadOnlyList<MappingBeam> Etabs);

internal sealed class MappingReview
{
    internal RevitMappingResult Mapping { get; }
    internal IReadOnlyList<MappingReviewLevel> Levels { get; }
    internal IReadOnlyList<VisualReviewPair> Pairs { get; }

    internal MappingReview(RevitMappingResult mapping, string? levelFilter = null)
    {
        Mapping = mapping;
        var comparer = StringComparer.OrdinalIgnoreCase;
        var names = mapping.Revit.Beams.Select(b => Name(b.Level)).Distinct(comparer).ToList();
        var etabsLevels = mapping.Links.GroupBy(l => l.Etabs)
            .ToDictionary(g => g.Key, g => g.Select(l => Name(l.Revit.Level)).Distinct(comparer).ToArray());
        var byLevel = new Dictionary<string, List<MappingBeam>>(comparer);
        foreach (var beam in mapping.Etabs)
        {
            string[] levels;
            if (!etabsLevels.TryGetValue(beam, out levels!))
            {
                string story = Name(beam.Level);
                var matchingLevels = names.Where(n => MappingLevels.StoryMatches(story, n)).ToArray();
                levels = matchingLevels.Length == 0 ? new[] { story } : matchingLevels;
            }
            foreach (string name in levels)
            {
                if (!byLevel.TryGetValue(name, out var beams)) byLevel[name] = beams = new();
                beams.Add(beam);
            }
        }
        var revitLevels = mapping.Revit.Beams.ToLookup(b => Name(b.Level), comparer);
        Levels = names.Concat(byLevel.Keys).Distinct(comparer)
            .Where(n => string.IsNullOrWhiteSpace(levelFilter) || MappingLevels.LevelMatches(n, levelFilter.Trim())
                || MappingLevels.StoryMatches(n, levelFilter.Trim()))
            .Select(n => new MappingReviewLevel(n, revitLevels[n].ToArray(),
                byLevel.TryGetValue(n, out var beams) ? beams : Array.Empty<MappingBeam>())).ToArray();
        var slides = Levels.Select((l, i) => (l.Name, Slide: i + 1)).ToDictionary(x => x.Name, x => x.Slide, comparer);
        Pairs = mapping.VisualCandidates
            .Where(a => slides.ContainsKey(Name(a.Revit!.Level)))
            .OrderBy(a => slides[Name(a.Revit!.Level)])
            .Select((a, i) => new VisualReviewPair($"VR-{i + 1:D3}", Name(a.Revit!.Level), slides[Name(a.Revit.Level)],
                a.Etabs!, a.Revit, a.Measurements!, a.Reason)).ToArray();
    }

    private static string Name(string? level) => string.IsNullOrWhiteSpace(level) ? "(No source level)" : level.Trim();
}

internal static class MappingDecisionSheets
{
    internal static readonly string[] VisualHeaders = { "Review ID", "Level", "Slide number", "ETABS Unique ID",
        "ETABS display name", "Revit ElementId", "Revit display name", "Longer-span coverage",
        "Shorter-span coverage", "Angle (degrees)", "Lateral (mm)", "Vertical (mm)", "Start difference (mm)",
        "End difference (mm)", "Reason not auto-assigned", "Mapping run ID", "Decision", "Reviewer", "Notes", "Decision check" };
    internal static readonly string[] EffectiveHeaders = { "Review ID", "Level", "ETABS Unique ID", "ETABS display name",
        "Revit ElementId", "Revit display name", "Mapping run ID", "Automatic pass", "Automatic group", "Baseline",
        "Decision state", "Effective status", "Active", "ETABS Story", "Revit Level", "Review provenance" };

    internal static (List<object?[]> Visual, List<object?[]> Effective) Rows(MappingReview review)
    {
        var visual = new List<object?[]> { VisualHeaders };
        var effective = new List<object?[]> { EffectiveHeaders };
        var mapping = review.Mapping;
        foreach (var link in mapping.Links)
            effective.Add(new object?[] { null, link.Revit.Level, link.Etabs.StableId, link.Etabs.DisplayName,
                link.Revit.StableId, link.Revit.DisplayName, mapping.RunId, link.PassName, link.GroupId,
                "Automatic", null, "Matched", "Yes", link.Etabs.Level, link.Revit.Level, link.Review ? "REVIEW" : null });

        int visualEnd = review.Pairs.Count + 1;
        int effectiveEnd = effective.Count + review.Pairs.Count;
        string VisualRange(string column) => $"'Visual Review'!${column}$2:${column}${visualEnd}";
        string EffectiveRange(string column) => $"'Effective Links'!${column}$2:${column}${effectiveEnd}";
        string Match(string range, string cell) => $"{range},{Criterion(cell)}";

        foreach (var pair in review.Pairs)
        {
            int v = visual.Count + 1, e = effective.Count + 1;
            var m = pair.Metrics;
            string visualKeys = string.Join(",", Match(EffectiveRange("A"), $"A{v}"),
                Match(EffectiveRange("C"), $"D{v}"), Match(EffectiveRange("E"), $"F{v}"),
                Match(EffectiveRange("G"), $"P{v}"));
            string effectiveKeys = string.Join(",", Match(VisualRange("A"), $"A{e}"),
                Match(VisualRange("D"), $"C{e}"), Match(VisualRange("F"), $"E{e}"),
                Match(VisualRange("P"), $"G{e}"));
            string DecisionCount(string value) => $"COUNTIFS({effectiveKeys},{VisualRange("Q")},\"{value}\")";
            string state = $"IF(COUNTIFS({effectiveKeys})=0,\"Stale / missing\",IF(COUNTIFS({effectiveKeys})>1,\"Conflict: duplicate decision key\"," +
                $"IF({DecisionCount("Accept")}=1,\"Accept\",IF({DecisionCount("Reject")}=1,\"Reject\"," +
                $"IF({DecisionCount("Defer")}=1,\"Defer\",IF({DecisionCount("")}=1,\"Pending\",\"Invalid decision\"))))))";
            string Claims(string col, string filterCol, string filter) =>
                $"COUNTIFS({Match(EffectiveRange(col), $"{col}{e}")},{EffectiveRange(filterCol)},\"{filter}\")";
            string status = $"IF(K{e}<>\"Accept\",K{e},IF(OR({Claims("C", "J", "Automatic")}>0," +
                $"{Claims("E", "J", "Automatic")}>0,{Claims("C", "K", "Accept")}>1,{Claims("E", "K", "Accept")}>1)," +
                "\"Conflict: beam already claimed\",\"Visual override\"))";
            visual.Add(new object?[] { pair.Id, pair.Level, (double)pair.Slide, pair.Etabs.StableId, pair.Etabs.DisplayName,
                pair.Revit.StableId, pair.Revit.DisplayName, m.LongerCoverage, m.ShorterCoverage, m.Angle,
                m.Lateral, m.Vertical, m.StartDifference, m.EndDifference, pair.Reason, mapping.RunId, null, null, null,
                new SpreadsheetFormula($"IF(COUNTIFS({visualKeys})=1,\"Current\",\"Stale decision key\")", "Current") });
            effective.Add(new object?[] { pair.Id, pair.Level, pair.Etabs.StableId, pair.Etabs.DisplayName, pair.Revit.StableId,
                pair.Revit.DisplayName, mapping.RunId, null, null, "Visual candidate",
                new SpreadsheetFormula(state, "Pending"), new SpreadsheetFormula(status, "Pending"),
                new SpreadsheetFormula($"IF(L{e}=\"Visual override\",\"Yes\",\"No\")", "No"), pair.Etabs.Level, pair.Revit.Level });
        }
        return (visual, effective);
    }

    private static string Criterion(string cell) => $"\"=\"&SUBSTITUTE(SUBSTITUTE(SUBSTITUTE({cell},\"~\",\"~~\"),\"*\",\"~*\"),\"?\",\"~?\")";
}
