using System.Globalization;
using CadToCode.Step;

namespace CadToCode.Business.Services;

public enum RecognizedFeatureKind
{
    Extrusion = 1,
    Cylinder = 2,
    Revolve = 3,
    Pipe = 4
}

public sealed class RecognizedParameter
{
    public RecognizedParameter(string name, double value, string description, bool isAssumed)
    {
        Name = name;
        Value = value;
        Description = description;
        IsAssumed = isAssumed;
    }

    public string Name { get; }

    public double Value { get; }

    public string Description { get; }

    public bool IsAssumed { get; }
}

public sealed class RecognizedHole
{
    public RecognizedHole(string name, double centerX, double centerY, double radius)
    {
        Name = name;
        CenterX = centerX;
        CenterY = centerY;
        Radius = radius;
    }

    public string Name { get; }

    public double CenterX { get; }

    public double CenterY { get; }

    public double Radius { get; }

    public double Diameter => Radius * 2.0;
}

public sealed class RecognizedFeature
{
    internal RecognizedFeature(RecognizedFeatureKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public RecognizedFeatureKind Kind { get; }

    public string Name { get; }

    public string Description { get; internal set; } = string.Empty;

    public Profile2D? Profile { get; internal init; }

    public IReadOnlyList<RecognizedParameter> Parameters { get; internal set; } = Array.Empty<RecognizedParameter>();

    public IReadOnlyList<RecognizedHole> Holes { get; internal init; } = Array.Empty<RecognizedHole>();

    public int OtherHoleCount { get; internal init; }

    public RecognizedParameter? DepthParameter { get; internal init; }

    public (double X, double Y) AxisOrigin { get; internal init; }

    public (double X, double Y) AxisDirection { get; internal init; }

    public (double X, double Y) RadialDirection { get; internal init; }

    public IReadOnlyList<ProfileSegment> Centerline { get; internal init; } = Array.Empty<ProfileSegment>();

    public RecognizedParameter? OuterDiameterParameter { get; internal init; }

    public RecognizedParameter? WallParameter { get; internal init; }
}

public sealed class FeatureRecognitionResult
{
    internal FeatureRecognitionResult(
        IReadOnlyList<RecognizedFeature> features,
        IReadOnlyList<RecognizedParameter> parameters,
        IReadOnlyList<string> warnings)
    {
        Features = features;
        Parameters = parameters;
        Warnings = warnings;
    }

    public IReadOnlyList<RecognizedFeature> Features { get; }

    public IReadOnlyList<RecognizedParameter> Parameters { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasFeatures => Features.Count > 0;
}

public sealed class FeatureRecognizerOptions
{
    public bool DetectRevolves { get; init; } = true;

    public bool DetectPipes { get; init; } = true;

    public double? MatchTolerance { get; init; }
}

public static class FeatureRecognizer
{
    private const double TwoPi = Math.PI * 2.0;
    private const double ParallelTolerance = 1e-3;
    private const double AngleTolerance = 1e-3;

    public static FeatureRecognitionResult Recognize(ProfileBuildResult profiles, FeatureRecognizerOptions? options = null)
    {
        if (profiles is null)
        {
            throw new ArgumentNullException(nameof(profiles));
        }

        options ??= new FeatureRecognizerOptions();

        var warnings = new List<string>(profiles.Warnings);
        var features = new List<RecognizedFeature>();
        var tol = profiles.Tolerance;
        var matchTol = options.MatchTolerance ?? Math.Max(tol * 10.0, 1e-3);

        var pipeCount = 0;
        var revolveCount = 0;
        var extrusionCount = 0;
        var cylinderCount = 0;

        if (options.DetectPipes && profiles.OpenChains.Count >= 2)
        {
            var pipes = FindPipes(profiles.OpenChains, matchTol, ref pipeCount);
            features.AddRange(pipes.Features);

            var left = profiles.OpenChains.Count - pipes.UsedChains;

            if (left > 0)
            {
                warnings.Add(left + (left == 1 ? " open chain was" : " open chains were") + " not recognised as a pipe wall.");
            }
        }

        var axes = options.DetectRevolves ? CollectAxes(profiles.AxisEntities, matchTol) : new List<AxisLine>();

        foreach (var profile in profiles.Profiles)
        {
            if (axes.Count > 0 && !profile.IsCircle)
            {
                var revolve = TryRevolve(profile, axes, tol, revolveCount + 1);

                if (revolve is not null)
                {
                    revolveCount++;
                    features.Add(revolve);
                    continue;
                }
            }

            if (profile.IsCircle && profile.HoleCount == 0)
            {
                cylinderCount++;
                features.Add(BuildCylinder(profile, cylinderCount));
                continue;
            }

            extrusionCount++;
            features.Add(BuildExtrusion(profile, extrusionCount));
        }

        var parameters = new List<RecognizedParameter>();

        foreach (var feature in features)
        {
            parameters.AddRange(feature.Parameters);
        }

        if (features.Count == 0)
        {
            warnings.Add("No features could be recognised from the drawing.");
        }

        return new FeatureRecognitionResult(features, parameters, warnings);
    }

