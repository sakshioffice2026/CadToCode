namespace CadToCode.Models.Dtos;

public class BlockPartDto
{
    public string BlockName { get; set; } = string.Empty;
    public string? Layer { get; set; }
    public int Quantity { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int LineCount { get; set; }
    public int ArcCount { get; set; }
    public int CircleCount { get; set; }
    public int PolylineCount { get; set; }
    public List<string> Labels { get; set; } = new();
    public Dictionary<string, string> Attributes { get; set; } = new();
    public List<BlockPlacementDto> Placements { get; set; } = new();
}

public class BlockPlacementDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double ScaleX { get; set; } = 1.0;
    public double ScaleY { get; set; } = 1.0;
}

public class ManufacturingBreakdownDto
{
    public int ProjectId { get; set; }
    public string ProjectTitle { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalBlockDefinitions { get; set; }
    public int TotalInstances { get; set; }
    public List<BlockPartDto> Parts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}