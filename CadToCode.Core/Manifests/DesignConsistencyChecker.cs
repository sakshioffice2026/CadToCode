namespace CadToCode.Core.Manifests;

public enum ConsistencySeverity
{
    Warning = 0,
    Error = 1
}

public sealed record ConsistencyIssue(ConsistencySeverity Severity, string Code, string Message, string Path);

/// <summary>
/// Plain-data description of what exists in the running system. The caller gathers it, so Core never
/// depends on the LLM project, the template catalog or any calculator implementation.
/// </summary>
public sealed class ConsistencyInputs
{
    public IReadOnlyList<EquipmentManifest> Manifests { get; init; } = Array.Empty<EquipmentManifest>();

    /// <summary>Ids of registered calculators (by convention the EquipmentType name, e.g. "AxialFan").</summary>
    public IReadOnlyCollection<string> CalculatorIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of loaded rulepacks.</summary>
    public IReadOnlyCollection<string> RulePackIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of registered calculator-to-template mappers.</summary>
    public IReadOnlyCollection<string> MapperIds { get; init; } = Array.Empty<string>();

    /// <summary>Template key to the parameter keys that template script requires.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> TemplateParameterKeys { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>();

    /// <summary>
    /// Manifest id to the template parameter keys its code-based mapper produces from calculator outputs.
    /// Keys declared in <see cref="EquipmentManifest.ParameterMappings"/> are counted automatically and
    /// do not need to be repeated here.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> MapperTargetKeys { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>();
}

public sealed class ConsistencyReport
{
    public ConsistencyReport(IReadOnlyList<ConsistencyIssue> issues)
    {
        Issues = issues;
    }

    public IReadOnlyList<ConsistencyIssue> Issues { get; }

    public IEnumerable<ConsistencyIssue> Errors => Issues.Where(i => i.Severity == ConsistencySeverity.Error);

    public IEnumerable<ConsistencyIssue> Warnings => Issues.Where(i => i.Severity == ConsistencySeverity.Warning);

    public bool HasErrors => Issues.Any(i => i.Severity == ConsistencySeverity.Error);

    /// <summary>Throws with every error listed. Intended for application startup.</summary>
    public void ThrowIfInvalid()
    {
        var errors = Errors.ToList();

        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Engineering configuration is inconsistent:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(e => "- [" + e.Code + "] " + e.Path + ": " + e.Message)));
    }
}

/// <summary>
/// Detects drift between manifests, calculators, rulepacks, CAD templates and mappers before any
/// user request runs. Pure and deterministic.
/// </summary>
public static class DesignConsistencyChecker
{
    private const double ScaleTolerance = 1e-9;

