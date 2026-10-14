using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface IVectorStoreService
{
    bool IsConfigured { get; }

    Task EnsureCollectionAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(
        VectorUpsertRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid pointId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        float[] queryVector,
        int topK,
        double minScore,
        string? fileTypeFilter = null,
        CancellationToken cancellationToken = default);

    Task<ulong> CountAsync(CancellationToken cancellationToken = default);
}