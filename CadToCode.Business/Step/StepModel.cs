using System.Text;
using CadToCode.Business.Sandbox;

namespace CadToCode.Step;

public sealed class StepDocument
{
    private readonly List<Solid> _solids = new();

    public StepDocument(string name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Model" : name.Trim();
        SandboxSession.Current?.RegisterDocument(this);
    }

    public string Name { get; }

    public int SolidCount => _solids.Count;

    internal IReadOnlyList<Solid> Solids => _solids;

    private StepAdder? _adder;

    public StepAdder Add => _adder ??= new StepAdder(this);

    internal StepDocument AddSolid(Solid solid)
    {
        if (solid is null)
        {
            throw new ArgumentNullException(nameof(solid));
        }

        SandboxSession.Current?.RegisterSolid();
        _solids.Add(solid);
        return this;
    }

    public string ToStep()
    {
        return StepWriter.Write(this);
    }

    public string ToStep(StepExportOptions? options)
    {
        return StepWriter.Write(this, options);
    }

    public bool Save(string path)
    {
        return Save(path, null);
    }

    public bool Save(string path, StepExportOptions? options)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path is required.", nameof(path));
        }

        var session = SandboxSession.Current;
        if (session is not null)
        {
            session.RecordSave(path);
            return true;
        }

        File.WriteAllText(path, ToStep(options), new UTF8Encoding(false));
        return true;
    }
}

public abstract class Solid
{
    private const double Epsilon = 1e-9;
    private const int MaxPoints = 100000;

    private string _name = "Solid";

    internal Solid()
    {
    }

    public string Name
    {
        get => _name;
        set => _name = string.IsNullOrWhiteSpace(value) ? "Solid" : value.Trim();
    }

    internal double Z { get; private set; }

    internal abstract int FaceCount { get; }

    internal abstract (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds { get; }

    public Solid Named(string name)
    {
        Name = name;
        return this;
    }

    public Solid AtZ(double z)
    {
        if (!double.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(z), "The Z value must be a finite number.");
        }

        Z = z;
        return this;
    }

    public static Solid Extrude(IEnumerable<(double X, double Y)> outline, double depth)
    {
        return Extrude(outline, Array.Empty<(double X, double Y)[]>(), depth);
    }

    public static Solid Extrude(
        IEnumerable<(double X, double Y)> outline,
        IEnumerable<(double X, double Y)[]> holes,
        double depth)
    {
        if (outline is null)
        {
            throw new ArgumentNullException(nameof(outline));
        }

        if (holes is null)
        {
            throw new ArgumentNullException(nameof(holes));
        }

        RequirePositive(depth, nameof(depth), "The extrusion depth");

        var outer = Normalize(outline, "The outline");
        if (outer.Count < 3 || Math.Abs(SignedArea(outer)) <= Epsilon)
        {
            throw new ArgumentException("An outline needs at least three distinct points that enclose an area.", nameof(outline));
        }

        if (SignedArea(outer) < 0)
        {
            outer.Reverse();
        }

        var cleanHoles = new List<List<(double X, double Y)>>();

        foreach (var hole in holes)
        {
            if (hole is null)
            {
                continue;
            }

            var points = Normalize(hole, "A hole");
            if (points.Count < 3 || Math.Abs(SignedArea(points)) <= Epsilon)
            {
                continue;
            }

            if (SignedArea(points) > 0)
            {
                points.Reverse();
            }

            cleanHoles.Add(points);
        }

        return new PrismSolid(outer, cleanHoles, depth);
    }

    public static Solid Cylinder(double x, double y, double radius, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The cylinder position must be finite numbers.");
        }

        RequirePositive(radius, nameof(radius), "The cylinder radius");
        RequirePositive(height, nameof(height), "The cylinder height");

