using CadToCode.Business.Interfaces;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services;

public class ComponentDecomposer : IComponentDecomposer
{
    private const double ProximityFraction = 0.005;
    private const double LabelReachFraction = 0.08;
    private const double SpanningFraction = 0.6;
    private const int SpanningMinItems = 12;
    private const int MaxLabelLength = 40;
    private const double LinearAspectLimit = 0.02;

    private static readonly string[] IgnoredMarkers =
    {
        "center", "centre", "hidden", "phantom", "dash", "dim", "defpoints"
    };

    private const double TitleBlockMaxAreaFraction = 0.25;
    private const double TitleBlockLinkFraction = 0.05;
    private const double SheetBorderFraction = 0.9;
    private const double SheetBorderEdgeFraction = 0.06;

    private static readonly string[] TitleBlockLayerMarkers =
    {
        "titleblock", "title_block", "title-block", "title block", "tblock", "revision", "rev_table", "rev table", "stamp", "viewport"
    };

    private const double PeripheralZoneFraction = 0.28;
    private const int PeripheralMinTexts = 4;
    private const int PeripheralMinRows = 3;
    private const double PeripheralLinkHeightFactor = 6.0;

    private static readonly string[] TitleBlockKeywords =
    {
        "date", "drawn", "drawn by", "checked", "checked by", "approved", "approved by", "designed by",
        "prepared by", "reviewed by", "created by", "modified by", "issued", "issue date", "plot date", "plotted",
        "last saved", "created", "modified",
        "scale", "sheet", "sheet no", "sheet of", "page", "rev", "revision", "drawing no", "drawing number",
        "drg no", "dwg no", "drg", "dwg", "part no", "part number", "supersedes", "zone", "eco", "ecn", "initials",
        "title", "project", "client", "customer", "company", "general tolerance", "tolerance",
        "material", "finish", "weight", "mass", "units", "dimensions in", "all dimensions", "do not scale",
        "unless otherwise", "projection", "first angle", "third angle", "copyright", "confidential", "proprietary"
    };

    private static readonly Regex NumericDatePattern = new(
        @"^\d{1,4}[./\-]\d{1,2}[./\-]\d{1,4}$",
        RegexOptions.Compiled);

