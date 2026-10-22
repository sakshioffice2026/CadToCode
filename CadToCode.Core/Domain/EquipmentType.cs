using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CadToCode.Core.Domain;

/// <summary>
/// Registry of engineering equipment domains supported by the Core Engineering Assistant.
/// </summary>
public enum EquipmentType
{
    /// <summary>Unclassified. Must never be silently mapped to a generic pipe.</summary>
    Unknown = 0,
    AxialFan = 1,
    Cyclone = 2,
    WetScrubber = 3,
    PressureVessel = 4,
    PipingLayout = 5
}

/// <summary>
/// Maturity of the deterministic calculator backing an equipment type.
/// </summary>
public enum CalculatorStatus
{
    NotImplemented = 0,
    Preview = 1,
    Released = 2
}

/// <summary>
/// Describes a single input or output parameter of an equipment type.
/// All values are expressed in SI units internally.
/// </summary>
/// <param name="Key">Stable machine key (e.g. "airflow_m3s").</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="SiUnit">Internal SI unit symbol (e.g. "m3/s", "Pa", "rad/s", "m").</param>
/// <param name="IsRequired">True when the calculator cannot run without the value.</param>
/// <param name="Description">Short description for UI and clarifying prompts.</param>
public sealed record ParameterDescriptor(
    string Key,
    string DisplayName,
    string SiUnit,
    bool IsRequired,
    string Description);

/// <summary>
/// Immutable metadata for an equipment type.
/// </summary>
public sealed record EquipmentMetadata
{
    public EquipmentType Type { get; }
    public string Code { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public CalculatorStatus CalculatorStatus { get; }
    public IReadOnlyList<ParameterDescriptor> Inputs { get; }
    public IReadOnlyList<ParameterDescriptor> Outputs { get; }
    public IReadOnlyList<string> Aliases { get; }
    public IReadOnlyList<string> KnowledgeFolders { get; }

    public EquipmentMetadata(
        EquipmentType type,
        string code,
        string displayName,
        string description,
        CalculatorStatus calculatorStatus,
        IEnumerable<ParameterDescriptor> inputs,
        IEnumerable<ParameterDescriptor> outputs,
        IEnumerable<string> aliases,
        IEnumerable<string> knowledgeFolders)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("DisplayName is required.", nameof(displayName));

        Type = type;
        Code = code;
        DisplayName = displayName;
        Description = description ?? string.Empty;
        CalculatorStatus = calculatorStatus;
        Inputs = new ReadOnlyCollection<ParameterDescriptor>((inputs ?? Enumerable.Empty<ParameterDescriptor>()).ToList());
        Outputs = new ReadOnlyCollection<ParameterDescriptor>((outputs ?? Enumerable.Empty<ParameterDescriptor>()).ToList());
        Aliases = new ReadOnlyCollection<string>((aliases ?? Enumerable.Empty<string>()).ToList());
        KnowledgeFolders = new ReadOnlyCollection<string>((knowledgeFolders ?? Enumerable.Empty<string>()).ToList());
    }

    /// <summary>Required inputs only.</summary>
    public IEnumerable<ParameterDescriptor> RequiredInputs => Inputs.Where(i => i.IsRequired);

    /// <summary>True when a calculator is available and may be used to drive CAD generation.</summary>
    public bool CanDriveCadGeneration => CalculatorStatus != CalculatorStatus.NotImplemented;
}

/// <summary>
/// Central, immutable registry of equipment metadata.
/// </summary>
public static class EquipmentRegistry
{
    private static readonly IReadOnlyDictionary<EquipmentType, EquipmentMetadata> Items = Build();

    /// <summary>All registered equipment types (excludes <see cref="EquipmentType.Unknown"/>).</summary>
    public static IReadOnlyCollection<EquipmentMetadata> All => Items.Values.ToList().AsReadOnly();

    /// <summary>Gets metadata for an equipment type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for <see cref="EquipmentType.Unknown"/> or unregistered values.</exception>
    public static EquipmentMetadata Get(EquipmentType type)
    {
        if (Items.TryGetValue(type, out var metadata))
            return metadata;

        throw new ArgumentOutOfRangeException(nameof(type), type,
            "Equipment type is not registered. Unknown equipment must be resolved through user clarification.");
    }

    /// <summary>Attempts to get metadata for an equipment type.</summary>
    public static bool TryGet(EquipmentType type, out EquipmentMetadata metadata)
        => Items.TryGetValue(type, out metadata!);

    /// <summary>
    /// Resolves an equipment type from free text (code, display name, or alias).
    /// Returns <see cref="EquipmentType.Unknown"/> when no confident match exists.
    /// Never defaults to a piping type.
    /// </summary>
    public static EquipmentType Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return EquipmentType.Unknown;

