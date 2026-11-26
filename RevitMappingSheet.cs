namespace BeamSchedulePlugin;

internal static class RevitMappingSheet
{
    internal static readonly string[] Headers = { "Record type", "ETABS Unique ID / setting", "ETABS label", "Revit reference / value",
        "Revit Excel row", "Status / reason", "Match pass", "Mapping group ID", "Review flag", "Revit Mark", "Endpoints reversed",
        "Max endpoint error (mm)", "Angle (degrees)", "Axis offset (mm)", "Lateral offset (mm)", "Vertical offset (mm)",
        "Projected segment overlap (mm)", "Longer-span coverage", "Shorter-span coverage", "Group Revit coverage",
        "Start difference / overhang (mm)", "End difference / overhang (mm)", "Maximum 3D join (mm)",
        "Maximum projected gap / join (mm)", "Maximum projected overlap (mm)",
        "ETABS I X (mm)", "ETABS I Y (mm)", "ETABS I Z (mm)", "ETABS J X (mm)", "ETABS J Y (mm)", "ETABS J Z (mm)",
        "Revit StartX_mm", "Revit StartY_mm", "Revit StartZ_mm", "Revit EndX_mm", "Revit EndY_mm", "Revit EndZ_mm",
        "Added Revit beams", "Added ETABS lines", "Added links", "Added groups", "Running Revit beams", "Running ETABS lines",
        "Revit coverage gain (percentage points)", "ETABS coverage gain (percentage points)",
        "Running Revit coverage (%)", "Running ETABS coverage (%)" };

