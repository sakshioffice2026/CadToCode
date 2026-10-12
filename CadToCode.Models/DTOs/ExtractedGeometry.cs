using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ExtractedGeometry
{
    public DrawingUnit DetectedUnit { get; set; } = DrawingUnit.Millimeter;
    public string? SourceVersion { get; set; }
    public string? OriginalVersion { get; set; }
    public bool HeaderPatched { get; set; }
    public List<GeometryLine> Lines { get; set; } = new();
    public List<GeometryPolyline> Polylines { get; set; } = new();
    public List<GeometryArc> Arcs { get; set; } = new();
    public List<GeometryCircle> Circles { get; set; } = new();
    public List<GeometryHatch> Hatches { get; set; } = new();
    public List<GeometryDimension> Dimensions { get; set; } = new();
    public List<GeometryText> Texts { get; set; } = new();
    public List<string> Layers { get; set; } = new();
    public GeometryBounds? Bounds { get; set; }
    public List<string> Warnings { get; set; } = new();

    public int PrimitiveCount =>
        Lines.Count + Polylines.Count + Arcs.Count + Circles.Count + Hatches.Count;

    public int TokenCount => Texts.Sum(t => t.Tokens.Count);
}

public record GeometryPoint(double X, double Y);

public record GeometryVertex(double X, double Y, double Bulge = 0);

public record GeometryBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) / 2.0;
    public double CenterY => (MinY + MaxY) / 2.0;
    public double AspectRatio => Height <= 0 ? 0 : Width / Height;
}

public class GeometryLine
{
    public GeometryPoint Start { get; set; } = new(0, 0);
    public GeometryPoint End { get; set; } = new(0, 0);
    public string? Layer { get; set; }
    public string? LineType { get; set; }
    public string? Handle { get; set; }

    public double Length
    {
        get
        {
            var dx = End.X - Start.X;
            var dy = End.Y - Start.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

public class GeometryPolyline
{
    public List<GeometryVertex> Vertices { get; set; } = new();
    public bool IsClosed { get; set; }
    public string? Layer { get; set; }
    public string? LineType { get; set; }
    public string? Handle { get; set; }
}

public class GeometryArc
{
    public GeometryPoint Center { get; set; } = new(0, 0);
    public double Radius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    public string? Layer { get; set; }
    public string? LineType { get; set; }
    public string? Handle { get; set; }
}

public class GeometryCircle
{
    public GeometryPoint Center { get; set; } = new(0, 0);
    public double Radius { get; set; }
    public string? Layer { get; set; }
    public string? LineType { get; set; }
    public string? Handle { get; set; }
}

public class GeometryHatch
{
    public string? PatternName { get; set; }
    public bool IsSolid { get; set; }
    public List<List<GeometryPoint>> BoundaryPaths { get; set; } = new();
    public string? Layer { get; set; }
    public string? LineType { get; set; }
    public string? Handle { get; set; }
}

public class GeometryDimension
{
    public string? Text { get; set; }
    public double? Value { get; set; }
    public GeometryPoint? Position { get; set; }
    public string? Layer { get; set; }
    public string? Handle { get; set; }
}

public class GeometryText
{
    public string Text { get; set; } = string.Empty;
    public GeometryPoint Position { get; set; } = new(0, 0);
    public double Height { get; set; }
    public double Rotation { get; set; }
    public bool IsMultiLine { get; set; }
    public string EntityType { get; set; } = "TEXT";
    public List<string> Tokens { get; set; } = new();
    public List<double> Numbers { get; set; } = new();
    public string? Layer { get; set; }
    public string? Handle { get; set; }
}