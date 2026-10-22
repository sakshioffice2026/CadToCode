using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;

namespace CadToCode.Core.Mapping;

/// <summary>
/// Maps <see cref="CentrifugalFanCalculator"/> output to the SI keys the centrifugal fan script composer consumes.
/// The fan is composed directly from SI values, not from a catalog template, so no template key is targeted.
/// Calculator keys are renamed where the composer uses a different name (for example
/// <c>casing_wall_thickness_m</c> to <c>casing_wall_m</c>). Blade thickness is produced by the calculator;
/// the user may override it. No other user input is required and none is ever defaulted.
/// </summary>
public sealed class CentrifugalFanTemplateMapper : ICalculatorTemplateMapper
{
    public const string Id = "centrifugal-fan-script-mapper";

    public const string ImpellerOuterDiameterM = "impeller_outer_diameter_m";
    public const string ImpellerInletDiameterM = "impeller_inlet_diameter_m";
    public const string BladeWidthM = "blade_width_m";
    public const string BladeInletWidthM = "blade_inlet_width_m";
    public const string HubDiameterM = "hub_diameter_m";
    public const string BladeCount = "blade_count";
    public const string BladeInletAngleDeg = "blade_inlet_angle_deg";
    public const string BladeOutletAngleDeg = "blade_outlet_angle_deg";
    public const string BladeThicknessM = "blade_thickness_m";
    public const string BladeProfileCode = "blade_profile_code";
    public const string PlateThicknessM = "plate_thickness_m";
    public const string CasingBaseDiameterM = "casing_base_diameter_m";
    public const string CasingWidthM = "casing_width_m";
    public const string CasingWallM = "casing_wall_m";
    public const string OutletHeightM = "outlet_height_m";
    public const string CasingInletDiameterM = "casing_inlet_diameter_m";
    public const string CutoffClearanceM = "cutoff_clearance_m";

    private const int SiDecimals = 6;

    private static readonly (string TargetKey, string SourceKey, string Unit, bool IsInteger)[] CalculatedMappings =
    {
        (ImpellerOuterDiameterM, CentrifugalFanCalculator.Keys.ImpellerOuterDiameterM, "m", false),
        (ImpellerInletDiameterM, CentrifugalFanCalculator.Keys.ImpellerInletDiameterM, "m", false),
        (BladeWidthM, CentrifugalFanCalculator.Keys.ImpellerWidthM, "m", false),
        (BladeInletWidthM, CentrifugalFanCalculator.Keys.BladeInletWidthM, "m", false),
        (HubDiameterM, CentrifugalFanCalculator.Keys.HubDiameterM, "m", false),
        (BladeCount, CentrifugalFanCalculator.Keys.BladeCount, "-", true),
        (BladeInletAngleDeg, CentrifugalFanCalculator.Keys.BladeInletAngleDeg, "deg", false),
        (BladeOutletAngleDeg, CentrifugalFanCalculator.Keys.BladeOutletAngleDeg, "deg", false),
        (BladeThicknessM, CentrifugalFanCalculator.Keys.BladeThicknessM, "m", false),
        (BladeProfileCode, CentrifugalFanCalculator.Keys.BladeProfileCode, "-", true),
        (PlateThicknessM, CentrifugalFanCalculator.Keys.PlateThicknessM, "m", false),
        (CasingBaseDiameterM, CentrifugalFanCalculator.Keys.CasingBaseDiameterM, "m", false),
        (CasingWidthM, CentrifugalFanCalculator.Keys.CasingWidthM, "m", false),
        (CasingWallM, CentrifugalFanCalculator.Keys.CasingWallThicknessM, "m", false),
        (OutletHeightM, CentrifugalFanCalculator.Keys.OutletHeightM, "m", false),
        (CasingInletDiameterM, CentrifugalFanCalculator.Keys.InletEyeDiameterM, "m", false),
        (CutoffClearanceM, CentrifugalFanCalculator.Keys.CasingCutoffClearanceM, "m", false)
    };

    public string MapperId => Id;

    public EquipmentType EquipmentType => EquipmentType.CentrifugalFan;

