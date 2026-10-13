using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ComponentDto
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
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
    public bool IsManuallyEdited { get; set; }
    public StructuralProfile? Profile { get; set; }
    public decimal Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
}