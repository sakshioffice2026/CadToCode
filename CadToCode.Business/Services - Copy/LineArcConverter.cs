namespace CadToCode.Business.Services;

/// <summary>
/// Turns runs of short connected lines (a tessellated curve) back into true arcs and circles.
/// Only chains whose vertices lie on one circle are converted; everything else passes through unchanged.
/// </summary>
public static class LineArcConverter
{
    private const double MinTurnDegrees = 0.5;
    private const double MaxTurnDegrees = 40.0;
    private const int MinArcVertices = 3;
    private const double MaxLengthRatio = 2.5;

    private sealed class Segment
    {
        public required DrawingEntity Entity { get; init; }

        public (double X, double Y) P0 { get; init; }

        public (double X, double Y) P1 { get; init; }

        public (long, long) K0 { get; init; }

        public (long, long) K1 { get; init; }

        public bool Consumed { get; set; }

        public bool Visited { get; set; }
    }

    public static List<DrawingEntity> Convert(IEnumerable<DrawingEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var all = entities.ToList();
        var result = new List<DrawingEntity>(all.Count);
        var lines = new List<DrawingEntity>();

        foreach (var entity in all)
        {
            if (entity.Kind == DrawingEntityKind.Line)
            {
                lines.Add(entity);
            }
            else
            {
                result.Add(entity);
            }
        }

        if (lines.Count < 5)
        {
            return all;
        }

        var minX = lines.Min(l => Math.Min(l.Start.X, l.End.X));
        var maxX = lines.Max(l => Math.Max(l.Start.X, l.End.X));
        var minY = lines.Min(l => Math.Min(l.Start.Y, l.End.Y));
        var maxY = lines.Max(l => Math.Max(l.Start.Y, l.End.Y));
        var diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        var nodeTolerance = Math.Max(1e-4, diagonal * 1e-6);

        foreach (var group in lines.GroupBy(l => (l.Layer ?? string.Empty, l.LineType ?? string.Empty)))
        {
            ConvertGroup(group.ToList(), nodeTolerance, result);
        }

        return result;
    }

    private static void ConvertGroup(List<DrawingEntity> lines, double nodeTolerance, List<DrawingEntity> output)
    {
        var segments = new List<Segment>();
        foreach (var line in lines)
        {
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= nodeTolerance)
            {
                output.Add(line);
                continue;
            }

            segments.Add(new Segment
            {
                Entity = line,
                P0 = line.Start,
                P1 = line.End,
                K0 = Key(line.Start, nodeTolerance),
                K1 = Key(line.End, nodeTolerance)
            });
        }

