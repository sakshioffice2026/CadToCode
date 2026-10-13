using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ProjectResultDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public SourceFileType SourceFileType { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DrawingUnit? DetectedUnit { get; set; }
    public DrawingUnit Unit { get; set; } = DrawingUnit.Millimeter;
    public decimal? ScaleMmPerPixel { get; set; }
    public ProjectStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public List<ComponentDto> Components { get; set; } = new();
}