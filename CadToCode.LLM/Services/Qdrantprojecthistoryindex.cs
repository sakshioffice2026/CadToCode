using System.Globalization;
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace CadToCode.LLM.Services;

/// <summary>A historical design returned by semantic similarity search.</summary>
public sealed class SimilarDesignHit
{
    public int ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string OriginalFileName { get; init; } = string.Empty;

    public float Score { get; init; }

    /// <summary>The semantic summary text that was embedded for this design.</summary>
    public string EmbeddingText { get; init; } = string.Empty;

    /// <summary>Full design state JSON stored alongside the vector. Null when absent.</summary>
    public string? DesignStateJson { get; init; }
}

public interface IProjectHistoryIndex
{
    bool IsConfigured { get; }

    Task UpsertAsync(ProjectHistoryItemDto item, CancellationToken cancellationToken = default);

    Task UpsertManyAsync(IReadOnlyCollection<ProjectHistoryItemDto> items, CancellationToken cancellationToken = default);

    Task DeleteAsync(int projectId, CancellationToken cancellationToken = default);

    Task<List<ProjectHistoryItemDto>> ListAsync(int? take = null, CancellationToken cancellationToken = default);

    Task<ulong> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches the serialized design state to the project's point payload and refreshes the point's
    /// vector from <see cref="DesignStateDocument.ToEmbeddingText"/>. No-op when the point does not exist yet.
    /// </summary>
    Task UpsertDesignStateAsync(int projectId, string designStateJson, CancellationToken cancellationToken = default);

    /// <summary>Reads the serialized design state from the project's point payload. Null when absent.</summary>
    Task<string?> GetDesignStateAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Semantic similarity search over indexed historical designs.
    /// Returns an empty list when embeddings or Qdrant are unavailable.
    /// </summary>
    Task<IReadOnlyList<SimilarDesignHit>> SearchSimilarAsync(
        string queryText,
        int topK,
        double minScore,
        int? excludeProjectId = null,
        CancellationToken cancellationToken = default);
}

public sealed class QdrantProjectHistoryIndex : IProjectHistoryIndex, IDisposable
{
    private const string HistoryCollectionName = "cad_project_history_v2";
    private const uint ScrollPageSize = 256;
    private const int UpsertBatchSize = 128;
    private const int EmbeddingChunkSize = 50;

    private const string FieldProjectId = "project_id";
    private const string FieldTitle = "title";
    private const string FieldOriginalFileName = "original_file_name";
    private const string FieldSourceFileType = "source_file_type";
    private const string FieldFileSizeBytes = "file_size_bytes";
    private const string FieldStatus = "status";
    private const string FieldErrorMessage = "error_message";
    private const string FieldComponentCount = "component_count";
    private const string FieldCreatedAt = "created_at";
    private const string FieldLastOpenedAt = "last_opened_at";
    private const string FieldDesignState = "design_state";
    private const string FieldEmbeddingText = "embedding_text";
    private const string FieldVectorReady = "vector_ready";

    private readonly QdrantOptions _options;
    private readonly IEmbeddingService? _embeddings;
    private readonly ILogger<QdrantProjectHistoryIndex> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QdrantClient? _client;
    private bool _collectionReady;
    private bool _disposed;

    public QdrantProjectHistoryIndex(
        IOptions<QdrantOptions> options,
        ILogger<QdrantProjectHistoryIndex> logger,
        IEmbeddingService? embeddings = null)
    {
        _options = options.Value;
        _logger = logger;
        _embeddings = embeddings;
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.Host)
        && !_options.Host.StartsWith("YOUR-", StringComparison.OrdinalIgnoreCase);

    private bool EmbeddingsAvailable => _embeddings is { IsConfigured: true };

    private int VectorSize =>
        _embeddings is { Dimensions: > 0 } ? _embeddings.Dimensions : Math.Max(1, _options.VectorSize);

    public async Task UpsertAsync(ProjectHistoryItemDto item, CancellationToken cancellationToken = default)
    {
        await UpsertManyAsync(new[] { item }, cancellationToken);
    }

