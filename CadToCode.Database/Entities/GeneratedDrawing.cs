using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("generated_drawings")]
public class GeneratedDrawing
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("job_id")] public int JobId { get; set; }
    [Column("part_id")] public int? PartId { get; set; }
    [Column("group_name")] public string? GroupName { get; set; }
    [Column("file_name")] public string FileName { get; set; } = "";
    [Column("file_path")] public string FilePath { get; set; } = "";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DxfJob? Job { get; set; }
}