    public IReadOnlyList<string> TemplateKeys { get; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredUserInputKeys { get; } = Array.Empty<string>();

    public TemplateMappingResult Map(
        CalculationResult result,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double>? userSupplied = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var guard = Guard(result, manifest);

        if (guard.Count > 0)
            return TemplateMappingResult.Failure(Id, EquipmentType.CentrifugalFan, guard);

        var calculated = result.AsDictionary();
        var builder = TemplateMappingResult.Create(Id, EquipmentType.CentrifugalFan);

        var supplied = NormalizeSupplied(userSupplied);
        var hasThicknessOverride = TryValidateThicknessOverride(builder, supplied, calculated, out var thicknessOverride);

        foreach (var (targetKey, sourceKey, unit, isInteger) in CalculatedMappings)
        {
            if (targetKey == BladeThicknessM && hasThicknessOverride)
                continue;

            if (!calculated.TryGetValue(sourceKey, out var value))
            {
                builder.AddIssue(
                    MappingIssueCodes.SourceMissing,
                    "Calculated value '" + sourceKey + "' is required for '" + targetKey + "' but was not produced.",
                    sourceKey);
                continue;
            }

            var mapped = isInteger
                ? Math.Round(value, MidpointRounding.AwayFromZero)
                : Math.Round(value, SiDecimals, MidpointRounding.AwayFromZero);

            builder.AddValue(
                targetKey,
                mapped,
                unit,
                ValueOrigin.Calculated,
                new[] { sourceKey },
                sourceKey == targetKey ? "copied unchanged" : "renamed from " + sourceKey);
        }

        if (hasThicknessOverride)
        {
            builder.AddValue(
                BladeThicknessM,
                Math.Round(thicknessOverride, SiDecimals, MidpointRounding.AwayFromZero),
                "m",
                ValueOrigin.User,
                new[] { BladeThicknessM },
                "user supplied, overrides the calculated blade thickness");
        }

        return builder.Build();
    }

    private static List<MappingIssue> Guard(CalculationResult result, EquipmentManifest manifest)
    {
        var issues = new List<MappingIssue>();

        if (result.EquipmentType != EquipmentType.CentrifugalFan)
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.WrongEquipment,
                "The centrifugal fan mapper cannot map a " + result.EquipmentType + " result."));
            return issues;
        }

        if (!string.Equals(manifest.MapperId, Id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.ManifestMismatch,
                "Manifest '" + manifest.Id + "' does not declare mapper '" + Id + "'."));
        }

        if (!result.IsCadReady)
        {
            var blockers = result.GetCadBlockers();

            foreach (var blocker in blockers)
            {
                issues.Add(new MappingIssue(MappingIssueCodes.NotCadReady, blocker));
            }

            if (blockers.Count == 0)
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

    private static Dictionary<string, double> NormalizeSupplied(IReadOnlyDictionary<string, double>? userSupplied)
    {
        var supplied = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (userSupplied is not null)
        {
            foreach (var pair in userSupplied)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    supplied[pair.Key.Trim()] = pair.Value;
            }
        }

        return supplied;
    }

    private static bool TryValidateThicknessOverride(
        TemplateMappingResult.Builder builder,
        IReadOnlyDictionary<string, double> supplied,
        IReadOnlyDictionary<string, double> calculated,
        out double thickness)
    {
        thickness = 0.0;

        if (!supplied.TryGetValue(BladeThicknessM, out var value))
            return false;

        if (!double.IsFinite(value))
        {
            builder.AddIssue(MappingIssueCodes.InputInvalid, "Blade thickness (m) must be a finite number.", BladeThicknessM);
            return false;
        }

        if (value <= 0.0)
        {
            builder.AddIssue(MappingIssueCodes.InputInvalid, "Blade thickness (m) must be greater than zero.", BladeThicknessM);
            return false;
        }

        if (calculated.TryGetValue(CentrifugalFanCalculator.Keys.ImpellerWidthM, out var bladeWidth)
            && value >= bladeWidth)
        {
            builder.AddIssue(
                MappingIssueCodes.InputInvalid,
                "Blade thickness must be smaller than the blade width ("
                + bladeWidth.ToString("0.####", CultureInfo.InvariantCulture) + " m).",
                BladeThicknessM);
            return false;
        }

        thickness = value;
        return true;
    }
}