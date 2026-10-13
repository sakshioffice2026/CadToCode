using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services;

public static class ContourMapper
{
    private const int CircleSides = 48;
    private const double ArcStepDegrees = 10.0;
    private const double FullArcDegrees = 359.5;
    private const int MaxGraphSegments = 40000;
    private const int MaxFaces = 20000;
    private const int MaxContours = 1500;
    private const double StrokeHalfWidthFactor = 10.0;

    private readonly record struct Seg(double X1, double Y1, double X2, double Y2, string? Layer);

    private sealed class Graph
    {
        private readonly double _tolerance;
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<int>> _cells = new();

        public Graph(double tolerance)
        {
            _tolerance = tolerance;
            _cell = tolerance * 2.0;
        }

        public List<(double X, double Y)> Vertices { get; } = new();

        public int GetVertex(double x, double y)
        {
            var cx = (long)Math.Floor(x / _cell);
            var cy = (long)Math.Floor(y / _cell);

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue((cx + dx, cy + dy), out var bucket))
                    {
                        continue;
                    }

                    foreach (var id in bucket)
                    {
                        var v = Vertices[id];
                        if (Math.Abs(v.X - x) <= _tolerance && Math.Abs(v.Y - y) <= _tolerance)
                        {
                            return id;
                        }
                    }
                }
            }

            var created = Vertices.Count;
            Vertices.Add((x, y));

            if (!_cells.TryGetValue((cx, cy), out var own))
            {
                own = new List<int>();
                _cells[(cx, cy)] = own;
            }

            own.Add(created);
            return created;
        }
    }

    public static List<GeometryContour> Map(ExtractedGeometry geometry, double tolerance)
    {
        var result = new List<GeometryContour>();

        if (geometry is null)
        {
            return result;
        }

        if (!double.IsFinite(tolerance) || tolerance <= 0)
        {
            tolerance = 1e-6;
        }

        var segments = new List<Seg>();
        var segmentKeys = new HashSet<(long, long, long, long)>();
        var circles = new List<GeometryContour>();
        var closedPolylines = new List<GeometryContour>();

        foreach (var polyline in geometry.Polylines)
        {
            var points = FlattenPolyline(polyline, out var closed);
            if (points.Count < 2)
            {
                continue;
            }

            AddChain(segments, segmentKeys, points, closed, polyline.Layer, tolerance);

            if (closed)
            {
                var contour = CreateContour(points, polyline.Layer, ContourSource.ClosedPolyline, tolerance);
                if (contour is not null)
                {
                    closedPolylines.Add(contour);
                }
            }
        }

        foreach (var line in geometry.Lines)
        {
            AddSegment(segments, segmentKeys, line.Start.X, line.Start.Y, line.End.X, line.End.Y, line.Layer, tolerance);
        }

        foreach (var arc in geometry.Arcs)
        {
            if (!double.IsFinite(arc.Radius) || arc.Radius <= 0)
            {
                continue;
            }

            var sweep = ArcSweep(arc);

            if (sweep >= FullArcDegrees)
            {
                var full = CircleContour(arc.Center.X, arc.Center.Y, arc.Radius, arc.Layer, tolerance);
                if (full is not null)
                {
                    circles.Add(full);
                }

                continue;
            }

            AddChain(segments, segmentKeys, FlattenArc(arc, sweep), false, arc.Layer, tolerance);
        }

        foreach (var circle in geometry.Circles)
        {
            if (!double.IsFinite(circle.Radius) || circle.Radius <= 0)
            {
                continue;
            }

            var contour = CircleContour(circle.Center.X, circle.Center.Y, circle.Radius, circle.Layer, tolerance);
            if (contour is not null)
            {
                circles.Add(contour);
            }
        }

        var candidates = new List<GeometryContour>(circles);
        var leftoverChains = new List<List<(double X, double Y)>>();
        var leftoverLayers = new List<string?>();

        if (segments.Count > 0 && segments.Count <= MaxGraphSegments)
        {
            candidates.AddRange(TraceFaces(segments, tolerance, leftoverChains, leftoverLayers));
        }
        else
        {
            candidates.AddRange(closedPolylines);
        }

        var quantum = Math.Max(tolerance * 5.0, 1e-9);
        var accepted = new List<GeometryContour>();
        var grid = new Dictionary<(long, long), List<int>>();
        var interior = new Dictionary<GeometryContour, (double X, double Y)>();

        foreach (var candidate in candidates)
        {
            if (TryAccept(accepted, grid, candidate, quantum))
            {
                interior[candidate] = InteriorPoint(candidate);
            }
        }

        foreach (var hatch in geometry.Hatches)
        {
            foreach (var path in hatch.BoundaryPaths)
            {
                var points = path.Select(p => (X: p.X, Y: p.Y)).ToList();
                var contour = CreateContour(points, hatch.Layer, ContourSource.Hatch, tolerance);
                if (contour is null)
                {
                    continue;
                }

                var point = InteriorPoint(contour);
                if (OverlapsExisting(contour, point, accepted, interior))
                {
                    continue;
                }

                if (TryAccept(accepted, grid, contour, quantum))
                {
                    interior[contour] = point;
                }
            }
        }

        if (accepted.Count == 0)
        {
            for (var i = 0; i < leftoverChains.Count; i++)
            {
                var strip = OffsetStrip(leftoverChains[i], tolerance * StrokeHalfWidthFactor);
                if (strip is null)
                {
                    continue;
                }

                var contour = CreateContour(strip, leftoverLayers[i], ContourSource.Stroke, tolerance);
                if (contour is not null && TryAccept(accepted, grid, contour, quantum))
                {
                    interior[contour] = InteriorPoint(contour);
                }
            }
        }

        result = accepted
            .OrderByDescending(c => c.Area)
            .ThenBy(c => c.MinX)
            .ThenBy(c => c.MinY)
            .Take(MaxContours)
            .ToList();

        AssignNesting(result, tolerance, interior);

        return result;
    }

    private static List<GeometryContour> TraceFaces(
        List<Seg> segments,
        double tolerance,
        List<List<(double X, double Y)>> leftoverChains,
        List<string?> leftoverLayers)
    {
        var faces = new List<GeometryContour>();
        var graph = new Graph(tolerance);
        var edgeLayers = new Dictionary<long, string?>();

        foreach (var piece in Node(segments, tolerance))
        {
            var a = graph.GetVertex(piece.X1, piece.Y1);
            var b = graph.GetVertex(piece.X2, piece.Y2);

            if (a == b)
            {
                continue;
            }

            var key = EdgeKey(a, b);
            if (!edgeLayers.ContainsKey(key))
            {
                edgeLayers[key] = piece.Layer;
            }
        }

        if (edgeLayers.Count == 0)
        {
            return faces;
        }

        var vertexCount = graph.Vertices.Count;
        var adjacency = new List<int>[vertexCount];
        var parent = new int[vertexCount];

        for (var i = 0; i < vertexCount; i++)
        {
            adjacency[i] = new List<int>();
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

        foreach (var key in edgeLayers.Keys)
        {
            var (lo, hi) = Decode(key);
            adjacency[lo].Add(hi);
            adjacency[hi].Add(lo);

            var rootLo = Find(lo);
            var rootHi = Find(hi);
            if (rootLo != rootHi)
            {
                parent[rootHi] = rootLo;
            }
        }

        var active = new HashSet<long>(edgeLayers.Keys);
        var degree = new int[vertexCount];
        var queue = new Queue<int>();

        for (var v = 0; v < vertexCount; v++)
        {
            degree[v] = adjacency[v].Count;
            if (degree[v] == 1)
            {
                queue.Enqueue(v);
            }
        }

        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (degree[v] != 1)
            {
                continue;
            }

            foreach (var n in adjacency[v])
            {
                if (active.Remove(EdgeKey(v, n)))
                {
                    degree[v]--;
                    degree[n]--;

                    if (degree[n] == 1)
                    {
                        queue.Enqueue(n);
                    }

                    break;
                }
            }
        }

        var sorted = new int[vertexCount][];
        var slot = new Dictionary<long, int>();

        for (var v = 0; v < vertexCount; v++)
        {
            var neighbors = adjacency[v].Where(n => active.Contains(EdgeKey(v, n))).ToList();

            if (neighbors.Count == 0)
            {
                sorted[v] = Array.Empty<int>();
                continue;
            }

            var origin = graph.Vertices[v];

            neighbors.Sort((p, q) =>
            {
                var ap = Math.Atan2(graph.Vertices[p].Y - origin.Y, graph.Vertices[p].X - origin.X);
                var aq = Math.Atan2(graph.Vertices[q].Y - origin.Y, graph.Vertices[q].X - origin.X);
                return ap.CompareTo(aq);
            });

            sorted[v] = neighbors.ToArray();

            for (var k = 0; k < neighbors.Count; k++)
            {
                slot[DirectedKey(v, neighbors[k])] = k;
            }
        }

        var visited = new HashSet<long>();
        var covered = new HashSet<long>();
        var guardLimit = edgeLayers.Count * 2 + 4;

        for (var u = 0; u < vertexCount && faces.Count < MaxFaces; u++)
        {
            foreach (var v in sorted[u])
            {
                if (visited.Contains(DirectedKey(u, v)))
                {
                    continue;
                }

                var ids = new List<int>();
                var cu = u;
                var cv = v;
                var guard = 0;

                while (visited.Add(DirectedKey(cu, cv)))
                {
                    ids.Add(cu);

                    var ring = sorted[cv];
                    var index = slot[DirectedKey(cv, cu)];
                    var next = ring[(index - 1 + ring.Length) % ring.Length];

                    cu = cv;
                    cv = next;

                    if (++guard > guardLimit)
                    {
                        break;
                    }
                }

                RemoveSpikes(ids);

                if (ids.Count < 3)
                {
                    continue;
                }

                var points = ids.Select(id => graph.Vertices[id]).ToList();

                if (SignedArea(points) <= tolerance * tolerance)
                {
                    continue;
                }

                points = SimplifyCollinear(points, tolerance * 0.01);

                var contour = CreateContour(points, DominantLayer(ids, edgeLayers), ContourSource.TracedFace, tolerance);
                if (contour is null)
                {
                    continue;
                }

                contour.ComponentId = Find(ids[0]) + 1;
                TryFitCircle(contour, tolerance);
                faces.Add(contour);

                for (var k = 0; k < ids.Count; k++)
                {
                    covered.Add(EdgeKey(ids[k], ids[(k + 1) % ids.Count]));
                }
            }
        }

        var rest = edgeLayers.Keys.Where(k => !covered.Contains(k)).ToList();
        if (rest.Count > 0)
        {
            BuildChains(rest, graph, edgeLayers, leftoverChains, leftoverLayers);
        }

        return faces;
    }

    private static List<Seg> Node(List<Seg> segments, double tolerance)
    {
        var count = segments.Count;
        var splits = new List<double>?[count];
        var order = Enumerable.Range(0, count)
            .OrderBy(i => Math.Min(segments[i].X1, segments[i].X2))
            .ToArray();

        for (var a = 0; a < count; a++)
        {
            var i = order[a];
            var si = segments[i];
            var iMaxX = Math.Max(si.X1, si.X2);
            var iMinY = Math.Min(si.Y1, si.Y2);
            var iMaxY = Math.Max(si.Y1, si.Y2);

            for (var b = a + 1; b < count; b++)
            {
                var j = order[b];
                var sj = segments[j];

                if (Math.Min(sj.X1, sj.X2) > iMaxX + tolerance)
                {
                    break;
                }

                if (Math.Min(sj.Y1, sj.Y2) > iMaxY + tolerance || Math.Max(sj.Y1, sj.Y2) < iMinY - tolerance)
                {
                    continue;
                }

                Intersect(segments, i, j, tolerance, splits);
            }
        }

        var pieces = new List<Seg>(count + 16);

        for (var i = 0; i < count; i++)
        {
            var s = segments[i];
            var list = splits[i];

            if (list is null || list.Count == 0)
            {
                pieces.Add(s);
                continue;
            }

            list.Sort();

            var previous = 0.0;
            var px = s.X1;
            var py = s.Y1;

            foreach (var t in list)
            {
                if (t <= previous)
                {
                    continue;
                }

                var x = s.X1 + (s.X2 - s.X1) * t;
                var y = s.Y1 + (s.Y2 - s.Y1) * t;
                pieces.Add(new Seg(px, py, x, y, s.Layer));
                previous = t;
                px = x;
                py = y;
            }

            pieces.Add(new Seg(px, py, s.X2, s.Y2, s.Layer));
        }

        return pieces;
    }

    private static void Intersect(List<Seg> segments, int i, int j, double tolerance, List<double>?[] splits)
    {
        var a = segments[i];
        var b = segments[j];
        var dx = a.X2 - a.X1;
        var dy = a.Y2 - a.Y1;
        var ex = b.X2 - b.X1;
        var ey = b.Y2 - b.Y1;
        var lenA = Math.Sqrt(dx * dx + dy * dy);
        var lenB = Math.Sqrt(ex * ex + ey * ey);

        if (lenA <= tolerance || lenB <= tolerance)
        {
            return;
        }

        Touch(a, dx, dy, lenA, b.X1, b.Y1, tolerance, i, splits);
        Touch(a, dx, dy, lenA, b.X2, b.Y2, tolerance, i, splits);
        Touch(b, ex, ey, lenB, a.X1, a.Y1, tolerance, j, splits);
        Touch(b, ex, ey, lenB, a.X2, a.Y2, tolerance, j, splits);

        var denominator = dx * ey - dy * ex;
        if (Math.Abs(denominator) <= 1e-9 * lenA * lenB)
        {
            return;
        }

        var qx = b.X1 - a.X1;
        var qy = b.Y1 - a.Y1;
        var t = (qx * ey - qy * ex) / denominator;
        var u = (qx * dy - qy * dx) / denominator;
        var epsA = tolerance / lenA;
        var epsB = tolerance / lenB;

        if (t > epsA && t < 1.0 - epsA && u > epsB && u < 1.0 - epsB)
        {
            AddSplit(splits, i, t);
            AddSplit(splits, j, u);
        }
    }

    private static void Touch(
        Seg segment,
        double dx,
        double dy,
        double length,
        double px,
        double py,
        double tolerance,
        int index,
        List<double>?[] splits)
    {
        var t = ((px - segment.X1) * dx + (py - segment.Y1) * dy) / (length * length);
        var eps = tolerance / length;

        if (t <= eps || t >= 1.0 - eps)
        {
            return;
        }

        var cx = segment.X1 + t * dx;
        var cy = segment.Y1 + t * dy;
        var ox = px - cx;
        var oy = py - cy;

        if (Math.Sqrt(ox * ox + oy * oy) <= tolerance)
        {
            AddSplit(splits, index, t);
        }
    }

    private static void AddSplit(List<double>?[] splits, int index, double t)
    {
        var list = splits[index];
        if (list is null)
        {
            list = new List<double>();
            splits[index] = list;
        }

        list.Add(t);
    }

    private static void BuildChains(
        List<long> rest,
        Graph graph,
        Dictionary<long, string?> edgeLayers,
        List<List<(double X, double Y)>> chains,
        List<string?> layers)
    {
        var incident = new Dictionary<int, List<int>>();

        for (var e = 0; e < rest.Count; e++)
        {
            var (lo, hi) = Decode(rest[e]);

            if (!incident.TryGetValue(lo, out var loList))
            {
                loList = new List<int>();
                incident[lo] = loList;
            }

            loList.Add(e);

            if (!incident.TryGetValue(hi, out var hiList))
            {
                hiList = new List<int>();
                incident[hi] = hiList;
            }

            hiList.Add(e);
        }

        var used = new bool[rest.Count];

        void Walk(int start, int firstEdge)
        {
            var points = new List<(double X, double Y)> { graph.Vertices[start] };
            var vertex = start;
            var edge = firstEdge;
            var layer = edgeLayers[rest[edge]];

            while (true)
            {
                used[edge] = true;

                var (lo, hi) = Decode(rest[edge]);
                vertex = lo == vertex ? hi : lo;
                points.Add(graph.Vertices[vertex]);

                var list = incident[vertex];
                if (list.Count != 2)
                {
                    break;
                }

                var next = list[0] == edge ? list[1] : list[0];
                if (used[next])
                {
                    break;
                }

                edge = next;
            }

            chains.Add(points);
            layers.Add(layer);
        }

        foreach (var pair in incident.ToList())
        {
            if (pair.Value.Count == 2)
            {
                continue;
            }

            foreach (var edge in pair.Value)
            {
                if (!used[edge])
                {
                    Walk(pair.Key, edge);
                }
            }
        }

        for (var e = 0; e < rest.Count; e++)
        {
            if (!used[e])
            {
                Walk(Decode(rest[e]).Lo, e);
            }
        }
    }

    private static string? DominantLayer(List<int> ids, Dictionary<long, string?> edgeLayers)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var k = 0; k < ids.Count; k++)
        {
            if (edgeLayers.TryGetValue(EdgeKey(ids[k], ids[(k + 1) % ids.Count]), out var layer)
                && !string.IsNullOrWhiteSpace(layer))
            {
                counts[layer] = counts.TryGetValue(layer, out var count) ? count + 1 : 1;
            }
        }

        return counts.Count == 0 ? null : counts.OrderByDescending(p => p.Value).First().Key;
    }

    private static void RemoveSpikes(List<int> ids)
    {
        var changed = true;

        while (changed && ids.Count >= 3)
        {
            changed = false;

            for (var i = 0; i < ids.Count && ids.Count >= 3; i++)
            {
                var previous = ids[(i + ids.Count - 1) % ids.Count];
                var next = ids[(i + 1) % ids.Count];

                if (previous == next || ids[i] == previous)
                {
                    ids.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
    }

    private static List<(double X, double Y)> SimplifyCollinear(List<(double X, double Y)> points, double epsilon)
    {
        var list = new List<(double X, double Y)>(points);
        var changed = true;

        while (changed && list.Count > 3)
        {
            changed = false;

            for (var i = 0; i < list.Count && list.Count > 3; i++)
            {
                var previous = list[(i + list.Count - 1) % list.Count];
                var next = list[(i + 1) % list.Count];

                if (DistanceToSegment(list[i], previous, next) <= epsilon)
                {
                    list.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }

        return list;
    }

    private static double DistanceToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared <= 1e-24)
        {
            return Distance(p, a);
        }

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0.0, 1.0);
        return Distance(p, (a.X + t * dx, a.Y + t * dy));
    }

    private static GeometryContour? CreateContour(
        List<(double X, double Y)> points,
        string? layer,
        ContourSource source,
        double tolerance)
    {
        var cleaned = Clean(points, tolerance);

        if (cleaned.Count < 3)
        {
            return null;
        }

        var area = Math.Abs(SignedArea(cleaned));
        var perimeter = Perimeter(cleaned);

        if (!(area > tolerance * tolerance * 4.0) || perimeter <= 0 || area / perimeter < tolerance * 0.5)
        {
            return null;
        }

        var contour = new GeometryContour
        {
            Layer = layer,
            Source = source,
            Area = area,
            Points = cleaned.Select(p => new GeometryPoint(p.X, p.Y)).ToList()
        };

        contour.UpdateBounds();
        return contour;
    }

    private static GeometryContour? CircleContour(double cx, double cy, double radius, string? layer, double tolerance)
    {
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || radius <= tolerance)
        {
            return null;
        }

        var ring = new List<(double X, double Y)>(CircleSides);

        for (var k = 0; k < CircleSides; k++)
        {
            var angle = 2.0 * Math.PI * k / CircleSides;
            ring.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
        }

        var contour = CreateContour(ring, layer, ContourSource.Circle, tolerance);
        if (contour is null)
        {
            return null;
        }

        contour.IsCircle = true;
        contour.CenterX = cx;
        contour.CenterY = cy;
        contour.Radius = radius;
        contour.Area = Math.PI * radius * radius;
        return contour;
    }

    private static void TryFitCircle(GeometryContour contour, double tolerance)
    {
        var width = contour.MaxX - contour.MinX;
        var height = contour.MaxY - contour.MinY;

        if (contour.Points.Count < 12 || width <= 0)
        {
            return;
        }

        if (Math.Abs(width - height) > 0.01 * width + tolerance)
        {
            return;
        }

        var cx = (contour.MinX + contour.MaxX) / 2.0;
        var cy = (contour.MinY + contour.MaxY) / 2.0;
        var radius = width / 2.0;

        foreach (var point in contour.Points)
        {
            var distance = Math.Sqrt((point.X - cx) * (point.X - cx) + (point.Y - cy) * (point.Y - cy));
            if (Math.Abs(distance - radius) > 0.01 * radius + tolerance)
            {
                return;
            }
        }

        contour.IsCircle = true;
        contour.CenterX = cx;
        contour.CenterY = cy;
        contour.Radius = radius;
        contour.Area = Math.PI * radius * radius;
    }

    private static bool TryAccept(
        List<GeometryContour> accepted,
        Dictionary<(long, long), List<int>> grid,
        GeometryContour contour,
        double quantum)
    {
        var cell = quantum * 4.0;
        var cx = (long)Math.Floor(contour.MinX / cell);
        var cy = (long)Math.Floor(contour.MinY / cell);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (Similar(accepted[index], contour, quantum))
                    {
                        return false;
                    }
                }
            }
        }

        if (!grid.TryGetValue((cx, cy), out var own))
        {
            own = new List<int>();
            grid[(cx, cy)] = own;
        }

        own.Add(accepted.Count);
        accepted.Add(contour);
        return true;
    }

    private static bool Similar(GeometryContour a, GeometryContour b, double quantum)
    {
        if (Math.Abs(a.MinX - b.MinX) > quantum
            || Math.Abs(a.MaxX - b.MaxX) > quantum
            || Math.Abs(a.MinY - b.MinY) > quantum
            || Math.Abs(a.MaxY - b.MaxY) > quantum)
        {
            return false;
        }

        var larger = Math.Max(a.Area, b.Area);
        return Math.Abs(a.Area - b.Area) <= Math.Max(0.03 * larger, quantum * quantum);
    }

    private static bool OverlapsExisting(
        GeometryContour hatch,
        (double X, double Y) point,
        List<GeometryContour> accepted,
        Dictionary<GeometryContour, (double X, double Y)> interior)
    {
        foreach (var existing in accepted)
        {
            if (existing.MinX > hatch.MaxX || existing.MaxX < hatch.MinX
                || existing.MinY > hatch.MaxY || existing.MaxY < hatch.MinY)
            {
                continue;
            }

            if (PointInPolygon(point.X, point.Y, existing.Points))
            {
                return true;
            }

            if (existing.Area < hatch.Area
                && interior.TryGetValue(existing, out var other)
                && PointInPolygon(other.X, other.Y, hatch.Points))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssignNesting(
        List<GeometryContour> list,
        double tolerance,
        Dictionary<GeometryContour, (double X, double Y)> interior)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var inner = list[i];
            inner.ParentIndex = -1;
            inner.Depth = 0;

            if (!interior.TryGetValue(inner, out var point))
            {
                point = InteriorPoint(inner);
                interior[inner] = point;
            }

            for (var j = i - 1; j >= 0; j--)
            {
                var outer = list[j];

                if (outer.ComponentId > 0 && outer.ComponentId == inner.ComponentId)
                {
                    continue;
                }

                if (outer.Area <= inner.Area)
                {
                    continue;
                }

                if (inner.MinX < outer.MinX - tolerance
                    || inner.MaxX > outer.MaxX + tolerance
                    || inner.MinY < outer.MinY - tolerance
                    || inner.MaxY > outer.MaxY + tolerance)
                {
                    continue;
                }

                if (PointInPolygon(point.X, point.Y, outer.Points))
                {
                    inner.ParentIndex = j;
                    inner.Depth = outer.Depth + 1;
                    break;
                }
            }
        }
    }

    private static (double X, double Y) InteriorPoint(GeometryContour contour)
    {
        var points = contour.Points;
        var fractions = new[] { 0.5, 0.37, 0.63, 0.25, 0.75, 0.12, 0.88 };

        foreach (var fraction in fractions)
        {
            var y = contour.MinY + (contour.MaxY - contour.MinY) * fraction;
            var crossings = new List<double>();

            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];

                if ((a.Y > y) != (b.Y > y))
                {
                    crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }

            crossings.Sort();

            var bestWidth = 0.0;
            var bestMid = 0.0;

            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var width = crossings[k + 1] - crossings[k];
                if (width > bestWidth)
                {
                    bestWidth = width;
                    bestMid = (crossings[k] + crossings[k + 1]) / 2.0;
                }
            }

            if (bestWidth > 0)
            {
                return (bestMid, y);
            }
        }

        return (points.Average(p => p.X), points.Average(p => p.Y));
    }

    private static bool PointInPolygon(double x, double y, List<GeometryPoint> polygon)
    {
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static List<(double X, double Y)>? OffsetStrip(List<(double X, double Y)> points, double halfWidth)
    {
        var clean = new List<(double X, double Y)>();

        foreach (var point in points)
        {
            if (clean.Count == 0 || Distance(clean[^1], point) > halfWidth * 0.01)
            {
                clean.Add(point);
            }
        }

        if (clean.Count < 2)
        {
            return null;
        }

        var normals = new List<(double X, double Y)>();

        for (var i = 0; i < clean.Count - 1; i++)
        {
            var dx = clean[i + 1].X - clean[i].X;
            var dy = clean[i + 1].Y - clean[i].Y;
            var length = Math.Sqrt(dx * dx + dy * dy);

            if (length <= 0)
            {
                return null;
            }

            normals.Add((-dy / length, dx / length));
        }

        var left = new List<(double X, double Y)>();
        var right = new List<(double X, double Y)>();

        for (var i = 0; i < clean.Count; i++)
        {
            var before = normals[Math.Max(i - 1, 0)];
            var after = normals[Math.Min(i, normals.Count - 1)];
            var sx = before.X + after.X;
            var sy = before.Y + after.Y;
            var magnitude = Math.Sqrt(sx * sx + sy * sy);

            double nx;
            double ny;
            double scale;

            if (magnitude <= 1e-9)
            {
                nx = after.X;
                ny = after.Y;
                scale = halfWidth;
            }
            else
            {
                nx = sx / magnitude;
                ny = sy / magnitude;
                var cosHalf = nx * after.X + ny * after.Y;
                scale = halfWidth / Math.Max(cosHalf, 0.5);
            }

            left.Add((clean[i].X + nx * scale, clean[i].Y + ny * scale));
            right.Add((clean[i].X - nx * scale, clean[i].Y - ny * scale));
        }

        right.Reverse();
        left.AddRange(right);
        return left;
    }

    private static void AddChain(
        List<Seg> segments,
        HashSet<(long, long, long, long)> keys,
        List<(double X, double Y)> points,
        bool close,
        string? layer,
        double tolerance)
    {
        for (var i = 0; i + 1 < points.Count; i++)
        {
            AddSegment(segments, keys, points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, layer, tolerance);
        }

        if (close && points.Count > 2)
        {
            AddSegment(segments, keys, points[^1].X, points[^1].Y, points[0].X, points[0].Y, layer, tolerance);
        }
    }

    private static void AddSegment(
        List<Seg> segments,
        HashSet<(long, long, long, long)> keys,
        double x1,
        double y1,
        double x2,
        double y2,
        string? layer,
        double tolerance)
    {
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2))
        {
            return;
        }

        var dx = x2 - x1;
        var dy = y2 - y1;

        if (dx * dx + dy * dy <= tolerance * tolerance)
        {
            return;
        }

        var ax = (long)Math.Round(x1 / tolerance);
        var ay = (long)Math.Round(y1 / tolerance);
        var bx = (long)Math.Round(x2 / tolerance);
        var by = (long)Math.Round(y2 / tolerance);

        if (ax > bx || (ax == bx && ay > by))
        {
            (ax, bx) = (bx, ax);
            (ay, by) = (by, ay);
        }

        if (keys.Add((ax, ay, bx, by)))
        {
            segments.Add(new Seg(x1, y1, x2, y2, layer));
        }
    }

    private static List<(double X, double Y)> FlattenPolyline(GeometryPolyline polyline, out bool closed)
    {
        var vertices = polyline.Vertices;
        var result = new List<(double X, double Y)>();
        closed = false;

        if (vertices.Count < 2)
        {
            return result;
        }

        var count = vertices.Count;
        var segmentCount = polyline.IsClosed ? count : count - 1;

        for (var i = 0; i < count; i++)
        {
            var current = vertices[i];
            var start = (X: current.X, Y: current.Y);
            result.Add(start);

            if (i >= segmentCount || Math.Abs(current.Bulge) <= 1e-9)
            {
                continue;
            }

            var next = vertices[(i + 1) % count];
            AppendBulgeArc(result, start, (next.X, next.Y), current.Bulge);
        }

        closed = polyline.IsClosed;

        if (!closed && result.Count >= 4 && Distance(result[0], result[^1]) <= 1e-6)
        {
            result.RemoveAt(result.Count - 1);
            closed = true;
        }

        return result;
    }

    private static void AppendBulgeArc(
        List<(double X, double Y)> points,
        (double X, double Y) start,
        (double X, double Y) end,
        double bulge)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);

        if (chord < 1e-12)
        {
            return;
        }

        var offset = chord / 2.0 * (1.0 - bulge * bulge) / (2.0 * bulge);
        var centerX = (start.X + end.X) / 2.0 + (-dy / chord) * offset;
        var centerY = (start.Y + end.Y) / 2.0 + (dx / chord) * offset;
        var radius = Math.Sqrt(offset * offset + chord * chord / 4.0);
        var sweep = 4.0 * Math.Atan(bulge);
        var first = Math.Atan2(start.Y - centerY, start.X - centerX);
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (ArcStepDegrees * Math.PI / 180.0)));

        for (var k = 1; k < steps; k++)
        {
            var angle = first + sweep * k / steps;
            points.Add((centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)));
        }
    }

    private static double ArcSweep(GeometryArc arc)
    {
        var sweep = arc.EndAngle - arc.StartAngle;

        while (sweep <= 0)
        {
            sweep += 360.0;
        }

        if (sweep > 360.0)
        {
            sweep %= 360.0;

            if (sweep <= 0)
            {
                sweep = 360.0;
            }
        }

        return sweep;
    }

    private static List<(double X, double Y)> FlattenArc(GeometryArc arc, double sweep)
    {
        var points = new List<(double X, double Y)>();
        var steps = Math.Max(2, (int)Math.Ceiling(sweep / ArcStepDegrees));

        for (var k = 0; k <= steps; k++)
        {
            var angle = (arc.StartAngle + sweep * k / steps) * Math.PI / 180.0;
            points.Add((
                arc.Center.X + arc.Radius * Math.Cos(angle),
                arc.Center.Y + arc.Radius * Math.Sin(angle)));
        }

        return points;
    }

    private static List<(double X, double Y)> Clean(List<(double X, double Y)> points, double tolerance)
    {
        var result = new List<(double X, double Y)>(points.Count);

        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                continue;
            }

            if (result.Count == 0 || Distance(result[^1], point) > tolerance)
            {
                result.Add(point);
            }
        }

        while (result.Count > 1 && Distance(result[0], result[^1]) <= tolerance)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static double SignedArea(List<(double X, double Y)> points)
    {
        var sum = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            sum += points[i].X * next.Y - next.X * points[i].Y;
        }

        return sum / 2.0;
    }

    private static double Perimeter(List<(double X, double Y)> points)
    {
        var sum = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            sum += Distance(points[i], points[(i + 1) % points.Count]);
        }

        return sum;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static long EdgeKey(int a, int b)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        return ((long)lo << 32) | (uint)hi;
    }

    private static long DirectedKey(int from, int to)
    {
        return ((long)from << 32) | (uint)to;
    }

    private static (int Lo, int Hi) Decode(long key)
    {
        return ((int)(key >> 32), (int)(key & 0xFFFFFFFFL));
    }
}