using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;
[Table("dxf_jobs")]
public class DxfJob
{
    [Key, Column("id")] public int Id { get; set; }
    [Column("original_file_name")] public string OriginalFileName { get; set; } = "";
    [Column("stored_file_path")] public string StoredFilePath { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "Uploaded";
    [Column("uploaded_at")] public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public List<ExtractedPart> Parts { get; set; } = new();
    public List<GeneratedDrawing> Drawings { get; set; } = new();
}
