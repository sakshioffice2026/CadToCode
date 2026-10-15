using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Domain;
using CadToCode.Core.Rules;
using CadToCode.Core.Units;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Deterministic single-stage axial fan sizing calculator.
/// Physics (dimensional analysis, Euler turbomachinery equation, free-vortex blade design) lives here.
/// All design constants, limits and bands come from <see cref="IDesignRulesProvider"/> (fans/design-rules.json).
/// All inputs and outputs are SI.
///
/// Method:
///   1. Hub-to-tip ratio from the static-pressure band rule.
///   2. Tip diameter from flow coefficient: D = ( 8 Q / ( phi * omega * pi * (1 - nu^2) ) )^(1/3).
///   3. Tip speed, axial velocity, pressure coefficient and envelope checks.
///   4. Total pressure = static + axial velocity pressure; free-vortex circulation from Euler work.
///   5. Blade angles at hub and tip from the velocity triangle: beta = atan( Cx / ( U - Cu2 / 2 ) ).
///   6. Blade count from the hub-ratio band rule; casing dimensions from clearance and wall rules.
/// </summary>
public sealed class AxialFanCalculator : IEquipmentCalculator
{
    public const string CalculatorVersion = "1.0.0";

    /// <summary>Input and output keys. Match <see cref="EquipmentRegistry"/> descriptors for <see cref="EquipmentType.AxialFan"/>.</summary>
    public static class Keys
    {
        // Inputs
        public const string AirflowM3s = "airflow_m3s";
        public const string StaticPressurePa = "static_pressure_pa";
        public const string SpeedRadS = "speed_rad_s";
        public const string AirDensityKgM3 = "air_density_kgm3";
        public const string StaticEfficiency = "static_efficiency";

        // Outputs (registry)
        public const string TipDiameterM = "tip_diameter_m";
        public const string HubDiameterM = "hub_diameter_m";
        public const string HubToTipRatio = "hub_to_tip_ratio";
        public const string TipSpeedMs = "tip_speed_ms";
        public const string BladeCount = "blade_count";
        public const string BladeAngleHubDeg = "blade_angle_hub_deg";
        public const string BladeAngleTipDeg = "blade_angle_tip_deg";
        public const string CasingInnerDiameterM = "casing_inner_diameter_m";
        public const string CasingOuterDiameterM = "casing_outer_diameter_m";
        public const string CasingLengthM = "casing_length_m";

        // Outputs (supporting)
        public const string AirDensityUsedKgM3 = "air_density_used_kgm3";
        public const string FlowCoefficient = "flow_coefficient";
        public const string AxialVelocityMs = "axial_velocity_ms";
        public const string StaticPressureCoefficient = "static_pressure_coefficient";
        public const string TotalPressurePa = "total_pressure_pa";
        public const string BladeCirculationM2s = "blade_circulation_m2s";
        public const string TipClearanceM = "tip_clearance_m";
        public const string CasingWallThicknessM = "casing_wall_thickness_m";
        public const string ShaftPowerW = "shaft_power_w";
    }

    /// <summary>Rule keys in fans/design-rules.json.</summary>
    private static class RuleKeys
    {
        public const string StandardAirDensity = "standard_air_density_kgm3";
        public const string TipSpeedCaution = "tip_speed_caution_ms";
        public const string TipSpeedCritical = "tip_speed_critical_ms";
        public const string HubToTipByPressure = "hub_to_tip_ratio_by_static_pressure";
        public const string HubToTipAllowed = "hub_to_tip_ratio_allowed";
        public const string BladeCountByHubRatio = "blade_count_by_hub_to_tip_ratio";
        public const string BladeCountAllowed = "blade_count_allowed";
        public const string DesignFlowCoefficient = "design_flow_coefficient";
        public const string FlowCoefficientAllowed = "flow_coefficient_allowed";
        public const string PressureCoefficientAllowed = "static_pressure_coefficient_allowed";
        public const string HydraulicEfficiency = "assumed_hydraulic_efficiency";
        public const string DefaultStaticEfficiency = "default_static_efficiency";
        public const string StaticEfficiencyAllowed = "static_efficiency_allowed";
        public const string TipClearanceFraction = "tip_clearance_fraction_of_diameter";
        public const string TipClearanceMin = "tip_clearance_min_m";
        public const string WallThicknessFraction = "casing_wall_thickness_fraction_of_diameter";
        public const string WallThicknessMin = "casing_wall_thickness_min_m";
        public const string CasingLengthRatio = "casing_length_to_tip_diameter_ratio";
        public const string BladeAngleAllowed = "blade_angle_allowed_deg";
        public const string TipDiameterAllowed = "tip_diameter_allowed_m";
        public const string AxialVelocityAllowed = "axial_velocity_allowed_ms";
        public const string StaticPressureAllowed = "static_pressure_allowed_pa";
        public const string AirflowAllowed = "airflow_allowed_m3s";
    }

