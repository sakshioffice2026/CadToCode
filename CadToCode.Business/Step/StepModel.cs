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

    public StepDocument Add(Solid solid)
    {
        if (solid is null)
        {
            throw new ArgumentNullException(nameof(solid));
        }

        SandboxSession.Current?.RegisterSolid();
        _solids.Add(solid);
        return this;
    }

    public StepDocument Add(IEnumerable<Solid> solids)
    {
        if (solids is null)
        {
            throw new ArgumentNullException(nameof(solids));
        }

        foreach (var solid in solids)
        {
            Add(solid);
        }

        return this;
    }

    public string ToStep()
    {
        return StepWriter.Write(this);
    }

    public bool Save(string path)
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

        File.WriteAllText(path, ToStep(), new UTF8Encoding(false));
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