using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CadToCode.Database.Entities;

[Table("CadLibraryItems")]
public class CadLibraryItem
{
    [Key, Column("Id")]
    public int Id { get; set; }

    [Required, MaxLength(200), Column("Title")]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(100), Column("Category")]
    public string Category { get; set; } = "General";

    [MaxLength(500), Column("Tags")]
    public string? Tags { get; set; }

    [MaxLength(1000), Column("Description")]
    public string? Description { get; set; }

    // Dxf, Step or CSharp
    [Required, MaxLength(20), Column("FileType")]
    public string FileType { get; set; } = string.Empty;

    [Required, MaxLength(260), Column("OriginalFileName")]
    public string OriginalFileName { get; set; } = string.Empty;

    [Column("FileSizeBytes")]
    public long FileSizeBytes { get; set; }

    [Column("Content", TypeName = "longblob")]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    [Column("CodeSnippet", TypeName = "longtext")]
    public string? CodeSnippet { get; set; }

    [Column("MetadataJson", TypeName = "longtext")]
    public string? MetadataJson { get; set; }

    [Column("EmbeddingText", TypeName = "longtext")]
    public string? EmbeddingText { get; set; }

    [Column("VectorPointId")]
    public Guid VectorPointId { get; set; } = Guid.NewGuid();

    // Pending, Synced or Failed
    [Required, MaxLength(20), Column("SyncStatus")]
    public string SyncStatus { get; set; } = "Pending";

    [MaxLength(1000), Column("SyncError")]
    public string? SyncError { get; set; }

    [Column("SyncedAt")]
    public DateTime? SyncedAt { get; set; }

    [Column("CreatedAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("UpdatedAt")]
    public DateTime? UpdatedAt { get; set; }
}