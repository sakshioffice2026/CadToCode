using CadToCode.Step;

namespace CadToCode.Business.Services;

public sealed class ProfileBuildResult
{
    internal ProfileBuildResult(
        IReadOnlyList<Profile2D> profiles,
        IReadOnlyList<ProfileLoop> loops,
        IReadOnlyList<IReadOnlyList<ProfileSegment>> openChains,
        IReadOnlyList<DrawingEntity> axisEntities,
        IReadOnlyList<string> warnings,
        double tolerance)
    {
        Profiles = profiles;
        Loops = loops;
        OpenChains = openChains;
        AxisEntities = axisEntities;
        Warnings = warnings;
        Tolerance = tolerance;
    }

    public IReadOnlyList<Profile2D> Profiles { get; }

    public IReadOnlyList<ProfileLoop> Loops { get; }

    public IReadOnlyList<IReadOnlyList<ProfileSegment>> OpenChains { get; }

    public IReadOnlyList<DrawingEntity> AxisEntities { get; }

    public IReadOnlyList<string> Warnings { get; }

    public double Tolerance { get; }

    public bool HasProfiles => Profiles.Count > 0;
}

public static class ProfileBuilder
{
    private const double MinTolerance = 1e-6;

    public static ProfileBuildResult Build(
        IEnumerable<DrawingEntity> entities,
        double? tolerance = null,
        bool outlinesOnly = true)
    {
        if (entities is null)
        {
            throw new ArgumentNullException(nameof(entities));
        }

        if (tolerance.HasValue && (!double.IsFinite(tolerance.Value) || tolerance.Value < MinTolerance))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be a number of at least 1e-6.");
        }

        var warnings = new List<string>();
        var axis = new List<DrawingEntity>();
        var outline = new List<DrawingEntity>();

        foreach (var entity in entities)
        {
            if (entity is null)
            {
                continue;
            }

            if (entity.IsAxisLine)
            {
                axis.Add(entity);
            }
            else if (!outlinesOnly || entity.IsOutlineCandidate)
            {
                outline.Add(entity);
            }
        }

        var tol = tolerance ?? AutoTolerance(outline);
        var loops = new List<ProfileLoop>();
        var pieces = new List<ProfileSegment>();

        foreach (var entity in outline)
        {
            var segments = entity.ToSegments();

            if (segments.Count == 0)
            {
                continue;
            }

            if (segments.Count == 1 && segments[0] is ArcSegment2D { IsFullCircle: true })
            {
                if (!TryAddLoop(loops, segments, tol))
                {
                    warnings.Add("A circle in the drawing could not be used as a loop.");
                }

                continue;
            }

            if (entity.Kind == DrawingEntityKind.Polyline && entity.IsClosed && TryAddLoop(loops, segments, tol))
            {
                continue;
            }

            pieces.AddRange(segments);
        }

        var unique = RemoveDuplicateSegments(pieces, tol);
        var openChains = new List<IReadOnlyList<ProfileSegment>>();

        var closedChains = Chain(unique, tol, out var open);

        foreach (var chain in closedChains)
        {
            if (!TryAddLoop(loops, chain, tol))
            {
                warnings.Add(
                    "A closed chain of " + chain.Count + " segments could not be used as a loop (zero area or invalid).");
            }
        }

        openChains.AddRange(open);

        if (openChains.Count > 0)
        {
            warnings.Add(
                openChains.Count + (openChains.Count == 1 ? " open chain was" : " open chains were")
                + " left out because the segments do not form a closed loop within a tolerance of "
                + tol.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        var distinct = RemoveDuplicateLoops(loops, tol);
        var profiles = BuildProfiles(distinct, tol, warnings);

        if (profiles.Count == 0 && distinct.Count == 0 && outline.Count > 0)
        {
            warnings.Add("No closed loops were found in the drawing.");
        }

        return new ProfileBuildResult(profiles, distinct, openChains, axis, warnings, tol);
    }

    private static double AutoTolerance(IReadOnlyList<DrawingEntity> entities)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var any = false;

        foreach (var entity in entities)
        {
            foreach (var segment in entity.ToSegments())
            {
                var b = segment.Bounds;

                minX = Math.Min(minX, b.MinX);
                minY = Math.Min(minY, b.MinY);
                maxX = Math.Max(maxX, b.MaxX);
                maxY = Math.Max(maxY, b.MaxY);
                any = true;
            }
        }

        if (!any)
        {
            return 1e-3;
        }

        var extent = Math.Max(maxX - minX, maxY - minY);

        return Math.Clamp(extent * 1e-5, 1e-4, 1e-2);
    }

