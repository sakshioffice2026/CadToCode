using System.Globalization;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds a STEP-generating C# script for an axial fan from validated SI design values.
/// Every required value must be supplied; nothing is defaulted or substituted silently.
/// Script geometry is emitted in millimetres to match the existing STEP sandbox templates.
/// </summary>
public static class AxialFanScriptComposer
{
    public const string TipDiameterM = "tip_diameter_m";
    public const string HubDiameterM = "hub_diameter_m";
    public const string TipClearanceM = "tip_clearance_m";
    public const string CasingWallM = "casing_wall_m";
    public const string CasingLengthM = "casing_length_m";
    public const string BladeCount = "blade_count";
    public const string BladeChordM = "blade_chord_m";
    public const string BladeThicknessM = "blade_thickness_m";
    public const string BladePitchDeg = "blade_pitch_deg";

    private const string FileBaseName = "AxialFan";
    private const int MinBlades = 2;
    private const int MaxBlades = 64;

    private static readonly string[] RequiredKeys =
    {
        TipDiameterM, HubDiameterM, TipClearanceM, CasingWallM, CasingLengthM,
        BladeCount, BladeChordM, BladeThicknessM, BladePitchDeg
    };

    public static ComposedScriptResult Compose(IReadOnlyDictionary<string, double> valuesSi)
    {
        ArgumentNullException.ThrowIfNull(valuesSi);

        var values = new Dictionary<string, double>(valuesSi, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var key in RequiredKeys)
        {
            if (!values.TryGetValue(key, out var v))
                errors.Add($"Missing required value '{key}'.");
            else if (!double.IsFinite(v))
                errors.Add($"Value '{key}' must be a finite number.");
        }

        if (errors.Count > 0)
            return ComposedScriptResult.Fail(errors.ToArray());

        var tip = values[TipDiameterM];
        var hub = values[HubDiameterM];
        var clearance = values[TipClearanceM];
        var wall = values[CasingWallM];
        var length = values[CasingLengthM];
        var countRaw = values[BladeCount];
        var chord = values[BladeChordM];
        var thickness = values[BladeThicknessM];
        var pitch = values[BladePitchDeg];

        if (tip <= 0) errors.Add($"'{TipDiameterM}' must be greater than zero.");
        if (hub <= 0) errors.Add($"'{HubDiameterM}' must be greater than zero.");
        if (clearance < 0) errors.Add($"'{TipClearanceM}' must not be negative.");
        if (wall <= 0) errors.Add($"'{CasingWallM}' must be greater than zero.");
        if (length <= 0) errors.Add($"'{CasingLengthM}' must be greater than zero.");
        if (chord <= 0) errors.Add($"'{BladeChordM}' must be greater than zero.");
        if (thickness <= 0) errors.Add($"'{BladeThicknessM}' must be greater than zero.");

        if (Math.Abs(countRaw - Math.Round(countRaw)) > 1e-9)
            errors.Add($"'{BladeCount}' must be a whole number.");

        var count = (int)Math.Round(countRaw);
        if (count < MinBlades || count > MaxBlades)
            errors.Add($"'{BladeCount}' must be between {MinBlades} and {MaxBlades}.");

        if (pitch <= -90 || pitch >= 90)
            errors.Add($"'{BladePitchDeg}' must be between -90 and 90 degrees (exclusive).");

        if (errors.Count == 0)
        {
            var span = tip / 2.0 - clearance - hub / 2.0;

            if (hub >= tip)
                errors.Add($"'{HubDiameterM}' must be smaller than '{TipDiameterM}'.");
            else if (span <= 0)
                errors.Add("Blade span is not positive: hub radius plus tip clearance must be smaller than tip radius.");

            if (chord * Math.Cos(pitch * Math.PI / 180.0) + thickness * Math.Sin(Math.Abs(pitch) * Math.PI / 180.0) > length)
                errors.Add($"Blade axial extent exceeds '{CasingLengthM}'.");

            if (thickness >= chord)
                errors.Add($"'{BladeThicknessM}' must be smaller than '{BladeChordM}'.");
        }

        if (errors.Count > 0)
            return ComposedScriptResult.Fail(errors.ToArray());

        var script = Template
            .Replace("@@TIP@@", Mm(tip))
            .Replace("@@HUB@@", Mm(hub))
            .Replace("@@CLR@@", Mm(clearance))
            .Replace("@@WALL@@", Mm(wall))
            .Replace("@@LEN@@", Mm(length))
            .Replace("@@CHORD@@", Mm(chord))
            .Replace("@@THK@@", Mm(thickness))
            .Replace("@@PITCH@@", Num(pitch))
            .Replace("@@COUNT@@", count.ToString(CultureInfo.InvariantCulture));

        var applied = RequiredKeys.ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);

        return new ComposedScriptResult
        {
            Success = true,
            Script = script,
            FileBaseName = FileBaseName,
            IsFullAssembly = true,
            IncludedScriptNames = new[] { "Casing", "Hub", "Blades" },
            AppliedValues = applied,
            Errors = Array.Empty<string>()
        };
    }

    private static string Mm(double meters) => Num(meters * 1000.0);

    private static string Num(double value)
    {
        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    private const string Template = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double TipDiameter = @@TIP@@;
const double HubDiameter = @@HUB@@;
const double TipClearance = @@CLR@@;
const double CasingWall = @@WALL@@;
const double CasingLength = @@LEN@@;
const double BladeChord = @@CHORD@@;
const double BladeThickness = @@THK@@;
const double BladePitch = @@PITCH@@;
const int BladeCount = @@COUNT@@;

var doc = new StepDocument("AxialFan");
var tipRadius = TipDiameter / 2.0;
var hubRadius = HubDiameter / 2.0;
var bladeSpan = tipRadius - TipClearance - hubRadius;
var midZ = CasingLength / 2.0;

doc.Add.Pipe(
    "Casing",
    SweepRoute.From(0, 0, 0).LineTo(0, 0, CasingLength),
    TipDiameter + 2.0 * TipClearance + 2.0 * CasingWall,
    CasingWall);

doc.Add.Cylinder("Hub", 0, 0, hubRadius, CasingLength);

var bladeProfile = Profile2D.Rectangle(0, -BladeChord / 2.0, bladeSpan, BladeChord);

for (var i = 0; i < BladeCount; i++)
{
    var angle = 2.0 * Math.PI * i / BladeCount;
    var radial = new Vec3(Math.Cos(angle), Math.Sin(angle), 0);
    var tangent = new Vec3(-Math.Sin(angle), Math.Cos(angle), 0);
    var root = radial * hubRadius + new Vec3(0, 0, midZ);
    var origin = root - tangent * (BladeThickness / 2.0);

    var blade = Solid.Extrude(bladeProfile, BladeThickness)
        .Placed(Frame3D.FromAxes(origin, radial, tangent))
        .RotateAbout(root, radial, BladePitch)
        .Named("Blade" + (i + 1));

    doc.Add.Part(blade);
}

var outputPath = Path.GetFullPath("AxialFan.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";
}
