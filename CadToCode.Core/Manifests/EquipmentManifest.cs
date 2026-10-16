using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CadToCode.Core.Manifests;

/// <summary>
/// How far the system may go for an equipment type.
/// Higher tiers require more verified engineering backing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DesignTier
{
    /// <summary>Nothing may be generated. The assistant lists options and stops.</summary>
    Refuse = 0,

    /// <summary>
    /// User-confirmed dimensions only. No calculation, no standard or compliance claims.
    /// Output must be labelled as generic and not engineered.
    /// </summary>
    ParametricGeneric = 1,

    /// <summary>Registered calculator plus rulepacks. Full math, constraints and provenance.</summary>
    Engineered = 2
}

/// <summary>Origin of a parameter value. Used for provenance and the gate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValueOrigin
{
    Default = 0,
    User = 1,
    DxfVerified = 2,
    ImageUnverified = 3,
    Rule = 4,
    Calculated = 5
}

/// <summary>A single input or output parameter declared by a manifest.</summary>
public sealed class ManifestParameter
{
    /// <summary>Stable machine key, e.g. "inlet_velocity_ms" or "BodyHeight".</summary>
    public string Key { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Unit symbol, e.g. "m", "mm", "Pa", "m3/s", "deg", "-".</summary>
    public string Unit { get; init; } = string.Empty;

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Default { get; init; }

    public bool IsRequired { get; init; }

    public bool IsInteger { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>A selectable part of the equipment (for example a shell, inlet or drain).</summary>
public sealed class ManifestComponent
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>Keys of <see cref="EquipmentManifest.Inputs"/> this component depends on.</summary>
    public IReadOnlyList<string> ParameterKeys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Declarative description of one equipment type. Loaded from JSON so new equipment
/// can be added without changing core logic.
/// </summary>
public sealed class EquipmentManifest
{
    /// <summary>Stable lowercase kebab-case id, e.g. "cyclone-separator".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Broad domain, e.g. "process-equipment", "piping", "pressure-containment", "air-moving".</summary>
    public string Domain { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Manifest version, e.g. "1.0.0". Bump on any parameter or rule change.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Highest tier this equipment is allowed to reach.</summary>
    public DesignTier MaxTier { get; init; } = DesignTier.ParametricGeneric;

    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>Id of the registered calculator. Required for <see cref="DesignTier.Engineered"/>.</summary>
    public string? CalculatorId { get; init; }

    /// <summary>Ids of rulepacks the calculator reads. Required for <see cref="DesignTier.Engineered"/>.</summary>
    public IReadOnlyList<string> RulePackIds { get; init; } = Array.Empty<string>();

    /// <summary>Keys of CAD templates that can render this equipment.</summary>
    public IReadOnlyList<string> TemplateKeys { get; init; } = Array.Empty<string>();

    /// <summary>Authoritative references, e.g. handbook and section.</summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ManifestParameter> Inputs { get; init; } = Array.Empty<ManifestParameter>();

    public IReadOnlyList<ManifestParameter> Outputs { get; init; } = Array.Empty<ManifestParameter>();

    public IReadOnlyList<ManifestComponent> Components { get; init; } = Array.Empty<ManifestComponent>();
}

/// <summary>A single validation finding for a manifest.</summary>
/// <param name="Code">Stable machine code, e.g. "MANIFEST_DUPLICATE_KEY".</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Path">Location, e.g. "cyclone-separator/Inputs[2]".</param>
public sealed record ManifestIssue(string Code, string Message, string Path);

/// <summary>Structural validation for manifests. Pure and deterministic.</summary>
public static class EquipmentManifestValidator
{
    private static readonly Regex IdPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex KeyPattern = new(
        "^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>Validates one manifest on its own.</summary>
    public static IReadOnlyList<ManifestIssue> Validate(EquipmentManifest? manifest)
    {
        var issues = new List<ManifestIssue>();

        if (manifest is null)
        {
            issues.Add(new ManifestIssue("MANIFEST_NULL", "Manifest is empty.", "(root)"));
            return issues;
        }

        var root = string.IsNullOrWhiteSpace(manifest.Id) ? "(no id)" : manifest.Id;

        if (string.IsNullOrWhiteSpace(manifest.Id) || !Matches(IdPattern, manifest.Id))
        {
            issues.Add(new ManifestIssue("MANIFEST_BAD_ID", "Id must be lowercase kebab-case (for example 'cyclone-separator').", root));
        }

        Require(issues, manifest.Domain, "MANIFEST_NO_DOMAIN", "Domain is required.", root);
        Require(issues, manifest.DisplayName, "MANIFEST_NO_NAME", "DisplayName is required.", root);

        if (string.IsNullOrWhiteSpace(manifest.Version) || !Matches(VersionPattern, manifest.Version))
        {
            issues.Add(new ManifestIssue("MANIFEST_BAD_VERSION", "Version must look like 1.0.0.", root));
        }

        ValidateAliases(issues, manifest.Aliases, root + "/Aliases");

        var inputKeys = ValidateParameters(issues, manifest.Inputs, root + "/Inputs");
        ValidateParameters(issues, manifest.Outputs, root + "/Outputs");

        ValidateComponents(issues, manifest.Components, inputKeys, root);
        ValidateTier(issues, manifest, root);

        return issues;
    }

    /// <summary>Validates every manifest and also checks collisions between manifests.</summary>
    public static IReadOnlyList<ManifestIssue> ValidateSet(IEnumerable<EquipmentManifest> manifests)
    {
        var list = manifests.ToList();
        var issues = new List<ManifestIssue>();

        foreach (var manifest in list)
        {
            issues.AddRange(Validate(manifest));
        }

        foreach (var group in list
                     .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                     .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(new ManifestIssue("MANIFEST_DUPLICATE_ID", "More than one manifest uses id '" + group.Key + "'.", group.Key));
        }

        var aliasOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in list.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
        {
            foreach (var alias in manifest.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                var normalized = alias.Trim();

                if (aliasOwners.TryGetValue(normalized, out var owner)
                    && !string.Equals(owner, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ManifestIssue(
                        "MANIFEST_ALIAS_CONFLICT",
                        "Alias '" + normalized + "' is used by both '" + owner + "' and '" + manifest.Id + "'.",
                        manifest.Id + "/Aliases"));
                }
                else
                {
                    aliasOwners[normalized] = manifest.Id;
                }
            }
        }

        return issues;
    }

    private static void ValidateTier(List<ManifestIssue> issues, EquipmentManifest manifest, string root)
    {
        var hasCalculator = !string.IsNullOrWhiteSpace(manifest.CalculatorId);

        if (manifest.MaxTier == DesignTier.Engineered)
        {
            if (!hasCalculator)
            {
                issues.Add(new ManifestIssue("MANIFEST_ENGINEERED_NO_CALCULATOR", "Engineered tier requires a CalculatorId.", root));
            }

            if (manifest.RulePackIds.Count == 0 || manifest.RulePackIds.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(new ManifestIssue("MANIFEST_ENGINEERED_NO_RULEPACK", "Engineered tier requires at least one RulePackId.", root));
            }

            if (!manifest.Inputs.Any(i => i.IsRequired))
            {
                issues.Add(new ManifestIssue("MANIFEST_ENGINEERED_NO_REQUIRED_INPUT", "Engineered tier requires at least one required input.", root));
            }

            if (manifest.Outputs.Count == 0)
            {
                issues.Add(new ManifestIssue("MANIFEST_ENGINEERED_NO_OUTPUT", "Engineered tier requires declared outputs.", root));
            }

            if (manifest.References.Count == 0 || manifest.References.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(new ManifestIssue("MANIFEST_ENGINEERED_NO_REFERENCE", "Engineered tier requires at least one reference.", root));
            }
        }
        else if (hasCalculator)
        {
            issues.Add(new ManifestIssue(
                "MANIFEST_CALCULATOR_BELOW_ENGINEERED",
                "A CalculatorId is declared but MaxTier is below Engineered. Raise the tier or remove the calculator.",
                root));
        }

        if (manifest.MaxTier == DesignTier.ParametricGeneric && manifest.Inputs.Count == 0)
        {
            issues.Add(new ManifestIssue("MANIFEST_NO_INPUTS", "ParametricGeneric tier requires declared inputs.", root));
        }

        if (manifest.TemplateKeys.Any(string.IsNullOrWhiteSpace))
        {
            issues.Add(new ManifestIssue("MANIFEST_BAD_TEMPLATE_KEY", "TemplateKeys must not contain empty values.", root));
        }
    }

    private static HashSet<string> ValidateParameters(
        List<ManifestIssue> issues,
        IReadOnlyList<ManifestParameter> parameters,
        string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var here = path + "[" + i + "]";

            if (string.IsNullOrWhiteSpace(p.Key) || !Matches(KeyPattern, p.Key))
            {
                issues.Add(new ManifestIssue("MANIFEST_BAD_KEY", "Parameter key must start with a letter and contain only letters, digits or underscore.", here));
            }
            else if (!seen.Add(p.Key))
            {
                issues.Add(new ManifestIssue("MANIFEST_DUPLICATE_KEY", "Duplicate parameter key '" + p.Key + "'.", here));
            }

            Require(issues, p.DisplayName, "MANIFEST_PARAM_NO_NAME", "Parameter DisplayName is required.", here);
            Require(issues, p.Unit, "MANIFEST_PARAM_NO_UNIT", "Parameter Unit is required (use '-' for dimensionless).", here);

            if (p.Min.HasValue && (double.IsNaN(p.Min.Value) || double.IsInfinity(p.Min.Value)))
            {
                issues.Add(new ManifestIssue("MANIFEST_PARAM_BAD_RANGE", "Min must be a finite number.", here));
            }

            if (p.Max.HasValue && (double.IsNaN(p.Max.Value) || double.IsInfinity(p.Max.Value)))
            {
                issues.Add(new ManifestIssue("MANIFEST_PARAM_BAD_RANGE", "Max must be a finite number.", here));
            }

            if (p.Min.HasValue && p.Max.HasValue && p.Min.Value > p.Max.Value)
            {
                issues.Add(new ManifestIssue("MANIFEST_PARAM_BAD_RANGE", "Min must not exceed Max.", here));
            }

            if (p.Default.HasValue)
            {
                var d = p.Default.Value;

                if (double.IsNaN(d) || double.IsInfinity(d)
                    || (p.Min.HasValue && d < p.Min.Value)
                    || (p.Max.HasValue && d > p.Max.Value))
                {
                    issues.Add(new ManifestIssue("MANIFEST_PARAM_BAD_DEFAULT", "Default must be finite and inside Min and Max.", here));
                }

                if (p.IsInteger && Math.Abs(d - Math.Round(d)) > 1e-9)
                {
                    issues.Add(new ManifestIssue("MANIFEST_PARAM_BAD_DEFAULT", "Default must be a whole number for an integer parameter.", here));
                }
            }

            ValidateAliases(issues, p.Aliases, here + "/Aliases");
        }

        return seen;
    }

    private static void ValidateComponents(
        List<ManifestIssue> issues,
        IReadOnlyList<ManifestComponent> components,
        HashSet<string> inputKeys,
        string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            var here = root + "/Components[" + i + "]";

            if (string.IsNullOrWhiteSpace(c.Key) || !Matches(KeyPattern, c.Key))
            {
                issues.Add(new ManifestIssue("MANIFEST_BAD_KEY", "Component key must start with a letter and contain only letters, digits or underscore.", here));
            }
            else if (!seen.Add(c.Key))
            {
                issues.Add(new ManifestIssue("MANIFEST_DUPLICATE_KEY", "Duplicate component key '" + c.Key + "'.", here));
            }

            Require(issues, c.Title, "MANIFEST_COMPONENT_NO_TITLE", "Component Title is required.", here);

            foreach (var key in c.ParameterKeys)
            {
                if (!inputKeys.Contains(key))
                {
                    issues.Add(new ManifestIssue(
                        "MANIFEST_COMPONENT_UNKNOWN_PARAM",
                        "Component '" + c.Key + "' references unknown input '" + key + "'.",
                        here));
                }
            }

            ValidateAliases(issues, c.Aliases, here + "/Aliases");
        }
    }

    private static void ValidateAliases(List<ManifestIssue> issues, IReadOnlyList<string> aliases, string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                issues.Add(new ManifestIssue("MANIFEST_BAD_ALIAS", "Aliases must not be empty.", path));
                continue;
            }

            if (!seen.Add(alias.Trim()))
            {
                issues.Add(new ManifestIssue("MANIFEST_DUPLICATE_ALIAS", "Duplicate alias '" + alias.Trim() + "'.", path));
            }
        }
    }

    private static void Require(List<ManifestIssue> issues, string? value, string code, string message, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new ManifestIssue(code, message, path));
        }
    }

    private static bool Matches(Regex regex, string value)
    {
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}