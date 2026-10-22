using CadToCode.Core.Domain;
using CadToCode.LLM.Interfaces;

namespace CadToCode.LLM.Services;

public interface IEquipmentDesignDispatcher
{
    bool TryHandle(
        int projectId,
        string sessionId,
        string? prompt,
        EquipmentType requestedEquipment,
        out EquipmentDesignTurn turn);

    bool IsActive(int projectId, string sessionId);

    void Reset(int projectId, string sessionId);
}