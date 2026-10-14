namespace CadToCode.Step;

internal static class ProfileGeometry
{
    public const double TwoPi = Math.PI * 2.0;

    private const double IntersectionEpsilon = 1e-9;
    private const long MaxPairTests = 4_000_000;
    private const int MaxSelfIntersectionEdges = 3000;

    public static double NormalizePositive(double angle)
    {
        angle %= TwoPi;

        if (angle < 0)
        {
            angle += TwoPi;
        }

        return angle;
    }

    public static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static bool PointInPolygon(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) point)
    {
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];

            if (((pi.Y > point.Y) != (pj.Y > point.Y))
                && (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static bool SelfIntersects(IReadOnlyList<(double X, double Y)> polygon)
    {
        var n = polygon.Count;

        if (n < 4 || n > MaxSelfIntersectionEdges)
        {
            return false;
        }

        for (var i = 0; i < n; i++)
        {
            var a0 = polygon[i];
            var a1 = polygon[(i + 1) % n];

            for (var j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1)
                {
                    continue;
                }

                var b0 = polygon[j];
                var b1 = polygon[(j + 1) % n];

                if (SegmentsIntersect(a0, a1, b0, b1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool PolygonsIntersect(
        IReadOnlyList<(double X, double Y)> a,
        IReadOnlyList<(double X, double Y)> b)
    {
        if ((long)a.Count * b.Count > MaxPairTests)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];

            for (var j = 0; j < b.Count; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Count];

                if (SegmentsIntersect(a0, a1, b0, b1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SegmentsIntersect(
        (double X, double Y) p1,
        (double X, double Y) p2,
        (double X, double Y) p3,
        (double X, double Y) p4)
    {
        var d1 = Orient(p3, p4, p1);
        var d2 = Orient(p3, p4, p2);
        var d3 = Orient(p1, p2, p3);
        var d4 = Orient(p1, p2, p4);

        if (((d1 > IntersectionEpsilon && d2 < -IntersectionEpsilon) || (d1 < -IntersectionEpsilon && d2 > IntersectionEpsilon))
            && ((d3 > IntersectionEpsilon && d4 < -IntersectionEpsilon) || (d3 < -IntersectionEpsilon && d4 > IntersectionEpsilon)))
        {
            return true;
        }

        if (Math.Abs(d1) <= IntersectionEpsilon && OnSegment(p3, p4, p1))
        {
            return true;
        }

        if (Math.Abs(d2) <= IntersectionEpsilon && OnSegment(p3, p4, p2))
        {
            return true;
        }

        if (Math.Abs(d3) <= IntersectionEpsilon && OnSegment(p1, p2, p3))
        {
            return true;
        }

        if (Math.Abs(d4) <= IntersectionEpsilon && OnSegment(p1, p2, p4))
        {
            return true;
        }

        return false;
    }

    private static double Orient((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
    {
        return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }

    private static bool OnSegment((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        return p.X >= Math.Min(a.X, b.X) - IntersectionEpsilon
            && p.X <= Math.Max(a.X, b.X) + IntersectionEpsilon
            && p.Y >= Math.Min(a.Y, b.Y) - IntersectionEpsilon
            && p.Y <= Math.Max(a.Y, b.Y) + IntersectionEpsilon;
    }
}

public abstract class ProfileSegment
{
    internal ProfileSegment()
    {
    }

    public abstract (double X, double Y) Start { get; }

    public abstract (double X, double Y) End { get; }

    public abstract double Length { get; }

    public abstract (double MinX, double MinY, double MaxX, double MaxY) Bounds { get; }

    internal abstract double GreenArea { get; }

    public abstract (double X, double Y) PointAt(double t);

    public abstract ProfileSegment Reversed();

    public abstract ProfileSegment Translated(double dx, double dy);

    internal abstract void AppendPoints(List<(double X, double Y)> points, double maxDeviation);

    protected static void RequireFinite(double value, string parameter)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameter, "The value must be a finite number.");
        }
    }
}

public sealed class LineSegment2D : ProfileSegment
{
    private readonly (double X, double Y) _start;
    private readonly (double X, double Y) _end;

    public LineSegment2D((double X, double Y) start, (double X, double Y) end)
    {
        RequireFinite(start.X, nameof(start));
        RequireFinite(start.Y, nameof(start));
        RequireFinite(end.X, nameof(end));
        RequireFinite(end.Y, nameof(end));

        if (ProfileGeometry.Distance(start, end) <= 1e-12)
        {
            throw new ArgumentException("A line segment needs two distinct points.", nameof(end));
        }

        _start = start;
        _end = end;
    }

    public LineSegment2D(double x0, double y0, double x1, double y1)
        : this((x0, y0), (x1, y1))
    {
    }

    public override (double X, double Y) Start => _start;

    public override (double X, double Y) End => _end;

    public override double Length => ProfileGeometry.Distance(_start, _end);

    public override (double MinX, double MinY, double MaxX, double MaxY) Bounds =>
        (Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y), Math.Max(_start.X, _end.X), Math.Max(_start.Y, _end.Y));

    internal override double GreenArea => 0.5 * (_start.X * _end.Y - _end.X * _start.Y);

    public override (double X, double Y) PointAt(double t)
    {
        return (_start.X + (_end.X - _start.X) * t, _start.Y + (_end.Y - _start.Y) * t);
    }

    public override ProfileSegment Reversed() => new LineSegment2D(_end, _start);

    public override ProfileSegment Translated(double dx, double dy)
    {
        return new LineSegment2D((_start.X + dx, _start.Y + dy), (_end.X + dx, _end.Y + dy));
    }

    internal override void AppendPoints(List<(double X, double Y)> points, double maxDeviation)
    {
        points.Add(_start);
    }
}

public sealed class ArcSegment2D : ProfileSegment
{
    private const double FullCircleTolerance = 1e-9;

    private readonly (double X, double Y) _start;
    private readonly (double X, double Y) _end;

    public ArcSegment2D((double X, double Y) center, double radius, double startAngle, double sweepAngle)
    {
        RequireFinite(center.X, nameof(center));
        RequireFinite(center.Y, nameof(center));
        RequireFinite(radius, nameof(radius));
        RequireFinite(startAngle, nameof(startAngle));
        RequireFinite(sweepAngle, nameof(sweepAngle));

        if (radius <= 1e-12)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "The arc radius must be greater than zero.");
        }

        if (Math.Abs(sweepAngle) <= 1e-12)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepAngle), "The arc sweep must not be zero.");
        }

        if (Math.Abs(sweepAngle) > ProfileGeometry.TwoPi - FullCircleTolerance)
        {
            sweepAngle = sweepAngle > 0 ? ProfileGeometry.TwoPi : -ProfileGeometry.TwoPi;
        }

        Center = center;
        Radius = radius;
        StartAngle = startAngle;
        SweepAngle = sweepAngle;

        _start = (center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle));
        _end = IsFullCircle
            ? _start
            : (center.X + radius * Math.Cos(startAngle + sweepAngle), center.Y + radius * Math.Sin(startAngle + sweepAngle));
    }

    public (double X, double Y) Center { get; }

    public double Radius { get; }

    public double StartAngle { get; }

    public double SweepAngle { get; }

    public double EndAngle => StartAngle + SweepAngle;

    public bool IsCounterClockwise => SweepAngle > 0;

    public bool IsFullCircle => Math.Abs(Math.Abs(SweepAngle) - ProfileGeometry.TwoPi) <= FullCircleTolerance;

    public double Bulge => Math.Tan(SweepAngle / 4.0);

    public override (double X, double Y) Start => _start;

    public override (double X, double Y) End => _end;

    public override double Length => Radius * Math.Abs(SweepAngle);

    public override (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            var minX = Math.Min(_start.X, _end.X);
            var minY = Math.Min(_start.Y, _end.Y);
            var maxX = Math.Max(_start.X, _end.X);
            var maxY = Math.Max(_start.Y, _end.Y);

            for (var k = 0; k < 4; k++)
            {
                var angle = k * Math.PI / 2.0;
                var delta = SweepAngle >= 0
                    ? ProfileGeometry.NormalizePositive(angle - StartAngle)
                    : ProfileGeometry.NormalizePositive(StartAngle - angle);

                if (delta > Math.Abs(SweepAngle))
                {
                    continue;
                }

                var x = Center.X + Radius * Math.Cos(angle);
                var y = Center.Y + Radius * Math.Sin(angle);

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return (minX, minY, maxX, maxY);
        }
    }

    internal override double GreenArea
    {
        get
        {
            var a0 = StartAngle;
            var a1 = StartAngle + SweepAngle;

            var integral = Radius * Radius * SweepAngle
                + Radius * Center.X * (Math.Sin(a1) - Math.Sin(a0))
                - Radius * Center.Y * (Math.Cos(a1) - Math.Cos(a0));

            return 0.5 * integral;
        }
    }

    public static ArcSegment2D FromDegrees(
        double centerX,
        double centerY,
        double radius,
        double startDegrees,
        double endDegrees,
        bool counterClockwise = true)
    {
        RequireFinite(startDegrees, nameof(startDegrees));
        RequireFinite(endDegrees, nameof(endDegrees));

        var start = startDegrees * Math.PI / 180.0;
        var end = endDegrees * Math.PI / 180.0;

        double sweep;

        if (counterClockwise)
        {
            sweep = ProfileGeometry.NormalizePositive(end - start);
        }
        else
        {
            sweep = -ProfileGeometry.NormalizePositive(start - end);
        }

        if (Math.Abs(sweep) <= 1e-12)
        {
            sweep = counterClockwise ? ProfileGeometry.TwoPi : -ProfileGeometry.TwoPi;
        }

        return new ArcSegment2D((centerX, centerY), radius, start, sweep);
    }

    public static ArcSegment2D FromBulge((double X, double Y) start, (double X, double Y) end, double bulge)
    {
        RequireFinite(bulge, nameof(bulge));

        if (Math.Abs(bulge) <= 1e-12)
        {
            throw new ArgumentOutOfRangeException(nameof(bulge), "A zero bulge is a straight line, not an arc.");
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);

        if (chord <= 1e-12)
        {
            throw new ArgumentException("An arc needs two distinct end points.", nameof(end));
        }

        var radius = chord * (1.0 + bulge * bulge) / (4.0 * Math.Abs(bulge));
        var offset = chord * (1.0 - bulge * bulge) / (4.0 * bulge);

        var mx = (start.X + end.X) / 2.0;
        var my = (start.Y + end.Y) / 2.0;
        var leftX = -dy / chord;
        var leftY = dx / chord;

        var center = (X: mx + leftX * offset, Y: my + leftY * offset);
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var sweep = 4.0 * Math.Atan(bulge);

        return new ArcSegment2D(center, radius, startAngle, sweep);
    }

    public static ArcSegment2D FromThreePoints(
        (double X, double Y) start,
        (double X, double Y) mid,
        (double X, double Y) end)
    {
        var ax = start.X;
        var ay = start.Y;
        var bx = mid.X;
        var by = mid.Y;
        var cx = end.X;
        var cy = end.Y;

        var d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));

        if (Math.Abs(d) <= 1e-12)
        {
            throw new ArgumentException("The three points are collinear and do not define an arc.");
        }

        var a2 = ax * ax + ay * ay;
        var b2 = bx * bx + by * by;
        var c2 = cx * cx + cy * cy;

        var ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
        var uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;

        var center = (X: ux, Y: uy);
        var radius = ProfileGeometry.Distance(center, start);

        var a0 = Math.Atan2(ay - uy, ax - ux);
        var a1 = Math.Atan2(cy - uy, cx - ux);

        var orientation = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        double sweep;

        if (orientation > 0)
        {
            sweep = ProfileGeometry.NormalizePositive(a1 - a0);
        }
        else
        {
            sweep = -ProfileGeometry.NormalizePositive(a0 - a1);
        }

        return new ArcSegment2D(center, radius, a0, sweep);
    }

    public static ArcSegment2D Circle(double centerX, double centerY, double radius, bool counterClockwise = true)
    {
        return new ArcSegment2D(
            (centerX, centerY),
            radius,
            0,
            counterClockwise ? ProfileGeometry.TwoPi : -ProfileGeometry.TwoPi);
    }

    public override (double X, double Y) PointAt(double t)
    {
        var angle = StartAngle + SweepAngle * t;
        return (Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));
    }

    public override ProfileSegment Reversed()
    {
        return new ArcSegment2D(Center, Radius, StartAngle + SweepAngle, -SweepAngle);
    }

    public override ProfileSegment Translated(double dx, double dy)
    {
        return new ArcSegment2D((Center.X + dx, Center.Y + dy), Radius, StartAngle, SweepAngle);
    }

    internal override void AppendPoints(List<(double X, double Y)> points, double maxDeviation)
    {
        var steps = StepCount(maxDeviation);
        points.Add(_start);

        for (var i = 1; i < steps; i++)
        {
            points.Add(PointAt((double)i / steps));
        }
    }

    private int StepCount(double maxDeviation)
    {
        var deviation = Math.Min(Math.Max(maxDeviation, 1e-9), Radius * 0.01);
        var step = 2.0 * Math.Acos(1.0 - deviation / Radius);

        var steps = step > 1e-9
            ? (int)Math.Ceiling(Math.Abs(SweepAngle) / step)
            : 4096;

        var minimum = (int)Math.Ceiling(Math.Abs(SweepAngle) / (Math.PI / 2.0));

        return Math.Clamp(Math.Max(steps, minimum), 1, 4096);
    }
}