    public async Task UpsertManyAsync(
        IReadOnlyCollection<ProjectHistoryItemDto> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        await EnsureCollectionAsync(cancellationToken);

        var client = GetClient();

        foreach (var chunk in items.Chunk(UpsertBatchSize))
        {
            var existingStates = await ReadExistingDesignStatesAsync(chunk, cancellationToken);

            var texts = new List<string?>(chunk.Length);
            var jsons = new List<string?>(chunk.Length);

            foreach (var item in chunk)
            {
                existingStates.TryGetValue(item.Id, out var json);
                jsons.Add(json);
                texts.Add(BuildEmbeddingText(json));
            }

            var vectors = await EmbedManySafeAsync(texts, cancellationToken);

            var batch = new List<PointStruct>(chunk.Length);

            for (var i = 0; i < chunk.Length; i++)
            {
                batch.Add(ToPoint(chunk[i], vectors[i], texts[i], jsons[i]));
            }

            await client.UpsertAsync(HistoryCollectionName, batch, cancellationToken: cancellationToken);
        }
    }

    public async Task DeleteAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        await GetClient().DeleteAsync(
            HistoryCollectionName,
            ToPointId(projectId),
            cancellationToken: cancellationToken);
    }

    public async Task<List<ProjectHistoryItemDto>> ListAsync(
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        var client = GetClient();
        var items = new List<ProjectHistoryItemDto>();
        PointId? offset = null;

        do
        {
            var page = await client.ScrollAsync(
                HistoryCollectionName,
                limit: ScrollPageSize,
                offset: offset,
                payloadSelector: new WithPayloadSelector
                {
                    Exclude = new PayloadExcludeSelector { Fields = { FieldDesignState, FieldEmbeddingText } }
                },
                vectorsSelector: false,
                cancellationToken: cancellationToken);

            foreach (var point in page.Result)
            {
                var item = ToItem(point);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            offset = page.NextPageOffset;
        }
        while (offset is not null);

        var ordered = items
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id);

        return take.HasValue && take.Value > 0
            ? ordered.Take(take.Value).ToList()
            : ordered.ToList();
    }

    public async Task UpsertDesignStateAsync(int projectId, string designStateJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(designStateJson))
        {
            return;
        }

        await EnsureCollectionAsync(cancellationToken);

        var client = GetClient();
        var pointId = ToPointId(projectId);
        var embeddingText = BuildEmbeddingText(designStateJson);

        float[]? vector = null;

        if (embeddingText is not null && EmbeddingsAvailable)
        {
            try
            {
                vector = await _embeddings!.EmbedAsync(embeddingText, EmbeddingTaskType.RetrievalDocument, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Embedding failed for project {ProjectId}. Vector was not refreshed.", projectId);
            }
        }

        var payload = new Dictionary<string, Value>
        {
            [FieldDesignState] = designStateJson,
            [FieldEmbeddingText] = embeddingText ?? string.Empty,
            [FieldVectorReady] = vector is not null && vector.Length == VectorSize
        };

        try
        {
            await client.SetPayloadAsync(
                HistoryCollectionName,
                payload,
                pointId,
                wait: true,
                cancellationToken: cancellationToken);

            if (vector is not null && vector.Length == VectorSize)
            {
                await client.UpdateVectorsAsync(
                    HistoryCollectionName,
                    new List<PointVectors>
                    {
                        new() { Id = pointId, Vectors = vector }
                    },
                    wait: true,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
        {
            // The history point is created by UpsertAsync; the design state is mirrored again right after it.
        }
    }

    public async Task<string?> GetDesignStateAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        var points = await GetClient().RetrieveAsync(
            HistoryCollectionName,
            ToPointId(projectId),
            withPayload: true,
            withVectors: false,
            cancellationToken: cancellationToken);

        var point = points.FirstOrDefault();

        if (point is null || !point.Payload.TryGetValue(FieldDesignState, out var value))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value.StringValue) ? null : value.StringValue;
    }

    public async Task<IReadOnlyList<SimilarDesignHit>> SearchSimilarAsync(
        string queryText,
        int topK,
        double minScore,
        int? excludeProjectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queryText) || !IsConfigured || !EmbeddingsAvailable)
        {
            return Array.Empty<SimilarDesignHit>();
        }

        try
        {
            await EnsureCollectionAsync(cancellationToken);

            var queryVector = await _embeddings!.EmbedAsync(queryText, EmbeddingTaskType.RetrievalQuery, cancellationToken);

            if (queryVector.Length != VectorSize)
            {
                return Array.Empty<SimilarDesignHit>();
            }

            var limit = Math.Max(1, topK) + (excludeProjectId.HasValue ? 1 : 0);

            var results = await GetClient().SearchAsync(
                HistoryCollectionName,
                queryVector,
                filter: Conditions.Match(FieldVectorReady, true),
                limit: (ulong)limit,
                payloadSelector: true,
                scoreThreshold: (float)minScore,
                cancellationToken: cancellationToken);

            var hits = new List<SimilarDesignHit>(results.Count);

            foreach (var r in results)
            {
                if (!r.Payload.TryGetValue(FieldProjectId, out var idValue))
                {
                    continue;
                }

                var projectId = (int)idValue.IntegerValue;

                if (excludeProjectId.HasValue && projectId == excludeProjectId.Value)
                {
                    continue;
                }

                var json = ReadString(r.Payload, FieldDesignState);

                hits.Add(new SimilarDesignHit
                {
                    ProjectId = projectId,
                    Title = ReadString(r.Payload, FieldTitle),
                    OriginalFileName = ReadString(r.Payload, FieldOriginalFileName),
                    Score = r.Score,
                    EmbeddingText = ReadString(r.Payload, FieldEmbeddingText),
                    DesignStateJson = string.IsNullOrWhiteSpace(json) ? null : json
                });

                if (hits.Count >= topK)
                {
                    break;
                }
            }

            return hits;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Historical design similarity search failed. Continuing without RAG history context.");
            return Array.Empty<SimilarDesignHit>();
        }
    }

    public async Task<ulong> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);
        return await GetClient().CountAsync(HistoryCollectionName, cancellationToken: cancellationToken);
    }

    private async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        if (_collectionReady)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_collectionReady)
            {
                return;
            }

            var client = GetClient();
            var exists = await client.CollectionExistsAsync(HistoryCollectionName, cancellationToken);

            if (!exists)
            {
                await client.CreateCollectionAsync(
                    HistoryCollectionName,
                    new VectorParams
                    {
                        Size = (ulong)VectorSize,
                        Distance = Distance.Cosine
                    },
                    cancellationToken: cancellationToken);

                await client.CreatePayloadIndexAsync(
                    HistoryCollectionName,
                    FieldVectorReady,
                    PayloadSchemaType.Bool,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Created Qdrant collection {Collection}.", HistoryCollectionName);
            }

            _collectionReady = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<int, string?>> ReadExistingDesignStatesAsync(
        IReadOnlyCollection<ProjectHistoryItemDto> items,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string?>();

        try
        {
            IReadOnlyList<PointId> ids = items.Select(i => (PointId)ToPointId(i.Id)).ToList();

            var points = await GetClient().RetrieveAsync(
                HistoryCollectionName,
                ids,
                withPayload: true,
                withVectors: false,
                cancellationToken: cancellationToken);

            foreach (var point in points)
            {
                if (!point.Payload.TryGetValue(FieldProjectId, out var idValue))
                {
                    continue;
                }

                var json = ReadString(point.Payload, FieldDesignState);
                result[(int)idValue.IntegerValue] = string.IsNullOrWhiteSpace(json) ? null : json;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read existing design states before upsert.");
        }

        return result;
    }

    private static string? BuildEmbeddingText(string? designStateJson)
    {
        var document = DesignStateDocument.FromJson(designStateJson);

        if (document is null)
        {
            return null;
        }

        var text = document.ToEmbeddingText();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Embeds the given texts. Entries that are null/empty, or whose embedding fails, get a null result
    /// so the point is still written (history listing keeps working) but is excluded from similarity search.
    /// </summary>
    private async Task<float[]?[]> EmbedManySafeAsync(
        IReadOnlyList<string?> texts,
        CancellationToken cancellationToken)
    {
        var vectors = new float[]?[texts.Count];

        if (!EmbeddingsAvailable)
        {
            return vectors;
        }

        var indexes = new List<int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(texts[i]))
            {
                indexes.Add(i);
            }
        }

        foreach (var chunk in indexes.Chunk(EmbeddingChunkSize))
        {
            try
            {
                var batchTexts = chunk.Select(i => texts[i]!).ToList();

                var embedded = await _embeddings!.EmbedBatchAsync(
                    batchTexts,
                    EmbeddingTaskType.RetrievalDocument,
                    cancellationToken);

                for (var j = 0; j < chunk.Length && j < embedded.Count; j++)
                {
                    if (embedded[j].Length == VectorSize)
                    {
                        vectors[chunk[j]] = embedded[j];
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Embedding batch failed. Affected points are indexed without a searchable vector.");
            }
        }

        return vectors;
    }

    private PointStruct ToPoint(ProjectHistoryItemDto item, float[]? embedding, string? embeddingText, string? designStateJson)
    {
        var ready = embedding is not null;

        var point = new PointStruct
        {
            Id = ToPointId(item.Id),
            Vectors = embedding ?? new float[VectorSize]
        };

        point.Payload[FieldProjectId] = item.Id;
        point.Payload[FieldTitle] = item.Title ?? string.Empty;
        point.Payload[FieldOriginalFileName] = item.OriginalFileName ?? string.Empty;
        point.Payload[FieldSourceFileType] = item.SourceFileType.ToString();
        point.Payload[FieldFileSizeBytes] = item.FileSizeBytes;
        point.Payload[FieldStatus] = item.Status.ToString();
        point.Payload[FieldErrorMessage] = item.ErrorMessage ?? string.Empty;
        point.Payload[FieldComponentCount] = item.ComponentCount;
        point.Payload[FieldCreatedAt] = FormatDate(item.CreatedAt);
        point.Payload[FieldLastOpenedAt] = item.LastOpenedAt.HasValue ? FormatDate(item.LastOpenedAt.Value) : string.Empty;
        point.Payload[FieldVectorReady] = ready;

        if (!string.IsNullOrWhiteSpace(designStateJson))
        {
            point.Payload[FieldDesignState] = designStateJson;
        }

        if (!string.IsNullOrWhiteSpace(embeddingText))
        {
            point.Payload[FieldEmbeddingText] = embeddingText;
        }

        return point;
    }

    private ProjectHistoryItemDto? ToItem(RetrievedPoint point)
    {
        try
        {
            var payload = point.Payload;

            if (!payload.TryGetValue(FieldProjectId, out var idValue))
            {
                return null;
            }

            var error = ReadString(payload, FieldErrorMessage);
            var lastOpened = ReadString(payload, FieldLastOpenedAt);

            return new ProjectHistoryItemDto
            {
                Id = (int)idValue.IntegerValue,
                Title = ReadString(payload, FieldTitle),
                OriginalFileName = ReadString(payload, FieldOriginalFileName),
                SourceFileType = Enum.Parse<SourceFileType>(ReadString(payload, FieldSourceFileType), true),
                FileSizeBytes = ReadLong(payload, FieldFileSizeBytes),
                Status = Enum.Parse<ProjectStatus>(ReadString(payload, FieldStatus), true),
                ErrorMessage = string.IsNullOrEmpty(error) ? null : error,
                ComponentCount = (int)ReadLong(payload, FieldComponentCount),
                CreatedAt = ParseDate(ReadString(payload, FieldCreatedAt)),
                LastOpenedAt = string.IsNullOrEmpty(lastOpened) ? null : ParseDate(lastOpened)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            _logger.LogWarning(ex, "Skipped a malformed project history point.");
            return null;
        }
    }

    private static Guid ToPointId(int projectId)
    {
        return new Guid(projectId, 0, 0, new byte[] { 0x43, 0x41, 0x44, 0x48, 0x49, 0x53, 0x54, 0x31 });
    }

    private static string FormatDate(DateTime value)
    {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseDate(string value)
    {
        return DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private static string ReadString(IDictionary<string, Value> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;
    }

    private static long ReadLong(IDictionary<string, Value> payload, string key)
    {
        return payload.TryGetValue(key, out var value) ? value.IntegerValue : 0;
    }

    private QdrantClient GetClient()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Qdrant is not configured. Set QdrantOptions:Host and QdrantOptions:ApiKey.");
        }

        return _client ??= new QdrantClient(
            _options.Host,
            _options.Port,
            _options.UseHttps,
            string.IsNullOrWhiteSpace(_options.ApiKey) ? null : _options.ApiKey,
            TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _gate.Dispose();
    }
}