using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;

namespace CadToCode.Core.Mapping;

/// <summary>
/// Maps <see cref="AxialFanCalculator"/> output to the SI keys the axial fan script composer consumes.
/// The fan is composed directly from SI values, not from a catalog template, so no template key is targeted.
/// Calculator keys are renamed (<c>casing_wall_thickness_m</c> to <c>casing_wall_m</c>). Blade chord,
/// blade thickness and blade pitch are not produced by the calculator; they must be supplied by the user
/// and are never defaulted.
/// </summary>
public sealed class AxialFanTemplateMapper : ICalculatorTemplateMapper
{
    public const string Id = "axial-fan-script-mapper";

    public const string TipDiameterM = "tip_diameter_m";
    public const string HubDiameterM = "hub_diameter_m";
    public const string TipClearanceM = "tip_clearance_m";
    public const string CasingWallM = "casing_wall_m";
    public const string CasingLengthM = "casing_length_m";
    public const string BladeCount = "blade_count";
    public const string BladeChordM = "blade_chord_m";
    public const string BladeThicknessM = "blade_thickness_m";
    public const string BladePitchDeg = "blade_pitch_deg";

    private const double SiDecimals = 6;

    private static readonly (string TargetKey, string SourceKey, string Unit)[] CalculatedMappings =
    {
        (TipDiameterM, AxialFanCalculator.Keys.TipDiameterM, "m"),
        (HubDiameterM, AxialFanCalculator.Keys.HubDiameterM, "m"),
        (TipClearanceM, AxialFanCalculator.Keys.TipClearanceM, "m"),
        (CasingWallM, AxialFanCalculator.Keys.CasingWallThicknessM, "m"),
        (CasingLengthM, AxialFanCalculator.Keys.CasingLengthM, "m"),
        (BladeCount, AxialFanCalculator.Keys.BladeCount, "-")
    };

    public string MapperId => Id;

    public EquipmentType EquipmentType => EquipmentType.AxialFan;

    public IReadOnlyList<string> TemplateKeys { get; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredUserInputKeys { get; } = new[] { BladeChordM, BladeThicknessM, BladePitchDeg };

    public TemplateMappingResult Map(
        CalculationResult result,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double>? userSupplied = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        var guard = Guard(result, manifest);

        if (guard.Count > 0)
            return TemplateMappingResult.Failure(Id, EquipmentType.AxialFan, guard);

        var calculated = result.AsDictionary();
        var builder = TemplateMappingResult.Create(Id, EquipmentType.AxialFan);

        foreach (var (targetKey, sourceKey, unit) in CalculatedMappings)
        {
            if (!calculated.TryGetValue(sourceKey, out var value))
            {
                builder.AddIssue(
                    MappingIssueCodes.SourceMissing,
                    "Calculated value '" + sourceKey + "' is required for '" + targetKey + "' but was not produced.",
                    sourceKey);
                continue;
            }

            var mapped = targetKey == BladeCount ? Math.Round(value) : Math.Round(value, (int)SiDecimals, MidpointRounding.AwayFromZero);

            builder.AddValue(
                targetKey,
                mapped,
                unit,
                ValueOrigin.Calculated,
                new[] { sourceKey },
                sourceKey == targetKey ? "copied unchanged" : "renamed from " + sourceKey);
        }

        AddUserValues(builder, userSupplied);

        return builder.Build();
    }

    private static List<MappingIssue> Guard(CalculationResult result, EquipmentManifest manifest)
    {
        var issues = new List<MappingIssue>();

        if (result.EquipmentType != EquipmentType.AxialFan)
        {
            issues.Add(new MappingIssue(
                MappingIssueCodes.WrongEquipment,
                "The axial fan mapper cannot map a " + result.EquipmentType + " result."));
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

    private static void AddUserValues(
        TemplateMappingResult.Builder builder,
        IReadOnlyDictionary<string, double>? userSupplied)
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

        var chordOk = TryAddUser(builder, supplied, BladeChordM, "m", "Blade chord (m)", positiveOnly: true);
        var thicknessOk = TryAddUser(builder, supplied, BladeThicknessM, "m", "Blade thickness (m)", positiveOnly: true);
        var pitchOk = TryAddUser(builder, supplied, BladePitchDeg, "deg", "Blade pitch (deg)", positiveOnly: false);

        if (pitchOk && builder.HasValue(BladePitchDeg))
        {
            var pitch = supplied[BladePitchDeg];

            if (pitch <= -90.0 || pitch >= 90.0)
            {
                builder.AddIssue(
                    MappingIssueCodes.InputInvalid,
                    "Blade pitch must be between -90 and 90 degrees (exclusive); got "
                    + pitch.ToString("0.###", CultureInfo.InvariantCulture) + ".",
                    BladePitchDeg);
            }
        }

        if (chordOk && thicknessOk && supplied[BladeThicknessM] >= supplied[BladeChordM])
        {
            builder.AddIssue(
                MappingIssueCodes.InputInvalid,
                "Blade thickness must be smaller than blade chord.",
                BladeThicknessM);
        }
    }

    private static bool TryAddUser(
        TemplateMappingResult.Builder builder,
        IReadOnlyDictionary<string, double> supplied,
        string key,
        string unit,
        string label,
        bool positiveOnly)
    {
        if (!supplied.TryGetValue(key, out var value))
        {
            builder.AddIssue(
                MappingIssueCodes.InputMissing,
                label + " is not produced by the calculator and must be provided.",
                key);
            return false;
        }

        if (!double.IsFinite(value))
        {
            builder.AddIssue(MappingIssueCodes.InputInvalid, label + " must be a finite number.", key);
            return false;
        }

        if (positiveOnly && value <= 0)
        {
            builder.AddIssue(MappingIssueCodes.InputInvalid, label + " must be greater than zero.", key);
            return false;
        }

        builder.AddValue(
            key,
            Math.Round(value, (int)SiDecimals, MidpointRounding.AwayFromZero),
            unit,
            ValueOrigin.User,
            new[] { key },
            "user supplied, not produced by the calculator");

        return true;
    }
}