        return new CylinderSolid(x, y, radius, height);
    }

    public static Solid Box(double x, double y, double width, double height, double depth)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "The box position must be finite numbers.");
        }

        RequirePositive(width, nameof(width), "The box width");
        RequirePositive(height, nameof(height), "The box height");

        var outline = new[]
        {
            (X: x, Y: y),
            (X: x + width, Y: y),
            (X: x + width, Y: y + height),
            (X: x, Y: y + height)
        };

        return Extrude(outline, depth);
    }

    public static FramedSolid Extrude(Profile2D profile, double depth)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        RequirePositive(depth, nameof(depth), "The extrusion depth");

        return new ExtrudedProfileSolid(profile, depth);
    }

    public static FramedSolid Revolve(Profile2D profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var bounds = profile.Bounds;

        if (bounds.MinX < -ProfileLoop.DefaultTolerance)
        {
            throw new ArgumentException(
                "A revolved profile must lie on one side of the axis: every X value (the radius) must be zero or greater.",
                nameof(profile));
        }

        if (bounds.MaxX <= Epsilon)
        {
            throw new ArgumentException("A revolved profile must have a radius greater than zero.", nameof(profile));
        }

        return new RevolveSolid(profile);
    }

    public static FramedSolid Sweep(Profile2D profile, SweepRoute route, Vec3? xHint = null)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (route is null)
        {
            throw new ArgumentNullException(nameof(route));
        }

        return new SweepSolid(profile, route, xHint);
    }

    public static FramedSolid Pipe(SweepRoute route, double outerDiameter, double wall)
    {
        if (route is null)
        {
            throw new ArgumentNullException(nameof(route));
        }

        RequirePositive(outerDiameter, nameof(outerDiameter), "The pipe outer diameter");
        RequirePositive(wall, nameof(wall), "The pipe wall thickness");

        var outerRadius = outerDiameter / 2.0;

        if (wall >= outerRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(wall), "The wall thickness must be smaller than half the outer diameter.");
        }

        var profile = Profile2D.Create(
            ProfileLoop.Circle(0, 0, outerRadius),
            new[] { ProfileLoop.Circle(0, 0, outerRadius - wall) });

        return new SweepSolid(profile, route, null);
    }

    public static FramedSolid Cone(double baseRadius, double topRadius, double height)
    {
        RequirePositive(baseRadius, nameof(baseRadius), "The cone base radius");
        RequirePositive(height, nameof(height), "The cone height");

        if (!double.IsFinite(topRadius) || topRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topRadius), "The cone top radius must be zero or greater.");
        }

        return new ConeSolid(baseRadius, topRadius, height);
    }

    public static FramedSolid Sphere(double radius)
    {
        RequirePositive(radius, nameof(radius), "The sphere radius");

        return new SphereSolid(radius);
    }

    public static FramedSolid Torus(double majorRadius, double minorRadius)
    {
        RequirePositive(majorRadius, nameof(majorRadius), "The torus major radius");
        RequirePositive(minorRadius, nameof(minorRadius), "The torus minor radius");

        if (minorRadius >= majorRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(minorRadius), "The torus minor radius must be smaller than the major radius.");
        }

        return new TorusSolid(majorRadius, minorRadius);
    }

    private static void RequirePositive(double value, string parameter, string label)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameter, label + " must be a number greater than zero.");
        }
    }

    private static List<(double X, double Y)> Normalize(IEnumerable<(double X, double Y)> source, string label)
    {
        var result = new List<(double X, double Y)>();

        foreach (var point in source)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentException(label + " contains a point that is not a finite number.");
            }

            if (result.Count == 0 || !Same(result[^1], point))
            {
                result.Add(point);
            }

            if (result.Count > MaxPoints)
            {
                throw new ArgumentException(label + " has too many points.");
            }
        }

        while (result.Count > 1 && Same(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static bool Same((double X, double Y) a, (double X, double Y) b)
    {
        return Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;
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
}

internal sealed class PrismSolid : Solid
{
    public PrismSolid(
        List<(double X, double Y)> outline,
        List<List<(double X, double Y)>> holes,
        double depth)
    {
        Outline = outline;
        Holes = holes;
        Depth = depth;
    }

    public List<(double X, double Y)> Outline { get; }

    public List<List<(double X, double Y)>> Holes { get; }

    public double Depth { get; }

    internal override int FaceCount => 2 + Outline.Count + Holes.Sum(h => h.Count);

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            return (
                Outline.Min(p => p.X),
                Outline.Min(p => p.Y),
                Z,
                Outline.Max(p => p.X),
                Outline.Max(p => p.Y),
                Z + Depth);
        }
    }
}

