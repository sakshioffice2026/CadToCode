using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class CadRagRetriever : ICadRagRetriever
{
    private const int MaxQueryChars = 4000;

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

        try
        {
            var text = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;

            var vector = await _embeddings.EmbedAsync(text, EmbeddingTaskType.RetrievalQuery, cancellationToken);

            var hits = await _vectors.SearchAsync(
                vector,
                topK ?? _options.TopK,
                _options.MinScore,
                fileTypeFilter,
                cancellationToken);

            return hits
                .OrderByDescending(h => h.Score)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RAG retrieval failed; continuing without library templates.");
            return Array.Empty<VectorSearchHit>();
        }
    }
}