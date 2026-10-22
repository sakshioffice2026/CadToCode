using System;
using System.Collections.Generic;
using System.Linq;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;

namespace CadToCode.Core.Mapping;

/// <summary>
/// Maps <see cref="WetScrubberCalculator"/> output (SI metres) to the <c>builtin-wet-scrubber</c>
/// template parameters (millimetres). Keys that are a plain scale come from the manifest
/// <c>parameterMappings</c>. One template parameter needs a conversion and is derived here:
/// <c>Radius</c> (the template uses the outer radius, the calculator sizes the inside diameter).
/// </summary>
public sealed class WetScrubberTemplateMapper : ICalculatorTemplateMapper
{
    public const string Id = "wet-scrubber-template-mapper";

    public const string WetScrubberTemplateKey = "builtin-wet-scrubber";

    private const double MetresToMillimetres = 1000.0;

    private static readonly string[] TemplateParameterKeys =
    {
        "Radius", "Wall", "ShellHeight", "ConeHeight", "DrainDiameter", "DrainLength",
        "StackDiameter", "StackHeight", "InletDiameter", "InletLength", "InletZ"
    };

    public string MapperId => Id;

    public EquipmentType EquipmentType => EquipmentType.WetScrubber;

    public IReadOnlyList<string> TemplateKeys { get; } = new[] { WetScrubberTemplateKey };

    public TemplateMappingResult Map(
        CalculationResult result,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double>? userSupplied = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var guard = Guard(result, manifest);

        if (guard.Count > 0)
            return TemplateMappingResult.Failure(Id, EquipmentType.WetScrubber, guard, WetScrubberTemplateKey);

        var calculated = result.AsDictionary();
        var builder = TemplateMappingResult.Create(Id, EquipmentType.WetScrubber, WetScrubberTemplateKey);

        foreach (var map in manifest.ParameterMappings)
        {
            if (!TemplateParameterKeys.Contains(map.TargetKey, StringComparer.OrdinalIgnoreCase))
            {
                builder.AddIssue(
                    MappingIssueCodes.ManifestMismatch,
                    "Manifest '" + manifest.Id + "' maps to '" + map.TargetKey + "', which is not a parameter of template '" + WetScrubberTemplateKey + "'.",
                    map.TargetKey);
            }
        }

        DeclarativeMapping.Apply(builder, manifest, calculated);

        DeriveOuterRadius(builder, calculated);

        foreach (var key in TemplateParameterKeys)
        {
            if (!builder.HasValue(key))
            {
                builder.AddIssue(
                    MappingIssueCodes.SourceMissing,
                    "Template parameter '" + key + "' was not mapped. Check the manifest parameterMappings for '" + manifest.Id + "'.",
                    key);
            }
        }

        return builder.Build();
    }

    private static List<MappingIssue> Guard(CalculationResult result, EquipmentManifest manifest)
    {
        var issues = new List<MappingIssue>();

        if (result.EquipmentType != EquipmentType.WetScrubber)
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.WrongEquipment,
                "The wet scrubber mapper cannot map a " + result.EquipmentType + " result."));
            return issues;
        }

        if (!string.Equals(manifest.MapperId, Id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.ManifestMismatch,
                "Manifest '" + manifest.Id + "' does not declare mapper '" + Id + "'."));
        }

        if (!manifest.TemplateKeys.Contains(WetScrubberTemplateKey, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.ManifestMismatch,
                "Manifest '" + manifest.Id + "' does not list template '" + WetScrubberTemplateKey + "'."));
        }

        if (!result.IsCadReady)
        {
            foreach (var blocker in result.GetCadBlockers())
            {
                issues.Add(new MappingIssue(MappingIssueCodes.NotCadReady, blocker));
            }

            if (result.GetCadBlockers().Count == 0)
            {
                issues.Add(new MappingIssue(MappingIssueCodes.NotCadReady, "The calculation is not CAD ready."));
            }
        }

        var required = manifest.Outputs.Where(o => o.IsRequired).Select(o => o.Key);

        foreach (var missing in result.MissingKeys(required))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.SourceMissing,
                "The calculation did not produce required value '" + missing + "'.",
                missing));
        }

        return issues;
    }

    private static void DeriveOuterRadius(
        TemplateMappingResult.Builder builder,
        IReadOnlyDictionary<string, double> calculated)
    {
        var diameterKey = WetScrubberCalculator.Keys.VesselInsideDiameterM;
        var wallKey = WetScrubberCalculator.Keys.WallThicknessUsedM;

        if (!calculated.TryGetValue(diameterKey, out var diameter) || !calculated.TryGetValue(wallKey, out var wall))
        {
            builder.AddIssue(
                MappingIssueCodes.SourceMissing,
                "Radius needs '" + diameterKey + "' and '" + wallKey + "', which were not both produced.",
                "Radius");
            return;
        }

        if (diameter <= 0 || wall <= 0)
        {
            builder.AddIssue(
                MappingIssueCodes.InputInvalid,
                "'" + diameterKey + "' and '" + wallKey + "' must be greater than zero to derive Radius.",
                "Radius");
            return;
        }

        builder.AddValue(
            "Radius",
            DeclarativeMapping.RoundValue((diameter / 2.0 + wall) * MetresToMillimetres),
            "mm",
            ValueOrigin.Calculated,
            new[] { diameterKey, wallKey },
            "outer radius = (vessel_inside_diameter_m / 2 + wall_thickness_m) x 1000");
    }
}