internal sealed class CylinderSolid : Solid
{
    public CylinderSolid(double x, double y, double radius, double height)
    {
        Cx = x;
        Cy = y;
        Radius = radius;
        Height = height;
    }

    public double Cx { get; }

    public double Cy { get; }

    public double Radius { get; }

    public double Height { get; }

    internal override int FaceCount => 3;

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            return (Cx - Radius, Cy - Radius, Z, Cx + Radius, Cy + Radius, Z + Height);
        }
    }
}

public abstract class FramedSolid : Solid
{
    internal FramedSolid()
    {
        Placement = Frame3D.World;
    }

    public Frame3D Placement { get; private set; }

    internal Frame3D WorldFrame => Placement.Translate(0, 0, Z);

    public new FramedSolid Named(string name)
    {
        base.Named(name);
        return this;
    }

    public new FramedSolid AtZ(double z)
    {
        base.AtZ(z);
        return this;
    }

    public FramedSolid Placed(Frame3D frame)
    {
        Placement = frame ?? throw new ArgumentNullException(nameof(frame));
        return this;
    }

    public FramedSolid Translate(double dx, double dy, double dz)
    {
        Placement = Placement.Translate(dx, dy, dz);
        return this;
    }

    public FramedSolid TranslateLocal(double dx, double dy, double dz)
    {
        Placement = Placement.TranslateLocal(dx, dy, dz);
        return this;
    }

    public FramedSolid Rotate(Vec3 axisDirection, double angleDegrees)
    {
        Placement = Placement.Rotate(axisDirection, angleDegrees);
        return this;
    }

    public FramedSolid RotateAbout(Vec3 pivot, Vec3 axisDirection, double angleDegrees)
    {
        Placement = Placement.RotateAbout(pivot, axisDirection, angleDegrees);
        return this;
    }

    protected static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) BoxBounds(
        Frame3D frame,
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ)
    {
        var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var hi = new[] { double.MinValue, double.MinValue, double.MinValue };

        foreach (var x in new[] { minX, maxX })
        {
            foreach (var y in new[] { minY, maxY })
            {
                foreach (var z in new[] { minZ, maxZ })
                {
                    Expand(lo, hi, frame.ToWorld(x, y, z));
                }
            }
        }

        return (lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
    }

    protected static void Expand(double[] lo, double[] hi, Vec3 point)
    {
        lo[0] = Math.Min(lo[0], point.X);
        lo[1] = Math.Min(lo[1], point.Y);
        lo[2] = Math.Min(lo[2], point.Z);
        hi[0] = Math.Max(hi[0], point.X);
        hi[1] = Math.Max(hi[1], point.Y);
        hi[2] = Math.Max(hi[2], point.Z);
    }
}

internal sealed class ExtrudedProfileSolid : FramedSolid
{
    public ExtrudedProfileSolid(Profile2D profile, double depth)
    {
        Profile = profile;
        Depth = depth;
    }

    public Profile2D Profile { get; }

    public double Depth { get; }

    internal override int FaceCount =>
        2 + Profile.Outer.Segments.Count + Profile.Holes.Sum(h => h.Segments.Count);

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            var b = Profile.Bounds;
            return BoxBounds(WorldFrame, b.MinX, b.MinY, 0, b.MaxX, b.MaxY, Depth);
        }
    }
}

internal sealed class RevolveSolid : FramedSolid
{
    public RevolveSolid(Profile2D profile)
    {
        Profile = profile;
    }

    public Profile2D Profile { get; }

    internal static bool IsOnAxis(ProfileSegment segment)
    {
        return segment is LineSegment2D
            && Math.Abs(segment.Start.X) <= 1e-9
            && Math.Abs(segment.End.X) <= 1e-9;
    }

