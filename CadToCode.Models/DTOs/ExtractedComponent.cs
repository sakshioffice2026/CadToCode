using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ExtractedComponent
{
    public ComponentType ComponentType { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SequenceIndex { get; set; }
    public string? Layer { get; set; }
    public decimal? Height { get; set; }
    public decimal? Width { get; set; }
    public decimal? Diameter { get; set; }
    public decimal? TopDiameter { get; set; }
    public decimal? BottomDiameter { get; set; }
    public decimal? Angle { get; set; }
    public bool HasHatch { get; set; }
    public decimal Confidence { get; set; } = 1.00m;
    public string? RawDataJson { get; set; }
}