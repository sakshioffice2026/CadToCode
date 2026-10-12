using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ExtractedGeometry
{
    public DrawingUnit DetectedUnit { get; set; } = DrawingUnit.Millimeter;
    public List<GeometryLine> Lines { get; set; } = new();
    public List<GeometryArc> Arcs { get; set; } = new();
    public List<GeometryCircle> Circles { get; set; } = new();
    public List<GeometryHatch> Hatches { get; set; } = new();
    public List<GeometryDimension> Dimensions { get; set; } = new();
}

public record GeometryPoint(double X, double Y);

public class GeometryLine
{
    public GeometryPoint Start { get; set; } = new(0, 0);
    public GeometryPoint End { get; set; } = new(0, 0);
    public string? Layer { get; set; }
    public string? LineType { get; set; }
}

public class GeometryArc
{
    public GeometryPoint Center { get; set; } = new(0, 0);
    public double Radius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    public string? Layer { get; set; }
    public string? LineType { get; set; }
}

public class GeometryCircle
{
    public GeometryPoint Center { get; set; } = new(0, 0);
    public double Radius { get; set; }
    public string? Layer { get; set; }
    public string? LineType { get; set; }
}

public class GeometryHatch
{
    public string? PatternName { get; set; }
    public List<List<GeometryPoint>> BoundaryPaths { get; set; } = new();
    public string? Layer { get; set; }
    public string? LineType { get; set; }
}

public class GeometryDimension
{
    public string? Text { get; set; }
    public double? Value { get; set; }
    public GeometryPoint? Position { get; set; }
    public string? Layer { get; set; }
}