    internal override int FaceCount
    {
        get
        {
            var count = 0;

            var loops = new List<ProfileLoop> { Profile.Outer };
            loops.AddRange(Profile.Holes);

            foreach (var loop in loops)
            {
                foreach (var segment in loop.Segments)
                {
                    if (!IsOnAxis(segment))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            var b = Profile.Bounds;
            var radius = Math.Max(Math.Abs(b.MinX), Math.Abs(b.MaxX));

            return BoxBounds(WorldFrame, -radius, -radius, b.MinY, radius, radius, b.MaxY);
        }
    }
}

internal sealed class SweepSolid : FramedSolid
{
    private const int ArcSamples = 24;

    private readonly List<Frame3D> _startFrames = new();

    public SweepSolid(Profile2D profile, SweepRoute route, Vec3? xHint)
    {
        if (route.Segments.Count == 0)
        {
            throw new ArgumentException("A sweep route needs at least one line or arc segment.", nameof(route));
        }

        Profile = profile;
        Route = route;

        var first = route.Segments[0];
        var tangent = first.StartTangent;

        var current = xHint.HasValue
            ? Frame3D.FromAxes(first.Start, xHint.Value, tangent)
            : Frame3D.FromZ(first.Start, tangent);

        foreach (var segment in route.Segments)
        {
            if (segment is ArcSegment3D bend)
            {
                ValidateBend(profile, current, bend);
            }

            _startFrames.Add(current);
            current = Advance(current, segment);
        }

        EndFrame = current;
    }

    public Profile2D Profile { get; }

    public SweepRoute Route { get; }

    public IReadOnlyList<Frame3D> StartFrames => _startFrames;

    public Frame3D EndFrame { get; }

    internal override int FaceCount
    {
        get
        {
            var perStation = Profile.Outer.Segments.Count + Profile.Holes.Sum(h => h.Segments.Count);
            return Route.Segments.Count * perStation + 2;
        }
    }

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            var hi = new[] { double.MinValue, double.MinValue, double.MinValue };

            var world = WorldFrame;
            var b = Profile.Bounds;
            var corners = new[]
            {
                (X: b.MinX, Y: b.MinY),
                (X: b.MaxX, Y: b.MinY),
                (X: b.MaxX, Y: b.MaxY),
                (X: b.MinX, Y: b.MaxY)
            };

            void Include(Frame3D station)
            {
                foreach (var corner in corners)
                {
                    Expand(lo, hi, world.ToWorld(station.ToWorld(corner.X, corner.Y, 0)));
                }
            }

            for (var i = 0; i < Route.Segments.Count; i++)
            {
                var start = _startFrames[i];
                Include(start);

                if (Route.Segments[i] is ArcSegment3D arc)
                {
                    for (var step = 1; step < ArcSamples; step++)
                    {
                        var angle = arc.SweepDegrees * step / ArcSamples;
                        Include(start.RotateAbout(arc.Center, arc.Normal, angle));
                    }
                }
            }

            Include(EndFrame);

            return (lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
        }
    }

    private static Frame3D Advance(Frame3D current, PathSegment3D segment)
    {
        if (segment is ArcSegment3D arc)
        {
            return current.RotateAbout(arc.Center, arc.Normal, arc.SweepDegrees);
        }

        return current.Translate(segment.End - segment.Start);
    }

    private static void ValidateBend(Profile2D profile, Frame3D station, ArcSegment3D bend)
    {
        var c = station.ToLocal(bend.Center);
        var axisPoint = (X: c.X, Y: c.Y);

        var loops = new List<ProfileLoop> { profile.Outer };
        loops.AddRange(profile.Holes);

        foreach (var loop in loops)
        {
            foreach (var segment in loop.Segments)
            {
                if (segment is not ArcSegment2D arc)
                {
                    continue;
                }

                var distance = ProfileGeometry.Distance(arc.Center, axisPoint);

                if (distance > 1e-7 && arc.Radius >= distance - 1e-7)
                {
                    throw new ArgumentException(
                        "A profile arc swept around a bend must have a radius smaller than its distance from the bend axis; increase the bend radius.");
                }
            }
        }

        if (profile.Outer.Contains(axisPoint) && !profile.Holes.Any(h => h.Contains(axisPoint)))
        {
            throw new ArgumentException(
                "The bend axis passes through the profile; the bend radius is too small for the profile size.");
        }
    }
}

internal sealed class ConeSolid : FramedSolid
{
    public ConeSolid(double baseRadius, double topRadius, double height)
    {
        BaseRadius = baseRadius;
        TopRadius = topRadius;
        Height = height;
    }

    public double BaseRadius { get; }

    public double TopRadius { get; }

    public double Height { get; }

    internal override int FaceCount => TopRadius > 1e-9 ? 3 : 2;

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            var r = Math.Max(BaseRadius, TopRadius);
            return BoxBounds(WorldFrame, -r, -r, 0, r, r, Height);
        }
    }
}

