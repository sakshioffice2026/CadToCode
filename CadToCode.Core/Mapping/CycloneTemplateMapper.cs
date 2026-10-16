using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;

namespace CadToCode.Core.Mapping;

/// <summary>
/// Maps <see cref="CycloneCalculator"/> output (SI metres) to the <c>builtin-cyclone-separator</c>
/// template parameters (millimetres). Keys that are a plain scale come from the manifest
/// <c>parameterMappings</c>. Two template parameters need a conversion and are derived here:
/// <c>Radius</c> (the template uses the outer radius) and <c>InletDiameter</c>
/// (the template inlet is round, the calculator sizes a rectangular inlet).
/// </summary>
public sealed class CycloneTemplateMapper : ICalculatorTemplateMapper
{
    public const string Id = "cyclone-template-mapper";

    public const string CycloneTemplateKey = "builtin-cyclone-separator";

    private const double MetresToMillimetres = 1000.0;

    private static readonly string[] TemplateParameterKeys =
    {
        "Radius", "Wall", "BodyHeight", "ConeHeight", "DustDiameter", "DustLength",
        "ExitDiameter", "ExitHeight", "FinderDepth", "InletDiameter", "InletLength"
    };

    public string MapperId => Id;

    public EquipmentType EquipmentType => EquipmentType.Cyclone;

    public IReadOnlyList<string> TemplateKeys { get; } = new[] { CycloneTemplateKey };

    public TemplateMappingResult Map(
        CalculationResult result,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double>? userSupplied = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var guard = Guard(result, manifest);

        if (guard.Count > 0)
            return TemplateMappingResult.Failure(Id, EquipmentType.Cyclone, guard, CycloneTemplateKey);

        var calculated = result.AsDictionary();
        var builder = TemplateMappingResult.Create(Id, EquipmentType.Cyclone, CycloneTemplateKey);

        foreach (var map in manifest.ParameterMappings)
        {
            if (!TemplateParameterKeys.Contains(map.TargetKey, StringComparer.OrdinalIgnoreCase))
            {
                builder.AddIssue(
                    MappingIssueCodes.ManifestMismatch,
                    "Manifest '" + manifest.Id + "' maps to '" + map.TargetKey + "', which is not a parameter of template '" + CycloneTemplateKey + "'.",
                    map.TargetKey);
            }
        }

        DeclarativeMapping.Apply(builder, manifest, calculated);

        DeriveOuterRadius(builder, calculated);
        DeriveRoundInlet(builder, calculated);

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

        if (result.EquipmentType != EquipmentType.Cyclone)
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.WrongEquipment,
                "The cyclone mapper cannot map a " + result.EquipmentType + " result."));
            return issues;
        }

        if (!string.Equals(manifest.MapperId, Id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.ManifestMismatch,
                "Manifest '" + manifest.Id + "' does not declare mapper '" + Id + "'."));
        }

        if (!manifest.TemplateKeys.Contains(CycloneTemplateKey, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.ManifestMismatch,
                "Manifest '" + manifest.Id + "' does not list template '" + CycloneTemplateKey + "'."));
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
        var barrelKey = CycloneCalculator.Keys.BarrelDiameterM;
        var wallKey = CycloneCalculator.Keys.WallThicknessUsedM;

        if (!calculated.TryGetValue(barrelKey, out var barrel) || !calculated.TryGetValue(wallKey, out var wall))
        {
            builder.AddIssue(
                MappingIssueCodes.SourceMissing,
                "Radius needs '" + barrelKey + "' and '" + wallKey + "', which were not both produced.",
                "Radius");
            return;
        }

        if (barrel <= 0 || wall <= 0)
        {
            builder.AddIssue(
                MappingIssueCodes.InputInvalid,
                "'" + barrelKey + "' and '" + wallKey + "' must be greater than zero to derive Radius.",
                "Radius");
            return;
        }

        builder.AddValue(
            "Radius",
            DeclarativeMapping.RoundValue((barrel / 2.0 + wall) * MetresToMillimetres),
            "mm",
            ValueOrigin.Calculated,
            new[] { barrelKey, wallKey },
            "outer radius = (barrel_diameter_m / 2 + wall_thickness_m) x 1000");
    }

    private static void DeriveRoundInlet(
        TemplateMappingResult.Builder builder,
        IReadOnlyDictionary<string, double> calculated)
    {
        var heightKey = CycloneCalculator.Keys.InletHeightM;
        var widthKey = CycloneCalculator.Keys.InletWidthM;

        if (!calculated.TryGetValue(heightKey, out var height) || !calculated.TryGetValue(widthKey, out var width))
        {
            builder.AddIssue(
                MappingIssueCodes.SourceMissing,
                "InletDiameter needs '" + heightKey + "' and '" + widthKey + "', which were not both produced.",
                "InletDiameter");
            return;
        }

        if (height <= 0 || width <= 0)
        {
            builder.AddIssue(
                MappingIssueCodes.InputInvalid,
                "'" + heightKey + "' and '" + widthKey + "' must be greater than zero to derive InletDiameter.",
                "InletDiameter");
            return;
        }

        var equivalentMm = DeclarativeMapping.RoundValue(Math.Sqrt(4.0 * height * width / Math.PI) * MetresToMillimetres);

        builder.AddValue(
            "InletDiameter",
            equivalentMm,
            "mm",
            ValueOrigin.Calculated,
            new[] { heightKey, widthKey },
            "equal-area round inlet = sqrt(4 x inlet_height_m x inlet_width_m / pi) x 1000");

        builder.AddIssue(
            MappingIssueCodes.DerivedValue,
            "The sized rectangular inlet "
            + Format(height * MetresToMillimetres) + " x " + Format(width * MetresToMillimetres)
            + " mm was converted to a round inlet of equal flow area, diameter "
            + Format(equivalentMm) + " mm, because the template inlet is round.",
            "InletDiameter",
            isBlocking: false);
    }

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
