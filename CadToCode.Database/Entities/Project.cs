using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("Projects")]
public class Project
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Required, MaxLength(200), Column("Title")]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(20), Column("SourceFileType")]
    public string SourceFileType { get; set; } = string.Empty;

    [Required, MaxLength(260), Column("OriginalFileName")]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required, MaxLength(500), Column("StoredFilePath")]
    public string StoredFilePath { get; set; } = string.Empty;

    [Column("FileSizeBytes")]
    public long FileSizeBytes { get; set; }

    [MaxLength(20), Column("DetectedUnit")]
    public string? DetectedUnit { get; set; }

    [Required, MaxLength(20), Column("Unit")]
    public string Unit { get; set; } = "Millimeter";

    [Column("ScaleMmPerPixel", TypeName = "decimal(18,8)")]
    public decimal? ScaleMmPerPixel { get; set; }

    [Required, MaxLength(20), Column("Status")]
    public string Status { get; set; } = "Pending";

    [MaxLength(1000), Column("ErrorMessage")]
    public string? ErrorMessage { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("LastOpenedAt")]
    public DateTime? LastOpenedAt { get; set; }

    public ICollection<DrawingComponent> DrawingComponents { get; set; } = new List<DrawingComponent>();
}