internal sealed class SphereSolid : FramedSolid
{
    public SphereSolid(double radius)
    {
        Radius = radius;
    }

    public double Radius { get; }

    internal override int FaceCount => 1;

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            return BoxBounds(WorldFrame, -Radius, -Radius, -Radius, Radius, Radius, Radius);
        }
    }
}

internal sealed class TorusSolid : FramedSolid
{
    public TorusSolid(double majorRadius, double minorRadius)
    {
        MajorRadius = majorRadius;
        MinorRadius = minorRadius;
    }

    public double MajorRadius { get; }

    public double MinorRadius { get; }

    internal override int FaceCount => 1;

    internal override (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds
    {
        get
        {
            var r = MajorRadius + MinorRadius;
            return BoxBounds(WorldFrame, -r, -r, -MinorRadius, r, r, MinorRadius);
        }
    }
}

public abstract class PathSegment3D
{
    internal PathSegment3D()
    {
    }

    public abstract Vec3 Start { get; }

    public abstract Vec3 End { get; }

    public abstract double Length { get; }

    public abstract Vec3 StartTangent { get; }

    public abstract Vec3 EndTangent { get; }

    protected static void RequireFinite(Vec3 value, string parameter)
    {
        if (!value.IsFinite)
        {
            throw new ArgumentException("The vector must be made of finite numbers.", parameter);
        }
    }
}

public sealed class LineSegment3D : PathSegment3D
{
    private readonly Vec3 _tangent;

    public LineSegment3D(Vec3 start, Vec3 end)
    {
        RequireFinite(start, nameof(start));
        RequireFinite(end, nameof(end));

        var delta = end - start;

        if (delta.Length <= 1e-9)
        {
            throw new ArgumentException("A path line needs two distinct points.", nameof(end));
        }

        Start = start;
        End = end;
        _tangent = delta.Normalized();
    }

    public override Vec3 Start { get; }

    public override Vec3 End { get; }

    public override double Length => (End - Start).Length;

    public override Vec3 StartTangent => _tangent;

    public override Vec3 EndTangent => _tangent;
}

public sealed class ArcSegment3D : PathSegment3D
{
    private readonly Vec3 _radial;

    public ArcSegment3D(Vec3 center, Vec3 start, Vec3 normal, double sweepDegrees)
    {
        RequireFinite(center, nameof(center));
        RequireFinite(start, nameof(start));
        RequireFinite(normal, nameof(normal));

        if (normal.Length <= 1e-12)
        {
            throw new ArgumentException("The arc normal must not be a zero-length vector.", nameof(normal));
        }

        if (!double.IsFinite(sweepDegrees) || Math.Abs(sweepDegrees) <= 1e-9 || Math.Abs(sweepDegrees) >= 360.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sweepDegrees), "The arc sweep must be a non-zero angle smaller than 360 degrees.");
        }

        var unitNormal = normal.Normalized();
        var radial = start - center;
        var radius = radial.Length;

        if (radius <= 1e-9)
        {
            throw new ArgumentException("The arc start must be away from its center.", nameof(start));
        }

        if (Math.Abs(radial.Dot(unitNormal)) > 1e-6 * Math.Max(1.0, radius))
        {
            throw new ArgumentException("The arc start must lie in the plane that passes through the center and is perpendicular to the normal.", nameof(start));
        }

        Center = center;
        Start = start;
        Normal = unitNormal;
        SweepDegrees = sweepDegrees;
        Radius = radius;
        _radial = radial;

        var radians = sweepDegrees * Math.PI / 180.0;
        var rotated = radial * Math.Cos(radians)
            + unitNormal.Cross(radial) * Math.Sin(radians)
            + unitNormal * (unitNormal.Dot(radial) * (1.0 - Math.Cos(radians)));

        End = center + rotated;
    }

    public Vec3 Center { get; }

    public Vec3 Normal { get; }

    public double SweepDegrees { get; }

    public double Radius { get; }

    public override Vec3 Start { get; }

    public override Vec3 End { get; }

    public override double Length => Radius * Math.Abs(SweepDegrees) * Math.PI / 180.0;

    public override Vec3 StartTangent => TangentAt(_radial);

    public override Vec3 EndTangent => TangentAt(End - Center);

    private Vec3 TangentAt(Vec3 radial)
    {
        var direction = SweepDegrees > 0 ? 1.0 : -1.0;
        return (Normal.Cross(radial) * direction).Normalized();
    }
}