public sealed class ProfileLoop
{
    public const double DefaultTolerance = 1e-6;
    public const double DefaultFlattenDeviation = 0.01;

    private readonly List<ProfileSegment> _segments;
    private List<(double X, double Y)>? _testPolygon;

    private ProfileLoop(List<ProfileSegment> segments, double signedArea, double tolerance)
    {
        _segments = segments;
        SignedArea = signedArea;
        Tolerance = tolerance;
    }

    public IReadOnlyList<ProfileSegment> Segments => _segments;

    public double SignedArea { get; }

    public double Area => Math.Abs(SignedArea);

    public double Tolerance { get; }

    public bool IsCounterClockwise => SignedArea > 0;

    public bool IsCircle => _segments.Count == 1 && _segments[0] is ArcSegment2D;

    public ArcSegment2D? CircleArc => IsCircle ? (ArcSegment2D)_segments[0] : null;

    public bool HasArcs => _segments.Any(s => s is ArcSegment2D);

    public double Perimeter => _segments.Sum(s => s.Length);

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            foreach (var segment in _segments)
            {
                var b = segment.Bounds;
                minX = Math.Min(minX, b.MinX);
                minY = Math.Min(minY, b.MinY);
                maxX = Math.Max(maxX, b.MaxX);
                maxY = Math.Max(maxY, b.MaxY);
            }