    private static readonly Regex DateTimePattern = new(
        @"^\d{1,4}[./\-]\d{1,2}[./\-]\d{1,4}[\sT,]+\d{1,2}:\d{2}(:\d{2})?(\s?(am|pm))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimePattern = new(
        @"^\d{1,2}:\d{2}(:\d{2})?(\s?(am|pm))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthFirstDatePattern = new(
        @"^(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s*\d{1,2}(st|nd|rd|th)?[\s,\-./]+\d{2,4}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RevisionCodePattern = new(
        @"^rev(ision)?\.?\s*[:=\-]?\s*[a-z]?\d{0,3}[a-z]?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthDatePattern = new(
        @"^\d{1,2}[\s\-./]?(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*[\s\-./,]*\d{2,4}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScalePattern = new(
        @"^(scale\s*[:=]?\s*)?\d+(\.\d+)?\s*:\s*\d+(\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum PrimitiveKind
    {
        Line,
        Polyline,
        Arc,
        Circle,
        Hatch
    }

    private sealed class Item
    {
        public PrimitiveKind Kind { get; init; }
        public int Index { get; init; }
        public BoundingBox2D Bounds { get; init; } = new(0, 0, 0, 0);
        public bool Spanning { get; set; }
    }

    public List<AssemblyNode> Decompose(ExtractedGeometry geometry)
    {
        var nodes = new List<AssemblyNode>();
        if (geometry == null)
        {
            return nodes;
        }

        var items = CollectItems(geometry, true);
        if (items.Count == 0)
        {
            items = CollectItems(geometry, false);
        }

        if (items.Count == 0)
        {
            return nodes;
        }

        var titleBlock = RemoveTitleBlock(geometry, items);
        items = titleBlock.Items;

        var overall = items[0].Bounds;
        foreach (var item in items)
        {
            overall = overall.Union(item.Bounds);
        }

        var diagonal = Math.Sqrt(overall.Width * overall.Width + overall.Height * overall.Height);
        if (diagonal <= 0)
        {
            diagonal = 1.0;
        }

        var tolerance = diagonal * ProximityFraction;
        var labelReach = diagonal * LabelReachFraction;
        var maxExtent = Math.Max(overall.Width, overall.Height);

        if (items.Count >= SpanningMinItems && maxExtent > 0)
        {
            foreach (var item in items)
            {
                item.Spanning = Math.Max(item.Bounds.Width, item.Bounds.Height) >= maxExtent * SpanningFraction;
            }

            if (items.All(i => i.Spanning))
            {
                foreach (var item in items)
                {
                    item.Spanning = false;
                }
            }
        }

        var clusters = Cluster(items, tolerance);
        var clusterBounds = new List<BoundingBox2D>();
        var clusterSpanning = new List<bool>();

        for (var c = 0; c < clusters.Count; c++)
        {
            var cluster = clusters[c];
            var bounds = cluster[0].Bounds;
            foreach (var item in cluster)
            {
                bounds = bounds.Union(item.Bounds);
            }

            clusterBounds.Add(bounds);
            clusterSpanning.Add(cluster.Any(i => i.Spanning));
            nodes.Add(CreateNode(c + 1, cluster, bounds, overall, geometry, tolerance));
        }

        AssignTexts(FilterTexts(geometry, titleBlock.Regions, titleBlock.Excluded), nodes, clusterBounds, clusterSpanning, tolerance, labelReach);
        FinalizeNodes(nodes);

        return nodes;
    }

    private static List<Item> CollectItems(ExtractedGeometry geometry, bool applyFilter)
    {
        var items = new List<Item>();

        for (var i = 0; i < geometry.Lines.Count; i++)
        {
            var line = geometry.Lines[i];
            if (applyFilter && IsIgnored(line.Layer, line.LineType))
            {
                continue;
            }

            AddItem(items, PrimitiveKind.Line, i, new BoundingBox2D(
                Math.Min(line.Start.X, line.End.X),
                Math.Min(line.Start.Y, line.End.Y),
                Math.Max(line.Start.X, line.End.X),
                Math.Max(line.Start.Y, line.End.Y)));
        }

        for (var i = 0; i < geometry.Polylines.Count; i++)
        {
            var polyline = geometry.Polylines[i];
            if (polyline.Vertices.Count == 0 || (applyFilter && IsIgnored(polyline.Layer, polyline.LineType)))
            {
                continue;
            }

            AddItem(items, PrimitiveKind.Polyline, i, new BoundingBox2D(
                polyline.Vertices.Min(v => v.X),
                polyline.Vertices.Min(v => v.Y),
                polyline.Vertices.Max(v => v.X),
                polyline.Vertices.Max(v => v.Y)));
        }

        for (var i = 0; i < geometry.Arcs.Count; i++)
        {
            var arc = geometry.Arcs[i];
            if (applyFilter && IsIgnored(arc.Layer, arc.LineType))
            {
                continue;
            }

            AddItem(items, PrimitiveKind.Arc, i, ArcBounds(arc));
        }

        for (var i = 0; i < geometry.Circles.Count; i++)
        {
            var circle = geometry.Circles[i];
            if (applyFilter && IsIgnored(circle.Layer, circle.LineType))
            {
                continue;
            }

            var r = Math.Abs(circle.Radius);
            AddItem(items, PrimitiveKind.Circle, i, new BoundingBox2D(
                circle.Center.X - r,
                circle.Center.Y - r,
                circle.Center.X + r,
                circle.Center.Y + r));
        }

        for (var i = 0; i < geometry.Hatches.Count; i++)
        {
            var hatch = geometry.Hatches[i];
            if (applyFilter && IsIgnored(hatch.Layer, hatch.LineType))
            {
                continue;
            }

            var points = hatch.BoundaryPaths.SelectMany(p => p).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            AddItem(items, PrimitiveKind.Hatch, i, new BoundingBox2D(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y)));
        }

        return items;
    }

