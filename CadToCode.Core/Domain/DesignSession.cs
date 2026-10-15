using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CadToCode.Core.Workflow;

namespace CadToCode.Core.Domain;

/// <summary>
/// Immutable audit snapshot of one calculation. Stored with every design revision.
/// </summary>
public sealed record CalculationSnapshot(
    string CalculatorVersion,
    string KnowledgeVersion,
    DateTimeOffset CalculatedAtUtc,
    bool IsCadReady,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyList<string> FormulaIdsUsed,
    IReadOnlyList<EngineeringWarning> Warnings,
    IReadOnlyList<string> Errors)
{
    /// <summary>Creates a snapshot from a calculation result.</summary>
    public static CalculationSnapshot From(CalculationResult result, string knowledgeVersion)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        return new CalculationSnapshot(
            result.CalculatorVersion,
            string.IsNullOrWhiteSpace(knowledgeVersion) ? "unknown" : knowledgeVersion,
            result.CalculatedAtUtc,
            result.IsCadReady,
            new ReadOnlyDictionary<string, double>(
                result.Values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)),
            result.FormulaIdsUsed,
            result.Warnings,
            result.Errors);
    }
}

/// <summary>
/// One numbered revision of a design: the inputs used, the calculation snapshot, and why it changed.
/// </summary>
public sealed record DesignRevision(
    int Number,
    DateTimeOffset CreatedUtc,
    EquipmentType EquipmentType,
    IReadOnlyDictionary<string, double> Inputs,
    CalculationSnapshot Calculation,
    string ChangeNote);

/// <summary>
/// Records that the user approved a workflow stage (for example the calculated parameters).
/// </summary>
public sealed record StageApproval(
    DesignWorkflowState State,
    int RevisionNumber,
    DateTimeOffset ApprovedUtc,
    string ApprovedBy);

/// <summary>
/// An interactive engineering design session: requirements, calculations, approvals and revision history.
/// Persistence-agnostic. Storage is provided through an IDesignSessionStore implemented outside Core.
/// All numeric values are SI.
/// </summary>
public sealed class DesignSession
{
    private readonly Dictionary<string, double> _inputs;
    private readonly List<DesignRevision> _revisions;
    private readonly List<StageApproval> _approvals;