            return (minX, minY, maxX, maxY);
        }
    }

    internal IReadOnlyList<(double X, double Y)> TestPolygon => _testPolygon ??= Flatten(DefaultFlattenDeviation);

    public static ProfileLoop Create(IEnumerable<ProfileSegment> segments, double tolerance = DefaultTolerance)
    {
        if (!TryCreate(segments, tolerance, out var loop, out var error))
        {
            throw new ArgumentException(error, nameof(segments));
        }

        return loop!;
    }

    public static bool TryCreate(
        IEnumerable<ProfileSegment> segments,
        double tolerance,
        out ProfileLoop? loop,
        out string error)
    {
        loop = null;
        error = string.Empty;

        if (segments is null)
        {
            error = "The segment list is required.";
            return false;
        }

        if (!double.IsFinite(tolerance) || tolerance <= 0)
        {
            error = "The tolerance must be a number greater than zero.";
            return false;
        }

        var list = new List<ProfileSegment>();

        foreach (var segment in segments)
        {
            if (segment is null)
            {
                error = "The segment list contains a null entry.";
                return false;
            }

            if (segment.Length <= tolerance)
            {
                continue;
            }

            list.Add(segment);
        }

        if (list.Count == 0)
        {
            error = "The loop has no usable segments.";
            return false;
        }

        if (list.Count == 1)
        {
            if (list[0] is not ArcSegment2D arc || !arc.IsFullCircle)
            {
                error = "A loop made of one segment must be a full circle.";
                return false;
            }
        }
        else
        {
            for (var i = 0; i < list.Count; i++)
            {
                var next = list[(i + 1) % list.Count];

                if (ProfileGeometry.Distance(list[i].End, next.Start) > tolerance)
                {
                    error = $"Segment {i} does not connect to segment {(i + 1) % list.Count}; the loop is not closed.";
                    return false;
                }
            }
        }

        var area = list.Sum(s => s.GreenArea);

        if (Math.Abs(area) <= tolerance * tolerance)
        {
            error = "The loop encloses no area.";
            return false;
        }

        loop = new ProfileLoop(list, area, tolerance);
        return true;
    }

    public static ProfileLoop Polygon(IEnumerable<(double X, double Y)> points, double tolerance = DefaultTolerance)
    {
        if (points is null)
        {
            throw new ArgumentNullException(nameof(points));
        }

        var cleaned = new List<(double X, double Y)>();

        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentException("The polygon contains a point that is not a finite number.", nameof(points));
            }

            if (cleaned.Count == 0 || ProfileGeometry.Distance(cleaned[^1], point) > tolerance)
            {
                cleaned.Add(point);
            }
        }

        while (cleaned.Count > 1 && ProfileGeometry.Distance(cleaned[0], cleaned[^1]) <= tolerance)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        if (cleaned.Count < 3)
        {
            throw new ArgumentException("A polygon needs at least three distinct points.", nameof(points));
        }

        var segments = new List<ProfileSegment>(cleaned.Count);

        for (var i = 0; i < cleaned.Count; i++)
        {
            segments.Add(new LineSegment2D(cleaned[i], cleaned[(i + 1) % cleaned.Count]));
        }

        return Create(segments, tolerance);
    }

    public static ProfileLoop Rectangle(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The rectangle position must be finite numbers.");
        }

        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The rectangle size must be numbers greater than zero.");
        }

        return Polygon(new (double X, double Y)[]
        {
            (x, y),
            (x + width, y),
            (x + width, y + height),
            (x, y + height)
        });
    }

    public static ProfileLoop Circle(double centerX, double centerY, double radius)
    {
        return Create(new ProfileSegment[] { ArcSegment2D.Circle(centerX, centerY, radius) });
    }

    public ProfileLoop Reversed()
    {
        var reversed = new List<ProfileSegment>(_segments.Count);

        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            reversed.Add(_segments[i].Reversed());
        }

        return new ProfileLoop(reversed, -SignedArea, Tolerance);
    }

    public ProfileLoop Oriented(bool counterClockwise)
    {
        return IsCounterClockwise == counterClockwise ? this : Reversed();
    }

    public ProfileLoop Translated(double dx, double dy)
    {
        return Create(_segments.Select(s => s.Translated(dx, dy)), Tolerance);
    }

    public List<(double X, double Y)> Flatten(double maxDeviation = DefaultFlattenDeviation)
    {
        if (!double.IsFinite(maxDeviation) || maxDeviation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDeviation), "The deviation must be a number greater than zero.");
        }

        var points = new List<(double X, double Y)>();

        foreach (var segment in _segments)
        {
            segment.AppendPoints(points, maxDeviation);
        }

        return points;
    }

    public bool Contains((double X, double Y) point)
    {
        return ProfileGeometry.PointInPolygon(TestPolygon, point);
    }

    public bool Contains(ProfileLoop inner)
    {
        if (inner is null)
        {
            throw new ArgumentNullException(nameof(inner));
        }

        var samples = 0;
        var step = Math.Max(1, inner._segments.Count / 16);

        for (var i = 0; i < inner._segments.Count; i += step)
        {
            samples++;

            if (!Contains(inner._segments[i].PointAt(0.5)))
            {
                return false;
            }
        }

        return samples > 0;
    }

    public bool Crosses(ProfileLoop other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        return ProfileGeometry.PolygonsIntersect(TestPolygon, other.TestPolygon);
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (ProfileGeometry.SelfIntersects(TestPolygon))
        {
            issues.Add("The loop crosses or touches itself.");
        }

        return issues;
    }
}

