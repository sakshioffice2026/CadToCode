using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("ZooGenerationFiles")]
public class ZooGenerationFile
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Column("GenerationId")]
    public Guid GenerationId { get; set; }

    [Required, MaxLength(260), Column("FileName")]
    public string FileName { get; set; } = string.Empty;

    [Column("SizeBytes")]
    public long SizeBytes { get; set; }

    [Column("Content", TypeName = "longblob")]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ZooGeneration? Generation { get; set; }
}