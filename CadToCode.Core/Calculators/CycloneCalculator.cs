using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Domain;
using CadToCode.Core.Rules;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Deterministic sizing of a dry gas-solid cyclone separator.
/// Body diameter follows from gas flow and inlet velocity using published proportion ratios.
/// All proportions, limits and constants come from the Cyclone rule set; nothing is hardcoded here.
/// No LLM, web, database or UI dependency.
/// </summary>
public sealed class CycloneCalculator : IEquipmentCalculator
{
    public const string CalculatorVersion = "1.1.0";

    /// <summary>Calculator id used by the cyclone-separator equipment manifest.</summary>
    public const string Id = "Cyclone";

    /// <summary>Input and output keys. Match the cyclone-separator equipment manifest.</summary>
    public static class Keys
    {
        // Inputs
        public const string AirflowM3s = "airflow_m3s";
        public const string InletVelocityMs = "inlet_velocity_ms";
        public const string CutDiameterM = "cut_diameter_m";
        public const string GasDensityKgM3 = "gas_density_kgm3";
        public const string GasViscosityPas = "gas_viscosity_pas";
        public const string ParticleDensityKgM3 = "particle_density_kgm3";
        public const string WallThicknessM = "wall_thickness_m";

        // Outputs (manifest)
        public const string BarrelDiameterM = "barrel_diameter_m";
        public const string BarrelHeightM = "barrel_height_m";
        public const string ConeHeightM = "cone_height_m";
        public const string DustOutletDiameterM = "dust_outlet_diameter_m";
        public const string DustOutletLengthM = "dust_outlet_length_m";
        public const string VortexFinderDiameterM = "vortex_finder_diameter_m";
        public const string VortexFinderDepthM = "vortex_finder_depth_m";
        public const string VortexFinderHeightM = "vortex_finder_height_m";
        public const string InletHeightM = "inlet_height_m";
        public const string InletWidthM = "inlet_width_m";
        public const string InletLengthM = "inlet_length_m";
        public const string WallThicknessUsedM = "wall_thickness_m";
        public const string InletVelocityUsedMs = "inlet_velocity_used_ms";
        public const string PressureDropPa = "pressure_drop_pa";
        public const string D50EstimatedM = "d50_estimated_m";

        // Outputs (supporting)
        public const string GasDensityUsedKgM3 = "gas_density_used_kgm3";
        public const string GasViscosityUsedPas = "gas_viscosity_used_pas";
        public const string PressureHeadNumber = "pressure_head_number";
    }

    /// <summary>Rule keys in cyclone/design-rules.json.</summary>
    private static class RuleKeys
    {
        public const string InletHeightRatio = "inlet_height_to_body_diameter_ratio";
        public const string InletWidthRatio = "inlet_width_to_body_diameter_ratio";
        public const string VortexFinderDiameterRatio = "vortex_finder_diameter_to_body_diameter_ratio";
        public const string VortexFinderDepthRatio = "vortex_finder_depth_to_body_diameter_ratio";
        public const string BarrelHeightRatio = "barrel_height_to_body_diameter_ratio";
        public const string TotalHeightRatio = "total_height_to_body_diameter_ratio";
        public const string DustOutletDiameterRatio = "dust_outlet_diameter_to_body_diameter_ratio";
        public const string DesignInletVelocity = "design_inlet_velocity_ms";
        public const string InletVelocityAllowed = "inlet_velocity_allowed_ms";
        public const string BodyDiameterAllowed = "body_diameter_allowed_m";
        public const string AirflowAllowed = "airflow_allowed_m3s";
        public const string PressureDropConstant = "pressure_drop_head_number_constant";
        public const string EffectiveTurns = "effective_turns";
        public const string WallFraction = "wall_thickness_fraction_of_diameter";
        public const string WallMin = "wall_thickness_min_m";
        public const string VortexFinderHeightRatio = "vortex_finder_height_to_diameter_ratio";
        public const string DustOutletLengthRatio = "dust_outlet_length_to_diameter_ratio";
        public const string InletLengthRatio = "inlet_length_to_body_diameter_ratio";
        public const string StandardGasDensity = "standard_gas_density_kgm3";
        public const string StandardGasViscosity = "standard_gas_viscosity_pas";
    }

