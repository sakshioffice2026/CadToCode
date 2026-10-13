namespace CadToCode.Models.Dtos;

public enum ContourSource
{
    ClosedPolyline = 1,
    Circle = 2,
    Hatch = 3,
    TracedFace = 4,
    Stroke = 5
}

public class GeometryContour
{
    public string Name { get; set; } = string.Empty;
    public string? Layer { get; set; }
    public ContourSource Source { get; set; }
    public List<GeometryPoint> Points { get; set; } = new();
    public bool IsCircle { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Radius { get; set; }
    public double Area { get; set; }
    public int ComponentId { get; set; }
    public int Depth { get; set; }
    public int ParentIndex { get; set; } = -1;
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }

    public bool IsSolid => Depth % 2 == 0;

    public void UpdateBounds()
    {
        if (Points.Count == 0)
        {
            MinX = MinY = MaxX = MaxY = 0;
            return;
        }

        MinX = Points.Min(p => p.X);
        MinY = Points.Min(p => p.Y);
        MaxX = Points.Max(p => p.X);
        MaxY = Points.Max(p => p.Y);
    }
}