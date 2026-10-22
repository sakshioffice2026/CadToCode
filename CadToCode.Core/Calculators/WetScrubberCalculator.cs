using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Domain;
using CadToCode.Core.Rules;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Deterministic sizing of a vertical spray-tower wet scrubber vessel.
/// Vessel diameter follows from gas flow and superficial gas velocity. Spray zone height follows from
/// superficial velocity and gas residence time. Nozzles, cone, drain and stack follow from rulepack ratios.
/// All proportions, limits and constants come from the WetScrubber rule set; nothing is hardcoded here.
/// Removal efficiency is not calculated. No LLM, web, database or UI dependency.
/// </summary>
public sealed class WetScrubberCalculator : IEquipmentCalculator
{
    public const string CalculatorVersion = "1.0.0";

    /// <summary>Calculator id used by the wet-scrubber equipment manifest.</summary>
    public const string Id = "WetScrubber";

    /// <summary>Input and output keys. Match the wet-scrubber equipment manifest.</summary>
    public static class Keys
    {
        // Inputs
        public const string AirflowM3s = "airflow_m3s";
        public const string GasVelocityMs = "gas_velocity_ms";
        public const string GasResidenceTimeS = "gas_residence_time_s";
        public const string InletVelocityMs = "inlet_velocity_ms";
        public const string StackVelocityMs = "stack_velocity_ms";
        public const string LiquidToGasRatioLm3 = "liquid_to_gas_ratio_lm3";
        public const string WallThicknessM = "wall_thickness_m";

        // Outputs (manifest)
        public const string VesselInsideDiameterM = "vessel_inside_diameter_m";
        public const string ShellHeightM = "shell_height_m";
        public const string ConeHeightM = "cone_height_m";
        public const string DrainDiameterM = "drain_diameter_m";
        public const string DrainLengthM = "drain_length_m";
        public const string StackDiameterM = "stack_diameter_m";
        public const string StackHeightM = "stack_height_m";
        public const string InletDiameterM = "inlet_diameter_m";
        public const string InletLengthM = "inlet_length_m";
        public const string InletElevationM = "inlet_elevation_m";
        public const string WallThicknessUsedM = "wall_thickness_m";
        public const string GasVelocityUsedMs = "gas_velocity_used_ms";
        public const string InletVelocityUsedMs = "inlet_velocity_used_ms";
        public const string StackVelocityUsedMs = "stack_velocity_used_ms";
        public const string ResidenceTimeS = "residence_time_s";
        public const string LiquidFlowLs = "liquid_flow_ls";
    }

    /// <summary>Rule keys in wet-scrubber/design-rules.json.</summary>
    private static class RuleKeys
    {
        public const string DesignSuperficialVelocity = "design_superficial_velocity_ms";
        public const string SuperficialVelocityAllowed = "superficial_velocity_allowed_ms";
        public const string DesignResidenceTime = "design_residence_time_s";
        public const string ResidenceTimeAllowed = "residence_time_allowed_s";
        public const string DesignInletVelocity = "design_inlet_velocity_ms";
        public const string InletVelocityAllowed = "inlet_velocity_allowed_ms";
        public const string DesignStackVelocity = "design_stack_velocity_ms";
        public const string StackVelocityAllowed = "stack_velocity_allowed_ms";
        public const string DesignLiquidToGasRatio = "design_liquid_to_gas_ratio_lm3";
        public const string LiquidToGasRatioAllowed = "liquid_to_gas_ratio_allowed_lm3";
        public const string VesselDiameterAllowed = "vessel_diameter_allowed_m";
        public const string AirflowAllowed = "airflow_allowed_m3s";
        public const string InletClearanceRatio = "inlet_clearance_to_inlet_diameter_ratio";
        public const string DisengagementHeightRatio = "disengagement_height_to_diameter_ratio";
        public const string ConeHeightRatio = "cone_height_to_diameter_ratio";
        public const string DrainDiameterRatio = "drain_diameter_to_vessel_diameter_ratio";
        public const string DrainDiameterMin = "drain_diameter_min_m";
        public const string DrainLengthRatio = "drain_length_to_diameter_ratio";
        public const string StackHeightRatio = "stack_height_to_diameter_ratio";
        public const string InletLengthRatio = "inlet_length_to_diameter_ratio";
        public const string WallFraction = "wall_thickness_fraction_of_diameter";
        public const string WallMin = "wall_thickness_min_m";
    }

