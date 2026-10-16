using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Services;

namespace CadToCode.Core.Workflow;

public sealed class DesignWorkflowEngine : IDesignWorkflowEngine
{
    private readonly IDesignSessionStore _store;
    private readonly EquipmentCalculatorRegistry _calculators;

    public DesignWorkflowEngine(IDesignSessionStore store, EquipmentCalculatorRegistry calculators)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _calculators = calculators ?? throw new ArgumentNullException(nameof(calculators));
    }

    public async Task<DesignSession> StartAsync(
        string userId,
        EquipmentType equipmentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));

        var session = new DesignSession(userId, "Untitled design");
        session.SetEquipmentType(equipmentType);

        return await _store.CreateAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesignSession> SubmitRequirementsAsync(
        Guid sessionId,
        IReadOnlyDictionary<string, double> inputsSi,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputsSi);

        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.RequirementsGathering);

        var stale = session.Inputs.Keys
            .Where(k => !inputsSi.Keys.Any(n => string.Equals(n?.Trim(), k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var key in stale)
            session.RemoveInput(key);

        foreach (var kv in inputsSi)
            session.SetInput(kv.Key, kv.Value);

        return await TransitionAsync(session, DesignWorkflowState.FormulaValidation, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DesignSession> ValidateFormulasAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.FormulaValidation);

        var calculator = _calculators.Get(session.EquipmentType);
        var result = calculator.Calculate(new EquipmentInputs(session.Inputs));

        session.RecordCalculation(result, string.Empty, "Formula validation");

        var next = result.Succeeded
            ? DesignWorkflowState.DesignReview
            : DesignWorkflowState.RequirementsGathering;

        return await TransitionAsync(session, next, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesignSession> ApproveDesignReviewAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.DesignReview);

        if (!session.HasCadReadyCalculation)
            throw new InvalidOperationException("The latest calculation is not CAD ready and cannot be approved.");

        session.Approve(DesignWorkflowState.DesignReview, session.OwnerId);

        return await TransitionAsync(session, DesignWorkflowState.ScriptGeneration, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DesignSession> RejectDesignReviewAsync(
        Guid sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));

        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.DesignReview);

        return await TransitionAsync(session, DesignWorkflowState.RequirementsGathering, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DesignSession> MarkScriptGeneratedAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.ScriptGeneration);

        return await TransitionAsync(session, DesignWorkflowState.StepExport, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DesignSession> MarkStepExportedAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.StepExport);

        return await TransitionAsync(session, DesignWorkflowState.Completed, cancellationToken)
            .ConfigureAwait(false);
    }

    public bool CanTransition(DesignWorkflowState from, DesignWorkflowState to) =>
        DesignWorkflowStates.CanTransition(from, to);

    public IReadOnlyList<DesignWorkflowState> GetAllowedTransitions(DesignWorkflowState from) =>
        DesignWorkflowStates.AllowedNext(from);

    private async Task<DesignSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session ?? throw new KeyNotFoundException($"Design session '{sessionId}' was not found.");
    }

    private static void EnsureState(DesignSession session, DesignWorkflowState expected)
    {
        if (session.State != expected)
            throw new InvalidOperationException(
                $"Session '{session.Id}' is in state '{session.State}', expected '{expected}'.");
    }

    private async Task<DesignSession> TransitionAsync(
        DesignSession session,
        DesignWorkflowState next,
        CancellationToken cancellationToken)
    {
        if (!CanTransition(session.State, next))
            throw new InvalidOperationException(
                $"Transition '{session.State}' -> '{next}' is not allowed.");

        session.SetState(next);

        await _store.UpdateAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }
}