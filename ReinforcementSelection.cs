namespace BeamSchedulePlugin;

internal sealed record ReinforcementOptions(
    double Cover, int[] BarDiameters, int MaxLayers, bool ClearSpacing,
    double HorizontalSpacing, double VerticalSpacing, int[] LinkDiameters,
    int[] LinkLegs, int MinimumLinkSpacing, int MaximumLinkSpacing, int LinkSpacingStep);

internal sealed record BarProposal(string Description, double Area, double Depth, int FirstLayerCount);
internal sealed record LinkProposal(string Description, double Area, int Diameter);
internal sealed record ZoneProposal(BarProposal Top, BarProposal Bottom, LinkProposal Link);

internal static class ReinforcementSelection
{
    internal static ZoneProposal? Select(double top, double bottom, double shear,
        double width, double height, ReinforcementOptions options)
    {
        foreach (int linkDiameter in options.LinkDiameters)
        {
            BarProposal? topBars = SelectBars(top, width, linkDiameter, options);
            BarProposal? bottomBars = SelectBars(bottom, width, linkDiameter, options);
            if (topBars == null || bottomBars == null) continue;
            double requiredHeight = 2 * (options.Cover + linkDiameter)
                + topBars.Depth + bottomBars.Depth + options.VerticalSpacing;
            if (requiredHeight > height + 1e-9) continue;
            foreach (int legs in options.LinkLegs)
            {
                if (legs > Math.Min(topBars.FirstLayerCount, bottomBars.FirstLayerCount)) continue;
                for (int spacing = options.MaximumLinkSpacing; spacing >= options.MinimumLinkSpacing;
                    spacing -= options.LinkSpacingStep)
                {
                    double area = legs * Math.PI * linkDiameter * linkDiameter / 4 * 1000 / spacing;
                    if (area < shear) continue;
                    string description = $"H{linkDiameter}-{spacing} (Link)";
                    if (legs > 2) description += $" + {legs - 2}H{linkDiameter}-{spacing} (Hooks)";
                    return new ZoneProposal(topBars, bottomBars, new LinkProposal(description, area, linkDiameter));
                }
            }
        }
        return null;
    }

    private static BarProposal? SelectBars(double demand, double width, int linkDiameter, ReinforcementOptions options)
    {
        int firstMinimum = Math.Max(2, (int)Math.Round(.0051 * width + 1.1118));
        var counts = new List<int>();
        var diameters = new List<int>();
        for (int layer = 0; layer < options.MaxLayers; layer++)
        {
            counts.Add(0);
            diameters.Add(0);
            int lastCount = 0, lastDiameter = 0;
            foreach (int diameter in options.BarDiameters)
            {
                diameters[layer] = diameter;
                int largest = diameters.Max();
                double available = width - 2 * (options.Cover + linkDiameter) - largest;
                double pitch = options.ClearSpacing ? options.HorizontalSpacing + largest : Math.Max(options.HorizontalSpacing, largest);
                int capacity = available < 0 ? 0 : (int)Math.Min(500, Math.Floor(available / pitch + 1 + 1e-9));
                if (counts.Take(layer).Any(count => count > capacity)) continue;
                int minimum = layer == 0 ? firstMinimum : 2;
                for (int count = minimum; count <= capacity; count++)
                {
                    if (layer > 0 && count != 2 && count % 2 != counts[0] % 2) continue;
                    counts[layer] = count;
                    double area = 0;
                    for (int i = 0; i <= layer; i++) area += counts[i] * Math.PI * diameters[i] * diameters[i] / 4;
                    if (area < demand) continue;
                    double depth = diameters.Sum() + layer * options.VerticalSpacing;
                    string description = string.Join("+", counts.Select((n, i) => $"{n}H{diameters[i]}"));
                    return new BarProposal(description, area, depth, counts[0]);
                }
                counts[layer] = capacity;
                if (layer > 0 && capacity > 2 && capacity % 2 != counts[0] % 2) counts[layer]--;
                if (counts[layer] >= minimum)
                {
                    lastCount = counts[layer];
                    lastDiameter = diameter;
                }
            }
            if (lastCount == 0) return null;
            counts[layer] = lastCount;
            diameters[layer] = lastDiameter;
        }
        return null;
    }
}