public sealed class Profile2D
{
    private Profile2D(ProfileLoop outer, List<ProfileLoop> holes, double tolerance)
    {
        Outer = outer;
        Holes = holes;
        Tolerance = tolerance;
    }

    public ProfileLoop Outer { get; }

    public IReadOnlyList<ProfileLoop> Holes { get; }

    public double Tolerance { get; }

    public int HoleCount => Holes.Count;

    public bool IsCircle => Outer.IsCircle;

    public bool HasArcs => Outer.HasArcs || Holes.Any(h => h.HasArcs);

    public double Area => Outer.Area - Holes.Sum(h => h.Area);

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds => Outer.Bounds;

    public static Profile2D Create(
        ProfileLoop outer,
        IEnumerable<ProfileLoop>? holes = null,
        double tolerance = ProfileLoop.DefaultTolerance)
    {
        if (!TryCreate(outer, holes, tolerance, out var profile, out var issues))
        {
            throw new ArgumentException(string.Join(" ", issues), nameof(outer));
        }

        return profile!;
    }

    public static bool TryCreate(
        ProfileLoop outer,
        IEnumerable<ProfileLoop>? holes,
        double tolerance,
        out Profile2D? profile,
        out IReadOnlyList<string> issues)
    {
        profile = null;

        if (outer is null)
        {
            issues = new[] { "The outer loop is required." };
            return false;
        }

        var cleanHoles = new List<ProfileLoop>();

        if (holes is not null)
        {
            foreach (var hole in holes)
            {
                if (hole is null)
                {
                    continue;
                }

                cleanHoles.Add(hole.Oriented(false));
            }
        }

        var candidate = new Profile2D(outer.Oriented(true), cleanHoles, tolerance);
        var found = candidate.Validate();

        issues = found;

        if (found.Count > 0)
        {
            return false;
        }

        profile = candidate;
        return true;
    }