    // Calculation formula identifiers. Rule-driven values use the design rule ID instead.
    private const string FSizing = "CYC-F-001";
    private const string FProportions = "CYC-F-002";
    private const string FPressureDrop = "CYC-F-003";
    private const string FCutDiameter = "CYC-F-004";

    private const string RefSizing = "Cyclone sizing from inlet area: Q = v * a * b with a = ra * D and b = rb * D; Perry's Chemical Engineers' Handbook";
    private const string RefProportions = "Cyclone proportion family expressed as ratios of body diameter; Stairmand (1951), Perry's Chemical Engineers' Handbook";
    private const string RefPressureDrop = "Shepherd and Lapple (1939) tangential inlet cyclone pressure drop, as given in Perry's Chemical Engineers' Handbook";
    private const string RefCutDiameter = "Lapple (1951) cut diameter model; indicative estimate only";
    private const string RefGeometry = "Cyclone geometry sanity checks derived from the sized proportions";

    private const string GeometryCheckId = "CYC-F-005";

    private static readonly string[] OptionalPositiveKeys =
    {
        Keys.InletVelocityMs,
        Keys.CutDiameterM,
        Keys.GasDensityKgM3,
        Keys.GasViscosityPas,
        Keys.ParticleDensityKgM3,
        Keys.WallThicknessM
    };

    /// <summary>Outputs the equipment manifest marks as required. A result missing any of them is never returned as a success.</summary>
    private static readonly string[] RequiredOutputKeys =
    {
        Keys.BarrelDiameterM,
        Keys.BarrelHeightM,
        Keys.ConeHeightM,
        Keys.DustOutletDiameterM,
        Keys.DustOutletLengthM,
        Keys.VortexFinderDiameterM,
        Keys.VortexFinderDepthM,
        Keys.VortexFinderHeightM,
        Keys.InletHeightM,
        Keys.InletWidthM,
        Keys.InletLengthM,
        Keys.WallThicknessUsedM
    };

    private readonly IDesignRulesProvider _rulesProvider;

