namespace DxfManufacturing.Models;

public class DrawingGroup
{
    public string Name { get; set; } = "";
    public List<string> Layers { get; set; } = new();
}