    // Calculation formula identifiers. Rule-driven values use the design rule ID instead.
    private const string FVesselDiameter = "WSC-F-001";
    private const string FSprayZone = "WSC-F-002";
    private const string FNozzleDiameter = "WSC-F-003";
    private const string FShellHeight = "WSC-F-004";
    private const string FLiquidFlow = "WSC-F-005";
    private const string GeometryCheckId = "WSC-F-006";

    private const string RefVesselDiameter = "Spray tower cross-section from continuity: A = Q / v, D = sqrt(4 * A / pi); Perry's Chemical Engineers' Handbook";
    private const string RefSprayZone = "Spray zone height from superficial gas velocity and gas residence time: H = v * t; Perry's Chemical Engineers' Handbook";
    private const string RefNozzleDiameter = "Nozzle diameter from continuity: d = sqrt(4 * Q / (pi * v))";
    private const string RefShellHeight = "Shell height assembled from inlet clearance, spray zone and disengagement space (rulepack fabrication assumptions)";
    private const string RefLiquidFlow = "Scrubbing liquid flow from liquid-to-gas ratio: L = (L/G) * Q";

    private static readonly string[] OptionalPositiveKeys =
    {
        Keys.GasVelocityMs,
        Keys.GasResidenceTimeS,
        Keys.InletVelocityMs,
        Keys.StackVelocityMs,
        Keys.LiquidToGasRatioLm3,
        Keys.WallThicknessM
    };

    /// <summary>Outputs the equipment manifest marks as required. A result missing any of them is never returned as a success.</summary>
    private static readonly string[] RequiredOutputKeys =
    {
        Keys.VesselInsideDiameterM,
        Keys.ShellHeightM,
        Keys.ConeHeightM,
        Keys.DrainDiameterM,
        Keys.DrainLengthM,
        Keys.StackDiameterM,
        Keys.StackHeightM,
        Keys.InletDiameterM,
        Keys.InletLengthM,
        Keys.InletElevationM,
        Keys.WallThicknessUsedM
    };

    private readonly IDesignRulesProvider _rulesProvider;

    public WetScrubberCalculator(IDesignRulesProvider rulesProvider)
    {
        _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public EquipmentType EquipmentType => EquipmentType.WetScrubber;

    public string CalculatorId => Id;

    public string Version => CalculatorVersion;

    public InputValidationResult Validate(EquipmentInputs inputs)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var issues = new List<InputIssue>();
        var missing = new List<string>();

        if (!inputs.TryGetValue(Keys.AirflowM3s, out var flow))
        {
            missing.Add(Keys.AirflowM3s);
            issues.Add(new InputIssue(
                "INPUT_MISSING",
                "Gas flow (m3/s) is required. Inlet gas volumetric flow rate at operating conditions.",
                Keys.AirflowM3s));
        }
        else if (!double.IsFinite(flow) || flow <= 0.0)
        {
            issues.Add(new InputIssue(
                "INPUT_INVALID",
                "Gas flow must be a positive, finite number (m3/s).",
                Keys.AirflowM3s));
        }

        foreach (var key in OptionalPositiveKeys)
        {
            if (inputs.TryGetValue(key, out var value) && (!double.IsFinite(value) || value <= 0.0))
            {
                issues.Add(new InputIssue(
                    "INPUT_INVALID",
                    DisplayName(key) + " must be a positive, finite number (" + Unit(key) + ").",
                    key));
            }
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            issues.Add(rulesIssue!);
            return InputValidationResult.Invalid(issues, missing);
        }

        CheckEnvelope(inputs, Keys.AirflowM3s, rules!, RuleKeys.AirflowAllowed, issues);
        CheckEnvelope(inputs, Keys.GasVelocityMs, rules!, RuleKeys.SuperficialVelocityAllowed, issues);
        CheckEnvelope(inputs, Keys.GasResidenceTimeS, rules!, RuleKeys.ResidenceTimeAllowed, issues);
        CheckEnvelope(inputs, Keys.InletVelocityMs, rules!, RuleKeys.InletVelocityAllowed, issues);
        CheckEnvelope(inputs, Keys.StackVelocityMs, rules!, RuleKeys.StackVelocityAllowed, issues);
        CheckEnvelope(inputs, Keys.LiquidToGasRatioLm3, rules!, RuleKeys.LiquidToGasRatioAllowed, issues);

        return issues.Count == 0
            ? InputValidationResult.Valid
            : InputValidationResult.Invalid(issues, missing);
    }

