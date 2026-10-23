using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("ZooGenerations")]
public class ZooGeneration
{
    [Key, Column("Id")]
    public Guid Id { get; set; }

    [Required, MaxLength(2000), Column("Prompt")]
    public string Prompt { get; set; } = string.Empty;

    [Required, MaxLength(10), Column("OutputFormat")]
    public string OutputFormat { get; set; } = string.Empty;

    // queued, in_progress, completed or failed
    [Required, MaxLength(20), Column("Status")]
    public string Status { get; set; } = "queued";

    [MaxLength(1000), Column("Error")]
    public string? Error { get; set; }

    [Column("Kcl", TypeName = "longtext")]
    public string? Kcl { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("UpdatedAt")]
    public DateTime? UpdatedAt { get; set; }

    public List<ZooGenerationFile> Files { get; set; } = new();
}