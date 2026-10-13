using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services;

public static class StructuralProfileExtractor
{
    private static readonly string[] IgnoredMarkers = { "center", "centre", "axis", "dim", "hidden", "phantom", "dash" };

    private readonly record struct Pt(double X, double Y);

    private sealed class Loop
    {
        public List<Pt> Points { get; init; } = new();
        public double Area { get; init; }
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
    }

    public static StructuralProfile? Extract(ExtractedGeometry geometry, double scale)
    {
        var lines = geometry.Lines.Where(l => !IsIgnored(l.Layer, l.LineType)).ToList();
        var polylines = geometry.Polylines.Where(p => !IsIgnored(p.Layer, p.LineType)).ToList();
        var circles = geometry.Circles.Where(c => !IsIgnored(c.Layer, c.LineType)).ToList();
        var arcs = geometry.Arcs.Where(a => !IsIgnored(a.Layer, a.LineType)).ToList();

        var bounds = ComputeBounds(lines, polylines, circles, arcs);
        if (bounds is null)
        {
            return null;
        }

        var (minX, minY, maxX, maxY) = bounds.Value;
        var diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        if (diagonal <= 0)
        {
            return null;
        }

        var tolerance = Math.Max(1e-9, diagonal * 1e-4);
        var loops = new List<Loop>();

        foreach (var polyline in polylines)
        {
            var points = polyline.Vertices.Select(v => new Pt(v.X, v.Y)).ToList();
            var closed = polyline.IsClosed || (points.Count > 2 && Distance(points[0], points[^1]) <= tolerance);

            if (closed)
            {
                AddLoop(loops, points, tolerance);
            }
        }

        foreach (var chain in ChainLines(lines, tolerance))
        {
            AddLoop(loops, chain, tolerance);
        }

        if (loops.Count == 0)
        {
            return BuildOpenProfile(lines, polylines, (maxX - minX) * scale, (maxY - minY) * scale);
        }

        var outer = loops.OrderByDescending(l => Math.Abs(l.Area)).First();
        var orientation = outer.Area >= 0 ? 1.0 : -1.0;
        var reflex = 0;
        var count = outer.Points.Count;

        for (var i = 0; i < count; i++)
        {
            var previous = outer.Points[(i + count - 1) % count];
            var current = outer.Points[i];
            var next = outer.Points[(i + 1) % count];

            var ax = current.X - previous.X;
            var ay = current.Y - previous.Y;
            var bx = next.X - current.X;
            var by = next.Y - current.Y;

            var cross = ax * by - ay * bx;
            var threshold = 1e-6 * Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by);

            if (cross * orientation < -threshold)
            {
                reflex++;
            }
        }

        var cutouts = 0;

        foreach (var loop in loops)
        {
            if (ReferenceEquals(loop, outer))
            {
                continue;
            }

            var inside = loop.MinX >= outer.MinX - tolerance
                && loop.MaxX <= outer.MaxX + tolerance
                && loop.MinY >= outer.MinY - tolerance
                && loop.MaxY <= outer.MaxY + tolerance;

            if (!inside)
            {
                continue;
            }

            var probe = new Pt(loop.Points.Average(p => p.X), loop.Points.Average(p => p.Y));
            if (PointInPolygon(probe, outer.Points))
            {
                cutouts++;
            }
        }

        foreach (var circle in circles)
        {
            if (PointInPolygon(new Pt(circle.Center.X, circle.Center.Y), outer.Points))
            {
                cutouts++;
            }
        }

