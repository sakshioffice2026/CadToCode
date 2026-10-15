using CadToCode.Core.Domain;

namespace CadToCode.Core.Knowledge;

/// <summary>
/// A single retrievable unit of local engineering knowledge.
/// </summary>
public sealed record KnowledgeChunk(
    string Id,
    EquipmentType EquipmentType,
    string Source,
    string Title,
    string Content,
    IReadOnlyList<string> Tags);

/// <summary>
/// A chunk returned from retrieval together with its relevance score.
/// </summary>
public sealed record KnowledgeSearchResult(
    KnowledgeChunk Chunk,
    double Score);

/// <summary>
/// Local-first engineering knowledge retrieval (RAG).
/// Results are reference material only; numeric design values come from
/// the deterministic calculators and design rules, never from retrieved text.
/// </summary>
public interface IEngineeringKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        string query,
        EquipmentType? equipmentType = null,
        int topK = 5,
        CancellationToken cancellationToken = default);

    Task<KnowledgeChunk?> GetChunkAsync(
        string chunkId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeChunk>> ListChunksAsync(
        EquipmentType? equipmentType = null,
        CancellationToken cancellationToken = default);

    Task ReloadAsync(CancellationToken cancellationToken = default);
}
