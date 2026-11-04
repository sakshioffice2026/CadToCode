namespace CadToCode.Business.Services;

/// <summary>One vertex of a joined contour. Bulge is the DXF bulge of the segment that starts at this vertex (0 for a straight segment).</summary>
public sealed record DxfContourPoint(double X, double Y, double Bulge);

/// <summary>A continuous run of lines and arcs joined end to end on one layer.</summary>
public sealed class DxfContour
{
    public string Layer { get; init; } = "0";

    public IReadOnlyList<DxfContourPoint> Points { get; init; } = Array.Empty<DxfContourPoint>();

    public bool IsClosed { get; init; }

    public int SegmentCount { get; init; }
}

/// <summary>Joins loose LINE and ARC segments into continuous contours by matching end points.</summary>
public static class DxfContourJoiner
{
    private const double FullCircleDegrees = 359.99;

    private sealed class Segment
    {
        public double X1 { get; init; }

        public double Y1 { get; init; }

        public double X2 { get; init; }

        public double Y2 { get; init; }

        public double Bulge { get; init; }

        public bool Used { get; set; }
    }

    public static IReadOnlyList<DxfContour> Join(
        IReadOnlyList<DxfLineData> lines,
        IReadOnlyList<DxfArcData> arcs,
        double extent)
    {
        var tolerance = Math.Max(0.001, extent * 1e-5);
        var byLayer = new Dictionary<string, List<Segment>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            LayerList(byLayer, line.Layer).Add(new Segment
            {
                X1 = line.X1,
                Y1 = line.Y1,
                X2 = line.X2,
                Y2 = line.Y2,
                Bulge = 0.0
            });
        }

        foreach (var arc in arcs)
        {
            if (arc.SweepDegrees >= FullCircleDegrees)
            {
                continue;
            }

            var start = arc.StartPoint;
            var end = arc.EndPoint;
            var bulge = Math.Tan(arc.SweepDegrees * Math.PI / 180.0 / 4.0);

            if (!double.IsFinite(bulge))
            {
                continue;
            }

            LayerList(byLayer, arc.Layer).Add(new Segment
            {
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Bulge = bulge
            });
        }

        var result = new List<DxfContour>();

        foreach (var pair in byLayer)
        {
            result.AddRange(JoinLayer(pair.Key, pair.Value, tolerance));
        }

        return result;
    }

    private static List<Segment> LayerList(Dictionary<string, List<Segment>> byLayer, string layer)
    {
        var key = string.IsNullOrWhiteSpace(layer) ? "0" : layer;

        if (!byLayer.TryGetValue(key, out var list))
        {
            list = new List<Segment>();
            byLayer[key] = list;
        }

        return list;
    }

    private static List<DxfContour> JoinLayer(string layer, List<Segment> segments, double tolerance)
    {
        var contours = new List<DxfContour>();
        var cell = Math.Max(tolerance * 4.0, 1e-6);
        var grid = new Dictionary<(long, long), List<int>>();

        for (var i = 0; i < segments.Count; i++)
        {
            Register(grid, cell, segments[i].X1, segments[i].Y1, i * 2);
            Register(grid, cell, segments[i].X2, segments[i].Y2, i * 2 + 1);
        }

        for (var seed = 0; seed < segments.Count; seed++)
        {
            if (segments[seed].Used)
            {
                continue;
            }

            segments[seed].Used = true;

            var chain = new List<(int Index, bool Reversed)> { (seed, false) };

            Extend(chain, grid, segments, cell, tolerance);

            if (!IsClosed(chain, segments, tolerance))
            {
                chain.Reverse();

                for (var k = 0; k < chain.Count; k++)
                {
                    chain[k] = (chain[k].Index, !chain[k].Reversed);
                }

                Extend(chain, grid, segments, cell, tolerance);
            }

            var closed = IsClosed(chain, segments, tolerance);
            var points = new List<DxfContourPoint>();

            foreach (var link in chain)
            {
                var segment = segments[link.Index];
                var start = Start(segment, link.Reversed);
                points.Add(new DxfContourPoint(start.X, start.Y, link.Reversed ? -segment.Bulge : segment.Bulge));
            }

            if (!closed)
            {
                var last = chain[chain.Count - 1];
                var end = End(segments[last.Index], last.Reversed);
                points.Add(new DxfContourPoint(end.X, end.Y, 0.0));
            }

            contours.Add(new DxfContour
            {
                Layer = layer,
                Points = points,
                IsClosed = closed,
                SegmentCount = chain.Count
            });
        }

        return contours;
    }

    private static void Extend(
        List<(int Index, bool Reversed)> chain,
        Dictionary<(long, long), List<int>> grid,
        List<Segment> segments,
        double cell,
        double tolerance)
    {
        while (true)
        {
            var last = chain[chain.Count - 1];
            var end = End(segments[last.Index], last.Reversed);

            if (!TryFindNext(grid, segments, cell, tolerance, end.X, end.Y, out var next, out var reversed))
            {
                break;
            }

            segments[next].Used = true;
            chain.Add((next, reversed));
        }
    }

    private static bool IsClosed(List<(int Index, bool Reversed)> chain, List<Segment> segments, double tolerance)
    {
        if (chain.Count < 2)
        {
            return false;
        }

        var first = chain[0];
        var last = chain[chain.Count - 1];
        var start = Start(segments[first.Index], first.Reversed);
        var end = End(segments[last.Index], last.Reversed);

        return Distance(start.X, start.Y, end.X, end.Y) <= tolerance;
    }

    private static bool TryFindNext(
        Dictionary<(long, long), List<int>> grid,
        List<Segment> segments,
        double cell,
        double tolerance,
        double x,
        double y,
        out int index,
        out bool reversed)
    {
        index = -1;
        reversed = false;

        var best = double.MaxValue;
        var cx = (long)Math.Floor(x / cell);
        var cy = (long)Math.Floor(y / cell);

        for (var dx = -1L; dx <= 1L; dx++)
        {
            for (var dy = -1L; dy <= 1L; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var list))
                {
                    continue;
                }

                foreach (var code in list)
                {
                    var candidate = code >> 1;

                    if (segments[candidate].Used)
                    {
                        continue;
                    }

                    var atEnd = (code & 1) == 1;
                    var px = atEnd ? segments[candidate].X2 : segments[candidate].X1;
                    var py = atEnd ? segments[candidate].Y2 : segments[candidate].Y1;
                    var distance = Distance(px, py, x, y);

                    if (distance <= tolerance && distance < best)
                    {
                        best = distance;
                        index = candidate;
                        reversed = atEnd;
                    }
                }
            }
        }

        return index >= 0;
    }

    private static void Register(Dictionary<(long, long), List<int>> grid, double cell, double x, double y, int code)
    {
        var key = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));

        if (!grid.TryGetValue(key, out var list))
        {
            list = new List<int>();
            grid[key] = list;
        }

        list.Add(code);
    }

    private static (double X, double Y) Start(Segment segment, bool reversed)
    {
        return reversed ? (segment.X2, segment.Y2) : (segment.X1, segment.Y1);
    }

    private static (double X, double Y) End(Segment segment, bool reversed)
    {
        return reversed ? (segment.X1, segment.Y1) : (segment.X2, segment.Y2);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