        var normalized = Normalize(text);

        foreach (var item in Items.Values)
        {
            if (Normalize(item.Code) == normalized || Normalize(item.DisplayName) == normalized)
                return item.Type;
        }

        EquipmentType best = EquipmentType.Unknown;
        int bestLength = 0;
        var matchedTypes = new HashSet<EquipmentType>();

        foreach (var item in Items.Values)
        {
            foreach (var alias in item.Aliases)
            {
                var a = Normalize(alias);
                if (a.Length == 0) continue;

                if (normalized == a)
                    return item.Type;

                if (normalized.Contains(a, StringComparison.Ordinal))
                {
                    matchedTypes.Add(item.Type);
                    if (a.Length > bestLength)
                    {
                        bestLength = a.Length;
                        best = item.Type;
                    }
                }
            }
        }

        // Ambiguous matches across different equipment types are not resolved silently.
        return matchedTypes.Count == 1 ? best : EquipmentType.Unknown;
    }

    private static string Normalize(string value)
        => new string(value.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray())
            .Replace("  ", " ");

    private static IReadOnlyDictionary<EquipmentType, EquipmentMetadata> Build()
    {
        var list = new List<EquipmentMetadata>
        {
            new(
                EquipmentType.AxialFan,
                "AXIAL_FAN",
                "Axial Fan",
                "Propeller or tube-axial fan sized from airflow, static pressure and rotational speed.",
                CalculatorStatus.Released,
                new[]
                {
                    new ParameterDescriptor("airflow_m3s", "Airflow", "m3/s", true, "Required volumetric flow rate."),
                    new ParameterDescriptor("static_pressure_pa", "Static Pressure", "Pa", true, "Required fan static pressure."),
                    new ParameterDescriptor("speed_rad_s", "Rotational Speed", "rad/s", true, "Shaft angular speed (derived from RPM)."),
                    new ParameterDescriptor("air_density_kgm3", "Air Density", "kg/m3", false, "Defaults to standard air (1.204 kg/m3) when not given."),
                    new ParameterDescriptor("static_efficiency", "Static Efficiency", "-", false, "Target static efficiency (0-1).")
                },
                new[]
                {
                    new ParameterDescriptor("tip_diameter_m", "Tip Diameter", "m", true, "Impeller outer diameter."),
                    new ParameterDescriptor("hub_diameter_m", "Hub Diameter", "m", true, "Hub outer diameter."),
                    new ParameterDescriptor("hub_to_tip_ratio", "Hub-to-Tip Ratio", "-", true, "Hub diameter divided by tip diameter."),
                    new ParameterDescriptor("tip_speed_ms", "Tip Speed", "m/s", true, "Blade tip peripheral speed."),
                    new ParameterDescriptor("blade_count", "Blade Count", "-", true, "Number of blades."),
                    new ParameterDescriptor("blade_angle_hub_deg", "Blade Angle at Hub", "deg", true, "Blade pitch angle at hub."),
                    new ParameterDescriptor("blade_angle_tip_deg", "Blade Angle at Tip", "deg", true, "Blade pitch angle at tip."),
                    new ParameterDescriptor("casing_inner_diameter_m", "Casing Inner Diameter", "m", true, "Casing bore including tip clearance."),
                    new ParameterDescriptor("casing_outer_diameter_m", "Casing Outer Diameter", "m", true, "Casing outer diameter."),
                    new ParameterDescriptor("casing_length_m", "Casing Length", "m", true, "Axial casing length.")
                },
                new[] { "axial fan", "axial flow fan", "tube axial", "tubeaxial", "vane axial", "vaneaxial", "propeller fan", "axial blower" },
                new[] { "fans", "common" }),

            new(
                EquipmentType.Cyclone,
                "CYCLONE",
                "Cyclone Separator",
                "Reverse-flow cyclone separator for gas-solid separation.",
                CalculatorStatus.NotImplemented,
                new[]
                {
                    new ParameterDescriptor("airflow_m3s", "Gas Flow", "m3/s", true, "Inlet gas volumetric flow."),
                    new ParameterDescriptor("inlet_velocity_ms", "Inlet Velocity", "m/s", false, "Target inlet velocity."),
                    new ParameterDescriptor("cut_diameter_m", "Cut Diameter", "m", false, "Target d50 particle size.")
                },
                new[]
                {
                    new ParameterDescriptor("barrel_diameter_m", "Barrel Diameter", "m", true, "Cylindrical body diameter."),
                    new ParameterDescriptor("cone_height_m", "Cone Height", "m", true, "Conical section height."),
                    new ParameterDescriptor("vortex_finder_diameter_m", "Vortex Finder Diameter", "m", true, "Gas outlet diameter.")
                },
                new[] { "cyclone", "cyclone separator", "dust cyclone", "reverse flow cyclone", "centrifugal separator" },
                new[] { "cyclones", "common" }),

            new(
                EquipmentType.WetScrubber,
                "WET_SCRUBBER",
                "Industrial Wet Scrubber",
                "Wet scrubber (spray tower) for gas cleaning, sized from gas flow, gas velocity and residence time.",
                CalculatorStatus.Preview,
                new[]
                {
                    new ParameterDescriptor("airflow_m3s", "Gas Flow", "m3/s", true, "Inlet gas volumetric flow."),
                    new ParameterDescriptor("gas_velocity_ms", "Superficial Gas Velocity", "m/s", false, "Gas velocity through the empty tower cross-section."),
                    new ParameterDescriptor("gas_residence_time_s", "Gas Residence Time", "s", false, "Gas contact time in the spray zone."),
                    new ParameterDescriptor("inlet_velocity_ms", "Inlet Nozzle Velocity", "m/s", false, "Gas velocity in the side inlet nozzle."),
                    new ParameterDescriptor("stack_velocity_ms", "Outlet Stack Velocity", "m/s", false, "Gas velocity in the outlet stack."),
                    new ParameterDescriptor("liquid_to_gas_ratio_lm3", "Liquid-to-Gas Ratio", "L/m3", false, "Scrubbing liquid per unit gas volume."),
                    new ParameterDescriptor("wall_thickness_m", "Wall Thickness", "m", false, "Shell wall thickness.")
                },
                new[]
                {
                    new ParameterDescriptor("vessel_inside_diameter_m", "Vessel Inside Diameter", "m", true, "Cylindrical shell inside diameter."),
                    new ParameterDescriptor("shell_height_m", "Shell Height", "m", true, "Cylindrical shell straight height."),
                    new ParameterDescriptor("cone_height_m", "Cone Height", "m", true, "Conical bottom height."),
                    new ParameterDescriptor("inlet_diameter_m", "Inlet Diameter", "m", true, "Side gas inlet nozzle diameter."),
                    new ParameterDescriptor("stack_diameter_m", "Stack Diameter", "m", true, "Gas outlet stack diameter."),
                    new ParameterDescriptor("liquid_flow_ls", "Scrubbing Liquid Flow", "L/s", false, "Scrubbing liquid flow from the liquid-to-gas ratio.")
                },
                new[] { "wet scrubber", "scrubber", "packed tower", "spray tower", "venturi scrubber", "gas scrubber", "packed bed scrubber" },
                new[] { "scrubbers", "common" }),

            new(
                EquipmentType.PressureVessel,
                "PRESSURE_VESSEL",
                "Pressure Vessel",
                "Cylindrical pressure vessel with formed heads and nozzles.",
                CalculatorStatus.NotImplemented,
                new[]
                {
                    new ParameterDescriptor("design_pressure_pa", "Design Pressure", "Pa", true, "Internal design pressure."),
                    new ParameterDescriptor("inside_diameter_m", "Inside Diameter", "m", true, "Shell inside diameter."),
                    new ParameterDescriptor("design_temperature_k", "Design Temperature", "K", true, "Design metal temperature.")
                },
                new[]
                {
                    new ParameterDescriptor("shell_thickness_m", "Shell Thickness", "m", true, "Required shell wall thickness."),
                    new ParameterDescriptor("head_thickness_m", "Head Thickness", "m", true, "Required head thickness.")
                },
                new[] { "pressure vessel", "vessel", "air receiver", "boiler drum", "separator vessel", "storage tank pressurized" },
                new[] { "vessels", "common" }),

            new(
                EquipmentType.PipingLayout,
                "PIPING_LAYOUT",
                "Piping Layout",
                "Pipe runs, fittings and routing between equipment connections.",
                CalculatorStatus.NotImplemented,
                new[]
                {
                    new ParameterDescriptor("flow_rate_m3s", "Flow Rate", "m3/s", true, "Fluid volumetric flow."),
                    new ParameterDescriptor("design_pressure_pa", "Design Pressure", "Pa", true, "Line design pressure."),
                    new ParameterDescriptor("target_velocity_ms", "Target Velocity", "m/s", false, "Preferred line velocity.")
                },
                new[]
                {
                    new ParameterDescriptor("nominal_diameter_m", "Nominal Diameter", "m", true, "Selected pipe diameter."),
                    new ParameterDescriptor("wall_thickness_m", "Wall Thickness", "m", true, "Required pipe wall thickness.")
                },
                new[] { "piping layout", "pipe layout", "pipe run", "pipe routing", "piping system", "pipeline layout", "pipe spool" },
                new[] { "piping", "common" })
        };

        return new ReadOnlyDictionary<EquipmentType, EquipmentMetadata>(list.ToDictionary(m => m.Type));
    }
}