public sealed class SweepRoute
{
    private const double TangentTolerance = 1e-6;
    private const int MaxSegments = 1000;

    private readonly Vec3 _origin;
    private readonly List<PathSegment3D> _segments;

    private SweepRoute(Vec3 origin, List<PathSegment3D> segments)
    {
        _origin = origin;
        _segments = segments;
    }

    public IReadOnlyList<PathSegment3D> Segments => _segments;

    public Vec3 StartPoint => _origin;

    public Vec3 EndPoint => _segments.Count == 0 ? _origin : _segments[^1].End;

    public Vec3 EndTangent
    {
        get
        {
            if (_segments.Count == 0)
            {
                throw new InvalidOperationException("The route has no direction yet; add a line first.");
            }

            return _segments[^1].EndTangent;
        }
    }

    public double Length => _segments.Sum(s => s.Length);

    public static SweepRoute From(double x, double y, double z)
    {
        return From(new Vec3(x, y, z));
    }

    public static SweepRoute From(Vec3 start)
    {
        if (!start.IsFinite)
        {
            throw new ArgumentException("The start point must be made of finite numbers.", nameof(start));
        }

        return new SweepRoute(start, new List<PathSegment3D>());
    }

    public static SweepRoute Line(Vec3 start, Vec3 end)
    {
        return From(start).LineTo(end);
    }

    public SweepRoute LineTo(double x, double y, double z)
    {
        return LineTo(new Vec3(x, y, z));
    }

    public SweepRoute LineTo(Vec3 target)
    {
        return Append(new LineSegment3D(EndPoint, target));
    }

    public SweepRoute LineBy(double dx, double dy, double dz)
    {
        return LineTo(EndPoint + new Vec3(dx, dy, dz));
    }

    public SweepRoute Straight(double length)
    {
        if (!double.IsFinite(length) || length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The straight length must be a number greater than zero.");
        }

        return LineTo(EndPoint + EndTangent * length);
    }

    public SweepRoute Bend(double radius, double angleDegrees, Vec3 planeNormal)
    {
        if (!double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "The bend radius must be a number greater than zero.");
        }

        if (!double.IsFinite(angleDegrees) || Math.Abs(angleDegrees) <= 1e-9)
        {
            throw new ArgumentOutOfRangeException(nameof(angleDegrees), "The bend angle must be a non-zero number.");
        }

        if (!planeNormal.IsFinite || planeNormal.Length <= 1e-12)
        {
            throw new ArgumentException("The bend plane normal must be a non-zero vector.", nameof(planeNormal));
        }

        var normal = planeNormal.Normalized();
        var tangent = EndTangent;

        if (Math.Abs(normal.Dot(tangent)) > 1e-6)
        {
            throw new ArgumentException("The bend plane normal must be perpendicular to the current direction of the route.", nameof(planeNormal));
        }

        var side = angleDegrees > 0 ? 1.0 : -1.0;
        var center = EndPoint + normal.Cross(tangent) * (radius * side);

        return Append(new ArcSegment3D(center, EndPoint, normal, angleDegrees));
    }

    public SweepRoute Arc(Vec3 center, Vec3 normal, double sweepDegrees)
    {
        return Append(new ArcSegment3D(center, EndPoint, normal, sweepDegrees));
    }

    private SweepRoute Append(PathSegment3D segment)
    {
        if (_segments.Count >= MaxSegments)
        {
            throw new InvalidOperationException("The route has too many segments.");
        }

        if (_segments.Count > 0 && !_segments[^1].EndTangent.IsApproximately(segment.StartTangent, TangentTolerance))
        {
            throw new ArgumentException(
                "The new segment does not continue in the direction the route is heading; routes must be tangent-continuous. Use Bend to turn a corner.");
        }

        var segments = new List<PathSegment3D>(_segments) { segment };

        return new SweepRoute(_origin, segments);
    }
}

public sealed class CustomProfile
{
    private readonly (double X, double Y)[] _outline;
    private readonly (double X, double Y)[][] _holes;