        return new StructuralProfile
        {
            BoundingWidth = Math.Round((outer.MaxX - outer.MinX) * scale, 4),
            BoundingHeight = Math.Round((outer.MaxY - outer.MinY) * scale, 4),
            SegmentCount = outer.Points.Count,
            VertexCount = outer.Points.Count,
            InnerCutoutsCount = cutouts,
            NotchCount = (reflex + 1) / 2,
            ClosedLoopCount = loops.Count,
            IsClosed = true
        };
    }

    private static StructuralProfile BuildOpenProfile(
        List<GeometryLine> lines,
        List<GeometryPolyline> polylines,
        double width,
        double height)
    {
        var lineSegments = lines.Count;
        var polylineSegments = polylines.Sum(p => Math.Max(0, p.Vertices.Count - (p.IsClosed ? 0 : 1)));
        var segments = Math.Max(lineSegments, polylineSegments);

        var endpoints = new HashSet<(long, long)>();
        foreach (var line in lines)
        {
            endpoints.Add(((long)Math.Round(line.Start.X * 1000), (long)Math.Round(line.Start.Y * 1000)));
            endpoints.Add(((long)Math.Round(line.End.X * 1000), (long)Math.Round(line.End.Y * 1000)));
        }

        var vertices = Math.Max(endpoints.Count, polylines.Sum(p => p.Vertices.Count));

        return new StructuralProfile
        {
            BoundingWidth = Math.Round(width, 4),
            BoundingHeight = Math.Round(height, 4),
            SegmentCount = segments,
            VertexCount = vertices,
            InnerCutoutsCount = 0,
            NotchCount = 0,
            ClosedLoopCount = 0,
            IsClosed = false
        };
    }

    private static void AddLoop(List<Loop> loops, List<Pt> raw, double tolerance)
    {
        var points = Simplify(raw, tolerance);
        if (points.Count < 3)
        {
            return;
        }

        var area = SignedArea(points);
        if (Math.Abs(area) <= tolerance * tolerance)
        {
            return;
        }

        var loop = new Loop
        {
            Points = points,
            Area = area,
            MinX = points.Min(p => p.X),
            MinY = points.Min(p => p.Y),
            MaxX = points.Max(p => p.X),
            MaxY = points.Max(p => p.Y)
        };

        var duplicate = loops.Any(existing =>
            Math.Abs(existing.MinX - loop.MinX) <= tolerance * 10
            && Math.Abs(existing.MinY - loop.MinY) <= tolerance * 10
            && Math.Abs(existing.MaxX - loop.MaxX) <= tolerance * 10
            && Math.Abs(existing.MaxY - loop.MaxY) <= tolerance * 10
            && Math.Abs(Math.Abs(existing.Area) - Math.Abs(loop.Area)) <= Math.Abs(loop.Area) * 0.01);

        if (!duplicate)
        {
            loops.Add(loop);
        }
    }

    private static List<List<Pt>> ChainLines(List<GeometryLine> lines, double tolerance)
    {
        var result = new List<List<Pt>>();

        var edges = lines
            .Where(l => l.Length > tolerance)
            .Select(l => (A: new Pt(l.Start.X, l.Start.Y), B: new Pt(l.End.X, l.End.Y)))
            .ToList();

        if (edges.Count < 3)
        {
            return result;
        }

        (long, long) Key(Pt p) => ((long)Math.Round(p.X / tolerance), (long)Math.Round(p.Y / tolerance));

        var map = new Dictionary<(long, long), List<int>>();

        for (var i = 0; i < edges.Count; i++)
        {
            foreach (var key in new[] { Key(edges[i].A), Key(edges[i].B) })
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    map[key] = list;
                }

                list.Add(i);
            }
        }

        var used = new bool[edges.Count];

        for (var start = 0; start < edges.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            used[start] = true;

            var startKey = Key(edges[start].A);
            var currentKey = Key(edges[start].B);
            var points = new List<Pt> { edges[start].A, edges[start].B };
            var closed = false;
            var guard = 0;

            while (guard++ <= edges.Count)
            {
                if (currentKey == startKey)
                {
                    closed = true;
                    break;
                }

                var next = -1;

                if (map.TryGetValue(currentKey, out var incident))
                {
                    foreach (var index in incident)
                    {
                        if (!used[index])
                        {
                            next = index;
                            break;
                        }
                    }
                }

                if (next < 0)
                {
                    break;
                }

                used[next] = true;

                var edge = edges[next];
                var to = Key(edge.A) == currentKey ? edge.B : edge.A;
                points.Add(to);
                currentKey = Key(to);
            }

            if (closed && points.Count >= 4)
            {
                result.Add(points.Take(points.Count - 1).ToList());
            }
        }

        return result;
    }

    private static List<Pt> Simplify(List<Pt> input, double tolerance)
    {
        var points = new List<Pt>();

        foreach (var point in input)
        {
            if (points.Count == 0 || Distance(points[^1], point) > tolerance)
            {
                points.Add(point);
            }
        }

        while (points.Count > 1 && Distance(points[0], points[^1]) <= tolerance)
        {
            points.RemoveAt(points.Count - 1);
        }

        var changed = true;

        while (changed && points.Count >= 3)
        {
            changed = false;

            for (var i = 0; i < points.Count; i++)
            {
                var previous = points[(i + points.Count - 1) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];

                var ax = current.X - previous.X;
                var ay = current.Y - previous.Y;
                var bx = next.X - current.X;
                var by = next.Y - current.Y;

                var la = Math.Sqrt(ax * ax + ay * ay);
                var lb = Math.Sqrt(bx * bx + by * by);
                var cross = ax * by - ay * bx;

                if (la <= 0 || lb <= 0 || Math.Abs(cross) <= 1e-6 * la * lb)
                {
                    points.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }

        return points;
    }

    private static double SignedArea(List<Pt> points)
    {
        var sum = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum / 2.0;
    }

    private static bool PointInPolygon(Pt point, List<Pt> polygon)
    {
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY)? ComputeBounds(
        List<GeometryLine> lines,
        List<GeometryPolyline> polylines,
        List<GeometryCircle> circles,
        List<GeometryArc> arcs)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var any = false;

        void Include(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                return;
            }

            any = true;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var line in lines)
        {
            Include(line.Start.X, line.Start.Y);
            Include(line.End.X, line.End.Y);
        }

        foreach (var polyline in polylines)
        {
            foreach (var vertex in polyline.Vertices)
            {
                Include(vertex.X, vertex.Y);
            }
        }

        foreach (var circle in circles)
        {
            Include(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius);
            Include(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius);
        }

        foreach (var arc in arcs)
        {
            Include(arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius);
            Include(arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius);
        }

        return any ? (minX, minY, maxX, maxY) : null;
    }

    private static double Distance(Pt a, Pt b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool IsIgnored(string? layer, string? lineType)
    {
        return Contains(layer) || Contains(lineType);

        static bool Contains(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && IgnoredMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
    }
}