    public static Profile2D Polygon(
        IEnumerable<(double X, double Y)> outline,
        IEnumerable<IEnumerable<(double X, double Y)>>? holes = null)
    {
        var holeLoops = holes?.Select(h => ProfileLoop.Polygon(h)).ToList();
        return Create(ProfileLoop.Polygon(outline), holeLoops);
    }

    public static Profile2D Rectangle(double x, double y, double width, double height)
    {
        return Create(ProfileLoop.Rectangle(x, y, width, height));
    }

    public static Profile2D Circle(double centerX, double centerY, double radius)
    {
        return Create(ProfileLoop.Circle(centerX, centerY, radius));
    }

    public static IReadOnlyList<Profile2D> FromLoops(
        IEnumerable<ProfileLoop> loops,
        double tolerance = ProfileLoop.DefaultTolerance)
    {
        if (loops is null)
        {
            throw new ArgumentNullException(nameof(loops));
        }

        var all = loops.Where(l => l is not null).ToList();
        var containers = new List<int>[all.Count];
        var depth = new int[all.Count];

        for (var i = 0; i < all.Count; i++)
        {
            containers[i] = new List<int>();

            for (var j = 0; j < all.Count; j++)
            {
                if (i != j && all[j].Area > all[i].Area && all[j].Contains(all[i]))
                {
                    containers[i].Add(j);
                }
            }

            depth[i] = containers[i].Count;
        }

        var holesByOuter = new Dictionary<int, List<ProfileLoop>>();

        for (var i = 0; i < all.Count; i++)
        {
            if (depth[i] % 2 != 0)
            {
                continue;
            }

            holesByOuter[i] = new List<ProfileLoop>();
        }

        for (var i = 0; i < all.Count; i++)
        {
            if (depth[i] % 2 == 0)
            {
                continue;
            }

            var parent = containers[i]
                .Where(j => depth[j] == depth[i] - 1)
                .OrderBy(j => all[j].Area)
                .Cast<int?>()
                .FirstOrDefault();

            if (parent.HasValue && holesByOuter.TryGetValue(parent.Value, out var list))
            {
                list.Add(all[i]);
            }
        }

        var result = new List<Profile2D>();

        foreach (var pair in holesByOuter.OrderByDescending(p => all[p.Key].Area))
        {
            result.Add(Create(all[pair.Key], pair.Value, tolerance));
        }

        return result;
    }