    private static bool TryAddLoop(List<ProfileLoop> loops, IEnumerable<ProfileSegment> segments, double tolerance)
    {
        if (ProfileLoop.TryCreate(segments, tolerance, out var loop, out _) && loop is not null)
        {
            loops.Add(loop);
            return true;
        }

        return false;
    }

    private static List<ProfileSegment> RemoveDuplicateSegments(IReadOnlyList<ProfileSegment> segments, double tolerance)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProfileSegment>();

        foreach (var segment in segments)
        {
            if (segment.Length <= tolerance)
            {
                continue;
            }

            if (seen.Add(Signature(segment, tolerance)))
            {
                result.Add(segment);
            }
        }

        return result;
    }

    private static string Signature(ProfileSegment segment, double tolerance)
    {
        var a = Quantize(segment.Start, tolerance);
        var b = Quantize(segment.End, tolerance);

        if (a.CompareTo(b) > 0)
        {
            (a, b) = (b, a);
        }

        if (segment is ArcSegment2D arc)
        {
            var mid = Quantize(arc.PointAt(0.5), tolerance);
            var center = Quantize(arc.Center, tolerance);

            return "A|" + a + "|" + b + "|" + mid + "|" + center;
        }

        return "L|" + a + "|" + b;
    }

    private static string Quantize((double X, double Y) point, double tolerance)
    {
        var x = (long)Math.Round(point.X / tolerance);
        var y = (long)Math.Round(point.Y / tolerance);

        return x + "," + y;
    }

    private static List<List<ProfileSegment>> Chain(
        IReadOnlyList<ProfileSegment> pieces,
        double tolerance,
        out List<List<ProfileSegment>> openChains)
    {
        var closedChains = new List<List<ProfileSegment>>();
        openChains = new List<List<ProfileSegment>>();

        var index = new EndpointIndex(pieces, tolerance);
        var used = new bool[pieces.Count];

        for (var seed = 0; seed < pieces.Count; seed++)
        {
            if (used[seed])
            {
                continue;
            }

            used[seed] = true;

            var chain = new List<ProfileSegment> { pieces[seed] };
            var closed = false;

            while (true)
            {
                if (chain.Count >= 2 && Distance(chain[^1].End, chain[0].Start) <= tolerance)
                {
                    closed = true;
                    break;
                }

                var next = index.Nearest(chain[^1].End, tolerance, used);

                if (next is null)
                {
                    break;
                }

                used[next.Value.Segment] = true;

                var segment = pieces[next.Value.Segment];
                chain.Add(next.Value.IsStart ? segment : segment.Reversed());
            }

            while (!closed)
            {
                var next = index.Nearest(chain[0].Start, tolerance, used);

                if (next is null)
                {
                    break;
                }

                used[next.Value.Segment] = true;

                var segment = pieces[next.Value.Segment];
                chain.Insert(0, next.Value.IsStart ? segment.Reversed() : segment);

                if (chain.Count >= 2 && Distance(chain[^1].End, chain[0].Start) <= tolerance)
                {
                    closed = true;
                }
            }

            if (closed)
            {
                closedChains.Add(chain);
            }
            else
            {
                openChains.Add(chain);
            }
        }

        return closedChains;
    }

    private static List<ProfileLoop> RemoveDuplicateLoops(IReadOnlyList<ProfileLoop> loops, double tolerance)
    {
        var result = new List<ProfileLoop>();

        foreach (var loop in loops)
        {
            var b = loop.Bounds;
            var duplicate = false;

            foreach (var other in result)
            {
                var o = other.Bounds;

                if (Math.Abs(o.MinX - b.MinX) <= tolerance
                    && Math.Abs(o.MinY - b.MinY) <= tolerance
                    && Math.Abs(o.MaxX - b.MaxX) <= tolerance
                    && Math.Abs(o.MaxY - b.MaxY) <= tolerance
                    && Math.Abs(other.Area - loop.Area) <= Math.Max(1e-6, loop.Area * 1e-6))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                result.Add(loop);
            }
        }

        return result;
    }

    private static List<Profile2D> BuildProfiles(IReadOnlyList<ProfileLoop> loops, double tolerance, List<string> warnings)
    {
        var count = loops.Count;
        var order = Enumerable.Range(0, count).OrderByDescending(i => loops[i].Area).ToList();
        var parent = new int[count];
        var depth = new int[count];

        Array.Fill(parent, -1);

        foreach (var i in order)
        {
            var best = -1;

            foreach (var j in order)
            {
                if (j == i)
                {
                    continue;
                }

                if (loops[j].Area <= loops[i].Area)
                {
                    continue;
                }

                if (!loops[j].Contains(loops[i]))
                {
                    continue;
                }

                if (best < 0 || loops[j].Area < loops[best].Area)
                {
                    best = j;
                }
            }

            parent[i] = best;
            depth[i] = best < 0 ? 0 : depth[best] + 1;
        }

        var holesByOuter = new Dictionary<int, List<ProfileLoop>>();

        foreach (var i in order)
        {
            if (depth[i] % 2 == 0)
            {
                holesByOuter[i] = new List<ProfileLoop>();
            }
        }

        foreach (var i in order)
        {
            if (depth[i] % 2 == 1 && holesByOuter.TryGetValue(parent[i], out var holes))
            {
                holes.Add(loops[i]);
            }
        }

        var profiles = new List<Profile2D>();

        foreach (var i in order)
        {
            if (!holesByOuter.TryGetValue(i, out var holes))
            {
                continue;
            }

            if (Profile2D.TryCreate(loops[i], holes, tolerance, out var profile, out var issues) && profile is not null)
            {
                profiles.Add(profile);
                continue;
            }

            warnings.Add("A profile with " + holes.Count + " holes was invalid: " + string.Join(" ", issues));

            if (holes.Count > 0
                && Profile2D.TryCreate(loops[i], null, tolerance, out var plain, out _)
                && plain is not null)
            {
                profiles.Add(plain);
                warnings.Add("The same profile was kept without its holes.");
            }
        }

        return profiles;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed class EndpointIndex
    {
        private readonly Dictionary<(long X, long Y), List<(int Segment, bool IsStart)>> _cells = new();
        private readonly IReadOnlyList<ProfileSegment> _segments;
        private readonly double _cell;

        public EndpointIndex(IReadOnlyList<ProfileSegment> segments, double cell)
        {
            _segments = segments;
            _cell = cell;

            for (var i = 0; i < segments.Count; i++)
            {
                Add(i, true, segments[i].Start);
                Add(i, false, segments[i].End);
            }
        }

        public (int Segment, bool IsStart)? Nearest((double X, double Y) point, double tolerance, bool[] used)
        {
            var cx = CellOf(point.X);
            var cy = CellOf(point.Y);

            (int Segment, bool IsStart)? best = null;
            var bestDistance = double.MaxValue;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    {
                        continue;
                    }

                    foreach (var item in list)
                    {
                        if (used[item.Segment])
                        {
                            continue;
                        }

                        var candidate = item.IsStart ? _segments[item.Segment].Start : _segments[item.Segment].End;
                        var distance = Distance(candidate, point);

                        if (distance <= tolerance && distance < bestDistance)
                        {
                            best = item;
                            bestDistance = distance;
                        }
                    }
                }
            }

            return best;
        }

        private void Add(int segment, bool isStart, (double X, double Y) point)
        {
            var key = (CellOf(point.X), CellOf(point.Y));

            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<(int Segment, bool IsStart)>();
                _cells[key] = list;
            }

            list.Add((segment, isStart));
        }

        private long CellOf(double value)
        {
            return (long)Math.Floor(value / _cell);
        }
    }
}