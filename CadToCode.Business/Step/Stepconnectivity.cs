using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Step;

internal sealed record ConnectivityReport(int ComponentCount, IReadOnlyList<string> IsolatedNames)
{
    public bool IsConnected => ComponentCount <= 1;
}

internal static class StepConnectivity
{
    public const double DefaultTolerance = 1.0;

    public static ConnectivityReport Analyze(IReadOnlyList<Solid> solids, double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(solids);

        var count = solids.Count;

        if (count <= 1)
        {
            return new ConnectivityReport(count, Array.Empty<string>());
        }

        var parent = new int[count];

        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        var boxes = new (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)[count];

        for (var i = 0; i < count; i++)
        {
            boxes[i] = solids[i].Bounds;
        }

        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                if (!Overlaps(boxes[i], boxes[j], tolerance))
                {
                    continue;
                }

                var rootI = Find(i);
                var rootJ = Find(j);

                if (rootI != rootJ)
                {
                    parent[rootJ] = rootI;
                }
            }
        }

        var groups = new Dictionary<int, List<int>>();

        for (var i = 0; i < count; i++)
        {
            var root = Find(i);

            if (!groups.TryGetValue(root, out var members))
            {
                members = new List<int>();
                groups[root] = members;
            }

            members.Add(i);
        }

        var largest = groups.Values.OrderByDescending(g => g.Count).First();
        var isolated = new List<string>();

        foreach (var group in groups.Values)
        {
            if (ReferenceEquals(group, largest))
            {
                continue;
            }

            foreach (var index in group)
            {
                isolated.Add(string.IsNullOrWhiteSpace(solids[index].Name) ? "Solid " + (index + 1) : solids[index].Name);
            }
        }

        return new ConnectivityReport(groups.Count, isolated);
    }

    private static bool Overlaps(
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) a,
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) b,
        double tolerance)
    {
        return a.MinX <= b.MaxX + tolerance && b.MinX <= a.MaxX + tolerance
            && a.MinY <= b.MaxY + tolerance && b.MinY <= a.MaxY + tolerance
            && a.MinZ <= b.MaxZ + tolerance && b.MinZ <= a.MaxZ + tolerance;
    }
}

public sealed class StepPrimitiveInfo
{
    public string SolidName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public double Radius { get; set; }
    public double TopRadius { get; set; }
    public double BottomRadius { get; set; }
    public double SemiAngleDegrees { get; set; }
    public double Length { get; set; }
}

public sealed class StepAnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<ComponentDto> Components { get; set; } = new();
    public AssemblyTree Tree { get; set; } = new();
    public List<StepPrimitiveInfo> Primitives { get; set; } = new();
}

public static class StepGeometryAnalyzer
{
    public const string SourceLayer = "STEP";

    private const double Tau = Math.PI * 2.0;
    private const int ArcSamples = 24;

    private static readonly Regex RefPattern = new(@"#(\d+)", RegexOptions.Compiled);

    public static StepAnalysisResult Analyze(string? stepContent, int projectId = 0)
    {
        var result = new StepAnalysisResult();

        if (string.IsNullOrWhiteSpace(stepContent))
        {
            result.Error = "There is no STEP content to analyze.";
            return result;
        }

        try
        {
            var parsed = new Parsed(ParseEntities(stepContent), DetectScale(stepContent));
            var solids = ReadSolids(parsed);

            if (solids.Count == 0)
            {
                result.Error = "The STEP file does not contain any solid bodies.";
                return result;
            }

            BuildComponents(solids, projectId, result);
            BuildTree(solids, projectId, result);
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = "The STEP file could not be analyzed: " + ex.Message;
            result.Components.Clear();
            result.Primitives.Clear();
            result.Tree = new AssemblyTree();
        }

        return result;
    }

    private readonly record struct Entity(string Name, string Args);

