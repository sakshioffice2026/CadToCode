namespace CadToCode.Models.Dtos;

public static class CadLibraryFileType
{
    public const string Dxf = "Dxf";
    public const string Step = "Step";
    public const string CSharp = "CSharp";
}

public static class CadLibrarySyncStatus
{
    public const string Pending = "Pending";
    public const string Synced = "Synced";
    public const string Failed = "Failed";
}

public class CadLibraryItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public string? Description { get; set; }
    public string FileType { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? CodeSnippet { get; set; }
    public string SyncStatus { get; set; } = CadLibrarySyncStatus.Pending;
    public string? SyncError { get; set; }
    public DateTime? SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CadLibraryUploadRequestDto
{
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string? Tags { get; set; }
    public string? Description { get; set; }
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class CadLibraryUpdateRequestDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string? Tags { get; set; }
    public string? Description { get; set; }
}

public class CadLibraryResultDto
{
    public bool Success { get; set; }
    public int? ItemId { get; set; }
    public string? ErrorMessage { get; set; }
}

public class CadLibrarySyncResultDto
{
    public int Total { get; set; }
    public int Synced { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class CadLibraryStatsDto
{
    public int TotalItems { get; set; }
    public int SyncedItems { get; set; }
    public int PendingItems { get; set; }
    public int FailedItems { get; set; }
    public List<string> Categories { get; set; } = new();
}

public class CadLibraryIndexViewModel
{
    public List<CadLibraryItemDto> Items { get; set; } = new();
    public CadLibraryStatsDto Stats { get; set; } = new();
    public string? SearchText { get; set; }
    public string? CategoryFilter { get; set; }
    public string? FileTypeFilter { get; set; }
}

public class VectorSearchHit
{
    public Guid PointId { get; set; }
    public int LibraryItemId { get; set; }
    public double Score { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string? CodeSnippet { get; set; }
    public string? MetadataSummary { get; set; }
}

public class VectorUpsertRequest
{
    public Guid PointId { get; set; }
    public float[] Vector { get; set; } = Array.Empty<float>();
    public int LibraryItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string? CodeSnippet { get; set; }
    public string? MetadataSummary { get; set; }
}