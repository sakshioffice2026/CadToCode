using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ICadRagRetriever
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<VectorSearchHit>> RetrieveAsync(
        string query,
        int? topK = null,
        string? fileTypeFilter = null,
        CancellationToken cancellationToken = default);
}