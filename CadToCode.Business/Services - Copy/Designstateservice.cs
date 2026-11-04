using CadToCode.Business.Interfaces;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public sealed record DesignStateUpdateResult(
    bool Success,
    DesignStateDocument? State,
    IReadOnlyList<string> Errors);

public interface IDesignStateService
{
    /// <summary>
    /// Copies equipment type, inputs and the latest calculation of an interactive design session
    /// into the stored design state of its linked project. No-op when the session has no source project.
    /// </summary>
    Task<DesignStateDocument?> CaptureFromSessionAsync(DesignSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes one input in the stored design state, re-runs the deterministic calculator and stores the result.
    /// Vision and OCR are not re-run.
    /// </summary>
    Task<DesignStateUpdateResult> UpdateInputAsync(
        int projectId,
        string key,
        double value,
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>Re-runs the calculator on the stored inputs without changing them.</summary>
    Task<DesignStateUpdateResult> RecalculateAsync(int projectId, CancellationToken cancellationToken = default);
}

public sealed class DesignStateService : IDesignStateService
{
    private readonly IProjectProcessingService _projects;
    private readonly IEquipmentCalculatorRegistry _calculators;
    private readonly ILogger<DesignStateService> _logger;

    public DesignStateService(
        IProjectProcessingService projects,
        IEquipmentCalculatorRegistry calculators,
        ILogger<DesignStateService> logger)
    {
        _projects = projects;
        _calculators = calculators;
        _logger = logger;
    }

    public async Task<DesignStateDocument?> CaptureFromSessionAsync(DesignSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.SourceProjectId is not int projectId || projectId <= 0)
        {
            return null;
        }

        try
        {
            var state = await _projects.LoadDesignStateAsync(projectId, cancellationToken)
                ?? await _projects.RefreshDesignStateAsync(projectId, cancellationToken)
                ?? new DesignStateDocument { ProjectId = projectId };

            state.SessionId = session.Id;
            state.WorkflowState = session.State.ToString();
            state.Classification.EquipmentType = session.EquipmentType.ToString();

            if (_calculators.TryGet(session.EquipmentType, out var calculator))
            {
                state.Classification.CalculatorId = calculator.CalculatorId;
            }

            state.Inputs = new Dictionary<string, double>(session.Inputs, StringComparer.OrdinalIgnoreCase);
            state.IsStale = session.IsCalculationStale;

            var latest = session.LatestRevision;
            if (latest is not null)
            {
                state.Calculation = ToStateCalculation(latest.Calculation);
            }

            state.Revision++;

            await _projects.SaveDesignStateAsync(projectId, state, cancellationToken);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Design state for project {ProjectId} could not be captured from session {SessionId}.", projectId, session.Id);
            return null;
        }
    }

    public async Task<DesignStateUpdateResult> UpdateInputAsync(
        int projectId,
        string key,
        double value,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Failed("An input key is required.");
        }

        if (!double.IsFinite(value))
        {
            return Failed($"Input '{key.Trim()}' must be a finite number.");
        }

        var state = await _projects.LoadDesignStateAsync(projectId, cancellationToken);
        if (state is null)
        {
            return Failed("No stored design state exists for this project.");
        }

        if (!TryResolveEquipment(state, out var type, out var error))
        {
            return Failed(error);
        }

        var normalized = key.Trim();

        if (EquipmentRegistry.TryGet(type, out var metadata)
            && !metadata.Inputs.Any(i => string.Equals(i.Key, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return Failed($"Unknown input for {metadata.DisplayName}: {normalized}.");
        }

        state.Inputs.TryGetValue(normalized, out var previous);
        var hadPrevious = state.Inputs.ContainsKey(normalized);

        state.Inputs[normalized] = value;
        state.Revision++;
        state.Edits.Add(new DesignStateEdit
        {
            Revision = state.Revision,
            Key = normalized,
            OldValue = hadPrevious ? previous : null,
            NewValue = value,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        });

        return await CalculateAndSaveAsync(projectId, state, type, cancellationToken);
    }

    public async Task<DesignStateUpdateResult> RecalculateAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var state = await _projects.LoadDesignStateAsync(projectId, cancellationToken);
        if (state is null)
        {
            return Failed("No stored design state exists for this project.");
        }

        if (!TryResolveEquipment(state, out var type, out var error))
        {
            return Failed(error);
        }

        state.Revision++;
        return await CalculateAndSaveAsync(projectId, state, type, cancellationToken);
    }

    private async Task<DesignStateUpdateResult> CalculateAndSaveAsync(
        int projectId,
        DesignStateDocument state,
        EquipmentType type,
        CancellationToken cancellationToken)
    {
        var calculator = _calculators.Get(type);
        var result = calculator.Calculate(new EquipmentInputs(state.Inputs));

        state.Calculation = ToStateCalculation(result);
        state.Classification.CalculatorId = calculator.CalculatorId;
        state.IsStale = !result.Succeeded;

        await _projects.SaveDesignStateAsync(projectId, state, cancellationToken);

        return result.Succeeded
            ? new DesignStateUpdateResult(true, state, Array.Empty<string>())
            : new DesignStateUpdateResult(false, state, result.GetCadBlockers());
    }

    private bool TryResolveEquipment(DesignStateDocument state, out EquipmentType type, out string error)
    {
        type = EquipmentType.Unknown;
        error = string.Empty;

        if (!Enum.TryParse(state.Classification.EquipmentType, true, out type) || type == EquipmentType.Unknown)
        {
            error = "The equipment type of this design is not known, so it cannot be recalculated.";
            return false;
        }

        if (!_calculators.TryGet(type, out _))
        {
            error = $"No calculator is registered for {type}.";
            return false;
        }

        return true;
    }

    private static DesignStateCalculation ToStateCalculation(CalculationResult result)
    {
        return new DesignStateCalculation
        {
            CalculatorVersion = result.CalculatorVersion,
            CalculatedUtc = result.CalculatedAtUtc,
            IsCadReady = result.IsCadReady,
            Values = result.Values
                .Select(v => new DesignStateValue
                {
                    Key = v.Key,
                    DisplayName = v.DisplayName,
                    Value = v.Value,
                    SiUnit = v.SiUnit,
                    FormulaId = v.FormulaId
                })
                .ToList(),
            FormulaIdsUsed = result.FormulaIdsUsed.ToList(),
            Warnings = result.Warnings
                .Select(w => new DesignStateWarning
                {
                    Code = w.Code,
                    Message = w.Message,
                    Severity = w.Severity.ToString(),
                    RelatedKey = w.RelatedKey
                })
                .ToList(),
            Errors = result.Errors.ToList()
        };
    }

    private static DesignStateCalculation ToStateCalculation(CalculationSnapshot snapshot)
    {
        return new DesignStateCalculation
        {
            CalculatorVersion = snapshot.CalculatorVersion,
            KnowledgeVersion = snapshot.KnowledgeVersion,
            CalculatedUtc = snapshot.CalculatedAtUtc,
            IsCadReady = snapshot.IsCadReady,
            Values = snapshot.Values
                .Select(v => new DesignStateValue { Key = v.Key, DisplayName = v.Key, Value = v.Value })
                .ToList(),
            FormulaIdsUsed = snapshot.FormulaIdsUsed.ToList(),
            Warnings = snapshot.Warnings
                .Select(w => new DesignStateWarning
                {
                    Code = w.Code,
                    Message = w.Message,
                    Severity = w.Severity.ToString(),
                    RelatedKey = w.RelatedKey
                })
                .ToList(),
            Errors = snapshot.Errors.ToList()
        };
    }

    private static DesignStateUpdateResult Failed(string error)
    {
        return new DesignStateUpdateResult(false, null, new[] { error });
    }
}