using CadToCode.Models.Dtos;
using CadToCode.Step;

namespace CadToCode.Business.Services;

public enum DrawingEntityKind
{
    Line = 1,
    Arc = 2,
    Circle = 3,
    Polyline = 4
}

public readonly record struct DrawingVertex(double X, double Y, double Bulge = 0.0);

public sealed class DrawingEntity
{
    private const double TwoPi = Math.PI * 2.0;
    private const double Epsilon = 1e-12;

    private static readonly string[] AxisMarkers = { "CENTER", "AXIS", "PHANTOM" };
    private static readonly string[] DashedMarkers = { "HIDDEN", "DASH", "DOT", "DIVIDE" };
    private static readonly string[] AnnotationLayers = { "DIM", "TEXT", "NOTE", "HATCH", "DEFPOINTS", "HIDDEN" };

    private DrawingEntity(DrawingEntityKind kind)
    {
        Kind = kind;
    }

    public DrawingEntityKind Kind { get; }

    public string? Layer { get; private init; }

    public string? LineType { get; private init; }

    public string? Handle { get; private init; }

    public (double X, double Y) Start { get; private init; }

    public (double X, double Y) End { get; private init; }

    public (double X, double Y) Center { get; private init; }

    public double Radius { get; private init; }

    public double StartAngle { get; private init; }

    public double SweepAngle { get; private init; }

    public IReadOnlyList<DrawingVertex> Vertices { get; private init; } = Array.Empty<DrawingVertex>();

    public bool IsClosed { get; private init; }

    public bool IsFullCircle =>
        Kind == DrawingEntityKind.Circle
        || (Kind == DrawingEntityKind.Arc && Math.Abs(SweepAngle) >= TwoPi - 1e-9);

    public bool IsAxisLine => ContainsAny(LineType, AxisMarkers) || ContainsAny(Layer, AxisMarkers);

    public bool IsOutlineCandidate =>
        !IsAxisLine
        && !ContainsAny(LineType, DashedMarkers)
        && !ContainsAny(Layer, AnnotationLayers);

    public static DrawingEntity CreateLine(
        double x0,
        double y0,
        double x1,
        double y1,
        string? layer = null,
        string? lineType = null,
        string? handle = null)
    {
        RequireFinite(x0, nameof(x0));
        RequireFinite(y0, nameof(y0));
        RequireFinite(x1, nameof(x1));
        RequireFinite(y1, nameof(y1));

        return new DrawingEntity(DrawingEntityKind.Line)
        {
            Start = (x0, y0),
            End = (x1, y1),
            Layer = layer,
            LineType = lineType,
            Handle = handle
        };
    }

    public static DrawingEntity CreateArc(
        double centerX,
        double centerY,
        double radius,
        double startDegrees,
        double endDegrees,
        string? layer = null,
        string? lineType = null,
        string? handle = null)
    {
        RequireFinite(centerX, nameof(centerX));
        RequireFinite(centerY, nameof(centerY));
        RequireFinite(startDegrees, nameof(startDegrees));
        RequireFinite(endDegrees, nameof(endDegrees));
        RequirePositive(radius, nameof(radius));

        var sweep = (endDegrees - startDegrees) * Math.PI / 180.0;
        sweep %= TwoPi;

        if (sweep <= Epsilon)
        {
            sweep += TwoPi;
        }

        return new DrawingEntity(DrawingEntityKind.Arc)
        {
            Center = (centerX, centerY),
            Radius = radius,
            StartAngle = startDegrees * Math.PI / 180.0,
            SweepAngle = sweep,
            Layer = layer,
            LineType = lineType,
            Handle = handle
        };
    }

    public static DrawingEntity CreateCircle(
        double centerX,
        double centerY,
        double radius,
        string? layer = null,
        string? lineType = null,
        string? handle = null)
    {
        RequireFinite(centerX, nameof(centerX));
        RequireFinite(centerY, nameof(centerY));
        RequirePositive(radius, nameof(radius));

        return new DrawingEntity(DrawingEntityKind.Circle)
        {
            Center = (centerX, centerY),
            Radius = radius,
            StartAngle = 0.0,
            SweepAngle = TwoPi,
            Layer = layer,
            LineType = lineType,
            Handle = handle
        };
    }

    public static DrawingEntity CreatePolyline(
        IEnumerable<DrawingVertex> vertices,
        bool isClosed,
        string? layer = null,
        string? lineType = null,
        string? handle = null)
    {
        if (vertices is null)
        {
            throw new ArgumentNullException(nameof(vertices));
        }

        var list = new List<DrawingVertex>();

        foreach (var vertex in vertices)
        {
            RequireFinite(vertex.X, nameof(vertices));
            RequireFinite(vertex.Y, nameof(vertices));
            RequireFinite(vertex.Bulge, nameof(vertices));
            list.Add(vertex);
        }

        if (list.Count < 2)
        {
            throw new ArgumentException("A polyline needs at least two vertices.", nameof(vertices));
        }

        return new DrawingEntity(DrawingEntityKind.Polyline)
        {
            Vertices = list,
            IsClosed = isClosed,
            Layer = layer,
            LineType = lineType,
            Handle = handle
        };
    }

