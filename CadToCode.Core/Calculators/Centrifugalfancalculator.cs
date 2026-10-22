using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Domain;
using CadToCode.Core.Rules;
using CadToCode.Core.Units;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Deterministic single-stage, single-inlet centrifugal fan sizing calculator.
/// Physics (dimensional analysis, Euler turbomachinery equation with Wiesner slip) lives here.
/// All design constants, limits and blade type proportions come from <see cref="IDesignRulesProvider"/>
/// (centrifugal-fans/design-rules.json). Rules that depend on the blade type use the prefix returned by
/// <see cref="CentrifugalBladeTypes.RulePrefix"/>. All inputs and outputs are SI.
///
/// Method:
///   1. Blade type from the numeric blade_type input (1 forward curved, 2 backward curved, 3 backward airfoil, 4 radial).
///   2. Tip speed and outer diameter from the static pressure coefficient: U2 = sqrt(2 Ps / (rho psi_s)), D2 = 2 U2 / omega.
///   3. Outlet blade width from the flow coefficient: cr2 = phi2 U2, b2 = Q / (pi D2 cr2).
///   4. Inlet diameter and inlet width from the blade type ratios.
///   5. Wiesner slip factor, total pressure with the discharge velocity pressure, Euler head with the hydraulic efficiency.
///   6. Blade outlet angle from the outlet velocity triangle: beta2 = atan2(cr2, sigma U2 - Cu2).
///   7. Blade count, blade inlet angle, thickness and profile from the blade type rules.
///   8. Impeller plates, hub, scroll casing, outlet opening and shaft power.
/// </summary>
public sealed class CentrifugalFanCalculator : IEquipmentCalculator
{
    public const string CalculatorVersion = "1.0.0";

    /// <summary>Calculator id used in equipment manifests.</summary>
    public const string Id = "CentrifugalFan";

    /// <summary>Input and output keys. Match <see cref="EquipmentRegistry"/> descriptors for <see cref="EquipmentType.CentrifugalFan"/>.</summary>
    public static class Keys
    {
        // Inputs
        public const string AirflowM3s = "airflow_m3s";
        public const string StaticPressurePa = "static_pressure_pa";
        public const string SpeedRadS = "speed_rad_s";
        public const string BladeType = CentrifugalBladeTypes.InputKey;
        public const string AirDensityKgM3 = "air_density_kgm3";
        public const string StaticEfficiency = "static_efficiency";

        // Outputs (registry)
        public const string ImpellerOuterDiameterM = "impeller_outer_diameter_m";
        public const string ImpellerInletDiameterM = "impeller_inlet_diameter_m";
        public const string ImpellerWidthM = "impeller_width_m";
        public const string HubDiameterM = "hub_diameter_m";
        public const string BladeCount = "blade_count";
        public const string BladeInletAngleDeg = "blade_inlet_angle_deg";
        public const string BladeOutletAngleDeg = "blade_outlet_angle_deg";
        public const string BladeThicknessM = "blade_thickness_m";
        public const string BladeProfileCode = "blade_profile_code";
        public const string PlateThicknessM = "plate_thickness_m";
        public const string TipSpeedMs = "tip_speed_ms";
        public const string CasingBaseDiameterM = "casing_base_diameter_m";
        public const string CasingWidthM = "casing_width_m";
        public const string CasingWallThicknessM = "casing_wall_thickness_m";
        public const string OutletHeightM = "outlet_height_m";
        public const string InletEyeDiameterM = "inlet_eye_diameter_m";
        public const string ShaftPowerW = "shaft_power_w";

        // Outputs (supporting)
        public const string AirDensityUsedKgM3 = "air_density_used_kgm3";
        public const string FlowCoefficient = "flow_coefficient";
        public const string StaticPressureCoefficient = "static_pressure_coefficient";
        public const string SpecificSpeed = "specific_speed";
        public const string TotalPressurePa = "total_pressure_pa";
        public const string SlipFactor = "slip_factor";
        public const string EulerHeadPa = "euler_head_pa";
        public const string BladeInletWidthM = "blade_inlet_width_m";
        public const string CasingCutoffClearanceM = "casing_cutoff_clearance_m";
        public const string InletFlowAngleDeg = "inlet_flow_angle_deg";
    }

