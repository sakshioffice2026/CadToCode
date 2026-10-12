using CadToCode.Business.Interfaces;
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

        AssignTexts(geometry, nodes, clusterBounds, clusterSpanning, tolerance, labelReach);
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

    private static bool IsIgnored(string? layer, string? lineType)
    {
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
        ExtractedGeometry geometry,
        List<AssemblyNode> nodes,
        List<BoundingBox2D> clusterBounds,
        List<bool> clusterSpanning,
        double tolerance,
        double labelReach)
    {
        foreach (var text in geometry.Texts)
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