namespace BeamSchedulePlugin;

internal sealed record MappingMeasurements(double? Angle = null, double? AxisOffset = null,
    double? Lateral = null, double? Vertical = null, double? Overlap = null,
    double? LongerCoverage = null, double? ShorterCoverage = null, double? GroupCoverage = null,
    double? StartDifference = null, double? EndDifference = null, double? MaximumJoin = null,
    double? MaximumGap = null, double? MaximumOverlap = null, double? EndpointError = null);

internal sealed record MappingLink(MappingBeam Etabs, MappingBeam Revit, int Pass, string GroupId,
    bool Reversed, MappingMeasurements Measurements)
{
    internal bool Review => Pass >= 3;
    internal string Status => RevitMappingPasses.PassNames[Pass - 1] + (Review ? " - review" : " - geometric match");
}

internal sealed record MappingAmbiguity(int Pass, MappingBeam? Etabs, MappingBeam? Revit, string Reason,
    MappingMeasurements? Measurements = null);

internal sealed record MappingPassTotal(int Pass, string Name, int AddedRevit, int AddedEtabs, int AddedLinks,
    int AddedGroups, int RunningRevit, int RunningEtabs, double RevitGainPercentagePoints,
    double EtabsGainPercentagePoints);

internal static class RevitMappingPasses
{
    internal const double StandardAngle = .25, WideAngle = .5, JoinLimit = 50, MinimumCoverage = .95;
    internal const double StandardLateral = 400, StandardVertical = 100, ReviewVertical = 650, RunnerUpMargin = 25;
    internal const double EndLimit = 600, ShortCoverage = .98, LongCoverage = .85, SplitOverhang = 350;
    internal const double WideLateral = 500, WideCoverage = .98, SegmentCoverage = .9, ChainLinkOverlap = 1;
    internal static readonly string[] PassNames = { "Endpoint", "Strict axis / split", "Offset review",
        "Elevation-offset review", "Split found (offset)", "End-extension review", "Wide-offset review",
        "Split found (extended)", "Chain-to-chain review" };

    private sealed record Line(BeamPoint Start, BeamPoint End)
    {
        internal BeamPoint Direction => End - Start;
        internal double Length => Math.Sqrt(Direction.SquaredLength);
        internal BeamPoint Unit => Direction / Length;
        internal BeamPoint Project(BeamPoint point) => Start + Unit * (point - Start).Dot(Unit);
    }

    private sealed record Candidate(int Source, int Target, double Start, double End, double Lo, double Hi,
        BeamPoint From, BeamPoint To, MappingMeasurements Metrics)
    {
        internal double Distance => Metrics.AxisOffset!.Value;
    }

    private sealed record Chain(List<int> Parts, Line Span, double MaximumJoin);
    private sealed record ChainCandidate(int Source, int Target, List<(int Source, int Target, double Overlap)> Links,
        MappingMeasurements Metrics);

