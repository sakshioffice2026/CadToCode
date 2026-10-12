using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ILLMGenerationService
{
    bool IsAvailable { get; }

    Task<ComponentClassificationResult> ClassifyAsync(
        ComponentClassificationRequest request,
        CancellationToken cancellationToken = default);
}