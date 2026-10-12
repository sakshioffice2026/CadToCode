using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("DrawingComponents")]
public class DrawingComponent
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Column("ProjectId")]
    public int ProjectId { get; set; }

    [Required, MaxLength(20), Column("ComponentType")]
    public string ComponentType { get; set; } = string.Empty;

    [Required, MaxLength(100), Column("Name")]
    public string Name { get; set; } = string.Empty;

    [Column("SequenceIndex")]
    public int SequenceIndex { get; set; }

    [MaxLength(100), Column("Layer")]
    public string? Layer { get; set; }

    [Column("Height", TypeName = "decimal(18,4)")]
    public decimal? Height { get; set; }

    [Column("Width", TypeName = "decimal(18,4)")]
    public decimal? Width { get; set; }

    [Column("Diameter", TypeName = "decimal(18,4)")]
    public decimal? Diameter { get; set; }

    [Column("TopDiameter", TypeName = "decimal(18,4)")]
    public decimal? TopDiameter { get; set; }

    [Column("BottomDiameter", TypeName = "decimal(18,4)")]
    public decimal? BottomDiameter { get; set; }

    [Column("Angle", TypeName = "decimal(18,4)")]
    public decimal? Angle { get; set; }

    [Column("HasHatch")]
    public bool HasHatch { get; set; }

    [Column("IsManuallyEdited")]
    public bool IsManuallyEdited { get; set; }

    [Column("Confidence", TypeName = "decimal(5,2)")]
    public decimal Confidence { get; set; } = 1.00m;

    [Column("RawDataJson", TypeName = "text")]
    public string? RawDataJson { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }
}