using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Interfaces;

public interface IChatMemoryStore
{
    string EnsureSession(int projectId, string? sessionId);

    void Append(int projectId, string sessionId, CadAssistMessageDto message);

    IReadOnlyList<CadAssistMessageDto> GetMessages(int projectId, string sessionId);

    void Reset(int projectId, string sessionId);
}