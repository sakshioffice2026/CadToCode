using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Repositories.Interfaces;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public sealed class CadLibraryService : ICadLibraryService
{
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const int MaxTitleLength = 200;
    private const int MaxCategoryLength = 100;
    private const int MaxTagsLength = 500;
    private const int MaxDescriptionLength = 1000;
    private const int MaxFileNameLength = 260;
    private const int MaxStoredCodeChars = 200_000;
    private const int MaxEmbeddingCodeChars = 4000;

    private static readonly Dictionary<string, string> ExtensionTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".dxf"] = CadLibraryFileType.Dxf,
            [".step"] = CadLibraryFileType.Step,
            [".stp"] = CadLibraryFileType.Step,
            [".cs"] = CadLibraryFileType.CSharp
        };

    private static readonly Regex TypeNameRegex =
        new(@"\b(?:class|record|struct|interface)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    private static readonly Regex StepEntityRegex =
        new(@"#\d+\s*=\s*([A-Z_0-9]+)\s*\(", RegexOptions.Compiled);

    private static readonly Regex StepFileNameRegex =
        new(@"FILE_NAME\s*\(\s*'([^']*)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StepDescriptionRegex =
        new(@"FILE_DESCRIPTION\s*\(\s*\(\s*'([^']*)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStoreService _vectors;
    private readonly ILogger<CadLibraryService> _logger;

    public CadLibraryService(
        IUnitOfWork unitOfWork,
        IEmbeddingService embeddings,
        IVectorStoreService vectors,
        ILogger<CadLibraryService> logger)
    {
        _unitOfWork = unitOfWork;
        _embeddings = embeddings;
        _vectors = vectors;
        _logger = logger;
    }

    public async Task<CadLibraryIndexViewModel> ListAsync(
        string? searchText,
        string? category,
        string? fileType,
        CancellationToken cancellationToken = default)
    {
        var items = await _unitOfWork.CadLibrary.ListAsync(searchText, category, fileType, cancellationToken);
        var stats = await _unitOfWork.CadLibrary.GetStatsAsync(cancellationToken);

        return new CadLibraryIndexViewModel
        {
            Items = items,
            Stats = stats,
            SearchText = searchText,
            CategoryFilter = category,
            FileTypeFilter = fileType
        };
    }

    public Task<CadLibraryItemDto?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return _unitOfWork.CadLibrary.GetAsync(id, cancellationToken);
    }

    public Task<(byte[] Content, string FileName)?> GetFileAsync(int id, CancellationToken cancellationToken = default)
    {
        return _unitOfWork.CadLibrary.GetFileAsync(id, cancellationToken);
    }

    public async Task<CadLibraryResultDto> AddAsync(
        CadLibraryUploadRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var fileName = Truncate(Path.GetFileName(request.FileName ?? string.Empty), MaxFileNameLength);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Fail("A file is required.");
        }

        if (!ExtensionTypes.TryGetValue(Path.GetExtension(fileName), out var fileType))
        {
            return Fail("Only .dxf, .step, .stp and .cs files can be added to the library.");
        }

        if (request.Content.Length == 0)
        {
            return Fail("The selected file is empty.");
        }

        if (request.Content.Length > MaxFileBytes)
        {
            return Fail("The selected file is larger than " + (MaxFileBytes / (1024 * 1024)) + " MB.");
        }

        var title = Truncate(
            string.IsNullOrWhiteSpace(request.Title) ? Path.GetFileNameWithoutExtension(fileName) : request.Title.Trim(),
            MaxTitleLength);
        var category = NormalizeCategory(request.Category);
        var tags = NormalizeTags(request.Tags);
        var description = NormalizeDescription(request.Description);

        var analysis = Analyze(fileType, request.Content);

        var record = new CadLibraryRecord
        {
            Title = title,
            Category = category,
            Tags = tags,
            Description = description,
            FileType = fileType,
            OriginalFileName = fileName,
            FileSizeBytes = request.Content.Length,
            Content = request.Content,
            CodeSnippet = analysis.Code,
            MetadataJson = analysis.MetadataJson,
            EmbeddingText = BuildEmbeddingText(title, category, tags, description, fileType, analysis.Summary, analysis.Code),
            VectorPointId = Guid.NewGuid()
        };

        var id = await _unitOfWork.CadLibrary.AddAsync(record, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var sync = await SyncCoreAsync(id, cancellationToken);

        return new CadLibraryResultDto
        {
            Success = true,
            ItemId = id,
            ErrorMessage = sync.Success ? null : "Saved to the database, but cloud sync failed: " + sync.ErrorMessage
        };
    }

    public async Task<CadLibraryResultDto> UpdateAsync(
        CadLibraryUpdateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var record = await _unitOfWork.CadLibrary.GetRecordAsync(request.Id, cancellationToken);

        if (record is null)
        {
            return Fail("Library item not found.");
        }

        var title = Truncate(
            string.IsNullOrWhiteSpace(request.Title) ? record.Title : request.Title.Trim(),
            MaxTitleLength);
        var category = NormalizeCategory(request.Category);
        var tags = NormalizeTags(request.Tags);
        var description = NormalizeDescription(request.Description);

        var summary = ExtractSummary(record.MetadataJson);
        var embeddingText = BuildEmbeddingText(title, category, tags, description, record.FileType, summary, record.CodeSnippet);

        await _unitOfWork.CadLibrary.UpdateMetadataAsync(
            record.Id, title, category, tags, description, embeddingText, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var sync = await SyncCoreAsync(record.Id, cancellationToken);

        return new CadLibraryResultDto
        {
            Success = true,
            ItemId = record.Id,
            ErrorMessage = sync.Success ? null : "Saved to the database, but cloud sync failed: " + sync.ErrorMessage
        };
    }

    public async Task<CadLibraryResultDto> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var record = await _unitOfWork.CadLibrary.GetRecordAsync(id, cancellationToken);

        if (record is null)
        {
            return Fail("Library item not found.");
        }

        try
        {
            if (_vectors.IsConfigured)
            {
                await _vectors.DeleteAsync(record.VectorPointId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not delete vector point {PointId} for library item {Id}.", record.VectorPointId, id);
        }

        await _unitOfWork.CadLibrary.DeleteAsync(id, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CadLibraryResultDto { Success = true, ItemId = id };
    }

    public Task<CadLibraryResultDto> SyncAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return SyncCoreAsync(id, cancellationToken);
    }

    public async Task<CadLibrarySyncResultDto> SyncAllAsync(
        bool pendingAndFailedOnly,
        CancellationToken cancellationToken = default)
    {
        var ids = await _unitOfWork.CadLibrary.ListIdsForSyncAsync(pendingAndFailedOnly, cancellationToken);
        var result = new CadLibrarySyncResultDto { Total = ids.Count };

        foreach (var id in ids)
        {
            var sync = await SyncCoreAsync(id, cancellationToken);

            if (sync.Success)
            {
                result.Synced++;
            }
            else
            {
                result.Failed++;
                result.Errors.Add("Item " + id + ": " + sync.ErrorMessage);
            }
        }

        return result;
    }

    private async Task<CadLibraryResultDto> SyncCoreAsync(int id, CancellationToken cancellationToken)
    {
        var record = await _unitOfWork.CadLibrary.GetRecordAsync(id, cancellationToken);

        if (record is null)
        {
            return Fail("Library item not found.");
        }

        try
        {
            if (!_embeddings.IsConfigured)
            {
                throw new InvalidOperationException("Gemini embedding is not configured.");
            }

            if (!_vectors.IsConfigured)
            {
                throw new InvalidOperationException("Qdrant is not configured.");
            }

            var summary = ExtractSummary(record.MetadataJson);
            var text = string.IsNullOrWhiteSpace(record.EmbeddingText)
                ? BuildEmbeddingText(record.Title, record.Category, record.Tags, record.Description, record.FileType, summary, record.CodeSnippet)
                : record.EmbeddingText;

            var vector = await _embeddings.EmbedAsync(text, EmbeddingTaskType.RetrievalDocument, cancellationToken);

            await _vectors.UpsertAsync(
                new VectorUpsertRequest
                {
                    PointId = record.VectorPointId,
                    Vector = vector,
                    LibraryItemId = record.Id,
                    Title = record.Title,
                    Category = record.Category,
                    FileType = record.FileType,
                    Tags = record.Tags,
                    CodeSnippet = record.CodeSnippet,
                    MetadataSummary = summary
                },
                cancellationToken);

            await _unitOfWork.CadLibrary.MarkSyncedAsync(id, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return new CadLibraryResultDto { Success = true, ItemId = id };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cloud sync failed for library item {Id}.", id);

            await _unitOfWork.CadLibrary.MarkFailedAsync(id, ex.Message, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return new CadLibraryResultDto { Success = false, ItemId = id, ErrorMessage = ex.Message };
        }
    }

    private static (string? Code, string Summary, string MetadataJson) Analyze(string fileType, byte[] content)
    {
        try
        {
            return fileType switch
            {
                CadLibraryFileType.CSharp => AnalyzeCSharp(content),
                CadLibraryFileType.Dxf => AnalyzeDxf(content),
                _ => AnalyzeStep(content)
            };
        }
        catch (Exception)
        {
            var summary = fileType + " file (metadata could not be parsed).";
            return (null, summary, JsonSerializer.Serialize(new { summary }));
        }
    }

    private static (string? Code, string Summary, string MetadataJson) AnalyzeCSharp(byte[] content)
    {
        var code = Encoding.UTF8.GetString(content).TrimStart('\uFEFF');

        if (code.Length > MaxStoredCodeChars)
        {
            code = code[..MaxStoredCodeChars];
        }

        var types = TypeNameRegex.Matches(code)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Take(10)
            .ToList();
        var lineCount = code.Count(c => c == '\n') + 1;

        var summary = "C# procedural template, " + lineCount + " lines"
            + (types.Count > 0 ? ", types: " + string.Join(", ", types) : string.Empty);

        var json = JsonSerializer.Serialize(new { summary, lines = lineCount, types });
        return (code, summary, json);
    }

    private static (string? Code, string Summary, string MetadataJson) AnalyzeDxf(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var file = DxfFile.Load(stream);

        var counts = file.Entities
            .GroupBy(e => e.EntityType.ToString())
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        var layers = file.Layers
            .Select(l => l.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(20)
            .ToList();

        var texts = new List<string>();

        foreach (var entity in file.Entities)
        {
            switch (entity)
            {
                case DxfText text when !string.IsNullOrWhiteSpace(text.Value):
                    texts.Add(text.Value.Trim());
                    break;
                case DxfMText mText when !string.IsNullOrWhiteSpace(mText.Text):
                    texts.Add(mText.Text.Trim());
                    break;
            }
        }

        var labels = texts
            .Select(t => t.Length > 40 ? t[..40] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();

        var unit = file.Header.DefaultDrawingUnits.ToString();

        var summary = "DXF drawing, unit " + unit
            + ", entities: " + string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value))
            + (layers.Count > 0 ? "; layers: " + string.Join(", ", layers) : string.Empty)
            + (labels.Count > 0 ? "; texts: " + string.Join(" | ", labels) : string.Empty);

        var json = JsonSerializer.Serialize(new { summary, unit, entities = counts, layers, labels });
        return (null, summary, json);
    }

    private static (string? Code, string Summary, string MetadataJson) AnalyzeStep(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);

        var name = StepFileNameRegex.Match(text);
        var description = StepDescriptionRegex.Match(text);

        var counts = StepEntityRegex.Matches(text)
            .GroupBy(m => m.Groups[1].Value)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        var summary = "STEP model"
            + (name.Success && name.Groups[1].Value.Length > 0 ? ", name " + name.Groups[1].Value : string.Empty)
            + (description.Success && description.Groups[1].Value.Length > 0 ? ", description " + description.Groups[1].Value : string.Empty)
            + (counts.Count > 0 ? ", entities: " + string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value)) : string.Empty);

        var json = JsonSerializer.Serialize(new { summary, entities = counts });
        return (null, summary, json);
    }

    private static string BuildEmbeddingText(
        string title,
        string category,
        string? tags,
        string? description,
        string fileType,
        string? summary,
        string? code)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Title: " + title);
        sb.AppendLine("Category: " + category);
        sb.AppendLine("File type: " + fileType);

        if (!string.IsNullOrWhiteSpace(tags))
        {
            sb.AppendLine("Tags: " + tags);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine("Description: " + description);
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine("Summary: " + summary);
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            sb.AppendLine("Code:");
            sb.AppendLine(code.Length > MaxEmbeddingCodeChars ? code[..MaxEmbeddingCodeChars] : code);
        }

        return sb.ToString();
    }

    private static string? ExtractSummary(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);

            return document.RootElement.TryGetProperty("summary", out var summary)
                ? summary.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeCategory(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? "General"
            : Truncate(category.Trim(), MaxCategoryLength);
    }

    private static string? NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var parts = tags
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var joined = string.Join(", ", parts);
        return joined.Length == 0 ? null : Truncate(joined, MaxTagsLength);
    }

    private static string? NormalizeDescription(string? description)
    {
        return string.IsNullOrWhiteSpace(description)
            ? null
            : Truncate(description.Trim(), MaxDescriptionLength);
    }

    private static string Truncate(string value, int max)
    {
        return value.Length > max ? value[..max] : value;
    }

    private static CadLibraryResultDto Fail(string message)
    {
        return new CadLibraryResultDto { Success = false, ErrorMessage = message };
    }
}