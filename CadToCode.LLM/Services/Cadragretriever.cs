// Repo path: CadToCode.LLM/Services/Cadragretriever.cs
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class CadRagRetriever : ICadRagRetriever
{
    private const int MaxQueryChars = 4000;
    private const int MaxTopK = 20;

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStoreService _vectors;
    private readonly QdrantOptions _options;
    private readonly ILogger<CadRagRetriever> _logger;

    public CadRagRetriever(
        IEmbeddingService embeddings,
        IVectorStoreService vectors,
        IOptions<QdrantOptions> options,
        ILogger<CadRagRetriever> logger)
    {
        _embeddings = embeddings;
        _vectors = vectors;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _embeddings.IsConfigured && _vectors.IsConfigured;

    /// <summary>
    /// Returns library template hits ranked by score. An empty list means the search ran and found
    /// nothing. A failed search is never reported as "no results": the exception is logged and rethrown
    /// so the caller decides, and records, how to degrade.
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchHit>> RetrieveAsync(
        string query,
        int? topK = null,
        string? fileTypeFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<VectorSearchHit>();
        }

        var limit = Math.Clamp(topK ?? _options.TopK, 1, MaxTopK);

        try
        {
            var text = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;

            var vector = await _embeddings.EmbedAsync(text, EmbeddingTaskType.RetrievalQuery, cancellationToken);

            if (vector is null || vector.Length == 0)
            {
                throw new InvalidOperationException("The embedding service returned an empty vector.");
            }

            if (_embeddings.Dimensions > 0 && vector.Length != _embeddings.Dimensions)
            {
                throw new InvalidOperationException(
                    "The embedding vector has " + vector.Length + " dimensions but " + _embeddings.Dimensions + " were expected.");
            }

            var hits = await _vectors.SearchAsync(
                vector,
                limit,
                _options.MinScore,
                fileTypeFilter,
                cancellationToken);

            return hits
                .Where(h => h is not null && double.IsFinite(h.Score))
                .GroupBy(HitKey, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(h => h.Score).First())
                .OrderByDescending(h => h.Score)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "RAG retrieval failed ({ExceptionType}): {Reason}. The caller handles the failure.",
                ex.GetType().Name,
                ex.Message);
            throw;
        }
    }

    private static string HitKey(VectorSearchHit hit)
    {
        return hit.PointId != Guid.Empty
            ? hit.PointId.ToString("N")
            : hit.LibraryItemId + ":" + hit.Title;
    }
}