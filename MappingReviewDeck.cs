using System.IO.Compression;
using System.Xml.Linq;

namespace BeamSchedulePlugin;

internal static class MappingReviewDeck
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const int Width = 2304, Height = 1296;
    private const string Navy = "10253C", White = "FFFFFF", Cyan = "A5D6EB";
    private const string PanelTint = "F3F7FA", Border = "CCD8E3", Muted = "455E73";
    private const string Green = "16755F", Gray = "526676", Magenta = "A51A77", ReviewTint = "F8EEF5";

    internal static void Write(Stream stream, MappingReview review)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var overrides = new List<XElement>();
        void WritePart(string name, XElement xml)
        {
            using var part = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            xml.Save(part, SaveOptions.DisableFormatting);
        }
        void Part(string name, string kind, XElement xml)
        {
            string contentType = kind == "theme" ? "application/vnd.openxmlformats-officedocument.theme+xml"
                : $"application/vnd.openxmlformats-officedocument.presentationml.{kind}+xml";
            overrides.Add(new(Types + "Override", new XAttribute("PartName", "/" + name), new XAttribute("ContentType", contentType)));
            WritePart(name, xml);
        }
        void Relationships(string name, params (string Type, string Target)[] relationships) =>
            WritePart(name, new XElement(Rel + "Relationships", relationships.Select((rel, i) => new XElement(Rel + "Relationship",
                new XAttribute("Id", $"rId{i + 1}"), new XAttribute("Type", R.NamespaceName + "/" + rel.Type), new XAttribute("Target", rel.Target)))));

        Relationships("_rels/.rels", ("officeDocument", "ppt/presentation.xml"));
        Part("ppt/presentation.xml", "presentation.main", new XElement(P + "presentation",
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", "2147483648"), Rid(1))),
            review.Levels.Count == 0 ? null : new XElement(P + "sldIdLst", review.Levels.Select((_, i) =>
                new XElement(P + "sldId", new XAttribute("id", i + 256), Rid(i + 2)))),
            new XElement(P + "sldSz", new XAttribute("cx", Emu(Width)), new XAttribute("cy", Emu(Height))),
            new XElement(P + "notesSz", new XAttribute("cx", "6858000"), new XAttribute("cy", "9144000"))));
        Relationships("ppt/_rels/presentation.xml.rels", new[] { ("slideMaster", "slideMasters/slideMaster1.xml") }
            .Concat(review.Levels.Select((_, i) => ("slide", $"slides/slide{i + 1}.xml"))).ToArray());
        Part("ppt/slideMasters/slideMaster1.xml", "slideMaster", new XElement(P + "sldMaster",
            new XElement(P + "cSld", ShapeTree()), ColorMap(),
            new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", "2147483649"), Rid(1))),
            new XElement(P + "txStyles", new XElement(P + "titleStyle"), new XElement(P + "bodyStyle"), new XElement(P + "otherStyle"))));
        Relationships("ppt/slideMasters/_rels/slideMaster1.xml.rels", ("slideLayout", "../slideLayouts/slideLayout1.xml"),
            ("theme", "../theme/theme1.xml"));
        Part("ppt/slideLayouts/slideLayout1.xml", "slideLayout", new XElement(P + "sldLayout", new XAttribute("type", "blank"),
            new XElement(P + "cSld", ShapeTree()), MapOverride()));
        Relationships("ppt/slideLayouts/_rels/slideLayout1.xml.rels", ("slideMaster", "../slideMasters/slideMaster1.xml"));
        Part("ppt/theme/theme1.xml", "theme", Theme());
        for (int i = 0; i < review.Levels.Count; i++)
        {
            Part($"ppt/slides/slide{i + 1}.xml", "slide", Slide(review, i));
            Relationships($"ppt/slides/_rels/slide{i + 1}.xml.rels", ("slideLayout", "../slideLayouts/slideLayout1.xml"));
        }
        WritePart("[Content_Types].xml", new XElement(Types + "Types",
            new XElement(Types + "Default", new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(Types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")), overrides));
    }

    private static XElement Slide(MappingReview review, int index)
    {
        var mapping = review.Mapping;
        var level = review.Levels[index];
        var pairs = review.Pairs.Where(p => p.Slide == index + 1).ToArray();
        var revitMatched = mapping.Links.Select(l => l.Revit).ToHashSet();
        var etabsMatched = mapping.Links.Select(l => l.Etabs).ToHashSet();
        var revitReview = pairs.ToLookup(p => p.Revit, p => p.Id);
        var etabsReview = pairs.ToLookup(p => p.Etabs, p => p.Id);
        var translation = mapping.Translation.GetValueOrDefault();
        var points = level.Revit.SelectMany(b => new[] { b.Start, b.End })
            .Concat(level.Etabs.SelectMany(b => new[] { b.Start + translation, b.End + translation })).ToArray();
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        const double panelWidth = 1102, panelTop = 179, panelHeaderHeight = 49, fullPanelBottom = 1183;
        const int queueColumns = 4, maxQueueRows = 10;
        const double queueRowHeight = 27, queueHeaderHeight = 30;
        int shown = Math.Min(pairs.Length, queueColumns * maxQueueRows);
        int queueRows = (shown + queueColumns - 1) / queueColumns;
        double panelBottom = shown == 0 ? fullPanelBottom : fullPanelBottom - queueHeaderHeight - queueRows * queueRowHeight;
        const double plotWidth = 1021;
        double plotHeight = panelBottom - panelTop - panelHeaderHeight - 127;
        double scale = Math.Min(plotWidth / Math.Max(1, maxX - minX), plotHeight / Math.Max(1, maxY - minY));
        var shapes = ShapeTree();
        int id = 1;
        void Text(double x, double y, double w, double h, string text, string color, double size,
            bool bold = false, string align = "l", bool fit = false) =>
            shapes.Add(Shape(++id, "Text", x, y, w, h, "rect", new XElement(A + "noFill"), null,
                TextBody(text, color, size, bold, align, fit)));
        void PanelShape(string name, double x, double y, double w, double h, string color, string? border = null) =>
            shapes.Add(Shape(++id, name, x, y, w, h, "rect", Fill(color), border == null ? null : Stroke(border, 1.5)));
        void LegendLine(double x, string text, string color)
        {
            shapes.Add(Shape(++id, $"{text} legend swatch", x, 153, 29, 0, "line", null, Stroke(color, 3)));
            Text(x + 40, 140, 320, 26, text, color, 17, bold: true);
        }
        void Plot(IReadOnlyList<MappingBeam> beams, HashSet<MappingBeam> matched, ILookup<MappingBeam, string> markers,
            double left, string title, BeamPoint delta)
        {
            int matchedCount = beams.Count(matched.Contains);
            PanelShape($"{title} panel", left, panelTop, panelWidth, panelBottom - panelTop, White, Border);
            PanelShape($"{title} panel header", left, panelTop, panelWidth, panelHeaderHeight, PanelTint, Border);
            Text(left + 14, 188, 590, 28, title, Navy, 20, bold: true);
            Text(left + 625, 188, 462, 28, $"{matchedCount} matched   |   {beams.Count - matchedCount} unmatched",
                Muted, 16, align: "r");
            if (beams.Count == 0)
            {
                Text(left, (panelTop + panelHeaderHeight + panelBottom - 34) / 2,
                    panelWidth, 34, "No beams on this level", Muted, 22, align: "ctr");
                return;
            }
            (double X, double Y) At(BeamPoint p) => (left + (panelWidth - (maxX - minX) * scale) / 2 + (p.X - minX) * scale,
                (panelTop + panelHeaderHeight + panelBottom - (maxY - minY) * scale) / 2 + (maxY - p.Y) * scale);
            foreach (var b in beams.OrderBy(matched.Contains))
            {
                var (x1, y1) = At(b.Start + delta); var (x2, y2) = At(b.End + delta);
                bool isMatched = matched.Contains(b);
                shapes.Add(Shape(++id, b.DisplayName, Math.Min(x1, x2), Math.Min(y1, y2),
                    Math.Abs(x2 - x1), Math.Abs(y2 - y1), "line", null, Stroke(isMatched ? Green : Gray, isMatched ? 1.8 : 1.2),
                    flip: (x2 - x1) * (y2 - y1) < 0));
            }
            foreach (var b in beams)
            {
                var (x, y) = At(b.Midpoint + delta);
                Text(x - 75, y - 15, 150, 13, b.DisplayName, Navy, 9, align: "ctr");
            }
            foreach (var b in beams)
            {
                var ids = markers[b].ToArray();
                if (ids.Length == 0) continue;
                var (x, y) = At(b.Midpoint + delta);
                shapes.Add(Shape(++id, "Visual review", x - 8, y - 8, 16, 16, "ellipse",
                    new XElement(A + "noFill"), Stroke(Magenta, 1.8)));
                bool placeLeft = x + 80 > left + panelWidth;
                double labelTop = Math.Min(y, Math.Max(panelTop + panelHeaderHeight, panelBottom - 16d * ids.Length));
                for (int i = 0; i < ids.Length; i++)
                    Text(placeLeft ? x - 80 : x + 10, labelTop + i * 16, 70, 16, ids[i], Magenta, 12,
                        align: placeLeft ? "r" : "l");
            }
        }
        PanelShape("White background", 0, 0, Width, Height, White);
        PanelShape("Navy masthead", 0, 0, Width, 127, Navy);
        Text(40, 16, 1500, 23, $"BEAM MAPPING  /  LEVEL {index + 1:D2} OF {review.Levels.Count:D2}", Cyan, 15, bold: true);
        Text(40, 43, 1600, 60, level.Name, White, 46, bold: true);
        Text(1735, 53, 529, 38, $"{pairs.Length} VISUAL CANDIDATE{(pairs.Length == 1 ? "" : "S")}",
            White, 20, bold: true, align: "r");
        LegendLine(43, "Matched", Green);
        LegendLine(490, "Unmatched", Gray);
        shapes.Add(Shape(++id, "Visual review legend marker", 1057, 145, 16, 16, "ellipse",
            new XElement(A + "noFill"), Stroke(Magenta, 1.5)));
        Text(1096, 140, 470, 26, "Circled: visual candidate", Magenta, 17, bold: true);
        Text(1647, 140, 617, 26, "XY PLAN  /  +Y UP  /  SAME SCALE", Muted, 15, bold: true, align: "r");
        Plot(level.Revit, revitMatched, revitReview, 40, "REVIT  /  MARK", default);
        Plot(level.Etabs, etabsMatched, etabsReview, 1162,
            mapping.Translation == null ? "ETABS  /  BEAM LABEL  (UNALIGNED)" : "ETABS  /  BEAM LABEL", translation);
        if (shown != 0)
        {
            Text(42, panelBottom + 2, 2220, 25, "VISUAL QUEUE  /  ID, BEAM NAMES, LONGER-SPAN OVERLAP",
                Magenta, 16, bold: true);
            for (int i = 0; i < shown; i++)
            {
                var pair = pairs[i];
                const double tileWidth = 551, columnPitch = 558;
                double x = 40 + i % queueColumns * columnPitch;
                double y = panelBottom + queueHeaderHeight + i / queueColumns * queueRowHeight;
                PanelShape("Visual queue entry", x, y, tileWidth, queueRowHeight - 3, ReviewTint);
                string names = $"{pair.Revit.DisplayName} / {pair.Etabs.DisplayName}";
                Text(x + 8, y, 80, 24, pair.Id, Magenta, 14, bold: true);
                Text(x + 94, y, 360, 24, names, Magenta, 14, fit: true);
                Text(x + 458, y, 83, 24, FormattableString.Invariant($"{pair.Metrics.LongerCoverage:0.00%}"),
                    Magenta, 14, bold: true, align: "r");
            }
        }
        string guidance = pairs.Length == 0
            ? "No unresolved same-level visual candidates in this run on this level."
            : shown == pairs.Length
                ? "The same VR ID marks both beams. Record decisions in Excel > Visual Review."
                : $"Showing {shown} of {pairs.Length} candidates. See Excel > Visual Review for the full list and decisions.";
        Text(42, 1205, 2220, 30, $"Zoom to read dense labels. {guidance}", Muted, 17);
        Text(42, 1248, 2220, 26, $"Run {mapping.RunId}   |   {mapping.Alignment}   |   Geometric matches are not design verification.",
            Muted, 15);
        return new XElement(P + "sld", new XElement(P + "cSld", shapes), MapOverride());
    }

    private static long Emu(double points) => (long)Math.Round(points * 12700);
    private static XAttribute Rid(int id) => new(R + "id", $"rId{id}");
    private static XElement MapOverride() => new(P + "clrMapOvr", new XElement(A + "masterClrMapping"));
    private static XElement ShapeTree() => new(P + "spTree", new XElement(P + "nvGrpSpPr",
        new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"),
        new XElement(P + "nvPr")), new XElement(P + "grpSpPr"));
    private static XElement Fill(string color) => new(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color)));
    private static XElement Stroke(string color, double width) => new(A + "ln", new XAttribute("w", Emu(width)), Fill(color));
    private static XElement Shape(int id, string name, double x, double y, double w, double h, string geometry,
        XElement? fill, XElement? stroke, XElement? text = null, bool flip = false) =>
        new(P + "sp", new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
            new XElement(P + "cNvSpPr", text == null ? null : new XAttribute("txBox", "1")), new XElement(P + "nvPr")),
            new XElement(P + "spPr", new XElement(A + "xfrm", flip ? new XAttribute("flipV", "1") : null,
                new XElement(A + "off", new XAttribute("x", Emu(x)), new XAttribute("y", Emu(y))),
                new XElement(A + "ext", new XAttribute("cx", Emu(w)), new XAttribute("cy", Emu(h)))),
                new XElement(A + "prstGeom", new XAttribute("prst", geometry), new XElement(A + "avLst")), fill, stroke), text);

    private static XElement TextBody(string text, string color, double size, bool bold, string align, bool fit) => new(P + "txBody",
        new XElement(A + "bodyPr", new XAttribute("wrap", fit ? "square" : "none"), new XAttribute("lIns", "0"), new XAttribute("rIns", "0"),
            new XAttribute("tIns", "0"), new XAttribute("bIns", "0"), new XAttribute("anchor", "ctr"),
            fit ? new XElement(A + "normAutofit") : null), new XElement(A + "lstStyle"),
        new XElement(A + "p", new XElement(A + "pPr", new XAttribute("algn", align)),
            new XElement(A + "r", new XElement(A + "rPr", new XAttribute("sz", (int)(size * 100)),
                bold ? new XAttribute("b", "1") : null, Fill(color),
                new XElement(A + "latin", new XAttribute("typeface", "Arial"))), new XElement(A + "t", text))));

    private static XElement ColorMap() => new(P + "clrMap",
        "bg1:lt1 tx1:dk1 bg2:lt2 tx2:dk2 accent1:accent1 accent2:accent2 accent3:accent3 accent4:accent4 accent5:accent5 accent6:accent6 hlink:hlink folHlink:folHlink"
            .Split(' ').Select(s => s.Split(':')).Select(p => new XAttribute(p[0], p[1])));

    private static XElement Theme()
    {
        XElement Named(string tag, string name, params object[] content) => new(A + tag, new XAttribute("name", name), content);
        XElement Font(string tag) => new(A + tag, new[] { "latin", "ea", "cs" }.Select(t =>
            new XElement(A + t, new XAttribute("typeface", t == "latin" ? "Arial" : ""))));
        return Named("theme", "Mapping Review", new XElement(A + "themeElements",
            Named("clrScheme", "Mapping Review", $"dk1:{Navy} lt1:{White} dk2:{Muted} lt2:{PanelTint} accent1:{Green} accent2:{Magenta} accent3:{Gray} accent4:365B94 accent5:A85518 accent6:457C89 hlink:0563C1 folHlink:954F72"
                .Split(' ').Select(s => s.Split(':')).Select(p => new XElement(A + p[0], new XElement(A + "srgbClr", new XAttribute("val", p[1]))))),
            Named("fontScheme", "Arial", Font("majorFont"), Font("minorFont")),
            Named("fmtScheme", "Mapping Review",
                new XElement(A + "fillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill("FFFFFF"))),
                new XElement(A + "lnStyleLst", Enumerable.Range(0, 3).Select(_ => Stroke(Navy, 1))),
                new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                new XElement(A + "bgFillStyleLst", Enumerable.Range(0, 3).Select(_ => Fill("FFFFFF"))))));
    }
}
