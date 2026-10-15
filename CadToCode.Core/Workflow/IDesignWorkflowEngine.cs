using CadToCode.Core.Domain;

namespace CadToCode.Core.Workflow;

/// <summary>
/// Drives a <see cref="DesignSession"/> through:
/// Requirements -> Formula Validation -> Design Review -> Script Generation -> STEP Export.
/// All transitions are deterministic; no LLM involvement.
/// </summary>
public interface IDesignWorkflowEngine
{
    Task<DesignSession> StartAsync(
        string userId,
        EquipmentType equipmentType,
        CancellationToken cancellationToken = default);

    Task<DesignSession> SubmitRequirementsAsync(
        Guid sessionId,
        IReadOnlyDictionary<string, double> inputsSi,
        CancellationToken cancellationToken = default);

    Task<DesignSession> ValidateFormulasAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<DesignSession> ApproveDesignReviewAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<DesignSession> RejectDesignReviewAsync(
        Guid sessionId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<DesignSession> MarkScriptGeneratedAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<DesignSession> MarkStepExportedAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    bool CanTransition(DesignWorkflowState from, DesignWorkflowState to);

    IReadOnlyList<DesignWorkflowState> GetAllowedTransitions(DesignWorkflowState from);
}