    private readonly record struct V3(double X, double Y, double Z)
    {
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public V3 Normalized()
        {
            var length = Length;
            return length <= 1e-12 ? this : new V3(X / length, Y / length, Z / length);
        }

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    private readonly record struct Axes(V3 Origin, V3 X, V3 Y, V3 Z);

    private sealed class Parsed
    {
        public Parsed(Dictionary<int, Entity> entities, double scale)
        {
            Entities = entities;
            Scale = scale;
        }

        public Dictionary<int, Entity> Entities { get; }

        public double Scale { get; }

        public bool TryGet(int id, string name, out Entity entity)
        {
            if (Entities.TryGetValue(id, out entity) && string.Equals(entity.Name, name, StringComparison.Ordinal))
            {
                return true;
            }

            entity = default;
            return false;
        }
    }

    private sealed class FaceInfo
    {
        public string Kind { get; set; } = "Other";
        public Axes Axes { get; set; }
        public double Radius { get; set; }
        public double SemiAngle { get; set; }
        public List<V3> Points { get; } = new();
    }

    private sealed class SolidInfo
    {
        public string Name { get; set; } = string.Empty;
        public List<FaceInfo> Faces { get; } = new();
        public V3 Min { get; set; }
        public V3 Max { get; set; }
        public ComponentType Type { get; set; } = ComponentType.Generic;
        public double Radius { get; set; }
        public double TopRadius { get; set; }
        public double BottomRadius { get; set; }
        public int AxisIndex { get; set; } = -1;
        public double Volume =>
            Math.Max(Max.X - Min.X, 1e-6) * Math.Max(Max.Y - Min.Y, 1e-6) * Math.Max(Max.Z - Min.Z, 1e-6);
    }

    private static Dictionary<int, Entity> ParseEntities(string text)
    {
        var map = new Dictionary<int, Entity>();
        var dataStart = text.IndexOf("DATA;", StringComparison.OrdinalIgnoreCase);
        var body = dataStart >= 0 ? text.Substring(dataStart + 5) : text;

        foreach (var raw in body.Split(';'))
        {
            var statement = raw.Trim();

            if (statement.Length < 4 || statement[0] != '#')
            {
                continue;
            }

            var equals = statement.IndexOf('=');

            if (equals < 2)
            {
                continue;
            }

            if (!int.TryParse(statement.Substring(1, equals - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var rest = statement.Substring(equals + 1).TrimStart();
            var open = rest.IndexOf('(');

            if (open <= 0)
            {
                continue;
            }

            var close = rest.LastIndexOf(')');

            if (close <= open)
            {
                continue;
            }

            var name = rest.Substring(0, open).Trim().ToUpperInvariant();
            map[id] = new Entity(name, rest.Substring(open + 1, close - open - 1));
        }

        return map;
    }

    private static double DetectScale(string text)
    {
        var compact = text.ToUpperInvariant().Replace(" ", string.Empty);

        if (compact.Contains("SI_UNIT(.MILLI.,.METRE.)", StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (compact.Contains("SI_UNIT(.CENTI.,.METRE.)", StringComparison.Ordinal))
        {
            return 10.0;
        }

        if (compact.Contains("SI_UNIT($,.METRE.)", StringComparison.Ordinal))
        {
            return 1000.0;
        }

        if (compact.Contains("'INCH'", StringComparison.Ordinal))
        {
            return 25.4;
        }

        return 1.0;
    }

    private static List<string> SplitArgs(string args)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];

            if (c == '\'')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(args.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        parts.Add(args.Substring(start).Trim());
        return parts;
    }

    private static int Ref(string token)
    {
        var text = token.Trim();

        if (text.Length > 1 && text[0] == '#'
            && int.TryParse(text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        return -1;
    }

    private static List<int> RefList(string token)
    {
        var ids = new List<int>();

        foreach (Match match in RefPattern.Matches(token))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static double Num(string token)
    {
        return double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : 0.0;
    }

    private static string Unquote(string token)
    {
        var text = token.Trim();

        if (text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'')
        {
            text = text.Substring(1, text.Length - 2);
        }

        return text.Replace("''", "'").Trim();
    }

    private static V3? Vector(Parsed p, int id, string entityName, double scale)
    {
        if (!p.TryGet(id, entityName, out var entity))
        {
            return null;
        }

        var args = SplitArgs(entity.Args);

        if (args.Count < 2)
        {
            return null;
        }

        var numbers = args[1].Trim().Trim('(', ')').Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (numbers.Length < 3)
        {
            return null;
        }

        return new V3(Num(numbers[0]) * scale, Num(numbers[1]) * scale, Num(numbers[2]) * scale);
    }

    private static Axes MakeAxes(Parsed p, int placementId)
    {
        var origin = new V3(0, 0, 0);
        var z = new V3(0, 0, 1);
        var reference = new V3(1, 0, 0);

        if (p.TryGet(placementId, "AXIS2_PLACEMENT_3D", out var entity))
        {
            var args = SplitArgs(entity.Args);

            if (args.Count >= 2 && Vector(p, Ref(args[1]), "CARTESIAN_POINT", p.Scale) is V3 location)
            {
                origin = location;
            }

            if (args.Count >= 3 && Vector(p, Ref(args[2]), "DIRECTION", 1.0) is V3 axis && axis.Length > 1e-12)
            {
                z = axis.Normalized();
            }

            if (args.Count >= 4 && Vector(p, Ref(args[3]), "DIRECTION", 1.0) is V3 refDirection && refDirection.Length > 1e-12)
            {
                reference = refDirection.Normalized();
            }
        }

        var x = reference - z * V3.Dot(reference, z);

        if (x.Length < 1e-9)
        {
            var fallback = Math.Abs(z.X) < 0.9 ? new V3(1, 0, 0) : new V3(0, 1, 0);
            x = fallback - z * V3.Dot(fallback, z);
        }

        x = x.Normalized();
        var y = V3.Cross(z, x);

        return new Axes(origin, x, y, z);
    }

    private static List<SolidInfo> ReadSolids(Parsed p)
    {
        var solids = new List<SolidInfo>();

        foreach (var pair in p.Entities.OrderBy(k => k.Key))
        {
            if (pair.Value.Name != "MANIFOLD_SOLID_BREP")
            {
                continue;
            }

            var args = SplitArgs(pair.Value.Args);

            if (args.Count < 2)
            {
                continue;
            }

            if (!p.Entities.TryGetValue(Ref(args[1]), out var shell)
                || (shell.Name != "CLOSED_SHELL" && shell.Name != "OPEN_SHELL"))
            {
                continue;
            }

            var shellArgs = SplitArgs(shell.Args);

            if (shellArgs.Count < 2)
            {
                continue;
            }

            var solid = new SolidInfo { Name = Unquote(args[0]) };

            foreach (var faceId in RefList(shellArgs[1]))
            {
                var face = ReadFace(p, faceId);

                if (face is not null)
                {
                    solid.Faces.Add(face);
                }
            }

            var points = solid.Faces.SelectMany(f => f.Points).ToList();

            if (points.Count == 0)
            {
                continue;
            }

            solid.Min = new V3(points.Min(v => v.X), points.Min(v => v.Y), points.Min(v => v.Z));
            solid.Max = new V3(points.Max(v => v.X), points.Max(v => v.Y), points.Max(v => v.Z));

            if (string.IsNullOrWhiteSpace(solid.Name))
            {
                solid.Name = "Solid " + (solids.Count + 1).ToString(CultureInfo.InvariantCulture);
            }

            solids.Add(solid);
        }

        return solids;
    }

    private static FaceInfo? ReadFace(Parsed p, int faceId)
    {
        if (!p.Entities.TryGetValue(faceId, out var face)
            || (face.Name != "ADVANCED_FACE" && face.Name != "FACE_SURFACE"))
        {
            return null;
        }

        var faceArgs = SplitArgs(face.Args);

        if (faceArgs.Count < 3)
        {
            return null;
        }

        var info = new FaceInfo();

        if (p.Entities.TryGetValue(Ref(faceArgs[2]), out var surface))
        {
            var surfaceArgs = SplitArgs(surface.Args);

            switch (surface.Name)
            {
                case "PLANE":
                    info.Kind = "Plane";

                    if (surfaceArgs.Count >= 2)
                    {
                        info.Axes = MakeAxes(p, Ref(surfaceArgs[1]));
                    }

                    break;

                case "CYLINDRICAL_SURFACE":
                    info.Kind = "Cylinder";

                    if (surfaceArgs.Count >= 3)
                    {
                        info.Axes = MakeAxes(p, Ref(surfaceArgs[1]));
                        info.Radius = Math.Abs(Num(surfaceArgs[2])) * p.Scale;
                    }

                    break;

                case "CONICAL_SURFACE":
                    info.Kind = "Cone";

                    if (surfaceArgs.Count >= 4)
                    {
                        info.Axes = MakeAxes(p, Ref(surfaceArgs[1]));
                        info.Radius = Math.Abs(Num(surfaceArgs[2])) * p.Scale;
                        info.SemiAngle = Num(surfaceArgs[3]);
                    }

                    break;
            }
        }

        foreach (var boundId in RefList(faceArgs[1]))
        {
            if (!p.Entities.TryGetValue(boundId, out var bound))
            {
                continue;
            }

            var boundArgs = SplitArgs(bound.Args);

            if (boundArgs.Count < 2
                || !p.Entities.TryGetValue(Ref(boundArgs[1]), out var loop)
                || loop.Name != "EDGE_LOOP")
            {
                continue;
            }

            var loopArgs = SplitArgs(loop.Args);

            if (loopArgs.Count < 2)
            {
                continue;
            }

            foreach (var orientedId in RefList(loopArgs[1]))
            {
                if (!p.TryGet(orientedId, "ORIENTED_EDGE", out var oriented))
                {
                    continue;
                }

                var orientedArgs = SplitArgs(oriented.Args);

                if (orientedArgs.Count >= 4)
                {
                    CollectEdgePoints(p, Ref(orientedArgs[3]), info.Points);
                }
            }
        }

        return info.Points.Count == 0 ? null : info;
    }

    private static void CollectEdgePoints(Parsed p, int edgeId, List<V3> points)
    {
        if (!p.TryGet(edgeId, "EDGE_CURVE", out var edge))
        {
            return;
        }

        var args = SplitArgs(edge.Args);

        if (args.Count < 5)
        {
            return;
        }

        var startId = Ref(args[1]);
        var endId = Ref(args[2]);
        var start = VertexPoint(p, startId);
        var end = VertexPoint(p, endId);

        if (start is V3 s)
        {
            points.Add(s);
        }

        if (end is V3 e)
        {
            points.Add(e);
        }

        if (!p.TryGet(Ref(args[3]), "CIRCLE", out var circle))
        {
            return;
        }

        var circleArgs = SplitArgs(circle.Args);

        if (circleArgs.Count < 3)
        {
            return;
        }

        var axes = MakeAxes(p, Ref(circleArgs[1]));
        var radius = Math.Abs(Num(circleArgs[2])) * p.Scale;

        if (radius <= 0)
        {
            return;
        }

        var sameSense = !args[4].Contains('F');
        double startAngle;
        double sweep;

        if (startId == endId || start is null || end is null)
        {
            startAngle = 0;
            sweep = Tau;
        }
        else
        {
            var a1 = AngleOf(axes, start.Value);
            var a2 = AngleOf(axes, end.Value);

            if (!sameSense)
            {
                (a1, a2) = (a2, a1);
            }

            startAngle = a1;
            sweep = (a2 - a1) % Tau;

            if (sweep < 0)
            {
                sweep += Tau;
            }

            if (sweep < 1e-9)
            {
                sweep = Tau;
            }
        }

        for (var i = 0; i <= ArcSamples; i++)
        {
            var angle = startAngle + sweep * i / ArcSamples;
            points.Add(axes.Origin + axes.X * (radius * Math.Cos(angle)) + axes.Y * (radius * Math.Sin(angle)));
        }
    }

    private static V3? VertexPoint(Parsed p, int vertexId)
    {
        if (!p.TryGet(vertexId, "VERTEX_POINT", out var vertex))
        {
            return null;
        }

        var args = SplitArgs(vertex.Args);

        return args.Count >= 2 ? Vector(p, Ref(args[1]), "CARTESIAN_POINT", p.Scale) : null;
    }

    private static double AngleOf(Axes axes, V3 point)
    {
        var d = point - axes.Origin;
        return Math.Atan2(V3.Dot(d, axes.Y), V3.Dot(d, axes.X));
    }

    private static (double Min, double Max) AxialRange(FaceInfo face)
    {
        if (face.Points.Count == 0)
        {
            return (0, 0);
        }

        var min = double.MaxValue;
        var max = double.MinValue;

        foreach (var point in face.Points)
        {
            var t = V3.Dot(point - face.Axes.Origin, face.Axes.Z);

            if (t < min)
            {
                min = t;
            }

            if (t > max)
            {
                max = t;
            }
        }

        return (min, max);
    }

    private static double RadialAverage(FaceInfo face, double t, double tolerance)
    {
        var sum = 0.0;
        var count = 0;

        foreach (var point in face.Points)
        {
            var d = point - face.Axes.Origin;
            var along = V3.Dot(d, face.Axes.Z);

            if (Math.Abs(along - t) > tolerance)
            {
                continue;
            }

            sum += (d - face.Axes.Z * along).Length;
            count++;
        }

        return count == 0 ? 0.0 : sum / count;
    }

    private static int AxisIndex(V3 axis)
    {
        if (Math.Abs(axis.X) > 0.999)
        {
            return 0;
        }

        if (Math.Abs(axis.Y) > 0.999)
        {
            return 1;
        }

        if (Math.Abs(axis.Z) > 0.999)
        {
            return 2;
        }

        return -1;
    }

    private static decimal? D(double value)
    {
        return double.IsFinite(value) && Math.Abs(value) < 1e12
            ? (decimal)Math.Round(value, 4)
            : null;
    }

    private static void BuildComponents(List<SolidInfo> solids, int projectId, StepAnalysisResult result)
    {
        for (var i = 0; i < solids.Count; i++)
        {
            var solid = solids[i];
            var dx = solid.Max.X - solid.Min.X;
            var dy = solid.Max.Y - solid.Min.Y;
            var dz = solid.Max.Z - solid.Min.Z;

            var component = new ComponentDto
            {
                Id = -(i + 1),
                ProjectId = projectId,
                Name = solid.Name,
                SequenceIndex = i,
                Layer = SourceLayer,
                Confidence = 0.95m,
                HasHatch = false,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var face in solid.Faces)
            {
                if (face.Kind == "Other")
                {
                    continue;
                }

                var range = AxialRange(face);

                result.Primitives.Add(new StepPrimitiveInfo
                {
                    SolidName = solid.Name,
                    Kind = face.Kind,
                    Radius = face.Radius,
                    SemiAngleDegrees = Math.Abs(face.SemiAngle) * 180.0 / Math.PI,
                    Length = face.Kind == "Plane" ? 0.0 : range.Max - range.Min
                });
            }

            var cone = solid.Faces
                .Where(f => f.Kind == "Cone" && f.Points.Count > 0)
                .OrderByDescending(f => AxialRange(f).Max - AxialRange(f).Min)
                .FirstOrDefault();

            var cylinder = solid.Faces
                .Where(f => f.Kind == "Cylinder" && f.Points.Count > 0)
                .OrderByDescending(f => f.Radius)
                .FirstOrDefault();

            if (cone is not null)
            {
                var range = AxialRange(cone);
                var length = range.Max - range.Min;
                var tolerance = Math.Max(1e-6, length * 1e-6);
                var low = RadialAverage(cone, range.Min, tolerance);
                var high = RadialAverage(cone, range.Max, tolerance);
                var zLow = cone.Axes.Origin.Z + range.Min * cone.Axes.Z.Z;
                var zHigh = cone.Axes.Origin.Z + range.Max * cone.Axes.Z.Z;
                var bottom = zLow <= zHigh ? low : high;
                var top = zLow <= zHigh ? high : low;

                solid.Type = ComponentType.Cone;
                solid.AxisIndex = AxisIndex(cone.Axes.Z);
                solid.BottomRadius = bottom;
                solid.TopRadius = top;
                solid.Radius = Math.Max(bottom, top);

                component.ComponentType = ComponentType.Cone;
                component.Height = D(length);
                component.BottomDiameter = D(bottom * 2.0);
                component.TopDiameter = D(top * 2.0);
                component.Angle = D(Math.Abs(cone.SemiAngle) * 180.0 / Math.PI);
            }
            else if (cylinder is not null && IsCylinderSolid(cylinder, dx, dy, dz))
            {
                var range = AxialRange(cylinder);

                solid.Type = ComponentType.Cylinder;
                solid.AxisIndex = AxisIndex(cylinder.Axes.Z);
                solid.Radius = cylinder.Radius;

                component.ComponentType = ComponentType.Cylinder;
                component.Diameter = D(cylinder.Radius * 2.0);
                component.Height = D(range.Max - range.Min);
            }
            else
            {
                solid.Type = ComponentType.Generic;

                component.ComponentType = ComponentType.Generic;
                component.Width = D(Math.Max(dx, dy));
                component.Height = D(dz);
            }

            result.Components.Add(component);
        }
    }

    private static bool IsCylinderSolid(FaceInfo cylinder, double dx, double dy, double dz)
    {
        var axis = AxisIndex(cylinder.Axes.Z);

        if (axis < 0)
        {
            return true;
        }

        var perpendicular = axis switch
        {
            0 => Math.Max(dy, dz),
            1 => Math.Max(dx, dz),
            _ => Math.Max(dx, dy)
        };

        var diameter = cylinder.Radius * 2.0;

        return Math.Abs(perpendicular - diameter) <= Math.Max(0.5, diameter * 0.02);
    }

    private static bool Contains(SolidInfo outer, SolidInfo inner, double tolerance)
    {
        return outer.Min.X <= inner.Min.X + tolerance && outer.Max.X >= inner.Max.X - tolerance
            && outer.Min.Y <= inner.Min.Y + tolerance && outer.Max.Y >= inner.Max.Y - tolerance
            && outer.Min.Z <= inner.Min.Z + tolerance && outer.Max.Z >= inner.Max.Z - tolerance;
    }

    private static bool Touches(SolidInfo a, SolidInfo b, double tolerance)
    {
        return a.Min.X <= b.Max.X + tolerance && b.Min.X <= a.Max.X + tolerance
            && a.Min.Y <= b.Max.Y + tolerance && b.Min.Y <= a.Max.Y + tolerance
            && a.Min.Z <= b.Max.Z + tolerance && b.Min.Z <= a.Max.Z + tolerance;
    }

    private static GeometryPolyline Polygon(params (double X, double Y)[] corners)
    {
        return new GeometryPolyline
        {
            IsClosed = true,
            Vertices = corners.Select(c => new GeometryVertex(c.X, c.Y)).ToList()
        };
    }

    private static void BuildTree(List<SolidInfo> solids, int projectId, StepAnalysisResult result)
    {
        var tree = new AssemblyTree { Unit = DrawingUnit.Millimeter };
        var nodes = new List<AssemblyNode>();

        for (var i = 0; i < solids.Count; i++)
        {
            var solid = solids[i];
            var component = result.Components[i];
            var bounds = new BoundingBox2D(solid.Min.X, solid.Min.Z, solid.Max.X, solid.Max.Z);
            var geometry = new ExtractedGeometry { DetectedUnit = DrawingUnit.Millimeter };
            var shape = PartShape.Rectangular;

            if (solid.Type == ComponentType.Cone
                && solid.AxisIndex == 2
                && (solid.TopRadius > 0 || solid.BottomRadius > 0))
            {
                shape = PartShape.Polygonal;

                geometry.Polylines.Add(Polygon(
                    (bounds.CenterX - solid.BottomRadius, bounds.MinY),
                    (bounds.CenterX + solid.BottomRadius, bounds.MinY),
                    (bounds.CenterX + solid.TopRadius, bounds.MaxY),
                    (bounds.CenterX - solid.TopRadius, bounds.MaxY)));
            }
            else if (solid.Type == ComponentType.Cylinder && solid.AxisIndex == 1 && solid.Radius > 0)
            {
                shape = PartShape.Circular;

                geometry.Circles.Add(new GeometryCircle
                {
                    Center = new GeometryPoint(bounds.CenterX, bounds.CenterY),
                    Radius = solid.Radius
                });
            }
            else
            {
                geometry.Polylines.Add(Polygon(
                    (bounds.MinX, bounds.MinY),
                    (bounds.MaxX, bounds.MinY),
                    (bounds.MaxX, bounds.MaxY),
                    (bounds.MinX, bounds.MaxY)));
            }

            var node = new AssemblyNode
            {
                Id = "step-" + (i + 1).ToString(CultureInfo.InvariantCulture),
                Name = solid.Name,
                Layer = StepGeometryAnalyzer.SourceLayer,
                Shape = shape,
                Bounds = bounds,
                Local = new BoundingBox2D(0, 0, bounds.Width, bounds.Height),
                PolylineCount = geometry.Polylines.Count,
                CircleCount = geometry.Circles.Count,
                HasClosedProfile = true,
                IsFilled = false,
                Geometry = geometry
            };

            if (component.Height.HasValue)
            {
                node.Parameters["height"] = (double)component.Height.Value;
            }

            if (component.Width.HasValue)
            {
                node.Parameters["width"] = (double)component.Width.Value;
            }

            if (component.Diameter.HasValue)
            {
                node.Parameters["diameter"] = (double)component.Diameter.Value;
            }

            if (component.TopDiameter.HasValue)
            {
                node.Parameters["topDiameter"] = (double)component.TopDiameter.Value;
            }

            if (component.BottomDiameter.HasValue)
            {
                node.Parameters["bottomDiameter"] = (double)component.BottomDiameter.Value;
            }

            nodes.Add(node);
        }

        for (var i = 0; i < solids.Count; i++)
        {
            var best = -1;
            var bestVolume = double.MaxValue;

            for (var j = 0; j < solids.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                if (Contains(solids[j], solids[i], 0.01)
                    && solids[j].Volume > solids[i].Volume * 1.0001
                    && solids[j].Volume < bestVolume)
                {
                    best = j;
                    bestVolume = solids[j].Volume;
                }
            }

            if (best >= 0)
            {
                nodes[i].ParentId = nodes[best].Id;
                nodes[best].ChildIds.Add(nodes[i].Id);
            }
        }

        var parent = Enumerable.Range(0, solids.Count).ToArray();

        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        for (var i = 0; i < solids.Count; i++)
        {
            for (var j = i + 1; j < solids.Count; j++)
            {
                if (Touches(solids[i], solids[j], StepConnectivity.DefaultTolerance))
                {
                    var rootI = Find(i);
                    var rootJ = Find(j);

                    if (rootI != rootJ)
                    {
                        parent[rootJ] = rootI;
                    }
                }
            }
        }

        var clusters = new List<List<int>>();
        var clusterOf = new Dictionary<int, int>();

        for (var i = 0; i < solids.Count; i++)
        {
            var root = Find(i);

            if (!clusterOf.TryGetValue(root, out var clusterIndex))
            {
                clusterIndex = clusters.Count;
                clusterOf[root] = clusterIndex;
                clusters.Add(new List<int>());
            }

            clusters[clusterIndex].Add(i);
        }

        for (var c = 0; c < clusters.Count; c++)
        {
            var members = clusters[c];
            var union = nodes[members[0]].Bounds;

            foreach (var index in members)
            {
                union = union.Union(nodes[index].Bounds);
            }

            var assembly = new ComponentAssembly
            {
                Id = "step-asm-" + (c + 1).ToString(CultureInfo.InvariantCulture),
                AssemblyName = clusters.Count == 1 ? "Assembly" : "Assembly " + (c + 1).ToString(CultureInfo.InvariantCulture),
                Category = "Assembly",
                Bounds = union,
                Confidence = 0.9m
            };

            foreach (var index in members)
            {
                nodes[index].AssemblyId = assembly.Id;
                assembly.NodeIds.Add(nodes[index].Id);
            }

            tree.Assemblies.Add(assembly);
        }

        if (clusters.Count > 1)
        {
            tree.Warnings.Add("The solids form " + clusters.Count.ToString(CultureInfo.InvariantCulture) + " separate groups.");
        }

        var all = nodes[0].Bounds;

        foreach (var node in nodes)
        {
            all = all.Union(node.Bounds);
        }

        tree.Bounds = all;
        tree.Nodes = nodes;
        tree.RootIds = nodes.Where(n => string.IsNullOrEmpty(n.ParentId)).Select(n => n.Id).ToList();

        result.Tree = tree;
    }
}