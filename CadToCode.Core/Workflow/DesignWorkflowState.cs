using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CadToCode.Core.Workflow;

/// <summary>
/// Stages of the interactive engineering design review workflow.
/// Requirements Gathering -> Formula Validation -> Design Review (conversational modification)
/// -> Script Generation -> STEP Export -> Completed.
/// </summary>
public enum DesignWorkflowState
{
    /// <summary>Collect equipment type and required inputs. Ask clarifying questions for anything missing.</summary>
    RequirementsGathering = 0,

    /// <summary>Run deterministic calculators, show formulas, results and warnings.</summary>
    FormulaValidation = 1,

    /// <summary>Review results with the user and apply conversational modifications. Recalculate on change.</summary>
    DesignReview = 2,

    /// <summary>Generate and validate the C# CAD script from approved parameters.</summary>
    ScriptGeneration = 3,

    /// <summary>Execute the script in the sandbox and write the STEP file.</summary>
    StepExport = 4,

    /// <summary>STEP file delivered. The session may be reopened for revision.</summary>
    Completed = 5
}

/// <summary>
/// Static description of the workflow: display text, ordering and the allowed transition graph.
/// The workflow engine enforces additional business conditions (inputs complete, results approved, CAD ready).
/// </summary>
public static class DesignWorkflowStates
{
    private static readonly IReadOnlyDictionary<DesignWorkflowState, IReadOnlyList<DesignWorkflowState>> Transitions =
        new ReadOnlyDictionary<DesignWorkflowState, IReadOnlyList<DesignWorkflowState>>(
            new Dictionary<DesignWorkflowState, IReadOnlyList<DesignWorkflowState>>
            {
                [DesignWorkflowState.RequirementsGathering] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.FormulaValidation
                }),
                [DesignWorkflowState.FormulaValidation] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.DesignReview,
                    DesignWorkflowState.RequirementsGathering
                }),
                [DesignWorkflowState.DesignReview] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.ScriptGeneration,
                    DesignWorkflowState.FormulaValidation,
                    DesignWorkflowState.RequirementsGathering
                }),
                [DesignWorkflowState.ScriptGeneration] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.StepExport,
                    DesignWorkflowState.DesignReview
                }),
                [DesignWorkflowState.StepExport] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.Completed,
                    DesignWorkflowState.DesignReview
                }),
                [DesignWorkflowState.Completed] = Array.AsReadOnly(new[]
                {
                    DesignWorkflowState.DesignReview
                })
            });

    /// <summary>All states in workflow order.</summary>
    public static IReadOnlyList<DesignWorkflowState> Ordered { get; } =
        Array.AsReadOnly(Enum.GetValues<DesignWorkflowState>().OrderBy(s => (int)s).ToArray());

    /// <summary>States reachable directly from <paramref name="from"/>.</summary>
    public static IReadOnlyList<DesignWorkflowState> AllowedNext(DesignWorkflowState from)
        => Transitions.TryGetValue(from, out var next) ? next : Array.Empty<DesignWorkflowState>();

    /// <summary>True when the transition exists in the workflow graph.</summary>
    public static bool CanTransition(DesignWorkflowState from, DesignWorkflowState to)
        => AllowedNext(from).Contains(to);

    /// <summary>True when <paramref name="to"/> moves the workflow forward.</summary>
    public static bool IsForward(DesignWorkflowState from, DesignWorkflowState to)
        => (int)to > (int)from;

    public static string DisplayName(DesignWorkflowState state) => state switch
    {
        DesignWorkflowState.RequirementsGathering => "Requirements Gathering",
        DesignWorkflowState.FormulaValidation => "Formula Validation",
        DesignWorkflowState.DesignReview => "Design Review",
        DesignWorkflowState.ScriptGeneration => "CAD Script Generation",
        DesignWorkflowState.StepExport => "STEP Export",
        DesignWorkflowState.Completed => "Completed",
        _ => state.ToString()
    };

    public static string Description(DesignWorkflowState state) => state switch
    {
        DesignWorkflowState.RequirementsGathering =>
            "Identify the equipment type and collect the required design inputs.",
        DesignWorkflowState.FormulaValidation =>
            "Run the deterministic calculators and review formulas, results and warnings.",
        DesignWorkflowState.DesignReview =>
            "Discuss the design and apply changes. Every change triggers a recalculation.",
        DesignWorkflowState.ScriptGeneration =>
            "Generate and validate the C# CAD script from the approved parameters.",
        DesignWorkflowState.StepExport =>
            "Run the script in the sandbox and write the STEP file.",
        DesignWorkflowState.Completed =>
            "The STEP file has been delivered. Reopen the design review to make further changes.",
        _ => string.Empty
    };
}