    private static RecognizedFeature BuildCylinder(Profile2D profile, int number)
    {
        var arc = profile.Outer.CircleArc!;
        var prefix = "cylinder" + number.ToString(CultureInfo.InvariantCulture);

        var diameter = new RecognizedParameter(
            prefix + "Diameter", arc.Radius * 2.0, "Diameter of cylinder " + number + " measured from the drawing.", false);

        var height = new RecognizedParameter(
            prefix + "Height", AssumedDepth(profile), "Height of cylinder " + number + " is not in the drawing; this value is assumed.", true);

        return new RecognizedFeature(RecognizedFeatureKind.Cylinder, "Cylinder" + number.ToString(CultureInfo.InvariantCulture))
        {
            Profile = profile,
            DepthParameter = height,
            Parameters = new[] { diameter, height },
            Description = "Cylinder" + number + ": circle of diameter " + F(diameter.Value) + " at (" + F(arc.Center.X) + ", "
                + F(arc.Center.Y) + "), height assumed " + F(height.Value) + " (parameter " + height.Name + ")."
        };
    }

    private static RecognizedFeature BuildExtrusion(Profile2D profile, int number)
    {
        var prefix = "extrusion" + number.ToString(CultureInfo.InvariantCulture);
        var holes = new List<RecognizedHole>();
        var other = 0;

        foreach (var hole in profile.Holes)
        {
            if (hole.IsCircle)
            {
                var arc = hole.CircleArc!;
                holes.Add(new RecognizedHole(
                    "Hole" + (holes.Count + 1).ToString(CultureInfo.InvariantCulture),
                    arc.Center.X,
                    arc.Center.Y,
                    arc.Radius));
            }
            else
            {
                other++;
            }
        }

        var depth = new RecognizedParameter(
            prefix + "Depth", AssumedDepth(profile), "Depth of extrusion " + number + " is not in the drawing; this value is assumed.", true);

        var bounds = profile.Bounds;
        var text = "Extrusion" + number + ": closed profile " + F(bounds.MaxX - bounds.MinX) + " x " + F(bounds.MaxY - bounds.MinY)
            + (profile.HasArcs ? " with arcs" : " of straight lines")
            + (holes.Count > 0 ? ", " + holes.Count + " round holes (diameters " + string.Join(", ", holes.Select(h => F(h.Diameter))) + ")" : string.Empty)
            + (other > 0 ? ", " + other + " other cut-outs" : string.Empty)
            + ", depth assumed " + F(depth.Value) + " (parameter " + depth.Name + ").";

        return new RecognizedFeature(RecognizedFeatureKind.Extrusion, "Extrusion" + number.ToString(CultureInfo.InvariantCulture))
        {
            Profile = profile,
            Holes = holes,
            OtherHoleCount = other,
            DepthParameter = depth,
            Parameters = new[] { depth },
            Description = text
        };
    }

    private static RecognizedFeature? TryRevolve(Profile2D profile, IReadOnlyList<AxisLine> axes, double tol, int number)
    {
        var points = profile.FlattenOuter(Math.Max(tol, 1e-3));

        AxisLine? bestAxis = null;
        var bestOverlap = 0.0;
        var bestPositive = true;

        foreach (var axis in axes)
        {
            var v = (X: axis.Direction.Y, Y: -axis.Direction.X);
            var minS = double.MaxValue;
            var maxS = double.MinValue;
            var minT = double.MaxValue;
            var maxT = double.MinValue;

            foreach (var p in points)
            {
                var dx = p.X - axis.Origin.X;
                var dy = p.Y - axis.Origin.Y;
                var s = dx * v.X + dy * v.Y;
                var t = dx * axis.Direction.X + dy * axis.Direction.Y;

                minS = Math.Min(minS, s);
                maxS = Math.Max(maxS, s);
                minT = Math.Min(minT, t);
                maxT = Math.Max(maxT, t);
            }

            var positive = minS >= -tol;
            var negative = maxS <= tol;

            if (positive == negative)
            {
                continue;
            }

            var span = maxT - minT;
            var overlap = Math.Min(maxT, axis.Length) - Math.Max(minT, 0.0);

            if (span <= 1e-9 || overlap < 0.5 * span)
            {
                continue;
            }

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestAxis = axis;
                bestPositive = positive;
            }
        }