    public CalculationResult Calculate(EquipmentInputs inputs)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var validation = Validate(inputs);

        if (!validation.IsValid)
        {
            return CalculationResult.Failure(
                EquipmentType.WetScrubber,
                validation.BlockingIssues.Select(i => i.Message),
                Version,
                calculatorId: Id);
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            return CalculationResult.Failure(EquipmentType.WetScrubber, new[] { rulesIssue!.Message }, Version, calculatorId: Id);
        }

        try
        {
            return Compute(inputs, rules!);
        }
        catch (DesignRuleNotFoundException ex)
        {
            return CalculationResult.Failure(EquipmentType.WetScrubber, new[] { ex.Message }, Version, calculatorId: Id);
        }
        catch (InvalidOperationException ex)
        {
            return CalculationResult.Failure(EquipmentType.WetScrubber, new[] { ex.Message }, Version, calculatorId: Id);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Core calculation
    // ------------------------------------------------------------------------------------------------

    private CalculationResult Compute(EquipmentInputs inputs, DesignRuleSet rules)
    {
        var builder = CalculationResult.Create(EquipmentType.WetScrubber, Version).ForCalculator(Id);

        double q = inputs[Keys.AirflowM3s];

        // 1. Operating values: supplied by the user or taken from the rulepack.
        double v = PickValue(builder, inputs, rules, Keys.GasVelocityMs, RuleKeys.DesignSuperficialVelocity,
            "WSC_VELOCITY_DEFAULTED", "Superficial gas velocity", "m/s", Keys.GasVelocityUsedMs, out _);
        double t = PickValue(builder, inputs, rules, Keys.GasResidenceTimeS, RuleKeys.DesignResidenceTime,
            "WSC_RESIDENCE_TIME_DEFAULTED", "Gas residence time", "s", null, out _);
        double vIn = PickValue(builder, inputs, rules, Keys.InletVelocityMs, RuleKeys.DesignInletVelocity,
            "WSC_INLET_VELOCITY_DEFAULTED", "Inlet nozzle velocity", "m/s", Keys.InletVelocityUsedMs, out _);
        double vStack = PickValue(builder, inputs, rules, Keys.StackVelocityMs, RuleKeys.DesignStackVelocity,
            "WSC_STACK_VELOCITY_DEFAULTED", "Outlet stack velocity", "m/s", Keys.StackVelocityUsedMs, out _);
        double lg = PickValue(builder, inputs, rules, Keys.LiquidToGasRatioLm3, RuleKeys.DesignLiquidToGasRatio,
            "WSC_LIQUID_GAS_RATIO_DEFAULTED", "Liquid-to-gas ratio", "L/m3", null, out _);

        // 2. Vessel diameter from continuity: D = sqrt(4 * Q / (pi * v))
        double d = Math.Sqrt(4.0 * q / (Math.PI * v));

        var diameterAllowed = rules.Get(RuleKeys.VesselDiameterAllowed);

        if (!diameterAllowed.IsWithinRange(d))
        {
            builder.AddWarning(
                "WSC_VESSEL_DIAMETER_OUT_OF_RANGE",
                "Calculated vessel diameter " + Format(d) + " m is outside the supported range " + FormatRange(diameterAllowed)
                + " m. Change the gas flow or superficial velocity to move the design into range.",
                WarningSeverity.Critical,
                Keys.VesselInsideDiameterM,
                diameterAllowed.Id);
        }

        builder.AddValue(Keys.VesselInsideDiameterM, "Vessel inside diameter", d, "m", FVesselDiameter, RefVesselDiameter,
            "D = sqrt(4 * Q / (pi * v))");

        // 3. Spray zone, nozzles and shell height
        var clearanceRule = rules.Get(RuleKeys.InletClearanceRatio);
        var disengageRule = rules.Get(RuleKeys.DisengagementHeightRatio);
        var coneRule = rules.Get(RuleKeys.ConeHeightRatio);
        var stackHeightRule = rules.Get(RuleKeys.StackHeightRatio);
        var inletLengthRule = rules.Get(RuleKeys.InletLengthRatio);

        double clearanceRatio = clearanceRule.Resolve();
        double disengageRatio = disengageRule.Resolve();
        double coneRatio = coneRule.Resolve();
        double stackHeightRatio = stackHeightRule.Resolve();
        double inletLengthRatio = inletLengthRule.Resolve();

        if (clearanceRatio <= 0.0 || disengageRatio < 0.0 || coneRatio <= 0.0 || stackHeightRatio <= 0.0 || inletLengthRatio <= 0.0)
        {
            throw new InvalidOperationException("Wet scrubber proportion ratios in the rulepack must be positive.");
        }

        double sprayHeight = v * t;
        double di = Math.Sqrt(4.0 * q / (Math.PI * vIn));
        double ds = Math.Sqrt(4.0 * q / (Math.PI * vStack));
        double cone = coneRatio * d;
        double clearance = clearanceRatio * di;
        double disengage = disengageRatio * d;
        double shell = clearance + di / 2.0 + sprayHeight + disengage;
        double inletElevation = cone + clearance;

        builder.AddValue(Keys.ResidenceTimeS, "Gas residence time", sprayHeight / v, "s", FSprayZone, RefSprayZone,
            "t = H / v with H = v * t");
        builder.AddValue(Keys.InletDiameterM, "Inlet diameter", di, "m", FNozzleDiameter, RefNozzleDiameter,
            "d = sqrt(4 * Q / (pi * v_inlet))");
        builder.AddValue(Keys.StackDiameterM, "Stack diameter", ds, "m", FNozzleDiameter, RefNozzleDiameter,
            "d = sqrt(4 * Q / (pi * v_stack))");
        builder.AddValue(Keys.ConeHeightM, "Cone height", cone, "m", coneRule.Id, coneRule.SourceReference,
            "cone = ratio * D");
        builder.AddValue(Keys.ShellHeightM, "Shell height", shell, "m", FShellHeight, RefShellHeight,
            "shell = clearance + d_inlet / 2 + v * t + disengagement");
        builder.AddValue(Keys.InletElevationM, "Inlet elevation", inletElevation, "m", clearanceRule.Id, clearanceRule.SourceReference,
            "elevation = cone + ratio * d_inlet");
        builder.AddValue(Keys.StackHeightM, "Stack height", stackHeightRatio * ds, "m", stackHeightRule.Id, stackHeightRule.SourceReference,
            "height = ratio * d_stack");
        builder.AddValue(Keys.InletLengthM, "Inlet length", inletLengthRatio * di, "m", inletLengthRule.Id, inletLengthRule.SourceReference,
            "length = ratio * d_inlet");

        // 3b. Geometry sanity checks
        if (di >= d)
        {
            builder.AddWarning(
                "WSC_INLET_TOO_LARGE",
                "Inlet diameter " + Format(di) + " m is not smaller than the vessel diameter " + Format(d)
                + " m. Raise the inlet nozzle velocity or lower the superficial gas velocity.",
                WarningSeverity.Critical,
                Keys.InletDiameterM,
                GeometryCheckId);
        }

        if (ds >= d)
        {
            builder.AddWarning(
                "WSC_STACK_TOO_LARGE",
                "Stack diameter " + Format(ds) + " m is not smaller than the vessel diameter " + Format(d)
                + " m. Raise the stack velocity or lower the superficial gas velocity.",
                WarningSeverity.Critical,
                Keys.StackDiameterM,
                GeometryCheckId);
        }

        // 4. Drain
        var drainRatioRule = rules.Get(RuleKeys.DrainDiameterRatio);
        var drainMinRule = rules.Get(RuleKeys.DrainDiameterMin);
        var drainLengthRule = rules.Get(RuleKeys.DrainLengthRatio);

        double drainDiameter = Math.Max(drainMinRule.Resolve(), drainRatioRule.Resolve() * d);
        double drainLength = drainLengthRule.Resolve() * drainDiameter;

        if (drainDiameter >= d)
        {
            builder.AddWarning(
                "WSC_DRAIN_TOO_LARGE",
                "Drain diameter " + Format(drainDiameter) + " m is not smaller than the vessel diameter " + Format(d)
                + " m. Check the drain rules in the rulepack.",
                WarningSeverity.Critical,
                Keys.DrainDiameterM,
                GeometryCheckId);
        }

        builder.AddValue(Keys.DrainDiameterM, "Drain diameter", drainDiameter, "m", drainRatioRule.Id, drainRatioRule.SourceReference,
            "drain = max(minimum, ratio * D)");
        builder.AddValue(Keys.DrainLengthM, "Drain length", drainLength, "m", drainLengthRule.Id, drainLengthRule.SourceReference,
            "length = ratio * drain diameter");

        // 5. Wall thickness
        var wallMinRule = rules.Get(RuleKeys.WallMin);
        var wallFractionRule = rules.Get(RuleKeys.WallFraction);
        var wallSupplied = inputs.TryGetValue(Keys.WallThicknessM, out var suppliedWall);
        double wallMin = wallMinRule.Resolve();
        double wall = wallSupplied ? suppliedWall : Math.Max(wallMin, wallFractionRule.Resolve() * d);

        if (wallSupplied && wall < wallMin)
        {
            builder.AddWarning(
                "WSC_WALL_BELOW_MINIMUM",
                "Wall thickness " + Format(wall) + " m is below the minimum " + Format(wallMin) + " m used by this calculator.",
                WarningSeverity.Caution,
                Keys.WallThicknessM,
                wallMinRule.Id);
        }

        if (wall * 2.0 >= d)
        {
            builder.AddWarning(
                "WSC_WALL_TOO_THICK",
                "Wall thickness " + Format(wall) + " m is too large for a vessel diameter of " + Format(d) + " m.",
                WarningSeverity.Critical,
                Keys.WallThicknessM,
                wallFractionRule.Id);
        }

        builder.AddValue(
            Keys.WallThicknessUsedM,
            "Wall thickness used",
            wall,
            "m",
            wallSupplied ? string.Empty : wallFractionRule.Id,
            wallSupplied ? "User-supplied value" : wallFractionRule.SourceReference,
            wallSupplied ? string.Empty : "wall = max(minimum, fraction * D)");

        builder.AddWarning(
            "WSC_PRESSURE_CONTAINMENT_NOT_VERIFIED",
            "Wall thickness is a fabrication assumption. Pressure containment, wind load and structural strength are not verified.",
            WarningSeverity.Info,
            Keys.WallThicknessUsedM,
            wallFractionRule.Id);

        // 6. Scrubbing liquid flow
        builder.AddValue(Keys.LiquidFlowLs, "Scrubbing liquid flow", lg * q, "L/s", FLiquidFlow, RefLiquidFlow,
            "L = (L/G) * Q");

        builder.AddWarning(
            "WSC_PERFORMANCE_NOT_VERIFIED",
            "The vessel is sized from gas velocity and residence time only. Removal efficiency, droplet size, spray nozzle selection, "
            + "mist eliminator performance and pump sizing are not calculated.",
            WarningSeverity.Info,
            null,
            FSprayZone);

        // 7. Contract with the equipment manifest: every required output must exist.
        foreach (var key in RequiredOutputKeys)
        {
            if (!builder.HasValue(key))
            {
                builder.AddError("Required output '" + key + "' was not produced.");
            }
        }

        return builder.Build();
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private bool TryGetRules(out DesignRuleSet? rules, out InputIssue? issue)
    {
        if (_rulesProvider.TryGetRuleSet(EquipmentType.WetScrubber, out var found))
        {
            rules = found;
            issue = null;
            return true;
        }

        rules = null;
        issue = new InputIssue(
            "RULES_MISSING",
            "No design rule set is loaded for WetScrubber. Add 'design-rules.json' under EngineeringKnowledge/wet-scrubber.");
        return false;
    }

    /// <summary>
    /// Returns the user-supplied value, or the rulepack design value with an informational warning.
    /// Adds the value to the result under <paramref name="outputKey"/> when one is given.
    /// </summary>
    private static double PickValue(
        CalculationResult.Builder builder,
        EquipmentInputs inputs,
        DesignRuleSet rules,
        string inputKey,
        string designRuleKey,
        string defaultedCode,
        string label,
        string unit,
        string? outputKey,
        out bool supplied)
    {
        var rule = rules.Get(designRuleKey);
        supplied = inputs.TryGetValue(inputKey, out var suppliedValue);
        double value = supplied ? suppliedValue : rule.Resolve();

        if (!supplied)
        {
            builder.AddWarning(
                defaultedCode,
                label + " was not supplied. The design value " + Format(value) + " " + unit + " from the rulepack was used.",
                WarningSeverity.Info,
                inputKey,
                rule.Id);
        }

        if (outputKey is not null)
        {
            builder.AddValue(
                outputKey,
                label + " used",
                value,
                unit,
                supplied ? string.Empty : rule.Id,
                supplied ? "User-supplied value" : rule.SourceReference);
        }

        return value;
    }

    private static void CheckEnvelope(
        EquipmentInputs inputs,
        string key,
        DesignRuleSet rules,
        string ruleKey,
        List<InputIssue> issues)
    {
        if (!inputs.TryGetValue(key, out var value) || !double.IsFinite(value) || value <= 0.0)
        {
            return;
        }

        var rule = rules.Get(ruleKey);

        if (!rule.IsWithinRange(value))
        {
            issues.Add(new InputIssue(
                "INPUT_OUT_OF_RANGE",
                DisplayName(key) + " " + Format(value) + " " + Unit(key) + " is outside the supported range "
                + FormatRange(rule) + " " + Unit(key) + ".",
                key));
        }
    }

    private static string DisplayName(string key) => key switch
    {
        Keys.AirflowM3s => "Gas flow",
        Keys.GasVelocityMs => "Superficial gas velocity",
        Keys.GasResidenceTimeS => "Gas residence time",
        Keys.InletVelocityMs => "Inlet nozzle velocity",
        Keys.StackVelocityMs => "Outlet stack velocity",
        Keys.LiquidToGasRatioLm3 => "Liquid-to-gas ratio",
        Keys.WallThicknessM => "Wall thickness",
        _ => key
    };

    private static string Unit(string key) => key switch
    {
        Keys.AirflowM3s => "m3/s",
        Keys.GasVelocityMs => "m/s",
        Keys.GasResidenceTimeS => "s",
        Keys.InletVelocityMs => "m/s",
        Keys.StackVelocityMs => "m/s",
        Keys.LiquidToGasRatioLm3 => "L/m3",
        Keys.WallThicknessM => "m",
        _ => string.Empty
    };

    private static string Format(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatRange(DesignRule rule)
    {
        var min = rule.Min.HasValue ? Format(rule.Min.Value) : "no lower limit";
        var max = rule.Max.HasValue ? Format(rule.Max.Value) : "no upper limit";
        return min + " to " + max;
    }
}