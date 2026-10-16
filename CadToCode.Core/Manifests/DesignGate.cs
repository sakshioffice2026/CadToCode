using System.Globalization;

namespace CadToCode.Core.Manifests;

public enum GateStatus
{
    /// <summary>All checks passed. Generation may continue at <see cref="GateDecision.EffectiveTier"/>.</summary>
    Proceed = 0,

    /// <summary>Something is missing or must be confirmed. The assistant must ask, not guess.</summary>
    NeedInfo = 1,

    /// <summary>Nothing may be generated for this request.</summary>
    Refuse = 2
}

/// <summary>One candidate value with its origin and confirmation state.</summary>
/// <param name="Key">Manifest input key.</param>
/// <param name="Value">Value in the manifest parameter's unit.</param>
/// <param name="Origin">Where the value came from.</param>
/// <param name="UserConfirmed">True once the user has explicitly confirmed this value.</param>
public sealed record GateValue(string Key, double Value, ValueOrigin Origin, bool UserConfirmed = false);

/// <summary>Everything the gate needs to decide. Built by the caller; the gate does no I/O.</summary>
public sealed class GateContext
{
    public EquipmentManifest? Manifest { get; init; }

    /// <summary>Highest tier the caller wants. The gate may lower it, never raise it.</summary>
    public DesignTier RequestedTier { get; init; } = DesignTier.Engineered;

    public IReadOnlyList<GateValue> Values { get; init; } = Array.Empty<GateValue>();

    /// <summary>True when a calculator with the manifest's CalculatorId is registered.</summary>
    public bool CalculatorRegistered { get; init; }

    /// <summary>Ids of rulepacks that are actually loaded.</summary>
    public IReadOnlyCollection<string> AvailableRulePackIds { get; init; } = Array.Empty<string>();

    /// <summary>True once the user has explicitly accepted a generic, non-engineered result.</summary>
    public bool UserAcceptedGeneric { get; init; }

    /// <summary>Confidence of the equipment match, 0 to 1. Null means the user named the equipment directly.</summary>
    public double? MatchConfidence { get; init; }

    public double MinMatchConfidence { get; init; } = 0.6;
}

/// <summary>A question the assistant must put to the user before continuing.</summary>
/// <param name="Code">Stable machine code.</param>
/// <param name="Key">Related parameter key, when there is one.</param>
/// <param name="Prompt">Plain-language question.</param>
public sealed record GateQuestion(string Code, string? Key, string Prompt);

public sealed record GateDecision(
    GateStatus Status,
    DesignTier EffectiveTier,
    IReadOnlyList<GateQuestion> Questions,
    IReadOnlyList<ManifestIssue> Blockers,
    bool RequiresGenericLabel);

/// <summary>
/// Decides what the system is allowed to do for a request. Values from vision, OCR or an LLM are
/// never trusted until confirmed. Nothing here guesses or substitutes a different equipment type.
/// </summary>
public static class DesignGate
{
    public static GateDecision Evaluate(GateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var manifest = context.Manifest;

        if (manifest is null)
        {
            return Refuse("GATE_NO_MANIFEST", "No equipment definition matches this request.");
        }

        if (manifest.MaxTier == DesignTier.Refuse || context.RequestedTier == DesignTier.Refuse)
        {
            return Refuse("GATE_TIER_REFUSE", manifest.DisplayName + " is not available for generation.");
        }

        var questions = new List<GateQuestion>();
        var blockers = new List<ManifestIssue>();

        if (context.MatchConfidence.HasValue && context.MatchConfidence.Value < context.MinMatchConfidence)
        {
            questions.Add(new GateQuestion(
                "GATE_CONFIRM_EQUIPMENT",
                null,
                "I am not confident this is a " + manifest.DisplayName + ". Please confirm the equipment type."));
        }

        var tier = ResolveTier(context, manifest, blockers);

        if (tier == DesignTier.ParametricGeneric && !context.UserAcceptedGeneric)
        {
            questions.Add(new GateQuestion(
                "GATE_GENERIC_OPT_IN",
                null,
                "A fully engineered design is not available for " + manifest.DisplayName
                + ". I can build a generic parametric model from dimensions you confirm. "
                + "It will be labelled as not engineered and will make no sizing or compliance claims. Do you want that?"));
        }

        CheckValues(context, manifest, tier, questions, blockers);

        var status = questions.Count == 0 && blockers.Count == 0
            ? GateStatus.Proceed
            : GateStatus.NeedInfo;

        return new GateDecision(
            status,
            tier,
            questions,
            blockers,
            tier == DesignTier.ParametricGeneric);
    }

