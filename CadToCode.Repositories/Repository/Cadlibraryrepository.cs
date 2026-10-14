using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Models.Dtos;
using CadToCode.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Repositories.Repository;

public class CadLibraryRepository : ICadLibraryRepository
{
    private const int MaxErrorLength = 1000;

    private readonly ApplicationDbContext _context;

    public CadLibraryRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> AddAsync(CadLibraryRecord record, CancellationToken cancellationToken = default)
    {
        var entity = new CadLibraryItem
        {
            Title = record.Title,
            Category = record.Category,
            Tags = record.Tags,
            Description = record.Description,
            FileType = record.FileType,
            OriginalFileName = record.OriginalFileName,
            FileSizeBytes = record.FileSizeBytes,
            Content = record.Content,
            CodeSnippet = record.CodeSnippet,
            MetadataJson = record.MetadataJson,
            EmbeddingText = record.EmbeddingText,
            VectorPointId = record.VectorPointId,
            SyncStatus = CadLibrarySyncStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _context.CadLibraryItems.Add(entity);

        // Flush here only to obtain the generated Id; the surrounding transaction stays open.
        await _context.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<CadLibraryRecord?> GetRecordAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        return new CadLibraryRecord
        {
            Id = entity.Id,
            Title = entity.Title,
            Category = entity.Category,
            Tags = entity.Tags,
            Description = entity.Description,
            FileType = entity.FileType,
            OriginalFileName = entity.OriginalFileName,
            FileSizeBytes = entity.FileSizeBytes,
            Content = entity.Content,
            CodeSnippet = entity.CodeSnippet,
            MetadataJson = entity.MetadataJson,
            EmbeddingText = entity.EmbeddingText,
            VectorPointId = entity.VectorPointId,
            SyncStatus = entity.SyncStatus,
            SyncError = entity.SyncError
        };
    }

    public async Task<CadLibraryItemDto?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        return entity is null ? null : ToDto(entity);
    }

    public async Task<(byte[] Content, string FileName)?> GetFileAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await _context.CadLibraryItems
            .AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { i.Content, i.OriginalFileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return (row.Content, row.OriginalFileName);
    }

    public async Task<List<CadLibraryItemDto>> ListAsync(
        string? searchText,
        string? category,
        string? fileType,
        CancellationToken cancellationToken = default)
    {
        var query = _context.CadLibraryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(i => i.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(fileType))
        {
            query = query.Where(i => i.FileType == fileType);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            query = query.Where(i =>
                i.Title.Contains(term)
                || (i.Tags != null && i.Tags.Contains(term))
                || (i.Description != null && i.Description.Contains(term))
                || i.OriginalFileName.Contains(term));
        }

        var entities = await query
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDto).ToList();
    }

    public async Task<List<int>> ListIdsForSyncAsync(bool pendingAndFailedOnly, CancellationToken cancellationToken = default)
    {
        var query = _context.CadLibraryItems.AsNoTracking().AsQueryable();

        if (pendingAndFailedOnly)
        {
            query = query.Where(i =>
                i.SyncStatus == CadLibrarySyncStatus.Pending
                || i.SyncStatus == CadLibrarySyncStatus.Failed);
        }

        return await query
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<CadLibraryStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var grouped = await _context.CadLibraryItems
            .AsNoTracking()
            .GroupBy(i => i.SyncStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var categories = await _context.CadLibraryItems
            .AsNoTracking()
            .Select(i => i.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);

        int Count(string status) => grouped.FirstOrDefault(g => g.Status == status)?.Count ?? 0;

        return new CadLibraryStatsDto
        {
            TotalItems = grouped.Sum(g => g.Count),
            SyncedItems = Count(CadLibrarySyncStatus.Synced),
            PendingItems = Count(CadLibrarySyncStatus.Pending),
            FailedItems = Count(CadLibrarySyncStatus.Failed),
            Categories = categories
        };
    }

    public async Task UpdateMetadataAsync(
        int id,
        string title,
        string category,
        string? tags,
        string? description,
        string? embeddingText,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.Title = title;
        entity.Category = category;
        entity.Tags = tags;
        entity.Description = description;
        entity.EmbeddingText = embeddingText;
        entity.SyncStatus = CadLibrarySyncStatus.Pending;
        entity.SyncError = null;
        entity.UpdatedAt = DateTime.UtcNow;
    }

    public async Task MarkSyncedAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.SyncStatus = CadLibrarySyncStatus.Synced;
        entity.SyncError = null;
        entity.SyncedAt = DateTime.UtcNow;
    }

    public async Task MarkFailedAsync(int id, string error, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.SyncStatus = CadLibrarySyncStatus.Failed;
        entity.SyncError = Truncate(error);
    }

    public async Task MarkPendingAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.SyncStatus = CadLibrarySyncStatus.Pending;
        entity.SyncError = null;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CadLibraryItems
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        if (entity is not null)
        {
            _context.CadLibraryItems.Remove(entity);
        }
    }

    private static CadLibraryItemDto ToDto(CadLibraryItem entity)
    {
        return new CadLibraryItemDto
        {
            Id = entity.Id,
            Title = entity.Title,
            Category = entity.Category,
            Tags = SplitTags(entity.Tags),
            Description = entity.Description,
            FileType = entity.FileType,
            OriginalFileName = entity.OriginalFileName,
            FileSizeBytes = entity.FileSizeBytes,
            CodeSnippet = entity.CodeSnippet,
            SyncStatus = entity.SyncStatus,
            SyncError = entity.SyncError,
            SyncedAt = entity.SyncedAt,
            CreatedAt = entity.CreatedAt
        };
    }

    private static List<string> SplitTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return new List<string>();
        }

        return tags
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string Truncate(string value)
    {
        return value.Length > MaxErrorLength ? value[..MaxErrorLength] : value;
    }
}