    public Guid Id { get; }
    public string OwnerId { get; }
    public string Title { get; private set; }
    public EquipmentType EquipmentType { get; private set; }
    public DesignWorkflowState State { get; private set; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Optional link to an uploaded project or drawing.</summary>
    public int? SourceProjectId { get; private set; }

    /// <summary>Current requirement inputs (SI), keyed by parameter key.</summary>
    public IReadOnlyDictionary<string, double> Inputs => _inputs;

    /// <summary>Revision history, oldest first.</summary>
    public IReadOnlyList<DesignRevision> Revisions => _revisions;

    /// <summary>Stage approvals, oldest first.</summary>
    public IReadOnlyList<StageApproval> Approvals => _approvals;

    /// <summary>Latest revision, or null when nothing has been calculated.</summary>
    public DesignRevision? LatestRevision => _revisions.Count == 0 ? null : _revisions[^1];

    /// <summary>True when the latest calculation is safe for CAD generation.</summary>
    public bool HasCadReadyCalculation => LatestRevision?.Calculation.IsCadReady == true;

    /// <summary>
    /// True when the current inputs and equipment type differ from the latest calculated revision
    /// (the calculation is out of date).
    /// </summary>
    public bool IsCalculationStale
    {
        get
        {
            var latest = LatestRevision;
            if (latest is null) return _inputs.Count > 0;
            if (latest.EquipmentType != EquipmentType) return true;
            if (latest.Inputs.Count != _inputs.Count) return true;

            foreach (var kv in _inputs)
            {
                if (!latest.Inputs.TryGetValue(kv.Key, out var previous) || previous != kv.Value)
                    return true;
            }

            return false;
        }
    }

    public DesignSession(string ownerId, string title, DateTimeOffset? createdUtc = null, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Owner id is required.", nameof(ownerId));

        Id = id ?? Guid.NewGuid();
        OwnerId = ownerId;
        Title = string.IsNullOrWhiteSpace(title) ? "Untitled design" : title.Trim();
        EquipmentType = EquipmentType.Unknown;
        State = DesignWorkflowState.RequirementsGathering;
        CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow;
        UpdatedUtc = CreatedUtc;
        _inputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        _revisions = new List<DesignRevision>();
        _approvals = new List<StageApproval>();
    }

    /// <summary>
    /// Rehydrates a session from storage without raising domain rules.
    /// Intended for persistence implementations only.
    /// </summary>
    public static DesignSession Restore(
        Guid id,
        string ownerId,
        string title,
        EquipmentType equipmentType,
        DesignWorkflowState state,
        DateTimeOffset createdUtc,
        DateTimeOffset updatedUtc,
        int? sourceProjectId,
        IEnumerable<KeyValuePair<string, double>> inputs,
        IEnumerable<DesignRevision> revisions,
        IEnumerable<StageApproval> approvals)
    {
        var session = new DesignSession(ownerId, title, createdUtc, id)
        {
            EquipmentType = equipmentType,
            State = state,
            UpdatedUtc = updatedUtc,
            SourceProjectId = sourceProjectId
        };

        foreach (var kv in inputs ?? Enumerable.Empty<KeyValuePair<string, double>>())
            session._inputs[kv.Key] = kv.Value;

        session._revisions.AddRange((revisions ?? Enumerable.Empty<DesignRevision>()).OrderBy(r => r.Number));
        session._approvals.AddRange((approvals ?? Enumerable.Empty<StageApproval>()).OrderBy(a => a.ApprovedUtc));
        return session;
    }

    public void Rename(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title is required.", nameof(title));
        Title = title.Trim();
        Touch();
    }

    public void LinkSourceProject(int? projectId)
    {
        SourceProjectId = projectId;
        Touch();
    }

    /// <summary>
    /// Sets the equipment type. Changing to a different known type clears inputs and approvals
    /// because parameters are not transferable between equipment types. Revision history is kept.
    /// </summary>
    public void SetEquipmentType(EquipmentType type)
    {
        if (type == EquipmentType) return;

        if (EquipmentType != EquipmentType.Unknown)
        {
            _inputs.Clear();
            _approvals.Clear();
        }

        EquipmentType = type;
        Touch();
    }

    /// <summary>Sets or replaces one input value (SI). Existing approvals are cleared because the design changed.</summary>
    public void SetInput(string key, double value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Input key is required.", nameof(key));
        if (!double.IsFinite(value)) throw new ArgumentException($"Input '{key}' must be a finite number.", nameof(value));

        var normalized = key.Trim();
        if (_inputs.TryGetValue(normalized, out var existing) && existing == value)
            return;

        _inputs[normalized] = value;
        _approvals.Clear();
        Touch();
    }

    /// <summary>Removes one input value. Existing approvals are cleared.</summary>
    public bool RemoveInput(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_inputs.Remove(key.Trim()))
            return false;

        _approvals.Clear();
        Touch();
        return true;
    }

    /// <summary>
    /// Records a calculation as a new revision using the current equipment type and inputs.
    /// </summary>
    public DesignRevision RecordCalculation(CalculationResult result, string knowledgeVersion, string changeNote = "")
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (EquipmentType == EquipmentType.Unknown)
            throw new InvalidOperationException("Equipment type must be set before recording a calculation.");
        if (result.EquipmentType != EquipmentType)
            throw new InvalidOperationException(
                $"Calculation result is for {result.EquipmentType} but the session equipment is {EquipmentType}.");

        var revision = new DesignRevision(
            _revisions.Count + 1,
            DateTimeOffset.UtcNow,
            EquipmentType,
            new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(_inputs, StringComparer.OrdinalIgnoreCase)),
            CalculationSnapshot.From(result, knowledgeVersion),
            changeNote?.Trim() ?? string.Empty);

        _revisions.Add(revision);
        _approvals.Clear();
        Touch();
        return revision;
    }

    /// <summary>
    /// Records an approval for the given stage against the latest revision.
    /// </summary>
    public StageApproval Approve(DesignWorkflowState stage, string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("Approver is required.", nameof(approvedBy));

        var latest = LatestRevision
            ?? throw new InvalidOperationException("There is no calculated revision to approve.");

        if (IsCalculationStale)
            throw new InvalidOperationException("Inputs changed after the latest calculation. Recalculate before approving.");

        var approval = new StageApproval(stage, latest.Number, DateTimeOffset.UtcNow, approvedBy.Trim());
        _approvals.RemoveAll(a => a.State == stage);
        _approvals.Add(approval);
        Touch();
        return approval;
    }

    /// <summary>True when the stage is approved against the latest revision.</summary>
    public bool IsApproved(DesignWorkflowState stage)
    {
        var latest = LatestRevision;
        return latest is not null
               && _approvals.Any(a => a.State == stage && a.RevisionNumber == latest.Number);
    }

    /// <summary>
    /// Sets the workflow state. Transition rules are enforced by the workflow engine, not by this method.
    /// </summary>
    public void SetState(DesignWorkflowState state)
    {
        if (State == state) return;
        State = state;
        Touch();
    }

    private void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
}
