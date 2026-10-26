using System.Globalization;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace CadToCode.LLM.Services;

public interface IProjectHistoryIndex
{
    bool IsConfigured { get; }

    Task UpsertAsync(ProjectHistoryItemDto item, CancellationToken cancellationToken = default);

    Task UpsertManyAsync(IReadOnlyCollection<ProjectHistoryItemDto> items, CancellationToken cancellationToken = default);

    Task DeleteAsync(int projectId, CancellationToken cancellationToken = default);

    Task<List<ProjectHistoryItemDto>> ListAsync(int? take = null, CancellationToken cancellationToken = default);

    Task<ulong> CountAsync(CancellationToken cancellationToken = default);
}

public sealed class QdrantProjectHistoryIndex : IProjectHistoryIndex, IDisposable
{
    private const string HistoryCollectionName = "cad_project_history";
    private const int PlaceholderVectorSize = 1;
    private const uint ScrollPageSize = 256;
    private const int UpsertBatchSize = 128;

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

    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantProjectHistoryIndex> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QdrantClient? _client;
    private bool _collectionReady;
    private bool _disposed;

    public QdrantProjectHistoryIndex(
        IOptions<QdrantOptions> options,
        ILogger<QdrantProjectHistoryIndex> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.Host)
        && !_options.Host.StartsWith("YOUR-", StringComparison.OrdinalIgnoreCase);

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
        var batch = new List<PointStruct>(UpsertBatchSize);

        foreach (var item in items)
        {
            batch.Add(ToPoint(item));

            if (batch.Count >= UpsertBatchSize)
            {
                await client.UpsertAsync(HistoryCollectionName, batch, cancellationToken: cancellationToken);
                batch = new List<PointStruct>(UpsertBatchSize);
            }
        }

        if (batch.Count > 0)
        {
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
                payloadSelector: true,
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
                        Size = PlaceholderVectorSize,
                        Distance = Distance.Cosine
                    },
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

    private static PointStruct ToPoint(ProjectHistoryItemDto item)
    {
        var point = new PointStruct
        {
            Id = ToPointId(item.Id),
            Vectors = new float[] { 1f }
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