        if (bestAxis is null)
        {
            return null;
        }

        var a = bestAxis.Value;
        var side = bestPositive ? (X: a.Direction.Y, Y: -a.Direction.X) : (X: -a.Direction.Y, Y: a.Direction.X);

        (double X, double Y) Map((double X, double Y) p)
        {
            var dx = p.X - a.Origin.X;
            var dy = p.Y - a.Origin.Y;
            var radius = dx * side.X + dy * side.Y;
            var height = dx * a.Direction.X + dy * a.Direction.Y;

            return (Math.Abs(radius) <= tol ? 0.0 : radius, height);
        }

        var local = TransformProfile(profile, Map, !bestPositive, tol * 2.0);

        if (local is null)
        {
            return null;
        }

        var bounds = local.Bounds;
        var name = "Revolve" + number.ToString(CultureInfo.InvariantCulture);

        return new RecognizedFeature(RecognizedFeatureKind.Revolve, name)
        {
            Profile = local,
            AxisOrigin = a.Origin,
            AxisDirection = a.Direction,
            RadialDirection = side,
            Description = name + ": half-section profile revolved about the centreline through (" + F(a.Origin.X) + ", " + F(a.Origin.Y)
                + "), max radius " + F(bounds.MaxX) + ", axial length " + F(bounds.MaxY - bounds.MinY) + "."
        };
    }