    public static DrawingEntity CreateSpline(
        IEnumerable<(double X, double Y)> sampledPoints,
        bool isClosed,
        string? layer = null,
        string? lineType = null,
        string? handle = null)
    {
        if (sampledPoints is null)
        {
            throw new ArgumentNullException(nameof(sampledPoints));
        }

        var points = new List<DrawingVertex>();

        foreach (var point in sampledPoints)
        {
            RequireFinite(point.X, nameof(sampledPoints));
            RequireFinite(point.Y, nameof(sampledPoints));

            if (points.Count > 0)
            {
                var previous = points[^1];
                var dx = point.X - previous.X;
                var dy = point.Y - previous.Y;

                if (Math.Sqrt(dx * dx + dy * dy) <= Epsilon)
                {
                    continue;
                }
            }

            points.Add(new DrawingVertex(point.X, point.Y));
        }

        return CreatePolyline(points, isClosed, layer, lineType, handle);
    }

    public static IReadOnlyList<DrawingEntity> FromGeometry(ExtractedGeometry geometry, bool includeHatchBoundaries = false)
    {
        if (geometry is null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        var list = new List<DrawingEntity>();

        foreach (var line in geometry.Lines)
        {
            var source = line;
            TryAdd(list, () => CreateLine(
                source.Start.X, source.Start.Y, source.End.X, source.End.Y,
                source.Layer, source.LineType, source.Handle));
        }

        foreach (var arc in geometry.Arcs)
        {
            var source = arc;
            TryAdd(list, () => CreateArc(
                source.Center.X, source.Center.Y, source.Radius, source.StartAngle, source.EndAngle,
                source.Layer, source.LineType, source.Handle));
        }

        foreach (var circle in geometry.Circles)
        {
            var source = circle;
            TryAdd(list, () => CreateCircle(
                source.Center.X, source.Center.Y, source.Radius,
                source.Layer, source.LineType, source.Handle));
        }

        foreach (var polyline in geometry.Polylines)
        {
            var source = polyline;
            TryAdd(list, () => CreatePolyline(
                source.Vertices.Select(v => new DrawingVertex(v.X, v.Y, v.Bulge)),
                source.IsClosed, source.Layer, source.LineType, source.Handle));
        }

        if (includeHatchBoundaries)
        {
            foreach (var hatch in geometry.Hatches)
            {
                foreach (var path in hatch.BoundaryPaths)
                {
                    if (path.Count < 3)
                    {
                        continue;
                    }

                    var source = path;
                    var layer = hatch.Layer;
                    var handle = hatch.Handle;

                    TryAdd(list, () => CreateSpline(
                        source.Select(p => (p.X, p.Y)), true, layer, hatch.LineType, handle));
                }
            }
        }

        return list;
    }

    internal IReadOnlyList<ProfileSegment> ToSegments()
    {
        switch (Kind)
        {
            case DrawingEntityKind.Line:
                return Distance(Start, End) > Epsilon
                    ? new List<ProfileSegment> { new LineSegment2D(Start, End) }
                    : new List<ProfileSegment>();

            case DrawingEntityKind.Arc:
            case DrawingEntityKind.Circle:
                return new List<ProfileSegment> { new ArcSegment2D(Center, Radius, StartAngle, SweepAngle) };

            default:
                return PolylineSegments();
        }
    }

    private List<ProfileSegment> PolylineSegments()
    {
        var result = new List<ProfileSegment>();
        var count = Vertices.Count;
        var last = IsClosed ? count : count - 1;

        for (var i = 0; i < last; i++)
        {
            var a = Vertices[i];
            var b = Vertices[(i + 1) % count];

            if (Distance((a.X, a.Y), (b.X, b.Y)) <= Epsilon)
            {
                continue;
            }

            if (Math.Abs(a.Bulge) > 1e-9)
            {
                try
                {
                    result.Add(ArcSegment2D.FromBulge((a.X, a.Y), (b.X, b.Y), a.Bulge));
                    continue;
                }
                catch (ArgumentException)
                {
                }
            }

            result.Add(new LineSegment2D(a.X, a.Y, b.X, b.Y));
        }

        return result;
    }

    private static void TryAdd(List<DrawingEntity> list, Func<DrawingEntity> create)
    {
        try
        {
            list.Add(create());
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool ContainsAny(string? text, string[] markers)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void RequireFinite(double value, string parameter)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("The value must be a finite number.", parameter);
        }
    }

    private static void RequirePositive(double value, string parameter)
    {
        if (!double.IsFinite(value) || value <= Epsilon)
        {
            throw new ArgumentException("The value must be a number greater than zero.", parameter);
        }
    }
}