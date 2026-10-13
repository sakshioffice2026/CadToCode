using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ProjectHistoryItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public SourceFileType SourceFileType { get; set; }
    public long FileSizeBytes { get; set; }
    public ProjectStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public int ComponentCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
}