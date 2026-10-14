using CadToCode.Models.Dtos;

namespace CadToCode.Repositories.Interfaces;

public class CadLibraryRecord
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string? Tags { get; set; }
    public string? Description { get; set; }
    public string FileType { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string? CodeSnippet { get; set; }
    public string? MetadataJson { get; set; }
    public string? EmbeddingText { get; set; }
    public Guid VectorPointId { get; set; } = Guid.NewGuid();
    public string SyncStatus { get; set; } = CadLibrarySyncStatus.Pending;
    public string? SyncError { get; set; }
}

public interface ICadLibraryRepository
{
    Task<int> AddAsync(CadLibraryRecord record, CancellationToken cancellationToken = default);

    Task<CadLibraryRecord?> GetRecordAsync(int id, CancellationToken cancellationToken = default);

    Task<CadLibraryItemDto?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<(byte[] Content, string FileName)?> GetFileAsync(int id, CancellationToken cancellationToken = default);

    Task<List<CadLibraryItemDto>> ListAsync(
        string? searchText,
        string? category,
        string? fileType,
        CancellationToken cancellationToken = default);

    Task<List<int>> ListIdsForSyncAsync(bool pendingAndFailedOnly, CancellationToken cancellationToken = default);

    Task<CadLibraryStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);

    Task UpdateMetadataAsync(
        int id,
        string title,
        string category,
        string? tags,
        string? description,
        string? embeddingText,
        CancellationToken cancellationToken = default);

    Task MarkSyncedAsync(int id, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(int id, string error, CancellationToken cancellationToken = default);

    Task MarkPendingAsync(int id, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}