    public CycloneCalculator(IDesignRulesProvider rulesProvider)
    {
        _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public EquipmentType EquipmentType => EquipmentType.Cyclone;

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
        CheckEnvelope(inputs, Keys.InletVelocityMs, rules!, RuleKeys.InletVelocityAllowed, issues);

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
                EquipmentType.Cyclone,
                validation.BlockingIssues.Select(i => i.Message),
                Version,
                calculatorId: Id);
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            return CalculationResult.Failure(EquipmentType.Cyclone, new[] { rulesIssue!.Message }, Version, calculatorId: Id);
        }

        try
        {
            return Compute(inputs, rules!);
        }
        catch (DesignRuleNotFoundException ex)
        {
            return CalculationResult.Failure(EquipmentType.Cyclone, new[] { ex.Message }, Version, calculatorId: Id);
        }
        catch (InvalidOperationException ex)
        {
            return CalculationResult.Failure(EquipmentType.Cyclone, new[] { ex.Message }, Version, calculatorId: Id);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Core calculation
    // ------------------------------------------------------------------------------------------------

    private CalculationResult Compute(EquipmentInputs inputs, DesignRuleSet rules)
    {
        var builder = CalculationResult.Create(EquipmentType.Cyclone, Version).ForCalculator(Id);

        double q = inputs[Keys.AirflowM3s];

        // 1. Inlet velocity
        var velocityRule = rules.Get(RuleKeys.DesignInletVelocity);
        var velocitySupplied = inputs.TryGetValue(Keys.InletVelocityMs, out var suppliedVelocity);
        double v = velocitySupplied ? suppliedVelocity : velocityRule.Resolve();

        if (!velocitySupplied)
        {
            builder.AddWarning(
                "CYC_VELOCITY_DEFAULTED",
                "Inlet velocity was not supplied. The design velocity " + Format(v) + " m/s from the rulepack was used.",
                WarningSeverity.Info,
                Keys.InletVelocityMs,
                velocityRule.Id);
        }

        builder.AddValue(
            Keys.InletVelocityUsedMs,
            "Inlet velocity used",
            v,
            "m/s",
            velocitySupplied ? string.Empty : velocityRule.Id,
            velocitySupplied ? "User-supplied value" : velocityRule.SourceReference);

        // 2. Proportion ratios
        var raRule = rules.Get(RuleKeys.InletHeightRatio);
        var rbRule = rules.Get(RuleKeys.InletWidthRatio);
        var rDeRule = rules.Get(RuleKeys.VortexFinderDiameterRatio);
        var rSRule = rules.Get(RuleKeys.VortexFinderDepthRatio);
        var rhRule = rules.Get(RuleKeys.BarrelHeightRatio);
        var rHRule = rules.Get(RuleKeys.TotalHeightRatio);
        var rBRule = rules.Get(RuleKeys.DustOutletDiameterRatio);

        double ra = raRule.Resolve();
        double rb = rbRule.Resolve();
        double rDe = rDeRule.Resolve();
        double rS = rSRule.Resolve();
        double rh = rhRule.Resolve();
        double rH = rHRule.Resolve();
        double rB = rBRule.Resolve();

        if (ra <= 0.0 || rb <= 0.0 || rDe <= 0.0 || rS <= 0.0 || rh <= 0.0 || rH <= 0.0 || rB <= 0.0)
        {
            throw new InvalidOperationException("Cyclone proportion ratios in the rulepack must all be positive.");
        }

        if (rH <= rh)
        {
            throw new InvalidOperationException(
                "Total height ratio must exceed the barrel height ratio so the cone has a positive height.");
        }

        if (rDe >= 1.0 || rB >= 1.0)
        {
            throw new InvalidOperationException(
                "Vortex finder and dust outlet diameter ratios must be smaller than 1 body diameter.");
        }

        // 3. Body diameter from inlet area: Q = v * (ra * D) * (rb * D)
        double d = Math.Sqrt(q / (v * ra * rb));

        var diameterAllowed = rules.Get(RuleKeys.BodyDiameterAllowed);

        if (!diameterAllowed.IsWithinRange(d))
        {
            builder.AddWarning(
                "CYC_BODY_DIAMETER_OUT_OF_RANGE",
                "Calculated body diameter " + Format(d) + " m is outside the supported range " + FormatRange(diameterAllowed)
                + " m. Change the gas flow or inlet velocity to move the design into range.",
                WarningSeverity.Critical,
                Keys.BarrelDiameterM,
                diameterAllowed.Id);
        }

        double a = ra * d;
        double b = rb * d;
        double de = rDe * d;
        double s = rS * d;
        double h = rh * d;
        double cone = (rH - rh) * d;
        double dustDiameter = rB * d;

        builder.AddValue(Keys.BarrelDiameterM, "Barrel diameter", d, "m", FSizing, RefSizing,
            "D = sqrt(Q / (v * ra * rb))");
        builder.AddValue(Keys.InletHeightM, "Inlet height", a, "m", raRule.Id, raRule.SourceReference, "a = ra * D");
        builder.AddValue(Keys.InletWidthM, "Inlet width", b, "m", rbRule.Id, rbRule.SourceReference, "b = rb * D");
        builder.AddValue(Keys.VortexFinderDiameterM, "Vortex finder diameter", de, "m", rDeRule.Id, rDeRule.SourceReference, "De = rDe * D");
        builder.AddValue(Keys.VortexFinderDepthM, "Vortex finder insertion depth", s, "m", rSRule.Id, rSRule.SourceReference, "S = rS * D");
        builder.AddValue(Keys.BarrelHeightM, "Barrel height", h, "m", rhRule.Id, rhRule.SourceReference, "h = rh * D");
        builder.AddValue(Keys.ConeHeightM, "Cone height", cone, "m", FProportions, RefProportions, "H - h = (rH - rh) * D");
        builder.AddValue(Keys.DustOutletDiameterM, "Dust outlet diameter", dustDiameter, "m", rBRule.Id, rBRule.SourceReference, "B = rB * D");

        // 3b. Geometry sanity checks on the sized proportions
        if (a >= h)
        {
            builder.AddWarning(
                "CYC_INLET_TALLER_THAN_BARREL",
                "Inlet height " + Format(a) + " m is not smaller than the barrel height " + Format(h)
                + " m. The tangential inlet does not fit on the barrel. Check the proportion ratios in the rulepack.",
                WarningSeverity.Critical,
                Keys.InletHeightM,
                GeometryCheckId);
        }

        if (s >= h + cone)
        {
            builder.AddWarning(
                "CYC_FINDER_TOO_DEEP",
                "Vortex finder depth " + Format(s) + " m is not shorter than the overall cyclone height " + Format(h + cone)
                + " m. Check the proportion ratios in the rulepack.",
                WarningSeverity.Critical,
                Keys.VortexFinderDepthM,
                GeometryCheckId);
        }
        else if (s < a)
        {
            builder.AddWarning(
                "CYC_FINDER_SHORTER_THAN_INLET",
                "Vortex finder depth " + Format(s) + " m is shorter than the inlet height " + Format(a)
                + " m. Gas can short-circuit from the inlet to the gas outlet.",
                WarningSeverity.Caution,
                Keys.VortexFinderDepthM,
                GeometryCheckId);
        }

        // 4. Fabrication dimensions
        var vfHeightRule = rules.Get(RuleKeys.VortexFinderHeightRatio);
        var dustLengthRule = rules.Get(RuleKeys.DustOutletLengthRatio);
        var inletLengthRule = rules.Get(RuleKeys.InletLengthRatio);

        builder.AddValue(Keys.VortexFinderHeightM, "Vortex finder height above top", vfHeightRule.Resolve() * de, "m",
            vfHeightRule.Id, vfHeightRule.SourceReference, "height = ratio * De");
        builder.AddValue(Keys.DustOutletLengthM, "Dust outlet length", dustLengthRule.Resolve() * dustDiameter, "m",
            dustLengthRule.Id, dustLengthRule.SourceReference, "length = ratio * B");
        builder.AddValue(Keys.InletLengthM, "Inlet length", inletLengthRule.Resolve() * d, "m",
            inletLengthRule.Id, inletLengthRule.SourceReference, "length = ratio * D");

        // 5. Wall thickness
        var wallMinRule = rules.Get(RuleKeys.WallMin);
        var wallFractionRule = rules.Get(RuleKeys.WallFraction);
        var wallSupplied = inputs.TryGetValue(Keys.WallThicknessM, out var suppliedWall);
        double wallMin = wallMinRule.Resolve();
        double wall = wallSupplied ? suppliedWall : Math.Max(wallMin, wallFractionRule.Resolve() * d);

        if (wallSupplied && wall < wallMin)
        {
            builder.AddWarning(
                "CYC_WALL_BELOW_MINIMUM",
                "Wall thickness " + Format(wall) + " m is below the minimum " + Format(wallMin) + " m used by this calculator.",
                WarningSeverity.Caution,
                Keys.WallThicknessM,
                wallMinRule.Id);
        }

        if (wall * 2.0 >= d)
        {
            builder.AddWarning(
                "CYC_WALL_TOO_THICK",
                "Wall thickness " + Format(wall) + " m is too large for a body diameter of " + Format(d) + " m.",
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
            "CYC_PRESSURE_CONTAINMENT_NOT_VERIFIED",
            "Wall thickness is a fabrication assumption. Pressure containment and structural strength are not verified.",
            WarningSeverity.Info,
            Keys.WallThicknessUsedM,
            wallFractionRule.Id);

        // 6. Gas properties
        var densityRule = rules.Get(RuleKeys.StandardGasDensity);
        var viscosityRule = rules.Get(RuleKeys.StandardGasViscosity);

        var densitySupplied = inputs.TryGetValue(Keys.GasDensityKgM3, out var suppliedDensity);
        var viscositySupplied = inputs.TryGetValue(Keys.GasViscosityPas, out var suppliedViscosity);

        double rho = densitySupplied ? suppliedDensity : densityRule.Resolve();
        double mu = viscositySupplied ? suppliedViscosity : viscosityRule.Resolve();

        if (!densitySupplied)
        {
            builder.AddWarning(
                "CYC_DENSITY_DEFAULTED",
                "Gas density was not supplied. Standard value " + Format(rho) + " kg/m3 was used.",
                WarningSeverity.Info,
                Keys.GasDensityKgM3,
                densityRule.Id);
        }

        if (!viscositySupplied)
        {
            builder.AddWarning(
                "CYC_VISCOSITY_DEFAULTED",
                "Gas viscosity was not supplied. Standard value " + Format(mu) + " Pa.s was used.",
                WarningSeverity.Info,
                Keys.GasViscosityPas,
                viscosityRule.Id);
        }

        builder.AddValue(Keys.GasDensityUsedKgM3, "Gas density used", rho, "kg/m3",
            densitySupplied ? string.Empty : densityRule.Id,
            densitySupplied ? "User-supplied value" : densityRule.SourceReference);
        builder.AddValue(Keys.GasViscosityUsedPas, "Gas viscosity used", mu, "Pa.s",
            viscositySupplied ? string.Empty : viscosityRule.Id,
            viscositySupplied ? "User-supplied value" : viscosityRule.SourceReference);

        // 7. Pressure drop (Shepherd-Lapple)
        var kRule = rules.Get(RuleKeys.PressureDropConstant);
        double headNumber = kRule.Resolve() * a * b / (de * de);
        double pressureDrop = headNumber * 0.5 * rho * v * v;

        builder.AddValue(Keys.PressureHeadNumber, "Pressure head number", headNumber, "-", FPressureDrop, RefPressureDrop,
            "NH = K * a * b / De^2");
        builder.AddValue(Keys.PressureDropPa, "Pressure drop", pressureDrop, "Pa", FPressureDrop, RefPressureDrop,
            "dP = NH * 0.5 * rho * v^2");

        // 8. Cut diameter estimate (needs particle density)
        var targetSupplied = inputs.TryGetValue(Keys.CutDiameterM, out var target);
        double? d50 = null;

        if (inputs.TryGetValue(Keys.ParticleDensityKgM3, out var rhoParticle))
        {
            if (rhoParticle <= rho)
            {
                builder.AddWarning(
                    "CYC_PARTICLE_DENSITY_INVALID",
                    "Particle density must exceed gas density. The cut diameter was not estimated.",
                    WarningSeverity.Caution,
                    Keys.ParticleDensityKgM3,
                    FCutDiameter);
            }
            else
            {
                var turnsRule = rules.Get(RuleKeys.EffectiveTurns);
                double turns = turnsRule.Resolve();

                if (turns <= 0.0)
                {
                    throw new InvalidOperationException("Effective turns in the rulepack must be positive.");
                }

                d50 = Math.Sqrt(9.0 * mu * b / (2.0 * Math.PI * turns * v * (rhoParticle - rho)));

                builder.AddValue(Keys.D50EstimatedM, "Estimated cut diameter", d50.Value, "m", FCutDiameter, RefCutDiameter,
                    "d50 = sqrt(9 * mu * b / (2 * pi * Ne * v * (rho_p - rho)))");
            }
        }

        if (targetSupplied)
        {
            if (d50.HasValue && d50.Value > target)
            {
                // NOTE: d50 falls as inlet velocity rises and as body diameter falls, so the remedies are a higher
                // inlet velocity within the supported range, or the flow split across several smaller cyclones.
                builder.AddWarning(
                    "CYC_CUT_DIAMETER_NOT_MET",
                    "Estimated cut diameter " + Format(d50.Value) + " m is larger than the target " + Format(target)
                    + " m. The design was not resized. Raise the inlet velocity within the supported range, "
                    + "or split the flow across several smaller cyclones in parallel.",
                    WarningSeverity.Caution,
                    Keys.CutDiameterM,
                    FCutDiameter);
            }
            else if (!d50.HasValue)
            {
                builder.AddWarning(
                    "CYC_CUT_DIAMETER_UNCHECKED",
                    "A target cut diameter was given but it could not be checked. Provide particle density to estimate it.",
                    WarningSeverity.Info,
                    Keys.CutDiameterM,
                    FCutDiameter);
            }
        }

        // 9. Contract with the equipment manifest: every required output must exist.
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
        if (_rulesProvider.TryGetRuleSet(EquipmentType.Cyclone, out var found))
        {
            rules = found;
            issue = null;
            return true;
        }

        rules = null;
        issue = new InputIssue(
            "RULES_MISSING",
            "No design rule set is loaded for Cyclone. Add 'design-rules.json' under EngineeringKnowledge/cyclone.");
        return false;
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
        Keys.InletVelocityMs => "Inlet velocity",
        Keys.CutDiameterM => "Target cut diameter",
        Keys.GasDensityKgM3 => "Gas density",
        Keys.GasViscosityPas => "Gas viscosity",
        Keys.ParticleDensityKgM3 => "Particle density",
        Keys.WallThicknessM => "Wall thickness",
        _ => key
    };

    private static string Unit(string key) => key switch
    {
        Keys.AirflowM3s => "m3/s",
        Keys.InletVelocityMs => "m/s",
        Keys.CutDiameterM => "m",
        Keys.GasDensityKgM3 => "kg/m3",
        Keys.GasViscosityPas => "Pa.s",
        Keys.ParticleDensityKgM3 => "kg/m3",
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