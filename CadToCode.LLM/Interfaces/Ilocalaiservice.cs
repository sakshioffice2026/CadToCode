using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface ILocalAiService
{
    bool IsAvailable { get; }

    string ModelName { get; }

    CadAssistStatusDto GetStatus();

    string EnsureSession(int projectId, string? sessionId);

    IReadOnlyList<CadAssistMessageDto> GetHistory(int projectId, string sessionId);

    void ResetSession(int projectId, string sessionId);

    Task<LocalAiGenerationResult> ChatAsync(
        LocalAiGenerationRequest request,
        CancellationToken cancellationToken = default);
}