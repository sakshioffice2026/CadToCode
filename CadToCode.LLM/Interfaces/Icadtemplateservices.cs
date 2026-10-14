using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ICadTemplateIndexer
{
    bool IsAvailable { get; }

    Task EnsureIndexedAsync(CancellationToken cancellationToken = default);

    Task<CadTemplateIndexResult> IndexBuiltInAsync(
        bool force,
        CancellationToken cancellationToken = default);
}

public interface ICadTemplateMatcher
{
    Task<CadTemplateMatch?> MatchAsync(
        ProjectResultDto? project,
        AssemblyTree? tree,
        string? prompt,
        string? drawingType,
        IReadOnlyList<CadAssistAttachmentDto>? attachments,
        CancellationToken cancellationToken = default);
}