    public CustomProfile(
        string name,
        string layer,
        (double X, double Y)[] outline,
        (double X, double Y)[][]? holes = null)
    {
        if (outline is null)
        {
            throw new ArgumentNullException(nameof(outline));
        }

        Name = string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim();
        Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer.Trim();
        _outline = outline;
        _holes = holes ?? Array.Empty<(double X, double Y)[]>();
    }

    private CustomProfile(string name, string layer, double x, double y, double radius)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "A circle profile needs a finite position and a radius greater than zero.");
        }

        Name = string.IsNullOrWhiteSpace(name) ? "Circle" : name.Trim();
        Layer = string.IsNullOrWhiteSpace(layer) ? "0" : layer.Trim();
        _outline = Array.Empty<(double X, double Y)>();
        _holes = Array.Empty<(double X, double Y)[]>();
        IsCircle = true;
        CenterX = x;
        CenterY = y;
        Radius = radius;
    }

    public string Name { get; }

    public string Layer { get; }

    public bool IsCircle { get; }

    public double CenterX { get; }

    public double CenterY { get; }

    public double Radius { get; }

    public double Z { get; private set; }

    public int HoleCount => _holes.Length;

    public (double X, double Y)[] Outline => _outline;

    public (double X, double Y)[][] Holes => _holes;

    public static CustomProfile Circle(string name, string layer, double x, double y, double radius)
    {
        return new CustomProfile(name, layer, x, y, radius);
    }

    public CustomProfile AtZ(double z)
    {
        if (!double.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(z), "The Z value must be a finite number.");
        }

        Z = z;
        return this;
    }

    internal Solid ToSolid(double depth)
    {
        var solid = IsCircle
            ? Solid.Cylinder(CenterX, CenterY, Radius, depth)
            : Solid.Extrude(_outline, _holes, depth);

        solid.Named(Name);

        if (Z != 0)
        {
            solid.AtZ(Z);
        }

        return solid;
    }
}

public sealed class StepAdder
{
    private readonly StepDocument _document;

    internal StepAdder(StepDocument document)
    {
        _document = document;
    }

    public StepDocument Extrude(CustomProfile profile, double depth)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        return _document.AddSolid(profile.ToSolid(depth));
    }

    public StepDocument Extrude(IEnumerable<CustomProfile> profiles, double depth)
    {
        if (profiles is null)
        {
            throw new ArgumentNullException(nameof(profiles));
        }

        foreach (var profile in profiles)
        {
            Extrude(profile, depth);
        }

        return _document;
    }

    public StepDocument Extrude(string name, Profile2D profile, double depth, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Extrude(profile, depth).Named(name), placement));
    }

    public StepDocument Revolve(string name, Profile2D profile, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Revolve(profile).Named(name), placement));
    }

    public StepDocument Sweep(string name, Profile2D profile, SweepRoute route, Frame3D? placement = null, Vec3? xHint = null)
    {
        return _document.AddSolid(Place(Solid.Sweep(profile, route, xHint).Named(name), placement));
    }

    public StepDocument Pipe(string name, SweepRoute route, double outerDiameter, double wall, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Pipe(route, outerDiameter, wall).Named(name), placement));
    }

    public StepDocument Cone(string name, double baseRadius, double topRadius, double height, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Cone(baseRadius, topRadius, height).Named(name), placement));
    }

    public StepDocument Sphere(string name, double radius, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Sphere(radius).Named(name), placement));
    }

    public StepDocument Torus(string name, double majorRadius, double minorRadius, Frame3D? placement = null)
    {
        return _document.AddSolid(Place(Solid.Torus(majorRadius, minorRadius).Named(name), placement));
    }

    public StepDocument Cylinder(string name, double x, double y, double radius, double height)
    {
        return _document.AddSolid(Solid.Cylinder(x, y, radius, height).Named(name));
    }

    public StepDocument Box(string name, double x, double y, double width, double height, double depth)
    {
        return _document.AddSolid(Solid.Box(x, y, width, height, depth).Named(name));
    }

    public StepDocument Part(Solid solid)
    {
        return _document.AddSolid(solid);
    }

    private static FramedSolid Place(FramedSolid solid, Frame3D? placement)
    {
        return placement is null ? solid : solid.Placed(placement);
    }
}