    public static ConsistencyReport Check(ConsistencyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var issues = new List<ConsistencyIssue>();

        var calculators = new HashSet<string>(inputs.CalculatorIds, StringComparer.OrdinalIgnoreCase);
        var rulePacks = new HashSet<string>(inputs.RulePackIds, StringComparer.OrdinalIgnoreCase);
        var mappers = new HashSet<string>(inputs.MapperIds, StringComparer.OrdinalIgnoreCase);

        var templates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in inputs.TemplateParameterKeys)
        {
            templates[pair.Key] = new HashSet<string>(pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        var usedCalculators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedRulePacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedMappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in inputs.Manifests)
        {
            CheckManifest(
                manifest,
                inputs,
                calculators,
                rulePacks,
                mappers,
                templates,
                usedCalculators,
                usedRulePacks,
                usedTemplates,
                usedMappers,
                issues);
        }

        foreach (var calculator in calculators.Where(c => !usedCalculators.Contains(c)))
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_CALCULATOR_UNUSED",
                "Calculator is registered but no manifest uses it.",
                calculator));
        }

        foreach (var pack in rulePacks.Where(p => !usedRulePacks.Contains(p)))
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_RULEPACK_UNUSED",
                "Rulepack is loaded but no manifest uses it.",
                pack));
        }

        foreach (var mapper in mappers.Where(m => !usedMappers.Contains(m)))
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_MAPPER_UNUSED",
                "Mapper is registered but no manifest uses it.",
                mapper));
        }

        foreach (var template in templates.Keys.Where(t => !usedTemplates.Contains(t)))
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_TEMPLATE_UNREFERENCED",
                "CAD template exists but no manifest references it, so it can never be reached from an equipment definition.",
                template));
        }

        return new ConsistencyReport(issues);
    }

    private static void CheckManifest(
        EquipmentManifest manifest,
        ConsistencyInputs inputs,
        HashSet<string> calculators,
        HashSet<string> rulePacks,
        HashSet<string> mappers,
        Dictionary<string, HashSet<string>> templates,
        HashSet<string> usedCalculators,
        HashSet<string> usedRulePacks,
        HashSet<string> usedTemplates,
        HashSet<string> usedMappers,
        List<ConsistencyIssue> issues)
    {
        var path = manifest.Id;

        if (!string.IsNullOrWhiteSpace(manifest.CalculatorId))
        {
            usedCalculators.Add(manifest.CalculatorId);

            if (!calculators.Contains(manifest.CalculatorId))
            {
                var severity = manifest.MaxTier == DesignTier.Engineered
                    ? ConsistencySeverity.Error
                    : ConsistencySeverity.Warning;

                issues.Add(new ConsistencyIssue(
                    severity,
                    "CONSISTENCY_CALCULATOR_NOT_REGISTERED",
                    "Calculator '" + manifest.CalculatorId + "' is not registered.",
                    path));
            }
        }

        foreach (var packId in manifest.RulePackIds)
        {
            usedRulePacks.Add(packId);

            if (!rulePacks.Contains(packId))
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Error,
                    "CONSISTENCY_RULEPACK_NOT_FOUND",
                    "Rulepack '" + packId + "' is not loaded.",
                    path));
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.MapperId))
        {
            usedMappers.Add(manifest.MapperId);

            if (!mappers.Contains(manifest.MapperId))
            {
                var severity = manifest.MaxTier == DesignTier.Engineered
                    ? ConsistencySeverity.Error
                    : ConsistencySeverity.Warning;

                issues.Add(new ConsistencyIssue(
                    severity,
                    "CONSISTENCY_MAPPER_NOT_REGISTERED",
                    "Mapper '" + manifest.MapperId + "' is not registered.",
                    path));
            }
        }

        var inputKeys = new HashSet<string>(manifest.Inputs.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

        var declaredTargets = new HashSet<string>(
            manifest.ParameterMappings
                .Select(m => m.TargetKey)
                .Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);

        var mapperKeys = new HashSet<string>(declaredTargets, StringComparer.OrdinalIgnoreCase);

        if (inputs.MapperTargetKeys.TryGetValue(manifest.Id, out var mapped))
        {
            foreach (var key in mapped)
            {
                mapperKeys.Add(key);
            }
        }

        var foundTemplateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var templateKey in manifest.TemplateKeys)
        {
            usedTemplates.Add(templateKey);

            if (!templates.TryGetValue(templateKey, out var templateKeys))
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Error,
                    "CONSISTENCY_TEMPLATE_NOT_FOUND",
                    "Template '" + templateKey + "' has no parameter schema.",
                    path));
                continue;
            }

            foundTemplateKeys.UnionWith(templateKeys);

            var uncovered = templateKeys
                .Where(k => !inputKeys.Contains(k) && !mapperKeys.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (uncovered.Count > 0)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Error,
                    "CONSISTENCY_TEMPLATE_PARAM_UNCOVERED",
                    "Template '" + templateKey + "' needs parameters that neither the manifest inputs nor the mapper provide: "
                    + string.Join(", ", uncovered) + ".",
                    path));
            }

            var unused = inputKeys
                .Where(k => !templateKeys.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (manifest.MaxTier != DesignTier.Engineered && unused.Count > 0)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Warning,
                    "CONSISTENCY_INPUT_NOT_IN_TEMPLATE",
                    "Inputs not used by template '" + templateKey + "': " + string.Join(", ", unused) + ".",
                    path));
            }
        }

        if (manifest.MaxTier == DesignTier.ParametricGeneric && manifest.TemplateKeys.Count == 0)
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_NO_TEMPLATE",
                "Manifest has no template, so no CAD script can be produced for it.",
                path));
        }

        CheckMappings(manifest, declaredTargets, foundTemplateKeys, issues);
        CheckParts(manifest, issues);

        foreach (var component in manifest.Components)
        {
            if (component.ParameterKeys.Count == 0)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Warning,
                    "CONSISTENCY_COMPONENT_NO_PARAMS",
                    "Component '" + component.Key + "' lists no parameters, so it cannot be checked for completeness.",
                    path));
            }
        }
    }

    private static void CheckMappings(
        EquipmentManifest manifest,
        HashSet<string> declaredTargets,
        HashSet<string> foundTemplateKeys,
        List<ConsistencyIssue> issues)
    {
        if (manifest.ParameterMappings.Count == 0)
        {
            return;
        }

        var path = manifest.Id;

        if (foundTemplateKeys.Count > 0)
        {
            var orphaned = declaredTargets
                .Where(t => !foundTemplateKeys.Contains(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (orphaned.Count > 0)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Warning,
                    "CONSISTENCY_MAPPING_TARGET_UNUSED",
                    "Mapped keys that no template of this manifest uses: " + string.Join(", ", orphaned) + ".",
                    path));
            }
        }

        var units = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in manifest.Outputs.Concat(manifest.Inputs))
        {
            if (!string.IsNullOrWhiteSpace(parameter.Key))
            {
                units.TryAdd(parameter.Key, parameter.Unit);
            }
        }

        foreach (var map in manifest.ParameterMappings)
        {
            if (string.IsNullOrWhiteSpace(map.SourceKey) || !units.TryGetValue(map.SourceKey, out var unit))
            {
                continue;
            }

            var expected = ExpectedScale(unit);

            if (expected.HasValue && Math.Abs(map.Scale - expected.Value) > ScaleTolerance)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Warning,
                    "CONSISTENCY_MAPPING_SCALE_SUSPECT",
                    "Mapping '" + map.SourceKey + "' to '" + map.TargetKey + "' uses scale "
                    + map.Scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + " but unit '" + unit + "' normally needs "
                    + expected.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ".",
                    path));
            }
        }
    }

    private static void CheckParts(EquipmentManifest manifest, List<ConsistencyIssue> issues)
    {
        var path = manifest.Id;

        if (manifest.Parts.Count == 0)
        {
            if (manifest.TemplateKeys.Count > 0)
            {
                issues.Add(new ConsistencyIssue(
                    ConsistencySeverity.Warning,
                    "CONSISTENCY_NO_PARTS",
                    "Manifest has templates but declares no parts, so only a single-body export is possible.",
                    path));
            }

            return;
        }

        var covered = new HashSet<string>(
            manifest.Parts.SelectMany(p => p.ComponentKeys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var component in manifest.Components.Where(c => !covered.Contains(c.Key)))
        {
            issues.Add(new ConsistencyIssue(
                ConsistencySeverity.Warning,
                "CONSISTENCY_COMPONENT_NOT_IN_PART",
                "Component '" + component.Key + "' is not built into any part, so it would be missing from the export.",
                path));
        }
    }

    /// <summary>Scale a unit normally needs when moving from SI to template millimetres. Null when unknown.</summary>
    private static double? ExpectedScale(string? unit)
    {
        var normalized = (unit ?? string.Empty).Trim().ToLowerInvariant();

        return normalized switch
        {
            "m" => 1000.0,
            "mm" => 1.0,
            "deg" => 1.0,
            "rad" => 1.0,
            "-" => 1.0,
            "" => 1.0,
            _ => null
        };
    }
}