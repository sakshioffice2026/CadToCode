using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("extracted_parts")]
public class ExtractedPart
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("job_id")] public int JobId { get; set; }
    [Column("layer_name")] public string LayerName { get; set; } = "";
    [Column("entity_count")] public int EntityCount { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DxfJob? Job { get; set; }
}