    public Profile2D Translated(double dx, double dy)
    {
        return Create(
            Outer.Translated(dx, dy),
            Holes.Select(h => h.Translated(dx, dy)).ToList(),
            Tolerance);
    }

    public List<(double X, double Y)> FlattenOuter(double maxDeviation = ProfileLoop.DefaultFlattenDeviation)
    {
        return Outer.Flatten(maxDeviation);
    }

    public List<List<(double X, double Y)>> FlattenHoles(double maxDeviation = ProfileLoop.DefaultFlattenDeviation)
    {
        return Holes.Select(h => h.Flatten(maxDeviation)).ToList();
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        foreach (var issue in Outer.Validate())
        {
            issues.Add("Outer loop: " + issue);
        }

        for (var i = 0; i < Holes.Count; i++)
        {
            foreach (var issue in Holes[i].Validate())
            {
                issues.Add($"Hole {i}: {issue}");
            }

            if (!Outer.Contains(Holes[i]))
            {
                issues.Add($"Hole {i} is not inside the outer loop.");
            }
            else if (Outer.Crosses(Holes[i]))
            {
                issues.Add($"Hole {i} crosses or touches the outer loop.");
            }
        }

        for (var i = 0; i < Holes.Count; i++)
        {
            for (var j = i + 1; j < Holes.Count; j++)
            {
                if (Holes[i].Crosses(Holes[j]))
                {
                    issues.Add($"Hole {i} and hole {j} cross or touch.");
                }
                else if (Holes[i].Contains(Holes[j]) || Holes[j].Contains(Holes[i]))
                {
                    issues.Add($"Hole {i} and hole {j} are nested; nested loops must be separate profiles.");
                }
            }
        }

        return issues;
    }
}