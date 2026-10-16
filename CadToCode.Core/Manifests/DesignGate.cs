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
    /// <summary>The resolved equipment. Leave null when equipment is unknown or ambiguous.</summary>
    public EquipmentManifest? Manifest { get; init; }

    /// <summary>
    /// Result of <see cref="IEquipmentManifestRegistry.ResolveDetailed"/>. When it is ambiguous the gate
    /// asks which equipment is meant, even if <see cref="Manifest"/> is also set.
    /// </summary>
    public ManifestResolution? Resolution { get; init; }

    /// <summary>
    /// Equipment the user can pick from when nothing matched. When empty and nothing matched,
    /// the gate refuses instead of asking.
    /// </summary>
    public IReadOnlyList<EquipmentManifest> AvailableManifests { get; init; } = Array.Empty<EquipmentManifest>();

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

    /// <summary>Template the caller intends to use. Checked against the manifest's allowed and forbidden templates.</summary>
    public string? RequestedTemplateKey { get; init; }

    /// <summary>Part keys selected for export. Null means the caller has no part selection to check.</summary>
    public IReadOnlyList<string>? RequestedPartKeys { get; init; }

    /// <summary>True once the user has confirmed that exporting without some required parts is intended.</summary>
    public bool PartialExportConfirmed { get; init; }
}

/// <summary>A question the assistant must put to the user before continuing.</summary>
/// <param name="Code">Stable machine code.</param>
/// <param name="Key">Related parameter key, when there is one.</param>
/// <param name="Prompt">Plain-language question.</param>
public sealed record GateQuestion(string Code, string? Key, string Prompt)
{
    /// <summary>Fixed answers to offer. Empty means free text.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
}

public sealed record GateDecision(
    GateStatus Status,
    DesignTier EffectiveTier,
    IReadOnlyList<GateQuestion> Questions,
    IReadOnlyList<ManifestIssue> Blockers,
    bool RequiresGenericLabel)
{
    /// <summary>True when the session must pause and wait for the user's answers.</summary>
    public bool PausesSession => Status == GateStatus.NeedInfo;

    /// <summary>
    /// Stable fingerprint of what is pending. Equal values mean the same questions and blockers are still open,
    /// so a stored session can tell whether the user's reply moved things forward.
    /// </summary>
    public string PendingKey => string.Join(
        "|",
        Questions.Select(q => q.Code + ":" + (q.Key ?? string.Empty))
            .Concat(Blockers.Select(b => b.Code + ":" + b.Path))
            .OrderBy(s => s, StringComparer.Ordinal));
}

/// <summary>
/// Decides what the system is allowed to do for a request. Values from vision, OCR or an LLM are
/// never trusted until confirmed. Nothing here guesses or substitutes a different equipment type.
/// </summary>
public static class DesignGate
{
    public static GateDecision Evaluate(GateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Resolution is { IsAmbiguous: true } ambiguous)
        {
            return NeedEquipment(
                "GATE_AMBIGUOUS_EQUIPMENT",
                ambiguous.Candidates,
                "This could be more than one type of equipment. Which one do you mean?");
        }

        var manifest = context.Manifest;