    private static DesignTier ResolveTier(GateContext context, EquipmentManifest manifest, List<ManifestIssue> blockers)
    {
        var tier = context.RequestedTier <= manifest.MaxTier ? context.RequestedTier : manifest.MaxTier;

        if (tier != DesignTier.Engineered)
        {
            return tier;
        }

        if (!context.CalculatorRegistered)
        {
            blockers.Add(new ManifestIssue(
                "GATE_CALCULATOR_MISSING",
                "The calculator '" + manifest.CalculatorId + "' is not registered. Engineered output is not available.",
                manifest.Id));
            return DesignTier.ParametricGeneric;
        }

        var available = new HashSet<string>(context.AvailableRulePackIds, StringComparer.OrdinalIgnoreCase);
        var missingPacks = manifest.RulePackIds.Where(id => !available.Contains(id)).ToList();

        if (missingPacks.Count > 0)
        {
            blockers.Add(new ManifestIssue(
                "GATE_RULEPACK_MISSING",
                "Rulepack(s) not loaded: " + string.Join(", ", missingPacks) + ". Engineered output is not available.",
                manifest.Id));
            return DesignTier.ParametricGeneric;
        }

        return tier;
    }

    private static void CheckValues(
        GateContext context,
        EquipmentManifest manifest,
        DesignTier tier,
        List<GateQuestion> questions,
        List<ManifestIssue> blockers)
    {
        var inputs = manifest.Inputs.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var supplied = new Dictionary<string, GateValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in context.Values)
        {
            if (!inputs.ContainsKey(value.Key))
            {
                blockers.Add(new ManifestIssue(
                    "GATE_UNKNOWN_PARAMETER",
                    "'" + value.Key + "' is not a parameter of " + manifest.DisplayName + ".",
                    manifest.Id + "/" + value.Key));
                continue;
            }

            if (!supplied.TryAdd(value.Key, value))
            {
                blockers.Add(new ManifestIssue(
                    "GATE_DUPLICATE_VALUE",
                    "More than one value was given for '" + value.Key + "'.",
                    manifest.Id + "/" + value.Key));
            }
        }

        foreach (var parameter in manifest.Inputs)
        {
            supplied.TryGetValue(parameter.Key, out var value);

            var mustBeExplicit = parameter.IsRequired || tier == DesignTier.ParametricGeneric;
            var hasUsableValue = value is not null && value.Origin != ValueOrigin.Default;

            if (value is null || (!hasUsableValue && mustBeExplicit))
            {
                if (mustBeExplicit)
                {
                    questions.Add(new GateQuestion(
                        "GATE_MISSING_INPUT",
                        parameter.Key,
                        "Please provide " + parameter.DisplayName + " (" + parameter.Unit + ")."
                        + (string.IsNullOrWhiteSpace(parameter.Description) ? string.Empty : " " + parameter.Description)));
                }

                continue;
            }

            var rangeError = CheckRange(parameter, value.Value);

            if (rangeError is not null)
            {
                blockers.Add(new ManifestIssue("GATE_OUT_OF_RANGE", rangeError, manifest.Id + "/" + parameter.Key));
                continue;
            }

            if (value.Origin == ValueOrigin.ImageUnverified && !value.UserConfirmed)
            {
                questions.Add(new GateQuestion(
                    "GATE_UNCONFIRMED_VALUE",
                    parameter.Key,
                    "I read " + parameter.DisplayName + " = " + Format(value.Value) + " " + parameter.Unit
                    + " from the image. Please confirm or correct it."));
            }
        }
    }

    private static string? CheckRange(ManifestParameter parameter, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return parameter.DisplayName + " must be a finite number.";
        }

        if (parameter.IsInteger && Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            return parameter.DisplayName + " must be a whole number.";
        }

        if ((parameter.Min.HasValue && value < parameter.Min.Value)
            || (parameter.Max.HasValue && value > parameter.Max.Value))
        {
            var min = parameter.Min.HasValue ? Format(parameter.Min.Value) : "no lower limit";
            var max = parameter.Max.HasValue ? Format(parameter.Max.Value) : "no upper limit";

            return parameter.DisplayName + " must be between " + min + " and " + max + " " + parameter.Unit + ".";
        }

        return null;
    }

    private static GateDecision Refuse(string code, string message)
    {
        return new GateDecision(
            GateStatus.Refuse,
            DesignTier.Refuse,
            Array.Empty<GateQuestion>(),
            new[] { new ManifestIssue(code, message, "(gate)") },
            false);
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