        var nodes = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            AddNode(nodes, segments[i].K0, i);
            AddNode(nodes, segments[i].K1, i);
        }

        var created = new List<DrawingEntity>();

        foreach (var node in nodes.Where(n => n.Value.Count != 2).Select(n => n.Key).ToList())
        {
            foreach (var index in nodes[node].ToList())
            {
                if (!segments[index].Visited)
                {
                    Walk(segments, nodes, index, node, nodeTolerance, created);
                }
            }
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (!segments[i].Visited)
            {
                Walk(segments, nodes, i, segments[i].K0, nodeTolerance, created);
            }
        }

        foreach (var segment in segments)
        {
            if (!segment.Consumed)
            {
                output.Add(segment.Entity);
            }
        }

        output.AddRange(created);
    }

    private static void Walk(
        List<Segment> segments,
        Dictionary<(long, long), List<int>> nodes,
        int startIndex,
        (long, long) startKey,
        double nodeTolerance,
        List<DrawingEntity> created)
    {
        var points = new List<(double X, double Y)>();
        var indices = new List<int>();

        var current = startIndex;
        var forward = segments[current].K0 == startKey;
        points.Add(forward ? segments[current].P0 : segments[current].P1);
        points.Add(forward ? segments[current].P1 : segments[current].P0);
        var endKey = forward ? segments[current].K1 : segments[current].K0;
        segments[current].Visited = true;
        indices.Add(current);

        while (nodes[endKey].Count == 2)
        {
            var next = nodes[endKey].FirstOrDefault(i => i != current && !segments[i].Visited, -1);
            if (next < 0)
            {
                break;
            }

            var nextForward = segments[next].K0 == endKey;
            points.Add(nextForward ? segments[next].P1 : segments[next].P0);
            endKey = nextForward ? segments[next].K1 : segments[next].K0;
            segments[next].Visited = true;
            indices.Add(next);
            current = next;
        }

        var closed = endKey == startKey && indices.Count > 2;
        ProcessChain(segments, points, indices, closed, nodeTolerance, created);
    }

    private static void ProcessChain(
        List<Segment> segments,
        List<(double X, double Y)> points,
        List<int> indices,
        bool closed,
        double nodeTolerance,
        List<DrawingEntity> created)
    {
        if (indices.Count < MinArcVertices + 1)
        {
            return;
        }

        var template = segments[indices[0]].Entity;

        if (closed)
        {
            var unique = points.Take(points.Count - 1).ToList();
            var turns = Enumerable.Range(0, unique.Count).Select(i => Turn(unique[(i + unique.Count - 1) % unique.Count], unique[i], unique[(i + 1) % unique.Count])).ToList();

            if (turns.All(IsArcTurn) && (turns.All(t => t > 0) || turns.All(t => t < 0)) && Math.Abs(Math.Abs(turns.Sum()) - 360.0) < 5.0)
            {
                if (TryFitCircle(unique, out var cx, out var cy, out var radius, nodeTolerance))
                {
                    created.Add(DrawingEntity.CreateCircle(cx, cy, radius, template.Layer, template.LineType, template.Handle));
                    indices.ForEach(i => segments[i].Consumed = true);
                }

                return;
            }

            var cut = turns.FindIndex(t => !IsArcTurn(t));
            if (cut < 0)
            {
                cut = 1;
                while (cut < turns.Count && Math.Sign(turns[cut]) == Math.Sign(turns[cut - 1]))
                {
                    cut++;
                }

                cut %= turns.Count;
            }

            points = unique.Skip(cut).Concat(unique.Take(cut)).ToList();
            points.Add(points[0]);
            indices = indices.Skip(cut).Concat(indices.Take(cut)).ToList();
        }

        var count = indices.Count;
        var vertexTurns = new double[count + 1];
        for (var v = 1; v < count; v++)
        {
            vertexTurns[v] = Turn(points[v - 1], points[v], points[v + 1]);
        }

        var vertex = 1;
        while (vertex < count)
        {
            if (!IsArcTurn(vertexTurns[vertex]))
            {
                vertex++;
                continue;
            }

            var first = vertex;
            var sign = Math.Sign(vertexTurns[vertex]);
            while (vertex < count && IsArcTurn(vertexTurns[vertex]) && Math.Sign(vertexTurns[vertex]) == sign)
            {
                vertex++;
            }

            var last = vertex - 1;
            if (last - first + 1 >= MinArcVertices)
            {
                TryCreateArc(segments, points, indices, first - 1, last + 1, sign, nodeTolerance, template, created);
            }
        }
    }

    private static void TryCreateArc(
        List<Segment> segments,
        List<(double X, double Y)> points,
        List<int> indices,
        int firstPoint,
        int lastPoint,
        int sign,
        double nodeTolerance,
        DrawingEntity template,
        List<DrawingEntity> created)
    {
        var run = points.Skip(firstPoint).Take(lastPoint - firstPoint + 1).ToList();

        var lengths = new List<double>();
        for (var i = 0; i < run.Count - 1; i++)
        {
            lengths.Add(Distance(run[i], run[i + 1]));
        }

        if (lengths.Max() / Math.Max(lengths.Min(), 1e-12) > MaxLengthRatio)
        {
            return;
        }

        if (!TryFitCircle(run, out var cx, out var cy, out var radius, nodeTolerance))
        {
            return;
        }

        var expected = lengths.Sum(l => 2.0 * Math.Asin(Math.Min(1.0, l / (2.0 * radius)))) * 180.0 / Math.PI;
        var startDegrees = Math.Atan2(run[0].Y - cy, run[0].X - cx) * 180.0 / Math.PI;
        var endDegrees = Math.Atan2(run[^1].Y - cy, run[^1].X - cx) * 180.0 / Math.PI;
        var sweep = sign > 0 ? Mod360(endDegrees - startDegrees) : Mod360(startDegrees - endDegrees);

        if (expected >= 359.0 || Math.Abs(sweep - expected) > Math.Max(5.0, expected * 0.05))
        {
            return;
        }

        created.Add(sign > 0
            ? DrawingEntity.CreateArc(cx, cy, radius, startDegrees, endDegrees, template.Layer, template.LineType, template.Handle)
            : DrawingEntity.CreateArc(cx, cy, radius, endDegrees, startDegrees, template.Layer, template.LineType, template.Handle));

        for (var i = firstPoint; i < lastPoint; i++)
        {
            segments[indices[i]].Consumed = true;
        }
    }

    /// <summary>Least squares circle through the points (Kasa fit), accepted only when every point is close to it.</summary>
    private static bool TryFitCircle(List<(double X, double Y)> points, out double cx, out double cy, out double radius, double nodeTolerance)
    {
        cx = cy = radius = 0.0;
        var n = points.Count;
        if (n < 3)
        {
            return false;
        }

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var (x, y) in points)
        {
            var z = x * x + y * y;
            sx += x;
            sy += y;
            sxx += x * x;
            syy += y * y;
            sxy += x * y;
            sxz += x * z;
            syz += y * z;
            sz += z;
        }

        // Normal equations for z + D x + E y + F = 0
        var a = new[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var b = new[] { -sxz, -syz, -sz };
        if (!Solve3(a, b, out var solution))
        {
            return false;
        }

        cx = -solution[0] / 2.0;
        cy = -solution[1] / 2.0;
        var square = cx * cx + cy * cy - solution[2];
        if (square <= 0.0 || double.IsNaN(square) || double.IsInfinity(square))
        {
            return false;
        }

        radius = Math.Sqrt(square);
        var limit = radius * 0.003 + nodeTolerance * 5.0;
        var fitCx = cx;
        var fitCy = cy;
        var fitRadius = radius;
        return points.All(p => Math.Abs(Math.Sqrt((p.X - fitCx) * (p.X - fitCx) + (p.Y - fitCy) * (p.Y - fitCy)) - fitRadius) <= limit);
    }

    private static bool Solve3(double[,] a, double[] b, out double[] x)
    {
        x = new double[3];
        var m = new double[3, 4];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                m[r, c] = a[r, c];
            }

            m[r, 3] = b[r];
        }

        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                return false;
            }

            for (var c = 0; c < 4; c++)
            {
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }

            for (var r = 0; r < 3; r++)
            {
                if (r == col)
                {
                    continue;
                }

                var factor = m[r, col] / m[col, col];
                for (var c = col; c < 4; c++)
                {
                    m[r, c] -= factor * m[col, c];
                }
            }
        }

        for (var r = 0; r < 3; r++)
        {
            x[r] = m[r, 3] / m[r, r];
        }

        return true;
    }

    private static double Turn((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        var v1x = b.X - a.X;
        var v1y = b.Y - a.Y;
        var v2x = c.X - b.X;
        var v2y = c.Y - b.Y;
        return Math.Atan2(v1x * v2y - v1y * v2x, v1x * v2x + v1y * v2y) * 180.0 / Math.PI;
    }

    private static bool IsArcTurn(double turn)
    {
        var magnitude = Math.Abs(turn);
        return magnitude >= MinTurnDegrees && magnitude <= MaxTurnDegrees;
    }

    private static double Mod360(double degrees)
    {
        var value = degrees % 360.0;
        return value < 0.0 ? value + 360.0 : value;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    private static (long, long) Key((double X, double Y) point, double tolerance)
    {
        return ((long)Math.Round(point.X / tolerance), (long)Math.Round(point.Y / tolerance));
    }

    private static void AddNode(Dictionary<(long, long), List<int>> nodes, (long, long) key, int index)
    {
        if (!nodes.TryGetValue(key, out var list))
        {
            list = new List<int>();
            nodes[key] = list;
        }

        list.Add(index);
    }
}