    internal static RevitMappingResult Apply(RevitMappingResult mapping)
    {
        if (mapping.Translation is not { } translation) return mapping;
        var etabs = mapping.Etabs.Select(b => new Line(b.Start + translation, b.End + translation)).ToArray();
        var revit = mapping.Revit.Beams.Select(b => new Line(b.Start, b.End)).ToArray();
        var sourceIndex = mapping.Etabs.Select((beam, i) => (beam, i)).ToDictionary(p => p.beam, p => p.i);
        var targetIndex = mapping.Revit.Beams.Select((beam, i) => (beam, i)).ToDictionary(p => p.beam, p => p.i);
        var links = new List<MappingLink>();
        var ambiguous = new List<MappingAmbiguity>();
        var usedSources = new HashSet<int>();
        var usedTargets = new HashSet<int>();
        int groupNumber = 0;
        string Group() => $"M{++groupNumber:D5}";
        void Add(int source, int target, int pass, string group, MappingMeasurements metrics)
        {
            links.Add(new(mapping.Etabs[source], mapping.Revit.Beams[target], pass, group,
                etabs[source].Direction.Dot(revit[target].Direction) < 0, metrics));
            usedSources.Add(source);
            usedTargets.Add(target);
        }
        void Ambiguous(Candidate c, int pass, string reason) =>
            ambiguous.Add(new(pass, mapping.Etabs[c.Source], mapping.Revit.Beams[c.Target], reason, c.Metrics));
        foreach (var p in mapping.Pairs.OrderBy(p => sourceIndex[p.Etabs]))
        {
            Add(sourceIndex[p.Etabs], targetIndex[p.Revit], 1, Group(), new(EndpointError: p.MaximumEndpointError));
            if (p.Ambiguous) ambiguous.Add(new(1, p.Etabs, p.Revit,
                "Prototype endpoint assignment has multiple qualifying counterparts or orientations; deterministic proposal requires review."));
        }

        double cosLimit = Math.Cos(StandardAngle * Math.PI / 180), cosWide = Math.Cos(WideAngle * Math.PI / 180);
        var strict = new List<Candidate>();
        var offsets = new List<Candidate>();
        var extensions = new List<Candidate>();
        var wide = new List<Candidate>();
        var splits = new List<Candidate>();
        for (int i = 0; i < etabs.Length; i++)
        {
            if (usedSources.Contains(i)) continue;
            for (int j = 0; j < revit.Length; j++)
            {
                if (usedTargets.Contains(j)) continue;
                var a = etabs[i]; var b = revit[j];
                double cos = Math.Abs(a.Unit.Dot(b.Unit));
                if (cos < cosWide) continue;
                bool standardDirection = cos >= cosLimit;
                var u = a.Start - b.Start; var v = a.End - b.Start; var axis = b.Unit;
                double x = u.Dot(axis), y = v.Dot(axis), lo = Math.Min(x, y), hi = Math.Max(x, y);
                double start = Math.Max(0, lo), end = Math.Min(b.Length, hi), overlap = end - start;
                if (overlap <= 0) continue;
                var p = u - axis * x; var q = v - axis * y;
                double axisError2 = Math.Max(p.SquaredLength, q.SquaredLength);
                double coverage = overlap / Math.Max(a.Length, b.Length);
                double lateral2 = Math.Max(p.X * p.X + p.Y * p.Y, q.X * q.X + q.Y * q.Y);
                double vertical = Math.Max(Math.Abs(p.Z), Math.Abs(q.Z));
                var metrics = new MappingMeasurements(Math.Acos(Math.Min(1, cos)) * 180 / Math.PI,
                    Math.Sqrt(axisError2), Math.Sqrt(lateral2), vertical, overlap, coverage,
                    overlap / Math.Min(a.Length, b.Length), StartDifference: Math.Abs(lo), EndDifference: Math.Abs(hi - b.Length));
                var c = new Candidate(i, j, start, end, lo, hi, x <= y ? a.Start : a.End, x <= y ? a.End : a.Start, metrics);
                if (standardDirection && axisError2 <= RevitBeamMatcher.Tolerance * RevitBeamMatcher.Tolerance
                    && lo >= -JoinLimit && hi <= b.Length + JoinLimit && overlap >= SegmentCoverage * a.Length)
                    strict.Add(c);
                if (standardDirection && lateral2 <= StandardLateral * StandardLateral && vertical <= StandardVertical
                    && lo >= -SplitOverhang && hi <= b.Length + SplitOverhang && overlap >= SegmentCoverage * a.Length)
                    splits.Add(c);
                bool standardOffset = standardDirection && lateral2 <= StandardLateral * StandardLateral;
                bool wideEligible = coverage >= WideCoverage && lateral2 <= WideLateral * WideLateral;
                if (vertical > ReviewVertical || !wideEligible && (!standardOffset || coverage < LongCoverage)) continue;
                bool extensionEligible = standardOffset && coverage >= LongCoverage && metrics.ShorterCoverage >= ShortCoverage
                    && metrics.StartDifference <= EndLimit && metrics.EndDifference <= EndLimit;
                if (standardOffset && coverage >= MinimumCoverage) offsets.Add(c);
                if (extensionEligible) extensions.Add(c);
                if (wideEligible) wide.Add(c);
            }
        }

        var competingSources = strict.GroupBy(p => p.Source).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var competingTargets = strict.Where(p => competingSources.Contains(p.Source)).Select(p => p.Target).ToHashSet();
        foreach (var c in strict.Where(p => competingTargets.Contains(p.Target)))
            Ambiguous(c, 2, "Strict-axis target excluded because an ETABS segment has competing targets.");
        foreach (var group in strict.Where(p => !competingTargets.Contains(p.Target)).GroupBy(p => p.Target).OrderBy(g => g.Key))
        {
            var parts = group.OrderBy(p => p.Start).ThenBy(p => p.End).ToArray();
            double end = 0, covered = 0, gap = 0, overlap = 0;
            foreach (var part in parts)
            {
                gap = Math.Max(gap, part.Start - end);
                overlap = Math.Max(overlap, Math.Min(end, part.End) - part.Start);
                covered += Math.Max(0, part.End - Math.Max(end, part.Start));
                end = Math.Max(end, part.End);
            }
            double coverage = covered / revit[group.Key].Length;
            double maximumGap = Math.Max(gap, revit[group.Key].Length - end);
            if (maximumGap > JoinLimit || overlap > JoinLimit || coverage < MinimumCoverage) continue;
            string id = Group();
            foreach (var p in parts) Add(p.Source, p.Target, 2, id,
                p.Metrics with { GroupCoverage = coverage, MaximumGap = maximumGap, MaximumOverlap = overlap });
        }

        bool Clear(Candidate[] nearest) => nearest.Length == 1 || nearest[1].Distance - nearest[0].Distance >= RunnerUpMargin;
        void Reviews(IEnumerable<Candidate> candidates, Func<Candidate, bool> canAdd, int pass)
        {
            var available = candidates.Where(p => !usedSources.Contains(p.Source) && !usedTargets.Contains(p.Target)).ToArray();
            var bySource = available.GroupBy(p => p.Source).OrderBy(g => g.Key)
                .Select(g => g.OrderBy(p => p.Distance).ThenBy(p => p.Target).Take(2).ToArray()).ToArray();
            var byTarget = available.GroupBy(p => p.Target).ToDictionary(g => g.Key,
                g => g.OrderBy(p => p.Distance).ThenBy(p => p.Source).Take(2).ToArray());
            foreach (var nearest in bySource)
            {
                var p = nearest[0];
                var reverse = byTarget[p.Target];
                if (!canAdd(p)) continue;
                if (reverse[0].Source != p.Source || !Clear(nearest) || !Clear(reverse))
                {
                    foreach (var c in available.Where(c => c.Source == p.Source || c.Target == p.Target))
                        Ambiguous(c, pass, "Not mutual-nearest or runner-up margin is below 25 mm in at least one direction.");
                    continue;
                }
                Add(p.Source, p.Target, pass, Group(), p.Metrics);
            }
        }
        Reviews(offsets.Where(p => p.Metrics.Vertical <= StandardVertical), _ => true, 3);
        Reviews(offsets, p => p.Metrics.Vertical > StandardVertical, 4);

        void Split(bool extended)
        {
            double outerLimit = extended ? SplitOverhang : JoinLimit;
            var groups = splits.Where(p => !usedSources.Contains(p.Source) && !usedTargets.Contains(p.Target)
                    && p.Lo >= -outerLimit && p.Hi <= revit[p.Target].Length + outerLimit)
                .GroupBy(p => p.Target).OrderBy(g => g.Key)
                .Select(g => g.OrderBy(p => p.Start).ThenBy(p => p.End).ToArray()).Where(parts => parts.Length > 1)
                .Select(parts =>
                {
                    double length = revit[parts[0].Target].Length, covered = parts[0].End - parts[0].Start;
                    double maxJoin = 0, maxGap = 0;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var previous = parts[i - 1]; var current = parts[i];
                        double join = extended ? current.Lo - previous.Hi : current.Start - previous.End;
                        double distance = Math.Sqrt((previous.To - current.From).SquaredLength);
                        maxJoin = Math.Max(maxJoin, distance); maxGap = Math.Max(maxGap, Math.Abs(join));
                        if (current.End <= previous.End || Math.Abs(join) > JoinLimit || distance > JoinLimit
                            || extended && (current.Lo < 0 || previous.Hi > length))
                            return (Parts: parts, Coverage: 0d, Start: 0d, End: 0d, Join: maxJoin, Gap: maxGap);
                        covered += current.End - Math.Max(previous.End, current.Start);
                    }
                    return (Parts: parts, Coverage: covered / length, Start: Math.Max(0, -parts[0].Lo),
                        End: Math.Max(0, parts[^1].Hi - length), Join: maxJoin, Gap: maxGap);
                }).Where(g => g.Coverage >= MinimumCoverage).ToArray();
            var shared = groups.SelectMany(g => g.Parts).GroupBy(p => p.Source).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var g in groups)
            {
                int pass = extended ? 8 : 5;
                if (g.Parts.Any(p => shared.Contains(p.Source)))
                {
                    foreach (var p in g.Parts) Ambiguous(p, pass, "Split groups compete for the same ETABS segment.");
                    continue;
                }
                if (extended && Math.Max(g.Start, g.End) <= JoinLimit) continue;
                string id = Group();
                foreach (var p in g.Parts) Add(p.Source, p.Target, pass, id, p.Metrics with
                { GroupCoverage = g.Coverage, StartDifference = g.Start, EndDifference = g.End, MaximumJoin = g.Join, MaximumGap = g.Gap });
            }
        }
        Split(false);
        Reviews(offsets.Concat(extensions.Where(p => p.Metrics.LongerCoverage < MinimumCoverage)),
            p => p.Metrics.LongerCoverage < MinimumCoverage, 6);
        Reviews(wide, p => p.Metrics.Lateral > StandardLateral
            || Math.Abs(etabs[p.Source].Unit.Dot(revit[p.Target].Unit)) < cosLimit, 7);
        Split(true);

        var sourceChains = BuildChains(etabs, usedSources, cosWide, (indices, reason) =>
        {
            foreach (int i in indices) ambiguous.Add(new(9, mapping.Etabs[i], null, reason));
        });
        var targetChains = BuildChains(revit, usedTargets, cosWide, (indices, reason) =>
        {
            foreach (int i in indices) ambiguous.Add(new(9, null, mapping.Revit.Beams[i], reason));
        });
        var chainCandidates = new List<ChainCandidate>();
        for (int i = 0; i < sourceChains.Count; i++)
        for (int j = 0; j < targetChains.Count; j++)
        {
            var source = sourceChains[i]; var target = targetChains[j]; var axis = target.Span;
            double cos = Math.Abs(source.Span.Unit.Dot(axis.Unit));
            if (cos < cosWide) continue;
            var span = Project(source.Span, axis);
            double overlap = Math.Min(span.Hi, axis.Length) - Math.Max(span.Lo, 0);
            double coverage = overlap / Math.Max(source.Span.Length, axis.Length);
            if (coverage < WideCoverage || Math.Abs(span.Lo) > EndLimit || Math.Abs(span.Hi - axis.Length) > EndLimit) continue;
            double lateral = 0, vertical = 0;
            void Measure(BeamPoint point, Line reference)
            {
                var delta = point - reference.Project(point);
                lateral = Math.Max(lateral, Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y));
                vertical = Math.Max(vertical, Math.Abs(delta.Z));
            }
            foreach (int k in source.Parts) { Measure(etabs[k].Start, axis); Measure(etabs[k].End, axis); }
            foreach (int k in target.Parts) { Measure(revit[k].Start, source.Span); Measure(revit[k].End, source.Span); }
            if (lateral > WideLateral || vertical > ReviewVertical) continue;
            var segmentLinks = new List<(int Source, int Target, double Overlap)>();
            foreach (int s in source.Parts)
            foreach (int r in target.Parts)
            {
                var a = Project(etabs[s], axis); var b = Project(revit[r], axis);
                double shared = Math.Min(a.Hi, b.Hi) - Math.Max(a.Lo, b.Lo);
                if (shared > ChainLinkOverlap) segmentLinks.Add((s, r, shared));
            }
            if (segmentLinks.Select(p => p.Source).Distinct().Count() != source.Parts.Count
                || segmentLinks.Select(p => p.Target).Distinct().Count() != target.Parts.Count) continue;
            chainCandidates.Add(new(i, j, segmentLinks, new(Math.Acos(Math.Min(1, cos)) * 180 / Math.PI,
                Lateral: lateral, Vertical: vertical, Overlap: overlap, LongerCoverage: coverage,
                StartDifference: Math.Abs(span.Lo), EndDifference: Math.Abs(span.Hi - axis.Length),
                MaximumJoin: Math.Max(source.MaximumJoin, target.MaximumJoin))));
        }
        var uniqueSources = chainCandidates.GroupBy(p => p.Source).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        var uniqueTargets = chainCandidates.GroupBy(p => p.Target).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        foreach (var c in chainCandidates)
        {
            if (!uniqueSources.Contains(c.Source) || !uniqueTargets.Contains(c.Target))
            {
                foreach (var p in c.Links) ambiguous.Add(new(9, mapping.Etabs[p.Source], mapping.Revit.Beams[p.Target],
                    "Competing chain pairing rejected.", c.Metrics));
                continue;
            }
            string id = Group();
            foreach (var p in c.Links) Add(p.Source, p.Target, 9, id, c.Metrics with { Overlap = p.Overlap });
        }
        int runningSources = 0, runningTargets = 0;
        var totals = Enumerable.Range(1, 9).Select(pass =>
        {
            var added = links.Where(p => p.Pass == pass).ToArray();
            int sources = added.Select(p => p.Etabs).Distinct().Count(), targets = added.Select(p => p.Revit).Distinct().Count();
            runningSources += sources; runningTargets += targets;
            return new MappingPassTotal(pass, PassNames[pass - 1], targets, sources, added.Length,
                added.Select(p => p.GroupId).Distinct().Count(), runningTargets, runningSources,
                revit.Length == 0 ? 0 : 100d * targets / revit.Length, etabs.Length == 0 ? 0 : 100d * sources / etabs.Length);
        }).ToArray();
        return mapping with { Links = links, Ambiguities = mapping.Ambiguities.Concat(ambiguous).Distinct().ToArray(), PassTotals = totals };
    }

    private static (double Lo, double Hi) Project(Line part, Line axis)
    {
        double a = (part.Start - axis.Start).Dot(axis.Unit), b = (part.End - axis.Start).Dot(axis.Unit);
        return (Math.Min(a, b), Math.Max(a, b));
    }

    private static List<Chain> BuildChains(Line[] lines, HashSet<int> used, double cosLimit, Action<int[], string> report)
    {
        BeamPoint End(int id) => id % 2 == 0 ? lines[id / 2].Start : lines[id / 2].End;
        var ends = Enumerable.Range(0, 2 * lines.Length).Where(id => !used.Contains(id / 2)).ToArray();
        var neighbors = Enumerable.Repeat(-1, 2 * lines.Length).ToArray();
        for (int i = 0; i < ends.Length; i++)
        for (int j = i + 1; j < ends.Length; j++)
        {
            int a = ends[i], b = ends[j];
            if (a / 2 == b / 2 || (End(a) - End(b)).SquaredLength > JoinLimit * JoinLimit) continue;
            var incoming = End(a) - End(a ^ 1); incoming /= Math.Sqrt(incoming.SquaredLength);
            var outgoing = End(b ^ 1) - End(b); outgoing /= Math.Sqrt(outgoing.SquaredLength);
            if (incoming.Dot(outgoing) < cosLimit || Math.Abs((End(b) - End(a)).Dot(incoming)) > JoinLimit) continue;
            neighbors[a] = neighbors[a] == -1 ? b : -2;
            neighbors[b] = neighbors[b] == -1 ? a : -2;
        }
        var branched = ends.Where(id => neighbors[id] == -2).Select(id => id / 2).Distinct().ToArray();
        if (branched.Length > 0) report(branched, "Multiple eligible chain neighbors; chain inference is ambiguous.");
        var chains = new List<Chain>();
        var visited = new HashSet<int>();
        foreach (int start in ends)
        {
            if (neighbors[start] != -1 || visited.Contains(start / 2)) continue;
            var parts = new List<int>(); var directions = new List<BeamPoint>();
            int current = start; double maxJoin = 0;
            while (visited.Add(current / 2))
            {
                parts.Add(current / 2);
                var direction = End(current ^ 1) - End(current);
                directions.Add(direction / Math.Sqrt(direction.SquaredLength));
                int next = neighbors[current ^ 1];
                if (next == -1)
                {
                    var span = new Line(End(start), End(current ^ 1));
                    if (parts.Count > 1 && span.Length > 0 && directions.All(d => d.Dot(span.Unit) >= cosLimit))
                        chains.Add(new(parts, span, maxJoin));
                    break;
                }
                if (next < 0 || neighbors[next] != (current ^ 1)) break;
                maxJoin = Math.Max(maxJoin, Math.Sqrt((End(next) - End(current ^ 1)).SquaredLength));
                current = next;
            }
        }
        return chains;
    }
}