    private static Profile2D? TransformProfile(
        Profile2D profile,
        Func<(double X, double Y), (double X, double Y)> map,
        bool mirrored,
        double tolerance)
    {
        try
        {
            var outer = TransformLoop(profile.Outer, map, mirrored, tolerance);

            if (outer is null)
            {
                return null;
            }

            var holes = new List<ProfileLoop>();

            foreach (var hole in profile.Holes)
            {
                var transformed = TransformLoop(hole, map, mirrored, tolerance);

                if (transformed is null)
                {
                    return null;
                }

                holes.Add(transformed);
            }

            return Profile2D.TryCreate(outer, holes, tolerance, out var result, out _) ? result : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ProfileLoop? TransformLoop(
        ProfileLoop loop,
        Func<(double X, double Y), (double X, double Y)> map,
        bool mirrored,
        double tolerance)
    {
        var segments = new List<ProfileSegment>();

        foreach (var segment in loop.Segments)
        {
            switch (segment)
            {
                case LineSegment2D line:
                    segments.Add(new LineSegment2D(map(line.Start), map(line.End)));
                    break;

                case ArcSegment2D arc:
                    var center = map(arc.Center);
                    var start = map(arc.Start);
                    var angle = Math.Atan2(start.Y - center.Y, start.X - center.X);

                    segments.Add(new ArcSegment2D(center, arc.Radius, angle, mirrored ? -arc.SweepAngle : arc.SweepAngle));
                    break;

                default:
                    return null;
            }
        }

        return ProfileLoop.TryCreate(segments, tolerance, out var result, out _) ? result : null;
    }

    private static List<AxisLine> CollectAxes(IReadOnlyList<DrawingEntity> entities, double minLength)
    {
        var axes = new List<AxisLine>();

        foreach (var entity in entities)
        {
            if (entity.Kind != DrawingEntityKind.Line)
            {
                continue;
            }

            var dx = entity.End.X - entity.Start.X;
            var dy = entity.End.Y - entity.Start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);

            if (length <= minLength)
            {
                continue;
            }

            axes.Add(new AxisLine(entity.Start, (dx / length, dy / length), length));
        }

        return axes;
    }

    private static (List<RecognizedFeature> Features, int UsedChains) FindPipes(
        IReadOnlyList<IReadOnlyList<ProfileSegment>> chains,
        double matchTol,
        ref int pipeCount)
    {
        var features = new List<RecognizedFeature>();
        var pairs = new List<PipePair>();

        for (var i = 0; i < chains.Count; i++)
        {
            for (var j = i + 1; j < chains.Count; j++)
            {
                if (chains[i].Count != chains[j].Count)
                {
                    continue;
                }

                foreach (var reversed in new[] { false, true })
                {
                    var other = reversed ? ReverseChain(chains[j]) : chains[j];

                    if (TryOffset(chains[i], other, matchTol, out var distance, out var centerline))
                    {
                        pairs.Add(new PipePair(i, j, Math.Abs(distance), centerline));
                        break;
                    }
                }
            }
        }

        var used = new bool[chains.Count];
        var usedCount = 0;

        foreach (var pair in pairs.OrderByDescending(p => p.Distance))
        {
            if (used[pair.A] || used[pair.B])
            {
                continue;
            }

            var inner = pairs.FirstOrDefault(q =>
                !used[q.A] && !used[q.B]
                && q.A != pair.A && q.A != pair.B && q.B != pair.A && q.B != pair.B
                && q.Distance < pair.Distance - matchTol
                && CenterlinesMatch(pair.Centerline, q.Centerline, matchTol));

            used[pair.A] = true;
            used[pair.B] = true;
            usedCount += 2;

            if (inner is not null)
            {
                used[inner.A] = true;
                used[inner.B] = true;
                usedCount += 2;
            }

            pipeCount++;

            var prefix = "pipe" + pipeCount.ToString(CultureInfo.InvariantCulture);
            var outerDiameter = new RecognizedParameter(
                prefix + "OuterDiameter", pair.Distance, "Outer diameter of pipe " + pipeCount + " measured between the two outer wall lines.", false);

            var wall = inner is not null
                ? new RecognizedParameter(
                    prefix + "Wall", (pair.Distance - inner.Distance) / 2.0, "Wall thickness of pipe " + pipeCount + " measured from the inner wall lines.", false)
                : new RecognizedParameter(
                    prefix + "Wall", AssumedWall(pair.Distance), "Wall thickness of pipe " + pipeCount + " is not in the drawing; this value is assumed.", true);

            var name = "Pipe" + pipeCount.ToString(CultureInfo.InvariantCulture);
            var length = pair.Centerline.Sum(s => s.Length);

            features.Add(new RecognizedFeature(RecognizedFeatureKind.Pipe, name)
            {
                Centerline = pair.Centerline,
                OuterDiameterParameter = outerDiameter,
                WallParameter = wall,
                Parameters = new[] { outerDiameter, wall },
                Description = name + ": pipe swept along a centreline of " + pair.Centerline.Count + " segments (length " + F(length)
                    + "), outer diameter " + F(outerDiameter.Value) + ", wall " + F(wall.Value) + (wall.IsAssumed ? " assumed" : " measured")
                    + " (parameter " + wall.Name + ")."
            });
        }

        return (features, usedCount);
    }

    private static bool TryOffset(
        IReadOnlyList<ProfileSegment> a,
        IReadOnlyList<ProfileSegment> b,
        double tol,
        out double distance,
        out List<ProfileSegment> centerline)
    {
        distance = 0.0;
        centerline = new List<ProfileSegment>();

        double? offset = null;

        for (var k = 0; k < a.Count; k++)
        {
            double d;

            if (a[k] is LineSegment2D la && b[k] is LineSegment2D lb)
            {
                var lenA = la.Length;
                var lenB = lb.Length;

                if (Math.Abs(lenA - lenB) > tol)
                {
                    return false;
                }

                var dirA = ((la.End.X - la.Start.X) / lenA, (la.End.Y - la.Start.Y) / lenA);
                var dirB = ((lb.End.X - lb.Start.X) / lenB, (lb.End.Y - lb.Start.Y) / lenB);
                var cross = dirA.Item1 * dirB.Item2 - dirA.Item2 * dirB.Item1;
                var dot = dirA.Item1 * dirB.Item1 + dirA.Item2 * dirB.Item2;

                if (Math.Abs(cross) > ParallelTolerance || dot <= 0.0)
                {
                    return false;
                }

                var relStart = (lb.Start.X - la.Start.X, lb.Start.Y - la.Start.Y);
                var relEnd = (lb.End.X - la.End.X, lb.End.Y - la.End.Y);

                var alongStart = relStart.Item1 * dirA.Item1 + relStart.Item2 * dirA.Item2;
                var alongEnd = relEnd.Item1 * dirA.Item1 + relEnd.Item2 * dirA.Item2;
                var sideStart = dirA.Item1 * relStart.Item2 - dirA.Item2 * relStart.Item1;
                var sideEnd = dirA.Item1 * relEnd.Item2 - dirA.Item2 * relEnd.Item1;

                if (Math.Abs(alongStart) > tol || Math.Abs(alongEnd) > tol || Math.Abs(sideStart - sideEnd) > tol)
                {
                    return false;
                }

                d = sideStart;

                centerline.Add(new LineSegment2D(
                    ((la.Start.X + lb.Start.X) / 2.0, (la.Start.Y + lb.Start.Y) / 2.0),
                    ((la.End.X + lb.End.X) / 2.0, (la.End.Y + lb.End.Y) / 2.0)));
            }
            else if (a[k] is ArcSegment2D aa && b[k] is ArcSegment2D ab)
            {
                if (Distance(aa.Center, ab.Center) > tol
                    || Math.Sign(aa.SweepAngle) != Math.Sign(ab.SweepAngle)
                    || Math.Abs(Math.Abs(aa.SweepAngle) - Math.Abs(ab.SweepAngle)) > AngleTolerance
                    || AngleDifference(aa.StartAngle, ab.StartAngle) > AngleTolerance)
                {
                    return false;
                }

                d = aa.SweepAngle > 0 ? aa.Radius - ab.Radius : ab.Radius - aa.Radius;

                var radius = (aa.Radius + ab.Radius) / 2.0;

                if (radius <= tol)
                {
                    return false;
                }

                centerline.Add(new ArcSegment2D(aa.Center, radius, aa.StartAngle, aa.SweepAngle));
            }
            else
            {
                return false;
            }

            if (offset is null)
            {
                offset = d;
            }
            else if (Math.Abs(offset.Value - d) > tol)
            {
                return false;
            }
        }

        if (offset is null || Math.Abs(offset.Value) <= tol)
        {
            return false;
        }

        distance = offset.Value;
        return true;
    }

    private static bool CenterlinesMatch(IReadOnlyList<ProfileSegment> a, IReadOnlyList<ProfileSegment> b, double tol)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        return SegmentsMatch(a, b, tol) || SegmentsMatch(a, ReverseChain(b), tol);
    }

