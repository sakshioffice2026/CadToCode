using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Services;

namespace CadToCode.Core.Workflow;

public sealed class DesignWorkflowEngine : IDesignWorkflowEngine
{
    private static readonly IReadOnlyDictionary<DesignWorkflowState, DesignWorkflowState[]> Transitions =
        new Dictionary<DesignWorkflowState, DesignWorkflowState[]>
        {
            [DesignWorkflowState.Requirements] = new[] { DesignWorkflowState.FormulaValidation },
            [DesignWorkflowState.FormulaValidation] = new[] { DesignWorkflowState.DesignReview, DesignWorkflowState.Requirements },
            [DesignWorkflowState.DesignReview] = new[] { DesignWorkflowState.ScriptGeneration, DesignWorkflowState.Requirements },
            [DesignWorkflowState.ScriptGeneration] = new[] { DesignWorkflowState.StepExport },
            [DesignWorkflowState.StepExport] = Array.Empty<DesignWorkflowState>()
        };

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

        var session = new DesignSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EquipmentType = equipmentType,
            State = DesignWorkflowState.Requirements,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        return await _store.CreateAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesignSession> SubmitRequirementsAsync(
        Guid sessionId,
        IReadOnlyDictionary<string, double> inputsSi,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputsSi);

        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.Requirements);

        session.Inputs = new Dictionary<string, double>(inputsSi);
        session.RejectionReason = null;

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
        var result = calculator.Calculate(session.Inputs);

        session.Result = result;

        var next = result.IsValid
            ? DesignWorkflowState.DesignReview
            : DesignWorkflowState.Requirements;

        return await TransitionAsync(session, next, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesignSession> ApproveDesignReviewAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureState(session, DesignWorkflowState.DesignReview);

        session.RejectionReason = null;

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

        session.RejectionReason = reason;

        return await TransitionAsync(session, DesignWorkflowState.Requirements, cancellationToken)
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

        session.UpdatedUtc = DateTime.UtcNow;
        await _store.UpdateAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public bool CanTransition(DesignWorkflowState from, DesignWorkflowState to) =>
        Transitions.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0;

    public IReadOnlyList<DesignWorkflowState> GetAllowedTransitions(DesignWorkflowState from) =>
        Transitions.TryGetValue(from, out var allowed) ? allowed : Array.Empty<DesignWorkflowState>();

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

        session.State = next;
        session.UpdatedUtc = DateTime.UtcNow;

        await _store.UpdateAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }
}
