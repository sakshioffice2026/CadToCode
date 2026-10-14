using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace CadToCode.LLM.Services;

public sealed class QdrantVectorStoreService : IVectorStoreService, IDisposable
{
    private const string FieldLibraryItemId = "library_item_id";
    private const string FieldTitle = "title";
    private const string FieldCategory = "category";
    private const string FieldFileType = "file_type";
    private const string FieldTags = "tags";
    private const string FieldCodeSnippet = "code_snippet";
    private const string FieldMetadata = "metadata_summary";

    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStoreService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QdrantClient? _client;
    private bool _collectionReady;
    private bool _disposed;

    public QdrantVectorStoreService(
        IOptions<QdrantOptions> options,
        ILogger<QdrantVectorStoreService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.Host)
        && !_options.Host.StartsWith("YOUR-", StringComparison.OrdinalIgnoreCase);

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken = default)
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
            var exists = await client.CollectionExistsAsync(_options.CollectionName, cancellationToken);

            if (!exists)
            {
                if (!_options.AutoCreateCollection)
                {
                    throw new InvalidOperationException(
                        "Qdrant collection '" + _options.CollectionName + "' does not exist and AutoCreateCollection is disabled.");
                }

                await client.CreateCollectionAsync(
                    _options.CollectionName,
                    new VectorParams
                    {
                        Size = (ulong)_options.VectorSize,
                        Distance = Distance.Cosine
                    },
                    cancellationToken: cancellationToken);

                _logger.LogInformation(
                    "Created Qdrant collection {Collection} (size {Size}, Cosine).",
                    _options.CollectionName, _options.VectorSize);
            }

            await EnsurePayloadIndexesAsync(client, cancellationToken);

            _collectionReady = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsurePayloadIndexesAsync(QdrantClient client, CancellationToken cancellationToken)
    {
        foreach (var field in new[] { FieldFileType, FieldCategory })
        {
            try
            {
                await client.CreatePayloadIndexAsync(
                    _options.CollectionName,
                    field,
                    schemaType: PayloadSchemaType.Keyword,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not create the Qdrant payload index for {Field}.", field);
            }
        }
    }

    public async Task UpsertAsync(
        VectorUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Vector.Length != _options.VectorSize)
        {
            throw new InvalidOperationException(
                "Vector has " + request.Vector.Length + " dimensions but collection expects " + _options.VectorSize + ".");
        }

        await EnsureCollectionAsync(cancellationToken);

        var point = new PointStruct
        {
            Id = request.PointId,
            Vectors = request.Vector
        };

        point.Payload[FieldLibraryItemId] = request.LibraryItemId;
        point.Payload[FieldTitle] = request.Title ?? string.Empty;
        point.Payload[FieldCategory] = request.Category ?? string.Empty;
        point.Payload[FieldFileType] = request.FileType ?? string.Empty;
        point.Payload[FieldTags] = request.Tags ?? string.Empty;
        point.Payload[FieldCodeSnippet] = request.CodeSnippet ?? string.Empty;
        point.Payload[FieldMetadata] = request.MetadataSummary ?? string.Empty;

        await GetClient().UpsertAsync(
            _options.CollectionName,
            new List<PointStruct> { point },
            cancellationToken: cancellationToken);
    }

    public async Task DeleteAsync(
        Guid pointId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        await GetClient().DeleteAsync(
            _options.CollectionName,
            pointId,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        float[] queryVector,
        int topK,
        double minScore,
        string? fileTypeFilter = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);

        Filter? filter = null;
        if (!string.IsNullOrWhiteSpace(fileTypeFilter))
        {
            filter = MatchKeyword(FieldFileType, fileTypeFilter);
        }

        var results = await GetClient().SearchAsync(
            _options.CollectionName,
            queryVector,
            filter: filter,
            limit: (ulong)Math.Max(1, topK),
            payloadSelector: true,
            scoreThreshold: (float)minScore,
            cancellationToken: cancellationToken);

        var hits = new List<VectorSearchHit>(results.Count);

        foreach (var r in results)
        {
            hits.Add(new VectorSearchHit
            {
                PointId = Guid.TryParse(r.Id?.Uuid, out var id) ? id : Guid.Empty,
                LibraryItemId = (int)ReadInt(r, FieldLibraryItemId),
                Score = r.Score,
                Title = ReadString(r, FieldTitle),
                Category = ReadString(r, FieldCategory),
                FileType = ReadString(r, FieldFileType),
                CodeSnippet = NullIfEmpty(ReadString(r, FieldCodeSnippet)),
                MetadataSummary = NullIfEmpty(ReadString(r, FieldMetadata))
            });
        }

        return hits;
    }

    public async Task<ulong> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCollectionAsync(cancellationToken);
        return await GetClient().CountAsync(_options.CollectionName, cancellationToken: cancellationToken);
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

    private static string ReadString(ScoredPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var value) ? value.StringValue : string.Empty;
    }

    private static long ReadInt(ScoredPoint point, string key)
    {
        return point.Payload.TryGetValue(key, out var value) ? value.IntegerValue : 0;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
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