    /// <summary>Rule keys shared by every blade type in centrifugal-fans/design-rules.json.</summary>
    private static class RuleKeys
    {
        public const string StandardAirDensity = "standard_air_density_kgm3";
        public const string SlipExponent = "slip_blade_count_exponent";
        public const string StaticEfficiencyAllowed = "static_efficiency_allowed";
        public const string AirflowAllowed = "airflow_allowed_m3s";
        public const string StaticPressureAllowed = "static_pressure_allowed_pa";
        public const string DiameterAllowed = "impeller_outer_diameter_allowed_m";
        public const string PressureCoefficientAllowed = "static_pressure_coefficient_allowed";
        public const string FlowCoefficientAllowed = "flow_coefficient_allowed";
        public const string DiameterRatioAllowed = "inlet_to_outlet_diameter_ratio_allowed";
        public const string SpecificSpeedAllowed = "specific_speed_allowed";
        public const string BladeThicknessMin = "blade_thickness_min_m";
        public const string PlateThicknessFraction = "plate_thickness_fraction_of_d2";
        public const string PlateThicknessMin = "plate_thickness_min_m";
        public const string HubToInletRatio = "hub_to_inlet_diameter_ratio";
        public const string CutoffClearanceFraction = "cutoff_clearance_fraction_of_d2";
        public const string CutoffClearanceMin = "cutoff_clearance_min_m";
        public const string CasingWidthRatio = "casing_width_to_blade_width_ratio";
        public const string CasingWallFraction = "casing_wall_thickness_fraction_of_diameter";
        public const string CasingWallMin = "casing_wall_thickness_min_m";
        public const string OutletVelocityRatio = "outlet_velocity_to_tip_speed_ratio";
        public const string CasingInletRatio = "casing_inlet_to_impeller_inlet_diameter_ratio";
    }

    /// <summary>Rule key suffixes that exist once per blade type. The full key is prefix + "." + suffix.</summary>
    private static class TypeRuleKeys
    {
        public const string DesignStaticPressureCoefficient = "design_static_pressure_coefficient";
        public const string DesignFlowCoefficient = "design_flow_coefficient";
        public const string InletToOutletDiameterRatio = "inlet_to_outlet_diameter_ratio";
        public const string BladeInletAngle = "blade_inlet_angle_deg";
        public const string BladeOutletAngle = "blade_outlet_angle_deg";
        public const string BladeCount = "blade_count";
        public const string BladeProfileCode = "blade_profile_code";
        public const string BladeThicknessFraction = "blade_thickness_fraction_of_d2";
        public const string InletToOutletWidthRatio = "blade_inlet_to_outlet_width_ratio";
        public const string HydraulicEfficiency = "assumed_hydraulic_efficiency";
        public const string DefaultStaticEfficiency = "default_static_efficiency";
        public const string TipSpeedCaution = "tip_speed_caution_ms";
        public const string TipSpeedCritical = "tip_speed_critical_ms";
        public const string WidthToDiameterAllowed = "width_to_diameter_ratio_allowed";
        public const string BladeOutletAngleAllowed = "blade_outlet_angle_allowed_deg";
        public const string BladeCountAllowed = "blade_count_allowed";
    }

    // Calculation formula identifiers (see formula.json). Rule-driven values use the design rule ID instead.
    private const string FDiameter = "CFAN-F-001";
    private const string FTipSpeed = "CFAN-F-002";
    private const string FWidth = "CFAN-F-003";
    private const string FInlet = "CFAN-F-004";
    private const string FSpecificSpeed = "CFAN-F-005";
    private const string FInletFlowAngle = "CFAN-F-006";
    private const string FSlip = "CFAN-F-007";
    private const string FTotalPressure = "CFAN-F-008";
    private const string FEuler = "CFAN-F-009";
    private const string FOutletAngle = "CFAN-F-010";
    private const string FShaftPower = "CFAN-F-011";
    private const string FBladeThickness = "CFAN-F-012";
    private const string FPlate = "CFAN-F-013";
    private const string FHub = "CFAN-F-014";
    private const string FCasingBase = "CFAN-F-016";
    private const string FCasingWidth = "CFAN-F-017";
    private const string FCasingWall = "CFAN-F-018";
    private const string FOutletHeight = "CFAN-F-019";
    private const string FCasingInlet = "CFAN-F-020";

