using CadToCode.LLM.Services;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ICadRagRetriever
{
    bool IsAvailable { get; }

    /// <summary>True when semantic search over historical designs can run.</summary>
    bool IsHistoryAvailable { get; }

    Task<IReadOnlyList<VectorSearchHit>> RetrieveAsync(
        string query,
        int? topK = null,
        string? fileTypeFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Semantic search over validated historical designs. Never throws; returns empty on failure.</summary>
    Task<IReadOnlyList<SimilarDesignHit>> RetrieveSimilarDesignsAsync(
        string query,
        int? excludeProjectId = null,
        int? topK = null,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves similar designs and returns Markdown few-shot reference blocks. Empty string when none.</summary>
    Task<string> BuildHistoryContextAsync(
        string query,
        int? excludeProjectId = null,
        int? topK = null,
        CancellationToken cancellationToken = default);

    /// <summary>Formats already retrieved designs as Markdown few-shot reference blocks.</summary>
    string FormatHistoryContext(IReadOnlyList<SimilarDesignHit> hits);
}