    internal static List<object?[]> Rows(RevitMappingResult mapping)
    {
        var rows = new List<object?[]> { Headers };
        void Setting(string name, object? value) => rows.Add(new object?[] { "Setting", name, null, value });
        Setting("Revit source workbook", mapping.Revit.Filename);
        Setting("Alignment", mapping.Alignment);
        Setting("Translation direction", "RevitXYZ = ETABSXYZ + (dX, dY, dZ); no rotation or scaling");
        Setting("Translation vector from one-to-one endpoint matches (dX, dY, dZ) (mm)",
            mapping.Translation is { } vector ? FormattableString.Invariant($"({vector.X:R}, {vector.Y:R}, {vector.Z:R})") : null);
        Setting("Translation endpoint-match basis",
            mapping.Translation == null ? "Translation not established; no endpoint-match basis"
                : $"{mapping.Pairs.Count} one-to-one endpoint match(es); selected by midpoint-offset voting and endpoint scoring");
        Setting("dX (mm)", mapping.Translation?.X); Setting("dY (mm)", mapping.Translation?.Y); Setting("dZ (mm)", mapping.Translation?.Z);
        Setting("Valid ETABS lines", mapping.Etabs.Count); Setting("Valid Revit beams", mapping.Revit.Beams.Count);
        Setting("Mapped / review ETABS lines", mapping.MappedEtabsCount); Setting("Mapped / review Revit beams", mapping.MappedRevitCount);
        Setting("Segment links (not distinct beams)", mapping.Links.Count);
        Setting("Unmatched ETABS lines", mapping.Etabs.Count - mapping.MappedEtabsCount);
        Setting("Unmatched Revit beams", mapping.Revit.Beams.Count - mapping.MappedRevitCount);
        Setting("Skipped ETABS rows", mapping.SkippedEtabs.Count); Setting("Skipped Revit rows", mapping.Revit.Skipped.Count);
        Setting("Ambiguity audit records (may be resolved in a later pass)", mapping.Ambiguities.Count);
        Setting("Competing translations", mapping.CompetingTranslations.Count);
        var options = mapping.Options;
        Setting("Endpoint / strict 3D axis tolerance (mm)", options.EndpointTolerance);
        Setting("Shared strict / review angle (degrees)", options.Angle);
        Setting("Span / internal join limit (mm)", options.JoinLimit);
        Setting("Minimum ETABS segment containment", options.SegmentCoverage);
        Setting("Minimum shared span / group coverage", options.SpanCoverage);
        Setting("Shared review lateral offset (mm)", options.ReviewLateral);
        Setting("Review split vertical offset (mm)", options.SplitVertical);
        Setting("Offset / chain vertical offset (mm)", options.ReviewVertical);
        Setting("Mutual-nearest runner-up margin in both directions (mm)", options.RunnerUpMargin);
        Setting("End-extension / chain outer-end limit (mm)", options.EndLimit);
        Setting("Legacy end-extension shorter / longer coverage (subsumed by offset review)", options.SpanCoverage);
        Setting("Extended split maximum outer overhang (mm)", options.SplitOverhang);
        Setting("Chain segment link overlap must exceed (mm)", options.ChainLinkOverlap);
        void Warning(string text) => rows.Add(new object?[] { "Review warning", null, null, text });
        Warning("Geometric matching does not verify beam design, structural identity, reinforcement, or model freshness.");
        Warning("Passes 3-6 are review candidates, not strict matches. Assignments are locked after each pass; a shared group may contain multiple segment links.");
        Warning("The reference's legacy end-extension pass is subsumed by offset review and creates no separate assignments.");
        Warning("Full match means all valid lines are linked (including review candidates) and none skipped; not structural verification.");
        Warning("Translation uses the top 24 midpoint voting bins and prototype greedy endpoint scoring; it is not exhaustive registration.");
        if (mapping.Translation == null) Warning("Translation not established. No mapping checks could run; correct the inputs or coordinate alignment.");
        if (mapping.HasCompetingTranslations) Warning("Competing translations found: every resulting link requires review.");
        foreach (var competitor in mapping.CompetingTranslations)
            rows.Add(new object?[] { "Competing translation", "dX / dY / dZ (mm)", null,
                FormattableString.Invariant($"{competitor.Translation.X:R}, {competitor.Translation.Y:R}, {competitor.Translation.Z:R}"),
                null, $"Endpoint pairs: {competitor.MatchCount}; sum squared error: {competitor.SquaredEndpointError}" });
        foreach (var total in mapping.PassTotals)
        {
            var row = new object?[Headers.Length];
            row[0] = "Pass total"; row[1] = total.Name; row[6] = (double)total.Pass;
            row[37] = (double)total.AddedRevit; row[38] = (double)total.AddedEtabs; row[39] = (double)total.AddedLinks;
            row[40] = (double)total.AddedGroups; row[41] = (double)total.RunningRevit; row[42] = (double)total.RunningEtabs;
            row[43] = total.RevitGainPercentagePoints; row[44] = total.EtabsGainPercentagePoints;
            row[45] = mapping.Revit.Beams.Count == 0 ? 0 : 100d * total.RunningRevit / mapping.Revit.Beams.Count;
            row[46] = mapping.Etabs.Count == 0 ? 0 : 100d * total.RunningEtabs / mapping.Etabs.Count;
            rows.Add(row);
        }
        var ambiguitySources = mapping.Ambiguities.Where(a => a.Etabs != null).Select(a => a.Etabs!).ToHashSet();
        var ambiguityTargets = mapping.Ambiguities.Where(a => a.Revit != null).Select(a => a.Revit!).ToHashSet();
        foreach (var link in mapping.Links)
        {
            bool review = link.Review || mapping.HasCompetingTranslations || ambiguitySources.Contains(link.Etabs) || ambiguityTargets.Contains(link.Revit);
            rows.Add(BeamRow("Segment link", link.Etabs, link.Revit, link.Status, link.Pass, link.GroupId,
                review ? "REVIEW" : "Strict geometric match", link.Reversed, link.Measurements));
        }
        var usedSources = mapping.Links.Select(l => l.Etabs).ToHashSet();
        var usedTargets = mapping.Links.Select(l => l.Revit).ToHashSet();
        foreach (var beam in mapping.Etabs.Where(b => !usedSources.Contains(b)))
            rows.Add(BeamRow("Unmatched ETABS", beam, null, mapping.Translation == null ? "Translation not established" : "No accepted assignment"));
        foreach (var beam in mapping.Revit.Beams.Where(b => !usedTargets.Contains(b)))
            rows.Add(BeamRow("Unmatched Revit", null, beam, mapping.Translation == null ? "Translation not established" : "No accepted assignment"));
        foreach (var a in mapping.Ambiguities)
            rows.Add(BeamRow("Ambiguous candidate", a.Etabs, a.Revit, a.Reason, a.Pass, review: "REVIEW", metrics: a.Measurements));
        foreach (var beam in mapping.SkippedEtabs) rows.Add(new object?[] { "Skipped ETABS", beam.Reference, beam.EtabsLabel, null, beam.ExcelRow, beam.Reason });
        foreach (var beam in mapping.Revit.Skipped) rows.Add(new object?[] { "Skipped Revit", null, null, beam.Reference, beam.ExcelRow, beam.Reason, null, null, "REVIEW", beam.Mark });
        return rows;
    }

    private static object?[] BeamRow(string kind, MappingBeam? etabs, MappingBeam? revit, string status,
        int? pass = null, string? group = null, string? review = null, bool? reversed = null, MappingMeasurements? metrics = null)
    {
        var m = metrics ?? new();
        object?[] values = { kind, etabs?.Reference, etabs?.EtabsLabel, revit?.Reference, revit?.ExcelRow, status, pass == null ? null : (double)pass.Value,
            group, review, revit?.Mark, reversed == null ? null : reversed.Value ? "Yes" : "No",
            m.EndpointError, m.Angle, m.AxisOffset, m.Lateral, m.Vertical, m.Overlap, m.LongerCoverage, m.ShorterCoverage,
            m.GroupCoverage, m.StartDifference, m.EndDifference, m.MaximumJoin, m.MaximumGap, m.MaximumOverlap,
            etabs?.Start.X, etabs?.Start.Y, etabs?.Start.Z, etabs?.End.X, etabs?.End.Y, etabs?.End.Z,
            revit?.Start.X, revit?.Start.Y, revit?.Start.Z, revit?.End.X, revit?.End.Y, revit?.End.Z };
        return values;
    }
}