    private const string RefSizing = "Centrifugal fan dimensional analysis and impeller proportions; Eck, Fans; Wright, Fluid Machinery";
    private const string RefKinematics = "Velocity triangle kinematics; Wright, Fluid Machinery";
    private const string RefBernoulli = "Bernoulli equation; total pressure = static pressure + velocity pressure";
    private const string RefEuler = "Euler turbomachinery equation; Wright, Fluid Machinery; Eck, Fans";
    private const string RefSlip = "Wiesner, A Review of Slip Factors for Centrifugal Impellers, J. Eng. Power, 1967";
    private const string RefPower = "Fan air power and static efficiency definition; AMCA 210 / ISO 5801";

    private readonly IDesignRulesProvider _rulesProvider;

    public CentrifugalFanCalculator(IDesignRulesProvider rulesProvider)
    {
        _rulesProvider = rulesProvider ?? throw new ArgumentNullException(nameof(rulesProvider));
    }

    public EquipmentType EquipmentType => EquipmentType.CentrifugalFan;

    public string CalculatorId => Id;

    public string Version => CalculatorVersion;

    public InputValidationResult Validate(EquipmentInputs inputs)
    {
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var issues = new List<InputIssue>();
        var missing = new List<string>();
        var metadata = EquipmentRegistry.Get(EquipmentType.CentrifugalFan);

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

        if (inputs.TryGetValue(Keys.BladeType, out var bladeCode)
            && double.IsFinite(bladeCode)
            && bladeCode > 0.0
            && !CentrifugalBladeTypes.TryFromCode(bladeCode, out _))
        {
            issues.Add(new InputIssue(
                "INPUT_INVALID",
                "Blade Type must be 1 (forward curved), 2 (backward curved), 3 (backward airfoil) or 4 (radial).",
                Keys.BladeType));
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
                EquipmentType.CentrifugalFan,
                validation.BlockingIssues.Select(i => i.Message),
                Version,
                null,
                Id);
        }

        if (!TryGetRules(out var rules, out var rulesIssue))
        {
            return CalculationResult.Failure(EquipmentType.CentrifugalFan, new[] { rulesIssue!.Message }, Version, null, Id);
        }

        if (!CentrifugalBladeTypes.TryFromCode(inputs[Keys.BladeType], out var bladeType))
        {
            return CalculationResult.Failure(
                EquipmentType.CentrifugalFan,
                new[] { "Blade Type must be 1 (forward curved), 2 (backward curved), 3 (backward airfoil) or 4 (radial)." },
                Version,
                null,
                Id);
        }

