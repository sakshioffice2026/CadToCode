namespace CadToCode.LLM.Interfaces;

public enum EmbeddingTaskType
{
    RetrievalDocument,
    RetrievalQuery
}

public interface IEmbeddingService
{
    bool IsConfigured { get; }

    int Dimensions { get; }

    Task<float[]> EmbedAsync(
        string text,
        EmbeddingTaskType taskType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        EmbeddingTaskType taskType,
        CancellationToken cancellationToken = default);
}