    // Calculation formula identifiers. Rule-driven values use the design rule ID instead.
    private const string FSizing = "FAN-F-001";
    private const string FTipSpeed = "FAN-F-002";
    private const string FAxialVelocity = "FAN-F-003";
    private const string FPressureCoefficient = "FAN-F-004";
    private const string FTotalPressure = "FAN-F-005";
    private const string FCirculation = "FAN-F-006";
    private const string FBladeAngle = "FAN-F-007";
    private const string FShaftPower = "FAN-F-008";
    private const string FCasing = "FAN-F-009";

    private const string RefDimensionless = "Axial fan dimensional analysis and flow coefficient design; Wright, Fluid Machinery; Eck, Fans";
    private const string RefKinematics = "Velocity triangle kinematics; Wright, Fluid Machinery";
    private const string RefBernoulli = "Bernoulli equation; total pressure = static pressure + velocity pressure";
    private const string RefEuler = "Euler turbomachinery equation with free-vortex blade design; Wright, Fluid Machinery; Eck, Fans";
    private const string RefPower = "Fan air power and static efficiency definition; AMCA 210 / ISO 5801";

    private readonly IDesignRulesProvider _rulesProvider;

    public AxialFanCalculator(IDesignRulesProvider rulesProvider)
    {
        _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public EquipmentType EquipmentType => EquipmentType.AxialFan;

    public string Version => CalculatorVersion;

    public InputValidationResult Validate(EquipmentInputs inputs)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var issues = new List<InputIssue>();
        var missing = new List<string>();
        var metadata = EquipmentRegistry.Get(EquipmentType.AxialFan);

        foreach (var descriptor in metadata.RequiredInputs)
        {
            if (!inputs.TryGetValue(descriptor.Key, out var value))
            {
                missing.Add(descriptor.Key);
                issues.Add(new InputIssue(
                    "INPUT_MISSING",
                    $"{descriptor.DisplayName} ({descriptor.SiUnit}) is required. {descriptor.Description}",
                    descriptor.Key));
                continue;
            }

            if (!double.IsFinite(value) || value <= 0.0)
            {
                issues.Add(new InputIssue(
                    "INPUT_INVALID",
                    $"{descriptor.DisplayName} must be a positive, finite number ({descriptor.SiUnit}).",
                    descriptor.Key));
            }
        }

        if (inputs.TryGetValue(Keys.AirDensityKgM3, out var density) && (!double.IsFinite(density) || density <= 0.0))
        {
            issues.Add(new InputIssue(
                "INPUT_INVALID",
                "Air Density must be a positive, finite number (kg/m3).",
                Keys.AirDensityKgM3));
        }

        if (inputs.TryGetValue(Keys.StaticEfficiency, out var efficiency) && !double.IsFinite(efficiency))
        {
            issues.Add(new InputIssue(
                "INPUT_INVALID",
                "Static Efficiency must be a finite number between 0 and 1.",
                Keys.StaticEfficiency));
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            issues.Add(rulesIssue!);
            return InputValidationResult.Invalid(issues, missing);
        }

        // Supported-envelope checks. Only run for values that are otherwise valid.
        CheckEnvelope(inputs, Keys.AirflowM3s, rules!, RuleKeys.AirflowAllowed, "Airflow", "m3/s", issues);
        CheckEnvelope(inputs, Keys.StaticPressurePa, rules!, RuleKeys.StaticPressureAllowed, "Static Pressure", "Pa", issues);

        if (inputs.TryGetValue(Keys.StaticEfficiency, out var eff) && double.IsFinite(eff))
        {
            var rule = rules!.Get(RuleKeys.StaticEfficiencyAllowed);
            if (!rule.IsWithinRange(eff))
            {
                issues.Add(new InputIssue(
                    "INPUT_OUT_OF_RANGE",
                    $"Static Efficiency {Format(eff)} is outside the supported range {FormatRange(rule)}.",
                    Keys.StaticEfficiency));
            }
        }

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
                EquipmentType.AxialFan,
                validation.BlockingIssues.Select(i => i.Message),
                Version);
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            return CalculationResult.Failure(EquipmentType.AxialFan, new[] { rulesIssue!.Message }, Version);
        }

        try
        {
            return Compute(inputs, rules!);
        }
        catch (DesignRuleNotFoundException ex)
        {
            return CalculationResult.Failure(EquipmentType.AxialFan, new[] { ex.Message }, Version);
        }
        catch (InvalidOperationException ex)
        {
            return CalculationResult.Failure(EquipmentType.AxialFan, new[] { ex.Message }, Version);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Core calculation
    // ------------------------------------------------------------------------------------------------

    private CalculationResult Compute(EquipmentInputs inputs, DesignRuleSet rules)
    {
        var warnings = new List<EngineeringWarning>();
        var builder = CalculationResult.Create(EquipmentType.AxialFan, Version);

        double q = inputs[Keys.AirflowM3s];
        double ps = inputs[Keys.StaticPressurePa];
        double omega = inputs[Keys.SpeedRadS];

        // Air density
        var densityRule = rules.Get(RuleKeys.StandardAirDensity);
        double rho = inputs.TryGetValue(Keys.AirDensityKgM3, out var suppliedRho) ? suppliedRho : densityRule.Resolve();
        if (!inputs.ContainsKey(Keys.AirDensityKgM3))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_DENSITY_DEFAULTED",
                $"Air density was not supplied. Standard air density {Format(rho)} kg/m3 was used.",
                WarningSeverity.Info,
                Keys.AirDensityKgM3,
                densityRule.Id));
        }

        builder.AddValue(Keys.AirDensityUsedKgM3, "Air Density Used", rho, "kg/m3", densityRule.Id, densityRule.SourceReference);

        // 1. Hub-to-tip ratio
        var hubRule = rules.Get(RuleKeys.HubToTipByPressure);
        double nu = hubRule.Resolve(ps);
        var hubAllowed = rules.Get(RuleKeys.HubToTipAllowed);
        if (!hubAllowed.IsWithinRange(nu))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_HUB_RATIO_OUT_OF_RANGE",
                $"Selected hub-to-tip ratio {Format(nu)} is outside the supported range {FormatRange(hubAllowed)}.",
                WarningSeverity.Critical,
                Keys.HubToTipRatio,
                hubAllowed.Id));
        }

        builder.AddValue(Keys.HubToTipRatio, "Hub-to-Tip Ratio", nu, "-", hubRule.Id, hubRule.SourceReference,
            $"Band lookup on static pressure = {Format(ps)} Pa");

        // 2. Tip diameter from flow coefficient
        var phiRule = rules.Get(RuleKeys.DesignFlowCoefficient);
        double phi = phiRule.Resolve();
        var phiAllowed = rules.Get(RuleKeys.FlowCoefficientAllowed);
        if (!phiAllowed.IsWithinRange(phi))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_FLOW_COEFFICIENT_OUT_OF_RANGE",
                $"Design flow coefficient {Format(phi)} is outside the supported range {FormatRange(phiAllowed)}.",
                WarningSeverity.Critical,
                Keys.FlowCoefficient,
                phiAllowed.Id));
        }

        double annulusFactor = 1.0 - nu * nu;
        double dTip = Math.Cbrt(8.0 * q / (phi * omega * Math.PI * annulusFactor));
        double dHub = nu * dTip;
        double rTip = dTip / 2.0;
        double rHub = dHub / 2.0;

        builder.AddValue(Keys.FlowCoefficient, "Design Flow Coefficient", phi, "-", phiRule.Id, phiRule.SourceReference);
        builder.AddValue(Keys.TipDiameterM, "Tip Diameter", dTip, "m", FSizing, RefDimensionless,
            "D = (8 Q / (phi * omega * pi * (1 - nu^2)))^(1/3)");
        builder.AddValue(Keys.HubDiameterM, "Hub Diameter", dHub, "m", FSizing, RefDimensionless, "D_hub = nu * D");

        var diameterAllowed = rules.Get(RuleKeys.TipDiameterAllowed);
        if (!diameterAllowed.IsWithinRange(dTip))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_TIP_DIAMETER_OUT_OF_RANGE",
                $"Calculated tip diameter {Format(dTip)} m is outside the supported range {FormatRange(diameterAllowed)} m. Increase or decrease speed to move the design into range.",
                WarningSeverity.Critical,
                Keys.TipDiameterM,
                diameterAllowed.Id));
        }

        // 3. Tip speed, axial velocity, pressure coefficient
        double uTip = omega * rTip;
        double annulusArea = Math.PI / 4.0 * dTip * dTip * annulusFactor;
        double cx = q / annulusArea;

        builder.AddValue(Keys.TipSpeedMs, "Tip Speed", uTip, "m/s", FTipSpeed, RefKinematics, "U_tip = omega * D / 2");
        builder.AddValue(Keys.AxialVelocityMs, "Axial Velocity", cx, "m/s", FAxialVelocity, RefKinematics,
            "Cx = Q / (pi/4 * D^2 * (1 - nu^2))");

        var cautionTipSpeed = rules.Get(RuleKeys.TipSpeedCaution);
        var criticalTipSpeed = rules.Get(RuleKeys.TipSpeedCritical);
        double cautionValue = cautionTipSpeed.Resolve();
        double criticalValue = criticalTipSpeed.Resolve();

        if (uTip > criticalValue)
        {
            warnings.Add(new EngineeringWarning(
                "FAN_TIP_SPEED_CRITICAL",
                $"Tip speed {Format(uTip)} m/s exceeds the validated limit of {Format(criticalValue)} m/s. Reduce rotational speed.",
                WarningSeverity.Critical,
                Keys.TipSpeedMs,
                criticalTipSpeed.Id));
        }
        else if (uTip > cautionValue)
        {
            warnings.Add(new EngineeringWarning(
                "FAN_TIP_SPEED_HIGH",
                $"Tip speed {Format(uTip)} m/s exceeds {Format(cautionValue)} m/s. Expect elevated noise and blade stress.",
                WarningSeverity.Caution,
                Keys.TipSpeedMs,
                cautionTipSpeed.Id));
        }

        var axialAllowed = rules.Get(RuleKeys.AxialVelocityAllowed);
        if (!axialAllowed.IsWithinRange(cx))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_AXIAL_VELOCITY_OUT_OF_RANGE",
                $"Axial velocity {Format(cx)} m/s is outside the recommended range {FormatRange(axialAllowed)} m/s.",
                WarningSeverity.Caution,
                Keys.AxialVelocityMs,
                axialAllowed.Id));
        }

        double psi = ps / (0.5 * rho * uTip * uTip);
        builder.AddValue(Keys.StaticPressureCoefficient, "Static Pressure Coefficient", psi, "-", FPressureCoefficient, RefDimensionless,
            "psi_s = Ps / (0.5 * rho * U_tip^2)");

        var psiAllowed = rules.Get(RuleKeys.PressureCoefficientAllowed);
        if (psi > psiAllowed.Max)
        {
            warnings.Add(new EngineeringWarning(
                "FAN_PRESSURE_COEFFICIENT_TOO_HIGH",
                $"Static pressure coefficient {Format(psi)} exceeds {Format(psiAllowed.Max!.Value)}. A single-stage axial fan cannot deliver this pressure at this speed. Increase speed or consider a multistage or centrifugal selection.",
                WarningSeverity.Critical,
                Keys.StaticPressureCoefficient,
                psiAllowed.Id));
        }
        else if (psi < psiAllowed.Min)
        {
            warnings.Add(new EngineeringWarning(
                "FAN_PRESSURE_COEFFICIENT_LOW",
                $"Static pressure coefficient {Format(psi)} is below {Format(psiAllowed.Min!.Value)}. The fan is lightly loaded and may be oversized for this duty.",
                WarningSeverity.Caution,
                Keys.StaticPressureCoefficient,
                psiAllowed.Id));
        }

        // 4. Total pressure and free-vortex circulation
        double pt = ps + 0.5 * rho * cx * cx;
        builder.AddValue(Keys.TotalPressurePa, "Total Pressure Rise", pt, "Pa", FTotalPressure, RefBernoulli,
            "Pt = Ps + 0.5 * rho * Cx^2");

        var etaHydRule = rules.Get(RuleKeys.HydraulicEfficiency);
        double etaH = etaHydRule.Resolve();
        double gamma = pt / (rho * etaH * omega);
        builder.AddValue(Keys.BladeCirculationM2s, "Blade Circulation (r*Cu2)", gamma, "m2/s", FCirculation, RefEuler,
            "Gamma = Pt / (rho * eta_h * omega)");

        // 5. Blade angles (from plane of rotation)
        double uHub = omega * rHub;
        double cuHub = gamma / rHub;
        double cuTip = gamma / rTip;
        double denomHub = uHub - cuHub / 2.0;
        double denomTip = uTip - cuTip / 2.0;

        if (denomHub <= 0.0 || denomTip <= 0.0)
        {
            return CalculationResult.Failure(
                EquipmentType.AxialFan,
                new[]
                {
                    "Blade loading is too high for a free-vortex design: the mean relative flow reverses at the hub. " +
                    "Increase rotational speed, reduce static pressure, or select a multistage or centrifugal fan."
                },
                Version,
                warnings);
        }

        double betaHub = UnitConversions.RadiansToDegrees(Math.Atan2(cx, denomHub));
        double betaTip = UnitConversions.RadiansToDegrees(Math.Atan2(cx, denomTip));

        builder.AddValue(Keys.BladeAngleHubDeg, "Blade Angle at Hub", betaHub, "deg", FBladeAngle, RefEuler,
            "beta = atan(Cx / (U - Cu2 / 2)) at r_hub");
        builder.AddValue(Keys.BladeAngleTipDeg, "Blade Angle at Tip", betaTip, "deg", FBladeAngle, RefEuler,
            "beta = atan(Cx / (U - Cu2 / 2)) at r_tip");

        var angleAllowed = rules.Get(RuleKeys.BladeAngleAllowed);
        foreach (var (key, angle, label) in new[]
                 {
                     (Keys.BladeAngleHubDeg, betaHub, "hub"),
                     (Keys.BladeAngleTipDeg, betaTip, "tip")
                 })
        {
            if (!angleAllowed.IsWithinRange(angle))
            {
                warnings.Add(new EngineeringWarning(
                    "FAN_BLADE_ANGLE_OUT_OF_RANGE",
                    $"Blade angle at {label} {Format(angle)} deg is outside the recommended range {FormatRange(angleAllowed)} deg.",
                    WarningSeverity.Caution,
                    key,
                    angleAllowed.Id));
            }
        }

        // 6. Blade count
        var bladeRule = rules.Get(RuleKeys.BladeCountByHubRatio);
        int bladeCount = (int)Math.Round(bladeRule.Resolve(nu), MidpointRounding.AwayFromZero);
        var bladeAllowed = rules.Get(RuleKeys.BladeCountAllowed);
        if (!bladeAllowed.IsWithinRange(bladeCount))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_BLADE_COUNT_OUT_OF_RANGE",
                $"Selected blade count {bladeCount} is outside the supported range {FormatRange(bladeAllowed)}.",
                WarningSeverity.Critical,
                Keys.BladeCount,
                bladeAllowed.Id));
        }

        builder.AddValue(Keys.BladeCount, "Blade Count", bladeCount, "-", bladeRule.Id, bladeRule.SourceReference,
            $"Band lookup on hub-to-tip ratio = {Format(nu)}");

        // 7. Casing
        var clearanceFraction = rules.Get(RuleKeys.TipClearanceFraction);
        var clearanceMin = rules.Get(RuleKeys.TipClearanceMin);
        double clearance = Math.Max(clearanceFraction.Resolve() * dTip, clearanceMin.Resolve());
        double casingInner = dTip + 2.0 * clearance;

        var wallFraction = rules.Get(RuleKeys.WallThicknessFraction);
        var wallMin = rules.Get(RuleKeys.WallThicknessMin);
        double wall = Math.Max(wallFraction.Resolve() * casingInner, wallMin.Resolve());
        double casingOuter = casingInner + 2.0 * wall;

        var lengthRule = rules.Get(RuleKeys.CasingLengthRatio);
        double casingLength = lengthRule.Resolve() * dTip;

        builder.AddValue(Keys.TipClearanceM, "Radial Tip Clearance", clearance, "m", clearanceFraction.Id, clearanceFraction.SourceReference,
            "c = max(fraction * D, minimum)");
        builder.AddValue(Keys.CasingInnerDiameterM, "Casing Inner Diameter", casingInner, "m", FCasing, clearanceFraction.SourceReference,
            "D_casing_in = D + 2 * c");
        builder.AddValue(Keys.CasingWallThicknessM, "Casing Wall Thickness", wall, "m", wallFraction.Id, wallFraction.SourceReference,
            "t = max(fraction * D_casing_in, minimum)");
        builder.AddValue(Keys.CasingOuterDiameterM, "Casing Outer Diameter", casingOuter, "m", FCasing, wallFraction.SourceReference,
            "D_casing_out = D_casing_in + 2 * t");
        builder.AddValue(Keys.CasingLengthM, "Casing Length", casingLength, "m", lengthRule.Id, lengthRule.SourceReference,
            "L = ratio * D");

        // 8. Shaft power estimate
        var defaultEff = rules.Get(RuleKeys.DefaultStaticEfficiency);
        double etaS = inputs.TryGetValue(Keys.StaticEfficiency, out var suppliedEta) ? suppliedEta : defaultEff.Resolve();
        if (!inputs.ContainsKey(Keys.StaticEfficiency))
        {
            warnings.Add(new EngineeringWarning(
                "FAN_EFFICIENCY_DEFAULTED",
                $"Static efficiency was not supplied. {Format(etaS)} was assumed for the shaft power estimate.",
                WarningSeverity.Info,
                Keys.StaticEfficiency,
                defaultEff.Id));
        }

        double shaftPower = q * ps / etaS;
        builder.AddValue(Keys.ShaftPowerW, "Estimated Shaft Power", shaftPower, "W", FShaftPower, RefPower,
            "P_shaft = Q * Ps / eta_static");

        foreach (var warning in warnings)
            builder.AddWarning(warning);

        return builder.Build();
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private bool TryGetRules(out DesignRuleSet? rules, out InputIssue? issue)
    {
        if (_rulesProvider.TryGetRuleSet(EquipmentType.AxialFan, out var found))
        {
            rules = found;
            issue = null;
            return true;
        }

        rules = null;
        issue = new InputIssue(
            "RULES_UNAVAILABLE",
            "Axial fan design rules are not loaded. Add fans/design-rules.json to the engineering knowledge base.");
        return false;
    }

    private static void CheckEnvelope(
        EquipmentInputs inputs,
        string inputKey,
        DesignRuleSet rules,
        string ruleKey,
        string displayName,
        string unit,
        List<InputIssue> issues)
    {
        if (!inputs.TryGetValue(inputKey, out var value) || !double.IsFinite(value) || value <= 0.0)
            return;

        var rule = rules.Get(ruleKey);
        if (!rule.IsWithinRange(value))
        {
            issues.Add(new InputIssue(
                "INPUT_OUT_OF_RANGE",
                $"{displayName} {Format(value)} {unit} is outside the supported range {FormatRange(rule)} {unit}.",
                inputKey));
        }
    }

    private static string Format(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatRange(DesignRule rule)
    {
        var min = rule.Min.HasValue ? Format(rule.Min.Value) : "-inf";
        var max = rule.Max.HasValue ? Format(rule.Max.Value) : "+inf";
        return $"{min} to {max}";
    }
}