        try
        {
            return Compute(inputs, rules!, bladeType);
        }
        catch (DesignRuleNotFoundException ex)
        {
            return CalculationResult.Failure(EquipmentType.CentrifugalFan, new[] { ex.Message }, Version, null, Id);
        }
        catch (InvalidOperationException ex)
        {
            return CalculationResult.Failure(EquipmentType.CentrifugalFan, new[] { ex.Message }, Version, null, Id);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Core calculation
    // ------------------------------------------------------------------------------------------------

    private CalculationResult Compute(EquipmentInputs inputs, DesignRuleSet rules, CentrifugalBladeType bladeType)
    {
        var warnings = new List<EngineeringWarning>();
        var builder = CalculationResult.Create(EquipmentType.CentrifugalFan, Version).ForCalculator(Id);

        var prefix = CentrifugalBladeTypes.RulePrefix(bladeType);
        var typeName = CentrifugalBladeTypes.DisplayName(bladeType);

        DesignRule TypeRule(string suffix) => rules.Get(prefix + "." + suffix);

        double q = inputs[Keys.AirflowM3s];
        double ps = inputs[Keys.StaticPressurePa];
        double omega = inputs[Keys.SpeedRadS];

        // Air density
        var densityRule = rules.Get(RuleKeys.StandardAirDensity);
        double rho = inputs.TryGetValue(Keys.AirDensityKgM3, out var suppliedRho) ? suppliedRho : densityRule.Resolve();
        if (!inputs.ContainsKey(Keys.AirDensityKgM3))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_DENSITY_DEFAULTED",
                $"Air density was not supplied. Standard air density {Format(rho)} kg/m3 was used.",
                WarningSeverity.Info,
                Keys.AirDensityKgM3,
                densityRule.Id));
        }

        builder.AddValue(Keys.AirDensityUsedKgM3, "Air Density Used", rho, "kg/m3", densityRule.Id, densityRule.SourceReference);

        // 1. Outer diameter and tip speed from the static pressure coefficient
        var psiRule = TypeRule(TypeRuleKeys.DesignStaticPressureCoefficient);
        double psi = psiRule.Resolve();
        var psiAllowed = rules.Get(RuleKeys.PressureCoefficientAllowed);
        if (!psiAllowed.IsWithinRange(psi))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_PRESSURE_COEFFICIENT_OUT_OF_RANGE",
                $"Design static pressure coefficient {Format(psi)} for the {typeName} blade type is outside the supported range {FormatRange(psiAllowed)}.",
                WarningSeverity.Critical,
                Keys.StaticPressureCoefficient,
                psiAllowed.Id));
        }

        double u2 = Math.Sqrt(2.0 * ps / (rho * psi));
        double d2 = 2.0 * u2 / omega;
        double r2 = d2 / 2.0;

        builder.AddValue(Keys.StaticPressureCoefficient, "Static Pressure Coefficient", psi, "-", psiRule.Id, psiRule.SourceReference,
            "psi_s = Ps / (0.5 * rho * U2^2)");
        builder.AddValue(Keys.TipSpeedMs, "Tip Speed", u2, "m/s", FTipSpeed, RefKinematics,
            "U2 = sqrt(2 * Ps / (rho * psi_s))");
        builder.AddValue(Keys.ImpellerOuterDiameterM, "Impeller Outer Diameter", d2, "m", FDiameter, RefSizing,
            "D2 = 2 * U2 / omega");

        var diameterAllowed = rules.Get(RuleKeys.DiameterAllowed);
        if (!diameterAllowed.IsWithinRange(d2))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_DIAMETER_OUT_OF_RANGE",
                $"Calculated impeller outer diameter {Format(d2)} m is outside the supported range {FormatRange(diameterAllowed)} m. Increase or decrease speed to move the design into range.",
                WarningSeverity.Critical,
                Keys.ImpellerOuterDiameterM,
                diameterAllowed.Id));
        }

        var cautionTipSpeed = TypeRule(TypeRuleKeys.TipSpeedCaution);
        var criticalTipSpeed = TypeRule(TypeRuleKeys.TipSpeedCritical);
        double cautionValue = cautionTipSpeed.Resolve();
        double criticalValue = criticalTipSpeed.Resolve();

        if (u2 > criticalValue)
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_TIP_SPEED_CRITICAL",
                $"Tip speed {Format(u2)} m/s exceeds the validated limit of {Format(criticalValue)} m/s for the {typeName} blade type. Reduce rotational speed.",
                WarningSeverity.Critical,
                Keys.TipSpeedMs,
                criticalTipSpeed.Id));
        }
        else if (u2 > cautionValue)
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_TIP_SPEED_HIGH",
                $"Tip speed {Format(u2)} m/s exceeds {Format(cautionValue)} m/s for the {typeName} blade type. Expect elevated noise and impeller stress.",
                WarningSeverity.Caution,
                Keys.TipSpeedMs,
                cautionTipSpeed.Id));
        }

        // 2. Outlet blade width from the flow coefficient
        var phiRule = TypeRule(TypeRuleKeys.DesignFlowCoefficient);
        double phi = phiRule.Resolve();
        var phiAllowed = rules.Get(RuleKeys.FlowCoefficientAllowed);
        if (!phiAllowed.IsWithinRange(phi))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_FLOW_COEFFICIENT_OUT_OF_RANGE",
                $"Design flow coefficient {Format(phi)} for the {typeName} blade type is outside the supported range {FormatRange(phiAllowed)}.",
                WarningSeverity.Critical,
                Keys.FlowCoefficient,
                phiAllowed.Id));
        }

        double cr2 = phi * u2;
        double b2 = q / (Math.PI * d2 * cr2);

        builder.AddValue(Keys.FlowCoefficient, "Design Flow Coefficient", phi, "-", phiRule.Id, phiRule.SourceReference);
        builder.AddValue(Keys.ImpellerWidthM, "Impeller Blade Width", b2, "m", FWidth, RefSizing,
            "b2 = Q / (pi * D2 * phi2 * U2)");

        var widthAllowed = TypeRule(TypeRuleKeys.WidthToDiameterAllowed);
        double widthRatio = b2 / d2;
        if (!widthAllowed.IsWithinRange(widthRatio))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_WIDTH_RATIO_OUT_OF_RANGE",
                $"Blade width to diameter ratio {Format(widthRatio)} is outside the supported range {FormatRange(widthAllowed)} for the {typeName} blade type. Change the speed or select another blade type.",
                WarningSeverity.Critical,
                Keys.ImpellerWidthM,
                widthAllowed.Id));
        }

        // 3. Inlet diameter and inlet width
        var diameterRatioRule = TypeRule(TypeRuleKeys.InletToOutletDiameterRatio);
        double diameterRatio = diameterRatioRule.Resolve();
        var diameterRatioAllowed = rules.Get(RuleKeys.DiameterRatioAllowed);
        if (!diameterRatioAllowed.IsWithinRange(diameterRatio))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_INLET_RATIO_OUT_OF_RANGE",
                $"Inlet to outlet diameter ratio {Format(diameterRatio)} for the {typeName} blade type is outside the supported range {FormatRange(diameterRatioAllowed)}.",
                WarningSeverity.Critical,
                Keys.ImpellerInletDiameterM,
                diameterRatioAllowed.Id));
        }

        var widthRatioRule = TypeRule(TypeRuleKeys.InletToOutletWidthRatio);
        double d1 = diameterRatio * d2;
        double b1 = widthRatioRule.Resolve() * b2;

        builder.AddValue(Keys.ImpellerInletDiameterM, "Impeller Inlet Diameter", d1, "m", FInlet, diameterRatioRule.SourceReference,
            "D1 = (D1/D2) * D2");
        builder.AddValue(Keys.BladeInletWidthM, "Blade Inlet Width", b1, "m", FInlet, widthRatioRule.SourceReference,
            "b1 = (b1/b2) * b2");

        double cr1 = q / (Math.PI * d1 * b1);
        double u1 = omega * d1 / 2.0;
        double beta1Flow = Math.Atan(cr1 / u1) * 180.0 / Math.PI;
        builder.AddValue(Keys.InletFlowAngleDeg, "Inlet Flow Angle (reference)", beta1Flow, "deg", FInletFlowAngle, RefKinematics,
            "beta1_flow = atan(cr1 / U1), cr1 = Q / (pi * D1 * b1), U1 = omega * D1 / 2");

        // 4. Specific speed
        double omegaS = omega * Math.Sqrt(q) / Math.Pow(ps / rho, 0.75);
        builder.AddValue(Keys.SpecificSpeed, "Specific Speed", omegaS, "-", FSpecificSpeed, RefSizing,
            "omega_s = omega * sqrt(Q) / (Ps / rho)^0.75");

        var omegaSAllowed = rules.Get(RuleKeys.SpecificSpeedAllowed);
        if (!omegaSAllowed.IsWithinRange(omegaS))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_SPECIFIC_SPEED_OUT_OF_RANGE",
                $"Specific speed {Format(omegaS)} is outside the usual range {FormatRange(omegaSAllowed)} for a centrifugal fan. Another fan type may suit this duty better.",
                WarningSeverity.Caution,
                Keys.SpecificSpeed,
                omegaSAllowed.Id));
        }

        // 5. Blade count
        var bladeCountRule = TypeRule(TypeRuleKeys.BladeCount);
        int bladeCount = (int)Math.Round(bladeCountRule.Resolve(), MidpointRounding.AwayFromZero);
        var bladeCountAllowed = TypeRule(TypeRuleKeys.BladeCountAllowed);
        if (!bladeCountAllowed.IsWithinRange(bladeCount))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_BLADE_COUNT_OUT_OF_RANGE",
                $"Selected blade count {bladeCount} is outside the supported range {FormatRange(bladeCountAllowed)} for the {typeName} blade type.",
                WarningSeverity.Critical,
                Keys.BladeCount,
                bladeCountAllowed.Id));
        }

        builder.AddValue(Keys.BladeCount, "Blade Count", bladeCount, "-", bladeCountRule.Id, bladeCountRule.SourceReference);

        // 6. Slip, total pressure, Euler head
        var nominalOutletAngleRule = TypeRule(TypeRuleKeys.BladeOutletAngle);
        double beta2Nominal = nominalOutletAngleRule.Resolve();
        double sinBeta2Nominal = Math.Sin(beta2Nominal * Math.PI / 180.0);
        var slipExponent = rules.Get(RuleKeys.SlipExponent);

        if (sinBeta2Nominal <= 0.0)
        {
            return CalculationResult.Failure(
                EquipmentType.CentrifugalFan,
                new[] { $"Nominal blade outlet angle {Format(beta2Nominal)} deg for the {typeName} blade type is not between 0 and 180 degrees." },
                Version,
                warnings,
                Id);
        }

        double sigma = 1.0 - Math.Sqrt(sinBeta2Nominal) / Math.Pow(bladeCount, slipExponent.Resolve());

        if (sigma <= 0.0)
        {
            return CalculationResult.Failure(
                EquipmentType.CentrifugalFan,
                new[] { "The slip factor is not positive for this blade count and outlet angle. Check the blade count rule." },
                Version,
                warnings,
                Id);
        }

        builder.AddValue(Keys.SlipFactor, "Slip Factor", sigma, "-", FSlip, RefSlip,
            "sigma = 1 - sqrt(sin(beta2_nominal)) / Z^0.7");

        var outletVelocityRule = rules.Get(RuleKeys.OutletVelocityRatio);
        double vOut = outletVelocityRule.Resolve() * u2;
        double pt = ps + 0.5 * rho * vOut * vOut;
        builder.AddValue(Keys.TotalPressurePa, "Total Pressure Rise", pt, "Pa", FTotalPressure, RefBernoulli,
            "Pt = Ps + 0.5 * rho * Vout^2");

        var etaHydRule = TypeRule(TypeRuleKeys.HydraulicEfficiency);
        double etaH = etaHydRule.Resolve();
        double cu2 = pt / (rho * etaH * u2);
        double eulerHead = rho * u2 * cu2;
        builder.AddValue(Keys.EulerHeadPa, "Euler Pressure Rise", eulerHead, "Pa", FEuler, RefEuler,
            "dp_euler = rho * U2 * Cu2, Cu2 = Pt / (rho * eta_h * U2)");

        // 7. Blade outlet angle from the outlet velocity triangle
        double beta2 = UnitConversions.RadiansToDegrees(Math.Atan2(cr2, sigma * u2 - cu2));
        builder.AddValue(Keys.BladeOutletAngleDeg, "Blade Outlet Angle", beta2, "deg", FOutletAngle, RefEuler,
            "beta2 = atan2(cr2, sigma * U2 - Cu2)");

        var outletAngleAllowed = TypeRule(TypeRuleKeys.BladeOutletAngleAllowed);
        if (!outletAngleAllowed.IsWithinRange(beta2))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_BLADE_OUTLET_ANGLE_OUT_OF_RANGE",
                $"Blade outlet angle {Format(beta2)} deg is outside the supported range {FormatRange(outletAngleAllowed)} deg for the {typeName} blade type. This duty does not suit that blade type at this speed; change the speed or select another blade type.",
                WarningSeverity.Critical,
                Keys.BladeOutletAngleDeg,
                outletAngleAllowed.Id));
        }

        // 8. Blade inlet angle, profile and thickness
        var inletAngleRule = TypeRule(TypeRuleKeys.BladeInletAngle);
        builder.AddValue(Keys.BladeInletAngleDeg, "Blade Inlet Angle", inletAngleRule.Resolve(), "deg", inletAngleRule.Id, inletAngleRule.SourceReference);

        var profileRule = TypeRule(TypeRuleKeys.BladeProfileCode);
        builder.AddValue(Keys.BladeProfileCode, "Blade Profile", profileRule.Resolve(), "-", profileRule.Id, profileRule.SourceReference);

        var thicknessFraction = TypeRule(TypeRuleKeys.BladeThicknessFraction);
        var thicknessMin = rules.Get(RuleKeys.BladeThicknessMin);
        double bladeThickness = Math.Max(thicknessFraction.Resolve() * d2, thicknessMin.Resolve());
        builder.AddValue(Keys.BladeThicknessM, "Blade Thickness", bladeThickness, "m", FBladeThickness, thicknessFraction.SourceReference,
            "t = max(fraction * D2, minimum)");

        // 9. Impeller plates and hub
        var plateFraction = rules.Get(RuleKeys.PlateThicknessFraction);
        var plateMin = rules.Get(RuleKeys.PlateThicknessMin);
        double plateThickness = Math.Max(plateFraction.Resolve() * d2, plateMin.Resolve());
        builder.AddValue(Keys.PlateThicknessM, "Impeller Plate Thickness", plateThickness, "m", FPlate, plateFraction.SourceReference,
            "t = max(fraction * D2, minimum)");

        var hubRule = rules.Get(RuleKeys.HubToInletRatio);
        double hubDiameter = hubRule.Resolve() * d1;
        builder.AddValue(Keys.HubDiameterM, "Hub Diameter", hubDiameter, "m", FHub, hubRule.SourceReference,
            "D_hub = k_hub * D1");

        // 10. Scroll casing
        var cutoffFraction = rules.Get(RuleKeys.CutoffClearanceFraction);
        var cutoffMin = rules.Get(RuleKeys.CutoffClearanceMin);
        double cutoff = Math.Max(cutoffFraction.Resolve() * d2, cutoffMin.Resolve());
        double d3 = d2 + 2.0 * cutoff;
        builder.AddValue(Keys.CasingCutoffClearanceM, "Cutoff Clearance", cutoff, "m", FCasingBase, cutoffFraction.SourceReference,
            "c = max(fraction * D2, minimum)");
        builder.AddValue(Keys.CasingBaseDiameterM, "Casing Base Circle Diameter", d3, "m", FCasingBase, cutoffFraction.SourceReference,
            "D3 = D2 + 2 * c");

        var widthRatioCasing = rules.Get(RuleKeys.CasingWidthRatio);
        double casingWidth = widthRatioCasing.Resolve() * (Math.Max(b1, b2) + 2.0 * plateThickness);
        builder.AddValue(Keys.CasingWidthM, "Casing Inner Width", casingWidth, "m", FCasingWidth, widthRatioCasing.SourceReference,
            "W = k_w * (max(b1, b2) + 2 * t_plate)");

        var wallFraction = rules.Get(RuleKeys.CasingWallFraction);
        var wallMin = rules.Get(RuleKeys.CasingWallMin);
        double wall = Math.Max(wallFraction.Resolve() * d3, wallMin.Resolve());
        builder.AddValue(Keys.CasingWallThicknessM, "Casing Wall Thickness", wall, "m", FCasingWall, wallFraction.SourceReference,
            "t = max(fraction * D3, minimum)");

        double outletHeight = q / (vOut * casingWidth);
        builder.AddValue(Keys.OutletHeightM, "Outlet Height", outletHeight, "m", FOutletHeight, outletVelocityRule.SourceReference,
            "h_out = Q / (Vout * W)");

        if (outletHeight > d2)
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_OUTLET_HEIGHT_LARGE",
                $"Outlet height {Format(outletHeight)} m is larger than the impeller diameter {Format(d2)} m. The scroll will be very large for this duty.",
                WarningSeverity.Caution,
                Keys.OutletHeightM,
                outletVelocityRule.Id));
        }

        var casingInletRule = rules.Get(RuleKeys.CasingInletRatio);
        double casingInlet = casingInletRule.Resolve() * d1;
        builder.AddValue(Keys.InletEyeDiameterM, "Casing Inlet Diameter", casingInlet, "m", FCasingInlet, casingInletRule.SourceReference,
            "D_inlet = k_in * D1");

        // 11. Shaft power estimate
        var defaultEff = TypeRule(TypeRuleKeys.DefaultStaticEfficiency);
        double etaS = inputs.TryGetValue(Keys.StaticEfficiency, out var suppliedEta) ? suppliedEta : defaultEff.Resolve();
        if (!inputs.ContainsKey(Keys.StaticEfficiency))
        {
            warnings.Add(new EngineeringWarning(
                "CFAN_EFFICIENCY_DEFAULTED",
                $"Static efficiency was not supplied. {Format(etaS)} was assumed for the shaft power estimate ({typeName} blade type).",
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
        if (_rulesProvider.TryGetRuleSet(EquipmentType.CentrifugalFan, out var found))
        {
            rules = found;
            issue = null;
            return true;
        }

        rules = null;
        issue = new InputIssue(
            "RULES_UNAVAILABLE",
            "Centrifugal fan design rules are not loaded. Add centrifugal-fans/design-rules.json to the engineering knowledge base.");
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