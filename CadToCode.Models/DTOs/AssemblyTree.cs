using System.Text.Json.Serialization;
using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public enum PartShape
{
    Unknown = 0,
    Rectangular = 1,
    Circular = 2,
    Annular = 3,
    Polygonal = 4,
    Linear = 5,
    Irregular = 6
}

public enum AssemblyRelationType
{
    Adjacent = 1,
    Overlapping = 2,
    Contained = 3,
    Aligned = 4,
    Concentric = 5,
    Connected = 6
}

public enum AssemblyArrayKind
{
    Linear = 1,
    Polar = 2
}

public record BoundingBox2D(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) / 2.0;
    public double CenterY => (MinY + MaxY) / 2.0;
    public double Area => Math.Max(Width, 0) * Math.Max(Height, 0);

    public BoundingBox2D Expand(double amount)
    {
        return new BoundingBox2D(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);
    }

    public BoundingBox2D Union(BoundingBox2D other)
    {
        return new BoundingBox2D(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY));
    }

    public bool Intersects(BoundingBox2D other)
    {
        return MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;
    }

    public bool Contains(BoundingBox2D other, double tolerance = 0)
    {
        return other.MinX >= MinX - tolerance
            && other.MaxX <= MaxX + tolerance
            && other.MinY >= MinY - tolerance
            && other.MaxY <= MaxY + tolerance;
    }

    public double GapTo(BoundingBox2D other)
    {
        var dx = Math.Max(0, Math.Max(MinX - other.MaxX, other.MinX - MaxX));
        var dy = Math.Max(0, Math.Max(MinY - other.MaxY, other.MinY - MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public BoundingBox2D Translate(double dx, double dy)
    {
        return new BoundingBox2D(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);
    }
}

public class AssemblyNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Layer { get; set; }
    public PartShape Shape { get; set; } = PartShape.Unknown;
    public BoundingBox2D Bounds { get; set; } = new(0, 0, 0, 0);
    public BoundingBox2D Local { get; set; } = new(0, 0, 0, 0);
    public int LineCount { get; set; }
    public int ArcCount { get; set; }
    public int CircleCount { get; set; }
    public int PolylineCount { get; set; }
    public int HatchCount { get; set; }
    public bool HasClosedProfile { get; set; }
    public bool IsFilled { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> Tokens { get; set; } = new();
    public Dictionary<string, double> Parameters { get; set; } = new();
    public string? ParentId { get; set; }
    public List<string> ChildIds { get; set; } = new();
    public string? ArrayGroupId { get; set; }

    [JsonIgnore]
    public ExtractedGeometry Geometry { get; set; } = new();
}

public class AssemblyRelation
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public AssemblyRelationType Type { get; set; }
    public string? FromAnchor { get; set; }
    public string? ToAnchor { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Gap { get; set; }
    public double OverlapX { get; set; }
    public double OverlapY { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class AssemblyArrayGroup
{
    public string Id { get; set; } = string.Empty;
    public AssemblyArrayKind Kind { get; set; }
    public List<string> MemberIds { get; set; } = new();
    public int Count { get; set; }
    public double Spacing { get; set; }
    public double DirectionDegrees { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Radius { get; set; }
    public double StartAngleDegrees { get; set; }
    public double AngleStepDegrees { get; set; }
    public string? CenterNodeId { get; set; }
}

public class AssemblyTree
{
    public DrawingUnit Unit { get; set; } = DrawingUnit.Millimeter;
    public BoundingBox2D Bounds { get; set; } = new(0, 0, 0, 0);
    public List<AssemblyNode> Nodes { get; set; } = new();
    public List<AssemblyRelation> Relations { get; set; } = new();
    public List<AssemblyArrayGroup> Arrays { get; set; } = new();
    public List<string> RootIds { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}