    private static void AddItem(List<Item> items, PrimitiveKind kind, int index, BoundingBox2D bounds)
    {
        if (!double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY)
            || !double.IsFinite(bounds.MaxX) || !double.IsFinite(bounds.MaxY))
        {
            return;
        }

        items.Add(new Item { Kind = kind, Index = index, Bounds = bounds });
    }

    private static bool IsTitleBlockLayer(string? layer)
    {
        if (string.IsNullOrEmpty(layer))
        {
            return false;
        }

        foreach (var marker in TitleBlockLayerMarkers)
        {
            if (layer.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTitleBlockText(GeometryText text)
    {
        if (text == null || string.IsNullOrWhiteSpace(text.Text))
        {
            return false;
        }

        if (IsTitleBlockLayer(text.Layer))
        {
            return true;
        }

        var normalized = text.Text.Trim().Trim(':').Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (NumericDatePattern.IsMatch(normalized)
            || DateTimePattern.IsMatch(normalized)
            || TimePattern.IsMatch(normalized)
            || MonthDatePattern.IsMatch(normalized)
            || MonthFirstDatePattern.IsMatch(normalized)
            || RevisionCodePattern.IsMatch(normalized)
            || ScalePattern.IsMatch(normalized))
        {
            return true;
        }

        foreach (var keyword in TitleBlockKeywords)
        {
            if (!normalized.StartsWith(keyword, StringComparison.Ordinal))
            {
                continue;
            }

            if (normalized.Length == keyword.Length || !char.IsLetterOrDigit(normalized[keyword.Length]))
            {
                return true;
            }
        }

        return false;
    }

    private static BoundingBox2D TitleTextBox(GeometryText text)
    {
        var height = text.Height > 0 ? text.Height : 0;
        var lines = text.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var lineCount = Math.Max(1, lines.Length);
        var longest = lines.Length == 0 ? text.Text.Length : lines.Max(l => l.Length);

        var width = longest * height * 0.7;
        var below = lineCount * height * 1.5;
        var above = height * 1.5;

        return new BoundingBox2D(
            text.Position.X,
            text.Position.Y - below,
            text.Position.X + width,
            text.Position.Y + above);
    }

    private static (List<Item> Items, List<BoundingBox2D> Regions, HashSet<GeometryText> Excluded) RemoveTitleBlock(
        ExtractedGeometry geometry,
        List<Item> items)
    {
        var regions = new List<BoundingBox2D>();
        var excluded = new HashSet<GeometryText>();

        if (items.Count == 0)
        {
            return (items, regions, excluded);
        }

        var overall = items[0].Bounds;
        foreach (var item in items)
        {
            overall = overall.Union(item.Bounds);
        }

        var diagonal = Math.Sqrt(overall.Width * overall.Width + overall.Height * overall.Height);
        if (diagonal <= 0 || overall.Area <= 0)
        {
            return (items, regions, excluded);
        }

        var matches = geometry.Texts.Where(IsTitleBlockText).ToList();
        foreach (var match in matches)
        {
            excluded.Add(match);
        }

        foreach (var peripheral in FindPeripheralTableTexts(geometry, overall, excluded))
        {
            if (excluded.Add(peripheral))
            {
                matches.Add(peripheral);
            }
        }

        if (matches.Count == 0)
        {
            return (items, regions, excluded);
        }

        var tolerance = diagonal * ProximityFraction;
        var maxRegionArea = overall.Area * TitleBlockMaxAreaFraction;
        var maxTextHeight = matches.Max(t => t.Height);
        var linkGap = Math.Max(diagonal * TitleBlockLinkFraction, maxTextHeight * 6.0);

        var boxes = matches.Select(TitleTextBox).ToList();
        var parent = Enumerable.Range(0, boxes.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                if (boxes[i].GapTo(boxes[j]) <= linkGap)
                {
                    var ra = Find(i);
                    var rb = Find(j);
                    if (ra != rb)
                    {
                        parent[rb] = ra;
                    }
                }
            }
        }

        foreach (var group in Enumerable.Range(0, boxes.Count).GroupBy(Find))
        {
            var members = group.ToList();
            var region = boxes[members[0]];
            var groupHeight = 0.0;

            foreach (var index in members)
            {
                region = region.Union(boxes[index]);
                groupHeight = Math.Max(groupHeight, matches[index].Height);
            }

            region = region.Expand(Math.Max(groupHeight * 2.0, tolerance * 2.0));

            if (region.Area > maxRegionArea)
            {
                continue;
            }

            regions.Add(AbsorbTableLines(region, items, tolerance, maxRegionArea));
        }

        if (regions.Count == 0)
        {
            return (items, regions, excluded);
        }

        var kept = items
            .Where(i => !regions.Any(r => r.Contains(i.Bounds, tolerance)) && !IsSheetBorder(i, overall))
            .ToList();

        if (kept.Count == 0)
        {
            return (items, new List<BoundingBox2D>(), excluded);
        }

        return (kept, regions, excluded);
    }

    private static List<GeometryText> FindPeripheralTableTexts(
        ExtractedGeometry geometry,
        BoundingBox2D overall,
        HashSet<GeometryText> excluded)
    {
        var result = new List<GeometryText>();
        if (geometry.Texts.Count < PeripheralMinTexts)
        {
            return result;
        }

        var frame = overall;
        foreach (var text in geometry.Texts)
        {
            frame = frame.Union(new BoundingBox2D(text.Position.X, text.Position.Y, text.Position.X, text.Position.Y));
        }

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return result;
        }

        var zoneWidth = frame.Width * PeripheralZoneFraction;
        var zoneHeight = frame.Height * PeripheralZoneFraction;

        var corners = new[]
        {
            new BoundingBox2D(frame.MinX, frame.MinY, frame.MinX + zoneWidth, frame.MinY + zoneHeight),
            new BoundingBox2D(frame.MaxX - zoneWidth, frame.MinY, frame.MaxX, frame.MinY + zoneHeight),
            new BoundingBox2D(frame.MinX, frame.MaxY - zoneHeight, frame.MinX + zoneWidth, frame.MaxY),
            new BoundingBox2D(frame.MaxX - zoneWidth, frame.MaxY - zoneHeight, frame.MaxX, frame.MaxY)
        };

        var picked = new HashSet<GeometryText>();

        foreach (var corner in corners)
        {
            var inZone = geometry.Texts
                .Where(t => !string.IsNullOrWhiteSpace(t.Text)
                    && t.Position.X >= corner.MinX && t.Position.X <= corner.MaxX
                    && t.Position.Y >= corner.MinY && t.Position.Y <= corner.MaxY)
                .ToList();

            if (inZone.Count < PeripheralMinTexts)
            {
                continue;
            }

            var anchors = inZone.Where(excluded.Contains).ToList();
            if (anchors.Count > 0)
            {
                var linkGap = Math.Max(anchors.Max(a => a.Height), 1e-6) * PeripheralLinkHeightFactor;
                var anchorBoxes = anchors.Select(TitleTextBox).ToList();

                foreach (var text in inZone)
                {
                    if (excluded.Contains(text))
                    {
                        continue;
                    }

                    var box = TitleTextBox(text);
                    if (anchorBoxes.Any(b => b.GapTo(box) <= linkGap))
                    {
                        picked.Add(text);
                    }
                }
            }

            foreach (var text in AlignedTableTexts(inZone))
            {
                if (!excluded.Contains(text))
                {
                    picked.Add(text);
                }
            }
        }

        result.AddRange(picked);
        return result;
    }

    private static List<GeometryText> AlignedTableTexts(List<GeometryText> texts)
    {
        var rows = new List<List<GeometryText>>();

        foreach (var text in texts.OrderByDescending(t => t.Position.Y))
        {
            var row = rows.FirstOrDefault(r =>
                Math.Abs(r[0].Position.Y - text.Position.Y) <= Math.Max(Math.Max(text.Height, r[0].Height), 1e-6) * 0.5);

            if (row == null)
            {
                rows.Add(new List<GeometryText> { text });
            }
            else
            {
                row.Add(text);
            }
        }

        var tableRows = rows.Where(r => r.Count >= 2).ToList();
        if (tableRows.Count < PeripheralMinRows)
        {
            return new List<GeometryText>();
        }

        var members = tableRows.SelectMany(r => r).ToList();
        var columnTolerance = Math.Max(members.Average(t => t.Height), 1e-6) * 2.0;
        var aligned = members
            .Where(t => members.Any(o => !ReferenceEquals(o, t)
                && Math.Abs(o.Position.Y - t.Position.Y) > columnTolerance * 0.25
                && Math.Abs(o.Position.X - t.Position.X) <= columnTolerance))
            .ToList();

        return aligned.Count >= PeripheralMinRows * 2 ? aligned : new List<GeometryText>();
    }

    private static BoundingBox2D AbsorbTableLines(
        BoundingBox2D region,
        List<Item> items,
        double tolerance,
        double maxArea)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;

            foreach (var item in items)
            {
                if (item.Kind is not (PrimitiveKind.Line or PrimitiveKind.Polyline))
                {
                    continue;
                }

                if (Math.Min(item.Bounds.Width, item.Bounds.Height) > tolerance)
                {
                    continue;
                }

                if (region.Contains(item.Bounds, tolerance) || region.GapTo(item.Bounds) > tolerance)
                {
                    continue;
                }

                var union = region.Union(item.Bounds);
                if (union.Area > maxArea)
                {
                    continue;
                }

                region = union;
                changed = true;
            }

            if (!changed)
            {
                break;
            }
        }

        return region;
    }

    private static bool IsSheetBorder(Item item, BoundingBox2D overall)
    {
        if (item.Kind is not (PrimitiveKind.Line or PrimitiveKind.Polyline))
        {
            return false;
        }

        var bounds = item.Bounds;
        var edgeX = overall.Width * SheetBorderEdgeFraction;
        var edgeY = overall.Height * SheetBorderEdgeFraction;
        var thinX = overall.Width * 0.002;
        var thinY = overall.Height * 0.002;

        var coversWidth = bounds.Width >= overall.Width * SheetBorderFraction;
        var coversHeight = bounds.Height >= overall.Height * SheetBorderFraction;

        if (coversWidth && coversHeight)
        {
            return true;
        }

        if (coversWidth && bounds.Height <= thinY
            && (bounds.MinY <= overall.MinY + edgeY || bounds.MaxY >= overall.MaxY - edgeY))
        {
            return true;
        }

        if (coversHeight && bounds.Width <= thinX
            && (bounds.MinX <= overall.MinX + edgeX || bounds.MaxX >= overall.MaxX - edgeX))
        {
            return true;
        }

        return false;
    }

    private static List<GeometryText> FilterTexts(
        ExtractedGeometry geometry,
        List<BoundingBox2D> regions,
        HashSet<GeometryText> excluded)
    {
        return geometry.Texts
            .Where(t => !excluded.Contains(t)
                && !IsTitleBlockText(t)
                && !regions.Any(r => t.Position.X >= r.MinX && t.Position.X <= r.MaxX
                    && t.Position.Y >= r.MinY && t.Position.Y <= r.MaxY))
            .ToList();
    }

    private static bool IsIgnored(string? layer, string? lineType)
    {
        if (IsTitleBlockLayer(layer))
        {
            return true;
        }

        foreach (var marker in IgnoredMarkers)
        {
            if (!string.IsNullOrEmpty(layer) && layer.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(lineType) && lineType.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static BoundingBox2D ArcBounds(GeometryArc arc)
    {
        var radius = Math.Abs(arc.Radius);
        var start = NormalizeAngle(arc.StartAngle);
        var end = NormalizeAngle(arc.EndAngle);
        if (end <= start)
        {
            end += 360.0;
        }

        var xs = new List<double>();
        var ys = new List<double>();

        void AddAngle(double degrees)
        {
            var radians = degrees * Math.PI / 180.0;
            xs.Add(arc.Center.X + radius * Math.Cos(radians));
            ys.Add(arc.Center.Y + radius * Math.Sin(radians));
        }

        AddAngle(start);
        AddAngle(end);

        for (var k = 0; k < 8; k++)
        {
            var cardinal = k * 90.0;
            if (cardinal >= start && cardinal <= end)
            {
                AddAngle(cardinal);
            }
        }

        return new BoundingBox2D(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    private static double NormalizeAngle(double degrees)
    {
        var value = degrees % 360.0;
        return value < 0 ? value + 360.0 : value;
    }

    private static List<List<Item>> Cluster(List<Item> items, double tolerance)
    {
        var ordered = items
            .OrderBy(i => i.Bounds.MinX)
            .ThenBy(i => i.Bounds.MinY)
            .ToList();

        var parent = new int[ordered.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var b = ordered[j];
                if (b.Bounds.MinX > a.Bounds.MaxX + tolerance)
                {
                    break;
                }

                if (a.Spanning != b.Spanning)
                {
                    continue;
                }

                if (a.Bounds.GapTo(b.Bounds) <= tolerance)
                {
                    var ra = Find(i);
                    var rb = Find(j);
                    if (ra != rb)
                    {
                        parent[rb] = ra;
                    }
                }
            }
        }

        var groups = new Dictionary<int, List<Item>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<Item>();
                groups[root] = list;
            }

            list.Add(ordered[i]);
        }

        return groups.Values
            .Select(g => new
            {
                Items = g,
                MinX = g.Min(i => i.Bounds.MinX),
                MinY = g.Min(i => i.Bounds.MinY)
            })
            .OrderBy(g => g.MinX)
            .ThenBy(g => g.MinY)
            .Select(g => g.Items)
            .ToList();
    }

    private static AssemblyNode CreateNode(
        int number,
        List<Item> cluster,
        BoundingBox2D bounds,
        BoundingBox2D overall,
        ExtractedGeometry source,
        double tolerance)
    {
        var geometry = new ExtractedGeometry
        {
            DetectedUnit = source.DetectedUnit,
            SourceVersion = source.SourceVersion,
            OriginalVersion = source.OriginalVersion,
            HeaderPatched = source.HeaderPatched,
            Bounds = new GeometryBounds(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY)
        };

        var layers = new List<string>();

        foreach (var item in cluster)
        {
            switch (item.Kind)
            {
                case PrimitiveKind.Line:
                    geometry.Lines.Add(source.Lines[item.Index]);
                    AddLayer(layers, source.Lines[item.Index].Layer);
                    break;
                case PrimitiveKind.Polyline:
                    geometry.Polylines.Add(source.Polylines[item.Index]);
                    AddLayer(layers, source.Polylines[item.Index].Layer);
                    break;
                case PrimitiveKind.Arc:
                    geometry.Arcs.Add(source.Arcs[item.Index]);
                    AddLayer(layers, source.Arcs[item.Index].Layer);
                    break;
                case PrimitiveKind.Circle:
                    geometry.Circles.Add(source.Circles[item.Index]);
                    AddLayer(layers, source.Circles[item.Index].Layer);
                    break;
                case PrimitiveKind.Hatch:
                    geometry.Hatches.Add(source.Hatches[item.Index]);
                    AddLayer(layers, source.Hatches[item.Index].Layer);
                    break;
            }
        }

        geometry.Layers = layers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var node = new AssemblyNode
        {
            Id = $"N{number}",
            Name = $"Part {number}",
            Layer = MostCommonLayer(layers),
            Bounds = bounds,
            Local = bounds.Translate(-overall.MinX, -overall.MinY),
            LineCount = geometry.Lines.Count,
            ArcCount = geometry.Arcs.Count,
            CircleCount = geometry.Circles.Count,
            PolylineCount = geometry.Polylines.Count,
            HatchCount = geometry.Hatches.Count,
            IsFilled = geometry.Hatches.Count > 0,
            Geometry = geometry
        };

        node.HasClosedProfile = geometry.Circles.Count > 0
            || geometry.Polylines.Any(p => p.IsClosed)
            || HasClosedLineLoop(geometry.Lines, tolerance);

        node.Shape = ClassifyShape(node, geometry, tolerance);
        FillParameters(node, geometry);

        return node;
    }

    private static void AddLayer(List<string> layers, string? layer)
    {
        if (!string.IsNullOrWhiteSpace(layer))
        {
            layers.Add(layer);
        }
    }

    private static string? MostCommonLayer(List<string> layers)
    {
        return layers
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    private static bool HasClosedLineLoop(List<GeometryLine> lines, double tolerance)
    {
        if (lines.Count < 3)
        {
            return false;
        }

        var q = Math.Max(tolerance, 1e-9);
        var degree = new Dictionary<(long, long), int>();

        void AddPoint(GeometryPoint p)
        {
            var key = ((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q));
            degree[key] = degree.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        foreach (var line in lines)
        {
            AddPoint(line.Start);
            AddPoint(line.End);
        }

        return degree.Values.All(v => v >= 2);
    }

    private static PartShape ClassifyShape(AssemblyNode node, ExtractedGeometry geometry, double tolerance)
    {
        var bounds = node.Bounds;
        var maxExtent = Math.Max(bounds.Width, bounds.Height);
        var minExtent = Math.Min(bounds.Width, bounds.Height);

        var onlyCircles = geometry.Circles.Count > 0
            && geometry.Lines.Count == 0
            && geometry.Polylines.Count == 0
            && geometry.Arcs.Count == 0;

        if (onlyCircles)
        {
            return HasConcentricPair(geometry.Circles, tolerance) ? PartShape.Annular : PartShape.Circular;
        }

        var primitiveTotal = geometry.Lines.Count + geometry.Polylines.Count + geometry.Arcs.Count + geometry.Circles.Count;

        if (geometry.Circles.Count == 0
            && (primitiveTotal == 1 && geometry.Lines.Count == 1
                || (maxExtent > 0 && minExtent <= maxExtent * LinearAspectLimit)))
        {
            return PartShape.Linear;
        }

        if (geometry.Arcs.Count == 0 && geometry.Circles.Count == 0)
        {
            var polylinesRectangular = geometry.Polylines.Count > 0
                && geometry.Polylines.All(p => IsRectanglePolyline(p, tolerance));

            if (geometry.Lines.Count == 0 && polylinesRectangular)
            {
                return PartShape.Rectangular;
            }

            if (geometry.Polylines.Count == 0
                && geometry.Lines.Count >= 4
                && geometry.Lines.All(l => IsAxisAligned(l.Start, l.End, tolerance))
                && node.HasClosedProfile)
            {
                return PartShape.Rectangular;
            }

            if (node.HasClosedProfile)
            {
                return PartShape.Polygonal;
            }
        }

        return PartShape.Irregular;
    }

    private static bool HasConcentricPair(List<GeometryCircle> circles, double tolerance)
    {
        for (var i = 0; i < circles.Count; i++)
        {
            for (var j = i + 1; j < circles.Count; j++)
            {
                var dx = circles[i].Center.X - circles[j].Center.X;
                var dy = circles[i].Center.Y - circles[j].Center.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= tolerance && Math.Abs(circles[i].Radius - circles[j].Radius) > tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAxisAligned(GeometryPoint a, GeometryPoint b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance || Math.Abs(a.Y - b.Y) <= tolerance;
    }

    private static bool IsRectanglePolyline(GeometryPolyline polyline, double tolerance)
    {
        var vertices = polyline.Vertices;
        var count = vertices.Count;

        if (count == 5 && !polyline.IsClosed)
        {
            var closedGap = Math.Abs(vertices[0].X - vertices[4].X) + Math.Abs(vertices[0].Y - vertices[4].Y);
            if (closedGap > tolerance)
            {
                return false;
            }

            count = 4;
        }
        else if (!(polyline.IsClosed && count == 4))
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % count];
            if (Math.Abs(a.Bulge) > 1e-9 || !IsAxisAligned(new GeometryPoint(a.X, a.Y), new GeometryPoint(b.X, b.Y), tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static void FillParameters(AssemblyNode node, ExtractedGeometry geometry)
    {
        var bounds = node.Bounds;

        node.Parameters["width"] = Round(bounds.Width);
        node.Parameters["height"] = Round(bounds.Height);
        node.Parameters["centerX"] = Round(bounds.CenterX);
        node.Parameters["centerY"] = Round(bounds.CenterY);

        switch (node.Shape)
        {
            case PartShape.Circular:
                node.Parameters["diameter"] = Round(geometry.Circles.Max(c => Math.Abs(c.Radius)) * 2.0);
                break;
            case PartShape.Annular:
                node.Parameters["outerDiameter"] = Round(geometry.Circles.Max(c => Math.Abs(c.Radius)) * 2.0);
                node.Parameters["innerDiameter"] = Round(geometry.Circles.Min(c => Math.Abs(c.Radius)) * 2.0);
                break;
            case PartShape.Linear:
                node.Parameters["length"] = Round(Math.Max(bounds.Width, bounds.Height));
                break;
        }
    }

    private static double Round(double value)
    {
        return Math.Round(value, 4);
    }

    private static void AssignTexts(
        IEnumerable<GeometryText> texts,
        List<AssemblyNode> nodes,
        List<BoundingBox2D> clusterBounds,
        List<bool> clusterSpanning,
        double tolerance,
        double labelReach)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            var target = FindCluster(text.Position, clusterBounds, clusterSpanning, tolerance, labelReach);
            if (target < 0)
            {
                continue;
            }

            var node = nodes[target];
            node.Labels.Add(text.Text.Trim());
            node.Geometry.Texts.Add(text);

            foreach (var token in text.Tokens)
            {
                if (!string.IsNullOrWhiteSpace(token)
                    && !node.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    node.Tokens.Add(token);
                }
            }
        }
    }

    private static int FindCluster(
        GeometryPoint point,
        List<BoundingBox2D> clusterBounds,
        List<bool> clusterSpanning,
        double tolerance,
        double labelReach)
    {
        var best = -1;
        var bestDistance = 0.0;
        var bestSpanning = false;
        var bestArea = 0.0;
        var bestInside = false;

        for (var i = 0; i < clusterBounds.Count; i++)
        {
            var distance = DistanceToBox(point, clusterBounds[i]);
            var inside = distance <= tolerance;

            if (!inside && distance > labelReach)
            {
                continue;
            }

            var spanning = clusterSpanning[i];
            var area = clusterBounds[i].Area;
            var better = false;

            if (best < 0)
            {
                better = true;
            }
            else if (inside != bestInside)
            {
                better = inside;
            }
            else if (inside)
            {
                better = spanning != bestSpanning ? !spanning : area < bestArea;
            }
            else
            {
                better = distance < bestDistance;
            }

            if (better)
            {
                best = i;
                bestDistance = distance;
                bestSpanning = spanning;
                bestArea = area;
                bestInside = inside;
            }
        }

        return best;
    }

    private static double DistanceToBox(GeometryPoint point, BoundingBox2D box)
    {
        var dx = Math.Max(0, Math.Max(box.MinX - point.X, point.X - box.MaxX));
        var dy = Math.Max(0, Math.Max(box.MinY - point.Y, point.Y - box.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void FinalizeNodes(List<AssemblyNode> nodes)
    {
        foreach (var node in nodes)
        {
            var label = node.Labels.FirstOrDefault(l => l.Any(char.IsLetter));
            if (label == null)
            {
                continue;
            }

            node.Label = label;
            node.Name = label.Length > MaxLabelLength ? label[..MaxLabelLength].Trim() : label;
        }
    }
}