        if (manifest is null)
        {
            if (context.AvailableManifests.Count > 0)
            {
                return NeedEquipment(
                    "GATE_IDENTIFY_EQUIPMENT",
                    context.AvailableManifests,
                    "I could not identify the equipment. Which type of equipment do you want to design?");
            }

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
                "I am not confident this is a " + manifest.DisplayName + ". Please confirm the equipment type.")
            {
                Choices = new[] { "Yes, it is a " + manifest.DisplayName, "No, it is a different equipment type" }
            });
        }

        var tier = ResolveTier(context, manifest, blockers);

        if (tier == DesignTier.ParametricGeneric && !context.UserAcceptedGeneric)
        {
            questions.Add(new GateQuestion(
                "GATE_GENERIC_OPT_IN",
                null,
                "A fully engineered design is not available for " + manifest.DisplayName
                + ". I can build a generic parametric model from dimensions you confirm. "
                + "It will be labelled as not engineered and will make no sizing or compliance claims. Do you want that?")
            {
                Choices = new[] { "Yes, build a generic model", "No, stop here" }
            });
        }

        CheckValues(context, manifest, tier, questions, blockers);
        CheckTemplate(context, manifest, questions, blockers);
        CheckParts(context, manifest, questions, blockers);

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
                        BuildMissingPrompt(parameter))
                    {
                        Choices = parameter.Choices
                    });
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

    private static void CheckTemplate(
        GateContext context,
        EquipmentManifest manifest,
        List<GateQuestion> questions,
        List<ManifestIssue> blockers)
    {
        if (string.IsNullOrWhiteSpace(context.RequestedTemplateKey))
        {
            return;
        }

        var key = context.RequestedTemplateKey.Trim();
        var path = manifest.Id + "/" + key;

        var isForbidden = manifest.ForbiddenSubstitutions.Any(f =>
            !string.IsNullOrWhiteSpace(f) && string.Equals(f.Trim(), key, StringComparison.OrdinalIgnoreCase));

        var isAllowed = manifest.TemplateKeys.Any(t =>
            !string.IsNullOrWhiteSpace(t) && string.Equals(t.Trim(), key, StringComparison.OrdinalIgnoreCase));

        if (isForbidden)
        {
            blockers.Add(new ManifestIssue(
                "GATE_FORBIDDEN_SUBSTITUTION",
                "Template '" + key + "' must not stand in for " + manifest.DisplayName + ".",
                path));
        }
        else if (manifest.TemplateKeys.Count > 0 && !isAllowed)
        {
            blockers.Add(new ManifestIssue(
                "GATE_TEMPLATE_NOT_ALLOWED",
                "Template '" + key + "' is not defined for " + manifest.DisplayName + ".",
                path));
        }
        else
        {
            return;
        }

        if (manifest.TemplateKeys.Count > 0)
        {
            questions.Add(new GateQuestion(
                "GATE_CHOOSE_TEMPLATE",
                null,
                "Which template should be used for " + manifest.DisplayName + "?")
            {
                Choices = manifest.TemplateKeys
            });
        }
    }

    private static void CheckParts(
        GateContext context,
        EquipmentManifest manifest,
        List<GateQuestion> questions,
        List<ManifestIssue> blockers)
    {
        if (manifest.Parts.Count == 0 || context.RequestedPartKeys is null)
        {
            return;
        }

        var known = new HashSet<string>(manifest.Parts.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in context.RequestedPartKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !known.Contains(key.Trim()))
            {
                blockers.Add(new ManifestIssue(
                    "GATE_UNKNOWN_PART",
                    "'" + key + "' is not a part of " + manifest.DisplayName + ".",
                    manifest.Id + "/" + key));
                continue;
            }

            selected.Add(key.Trim());
        }

        if (context.PartialExportConfirmed)
        {
            return;
        }

        var missing = manifest.Parts
            .Where(p => p.IsRequired && !selected.Contains(p.Key))
            .Select(p => string.IsNullOrWhiteSpace(p.Title) ? p.Key : p.Title)
            .ToList();

        if (missing.Count > 0)
        {
            questions.Add(new GateQuestion(
                "GATE_CONFIRM_PARTS",
                null,
                "These required parts are not selected: " + string.Join(", ", missing)
                + ". The export is incomplete without them. Include them, or confirm a partial export?")
            {
                Choices = new[] { "Include all required parts", "Confirm partial export" }
            });
        }
    }

    private static string BuildMissingPrompt(ManifestParameter parameter)
    {
        if (!string.IsNullOrWhiteSpace(parameter.ClarifyingQuestion))
        {
            return parameter.ClarifyingQuestion;
        }

        return "Please provide " + parameter.DisplayName + " (" + parameter.Unit + ")."
               + (string.IsNullOrWhiteSpace(parameter.Description) ? string.Empty : " " + parameter.Description);
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

    private static GateDecision NeedEquipment(string code, IReadOnlyList<EquipmentManifest> options, string prompt)
    {
        var choices = options
            .Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GateDecision(
            GateStatus.NeedInfo,
            DesignTier.Refuse,
            new[] { new GateQuestion(code, null, prompt) { Choices = choices } },
            Array.Empty<ManifestIssue>(),
            false);
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