    private static bool SegmentsMatch(IReadOnlyList<ProfileSegment> a, IReadOnlyList<ProfileSegment> b, double tol)
    {
        for (var k = 0; k < a.Count; k++)
        {
            if (a[k] is LineSegment2D la && b[k] is LineSegment2D lb)
            {
                if (Distance(la.Start, lb.Start) > tol || Distance(la.End, lb.End) > tol)
                {
                    return false;
                }
            }
            else if (a[k] is ArcSegment2D aa && b[k] is ArcSegment2D ab)
            {
                if (Distance(aa.Center, ab.Center) > tol
                    || Math.Abs(aa.Radius - ab.Radius) > tol
                    || Math.Abs(aa.SweepAngle - ab.SweepAngle) > AngleTolerance
                    || AngleDifference(aa.StartAngle, ab.StartAngle) > AngleTolerance)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static List<ProfileSegment> ReverseChain(IReadOnlyList<ProfileSegment> chain)
    {
        var result = new List<ProfileSegment>(chain.Count);

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            result.Add(chain[i].Reversed());
        }

        return result;
    }

    private static double AssumedDepth(Profile2D profile)
    {
        var b = profile.Bounds;
        var smallest = Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY);

        return RoundNice(Math.Max(smallest * 0.25, 1e-3));
    }

    private static double AssumedWall(double outerDiameter)
    {
        return RoundNice(Math.Max(outerDiameter * 0.1, 1e-4));
    }

    private static double RoundNice(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            return 1.0;
        }

        var magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(value)) - 1.0);

        return Math.Round(value / magnitude) * magnitude;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;

        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double AngleDifference(double a, double b)
    {
        var d = Math.Abs(a - b) % TwoPi;

        return Math.Min(d, TwoPi - d);
    }

    private static string F(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private readonly record struct AxisLine((double X, double Y) Origin, (double X, double Y) Direction, double Length);

    private sealed class PipePair
    {
        public PipePair(int a, int b, double distance, List<ProfileSegment> centerline)
        {
            A = a;
            B = b;
            Distance = distance;
            Centerline = centerline;
        }

        public int A { get; }

        public int B { get; }

        public double Distance { get; }

        public List<ProfileSegment> Centerline { get; }
    }
}
