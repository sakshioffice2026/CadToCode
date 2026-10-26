// Repo path: CadToCode.LLM/Services/Cadragretriever.cs
using System.Globalization;
using System.Text;
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
    private const int DefaultHistoryTopK = 3;
    private const int MaxHistoryTopK = 5;
    private const int MaxBlockChars = 2200;
    private const int MaxContextChars = 7000;
    private const int MaxListedItems = 24;

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStoreService _vectors;
    private readonly IProjectHistoryIndex _history;
    private readonly QdrantOptions _options;
    private readonly ILogger<CadRagRetriever> _logger;

    public CadRagRetriever(
        IEmbeddingService embeddings,
        IVectorStoreService vectors,
        IProjectHistoryIndex history,
        IOptions<QdrantOptions> options,
        ILogger<CadRagRetriever> logger)
    {
        _embeddings = embeddings;
        _vectors = vectors;
        _history = history;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _embeddings.IsConfigured && _vectors.IsConfigured;

    public bool IsHistoryAvailable => _embeddings.IsConfigured && _history.IsConfigured;

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

    /// <summary>
    /// Semantic search over previously validated designs. Enhancement only: any failure is logged and
    /// an empty list is returned, so the existing pipeline is never interrupted.
    /// </summary>
    public async Task<IReadOnlyList<SimilarDesignHit>> RetrieveSimilarDesignsAsync(
        string query,
        int? excludeProjectId = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsHistoryAvailable || string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SimilarDesignHit>();
        }

        var limit = Math.Clamp(topK ?? DefaultHistoryTopK, 1, MaxHistoryTopK);
        var text = query.Length > MaxQueryChars ? query[..MaxQueryChars] : query;

        try
        {
            var hits = await _history.SearchSimilarAsync(
                text,
                limit,
                _options.MinScore,
                excludeProjectId,
                cancellationToken);

            return hits
                .Where(h => h is not null && double.IsFinite(h.Score))
                .OrderByDescending(h => h.Score)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Historical design retrieval failed ({ExceptionType}): {Reason}. Continuing without history context.",
                ex.GetType().Name,
                ex.Message);
            return Array.Empty<SimilarDesignHit>();
        }
    }

    /// <summary>
    /// Retrieves similar historical designs and formats them as Markdown few-shot reference blocks
    /// ready to append to a system prompt. Returns an empty string when there is nothing to inject.
    /// </summary>
    public async Task<string> BuildHistoryContextAsync(
        string query,
        int? excludeProjectId = null,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        var hits = await RetrieveSimilarDesignsAsync(query, excludeProjectId, topK, cancellationToken);
        return FormatHistoryContext(hits);
    }

    public string FormatHistoryContext(IReadOnlyList<SimilarDesignHit> hits)
    {
        if (hits is null || hits.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Reference Designs From Project History");
        sb.AppendLine();
        sb.AppendLine("Previously validated designs similar to the current request. Use them only as reference examples for structure, naming and typical proportions. Do not copy values blindly; the current inputs and calculation results always take precedence.");
        sb.AppendLine();

        var index = 0;

        foreach (var hit in hits)
        {
            index++;
            var block = FormatBlock(hit, index);

            if (sb.Length + block.Length > MaxContextChars)
            {
                break;
            }

            sb.Append(block);
        }

        return index == 0 ? string.Empty : sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string FormatBlock(SimilarDesignHit hit, int index)
    {
        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(hit.Title) ? "Project " + hit.ProjectId : hit.Title.Trim();

        sb.Append("### Reference ").Append(index).Append(": ").Append(title)
          .Append(" (similarity ").Append(hit.Score.ToString("0.00", CultureInfo.InvariantCulture)).AppendLine(")");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(hit.EmbeddingText))
        {
            sb.AppendLine("**Summary:** " + hit.EmbeddingText.Trim());
            sb.AppendLine();
        }

        var document = DesignStateDocument.FromJson(hit.DesignStateJson);

        if (document is not null)
        {
            if (document.Inputs.Count > 0)
            {
                sb.AppendLine("**Inputs (SI):**");
                foreach (var input in document.Inputs.Take(MaxListedItems))
                {
                    sb.Append("- ").Append(input.Key).Append(": ").AppendLine(Format(input.Value));
                }

                sb.AppendLine();
            }

            if (document.Calculation.Values.Count > 0)
            {
                sb.AppendLine("**Calculated values:**");
                foreach (var v in document.Calculation.Values.Take(MaxListedItems))
                {
                    var unit = string.IsNullOrWhiteSpace(v.SiUnit) ? string.Empty : " " + v.SiUnit;
                    sb.Append("- ").Append(v.Key).Append(": ").Append(Format(v.Value)).AppendLine(unit);
                }

                sb.AppendLine();
            }

            if (document.PartCounts.Count > 0)
            {
                sb.AppendLine("**Part counts:**");
                foreach (var part in document.PartCounts.Take(MaxListedItems))
                {
                    sb.Append("- ").Append(part.Key).Append(": ").AppendLine(part.Value.ToString(CultureInfo.InvariantCulture));
                }

                sb.AppendLine();
            }

            if (document.Components.Count > 0)
            {
                sb.Append("**Components:** ")
                  .AppendLine(string.Join(", ", document.Components
                      .OrderBy(c => c.SequenceIndex)
                      .Take(MaxListedItems)
                      .Select(c => c.Count > 1 ? c.Name + " x" + c.Count : c.Name)));
                sb.AppendLine();
            }
        }

        var text = sb.ToString();
        return text.Length > MaxBlockChars ? text[..MaxBlockChars].TrimEnd() + Environment.NewLine + Environment.NewLine : text;
    }

    private static string Format(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string HitKey(VectorSearchHit hit)
    {
        return hit.PointId != Guid.Empty
            ? hit.PointId.ToString("N")
            : hit.LibraryItemId + ":" + hit.Title;
    }
}