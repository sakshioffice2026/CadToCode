using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ILLMGenerationService
{
    bool IsAvailable { get; }

    Task<ComponentClassificationResult> ClassifyAsync(
        ComponentClassificationRequest request,
        CancellationToken cancellationToken = default);

    Task<AssemblyAnalysisResult> AnalyzeAssemblyAsync(
        AssemblyAnalysisRequest request,
        CancellationToken cancellationToken = default);

    Task<CodeSynthesisResult> SynthesizeCodeAsync(
        CodeSynthesisRequest request,
        CancellationToken cancellationToken = default);
}