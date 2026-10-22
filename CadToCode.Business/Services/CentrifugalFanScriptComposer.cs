using System.Globalization;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds a STEP-generating C# script for a single-inlet centrifugal fan from validated SI design values.
/// Every required value must be supplied; nothing is defaulted or substituted silently.
/// Script geometry is emitted in millimetres to match the existing STEP sandbox templates.
/// Bodies: ScrollCasing, InletPlate, BackPlate, Hub, ImpellerBackPlate, Shroud and one body per blade (Blade1..BladeN).
/// </summary>
public static class CentrifugalFanScriptComposer
{
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

    private const string FileBaseName = "CentrifugalFan";
    private const int MinBlades = 3;
    private const int MaxBlades = 64;

    private static readonly string[] RequiredKeys =
    {
        ImpellerOuterDiameterM, ImpellerInletDiameterM, BladeWidthM, BladeInletWidthM, HubDiameterM,
        BladeCount, BladeInletAngleDeg, BladeOutletAngleDeg, BladeThicknessM, BladeProfileCode,
        PlateThicknessM, CasingBaseDiameterM, CasingWidthM, CasingWallM, OutletHeightM, CasingInletDiameterM
    };

    private static readonly string[] PositiveKeys =
    {
        ImpellerOuterDiameterM, ImpellerInletDiameterM, BladeWidthM, BladeInletWidthM, HubDiameterM,
        BladeThicknessM, PlateThicknessM, CasingBaseDiameterM, CasingWidthM, CasingWallM, OutletHeightM,
        CasingInletDiameterM
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

        foreach (var key in PositiveKeys)
        {
            if (values[key] <= 0)
                errors.Add($"'{key}' must be greater than zero.");
        }

        var d2 = values[ImpellerOuterDiameterM];
        var d1 = values[ImpellerInletDiameterM];
        var b2 = values[BladeWidthM];
        var b1 = values[BladeInletWidthM];
        var hub = values[HubDiameterM];
        var countRaw = values[BladeCount];
        var beta1 = values[BladeInletAngleDeg];
        var beta2 = values[BladeOutletAngleDeg];
        var thickness = values[BladeThicknessM];
        var profileRaw = values[BladeProfileCode];
        var plate = values[PlateThicknessM];
        var d3 = values[CasingBaseDiameterM];
        var width = values[CasingWidthM];
        var wall = values[CasingWallM];
        var outlet = values[OutletHeightM];
        var inlet = values[CasingInletDiameterM];

        if (Math.Abs(countRaw - Math.Round(countRaw)) > 1e-9)
            errors.Add($"'{BladeCount}' must be a whole number.");

        var count = (int)Math.Round(countRaw);
        if (count < MinBlades || count > MaxBlades)
            errors.Add($"'{BladeCount}' must be between {MinBlades} and {MaxBlades}.");

        if (Math.Abs(profileRaw - Math.Round(profileRaw)) > 1e-9 || (Math.Round(profileRaw) != 0 && Math.Round(profileRaw) != 1))
            errors.Add($"'{BladeProfileCode}' must be 0 (sheet blade) or 1 (airfoil blade).");

        var profile = (int)Math.Round(profileRaw);

        if (beta1 <= 0 || beta1 >= 180)
            errors.Add($"'{BladeInletAngleDeg}' must be between 0 and 180 degrees (exclusive).");

        if (beta2 <= 0 || beta2 >= 180)
            errors.Add($"'{BladeOutletAngleDeg}' must be between 0 and 180 degrees (exclusive).");

        if (errors.Count == 0)
        {
            if (d1 >= d2)
                errors.Add($"'{ImpellerInletDiameterM}' must be smaller than '{ImpellerOuterDiameterM}'.");

            if (hub >= d1)
                errors.Add($"'{HubDiameterM}' must be smaller than '{ImpellerInletDiameterM}'.");

            if (d3 <= d2)
                errors.Add($"'{CasingBaseDiameterM}' must be larger than '{ImpellerOuterDiameterM}'.");

            if (inlet >= d3 - 2.0 * wall)
                errors.Add($"'{CasingInletDiameterM}' must be smaller than the casing base circle.");

            if (outlet <= 2.0 * wall)
                errors.Add($"'{OutletHeightM}' must be larger than twice '{CasingWallM}'.");

            var bladeHeight = Math.Max(b1, b2);
            var impellerHeight = 2.0 * plate + bladeHeight;

            if (impellerHeight > width)
                errors.Add($"Impeller axial height exceeds '{CasingWidthM}'.");

            if (thickness >= b2)
                errors.Add($"'{BladeThicknessM}' must be smaller than '{BladeWidthM}'.");

            if (thickness >= (d2 - d1) / 2.0)
                errors.Add($"'{BladeThicknessM}' must be smaller than the blade radial length.");

            if (count >= MinBlades && Math.PI * d1 / count <= 2.0 * thickness)
                errors.Add("Blades overlap at the impeller inlet: reduce blade thickness or blade count.");
        }

        if (errors.Count > 0)
            return ComposedScriptResult.Fail(errors.ToArray());

        var script = Template
            .Replace("@@D2@@", Mm(d2))
            .Replace("@@D1@@", Mm(d1))
            .Replace("@@B2@@", Mm(b2))
            .Replace("@@B1@@", Mm(b1))
            .Replace("@@HUB@@", Mm(hub))
            .Replace("@@BETA1@@", Num(beta1))
            .Replace("@@BETA2@@", Num(beta2))
            .Replace("@@THK@@", Mm(thickness))
            .Replace("@@PROFILE@@", profile.ToString(CultureInfo.InvariantCulture))
            .Replace("@@PLATE@@", Mm(plate))
            .Replace("@@D3@@", Mm(d3))
            .Replace("@@WIDTH@@", Mm(width))
            .Replace("@@WALL@@", Mm(wall))
            .Replace("@@HOUT@@", Mm(outlet))
            .Replace("@@DIN@@", Mm(inlet))
            .Replace("@@COUNT@@", count.ToString(CultureInfo.InvariantCulture));

        var applied = RequiredKeys.ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);

        return new ComposedScriptResult
        {
            Success = true,
            Script = script,
            FileBaseName = FileBaseName,
            IsFullAssembly = true,
            IncludedScriptNames = new[]
            {
                "ScrollCasing", "InletPlate", "BackPlate", "Hub", "ImpellerBackPlate", "Shroud", "Blades"
            },
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

const double ImpellerOuterDiameter = @@D2@@;
const double ImpellerInletDiameter = @@D1@@;
const double BladeWidthOutlet = @@B2@@;
const double BladeWidthInlet = @@B1@@;
const double HubDiameter = @@HUB@@;
const double InletAngle = @@BETA1@@;
const double OutletAngle = @@BETA2@@;
const double BladeThickness = @@THK@@;
const int BladeProfile = @@PROFILE@@;
const double PlateThickness = @@PLATE@@;
const double CasingBaseDiameter = @@D3@@;
const double CasingWidth = @@WIDTH@@;
const double CasingWall = @@WALL@@;
const double OutletHeight = @@HOUT@@;
const double CasingInletDiameter = @@DIN@@;
const int BladeCount = @@COUNT@@;
const int ScrollSteps = 120;
const int BladeSteps = 24;

var doc = new StepDocument("CentrifugalFan");

var r2 = ImpellerOuterDiameter / 2.0;
var r1 = ImpellerInletDiameter / 2.0;
var hubRadius = HubDiameter / 2.0;
var r3 = CasingBaseDiameter / 2.0;
var bladeHeight = Math.Max(BladeWidthInlet, BladeWidthOutlet);
var impellerHeight = 2.0 * PlateThickness + bladeHeight;
var impellerZ = CasingWall + (CasingWidth - impellerHeight) / 2.0;
var bladeZ = impellerZ + PlateThickness;
var shroudZ = bladeZ + bladeHeight;
var casingTopZ = CasingWall + CasingWidth;

var outerSpiral = new List<(double X, double Y)>();
var innerSpiral = new List<(double X, double Y)>();

for (var i = 0; i <= ScrollSteps; i++)
{
    var t = (double)i / ScrollSteps;
    var a = 2.0 * Math.PI * t;
    var r = r3 + OutletHeight * t;
    var cosA = Math.Cos(a);
    var sinA = Math.Sin(a);
    innerSpiral.Add((r * cosA, r * sinA));
    outerSpiral.Add(((r + CasingWall) * cosA, (r + CasingWall) * sinA));
}

var wallOutline = new List<(double X, double Y)>();

for (var i = 0; i <= ScrollSteps; i++)
{
    wallOutline.Add(outerSpiral[i]);
}

for (var i = ScrollSteps; i >= 0; i--)
{
    wallOutline.Add(innerSpiral[i]);
}

doc.Add.Part(Solid.Extrude(outerSpiral, CasingWall).AtZ(0.0).Named("BackPlate"));
doc.Add.Part(Solid.Extrude(wallOutline, CasingWidth).AtZ(CasingWall).Named("ScrollCasing"));

var inletProfile = Profile2D.Create(
    ProfileLoop.Polygon(outerSpiral),
    new[] { ProfileLoop.Circle(0, 0, CasingInletDiameter / 2.0) });

doc.Add.Part(Solid.Extrude(inletProfile, CasingWall).Placed(Frame3D.At(0, 0, casingTopZ)).Named("InletPlate"));

doc.Add.Part(Solid.Cylinder(0, 0, hubRadius, PlateThickness + bladeHeight).AtZ(impellerZ).Named("Hub"));

var backPlateProfile = Profile2D.Rectangle(hubRadius, 0, r2 - hubRadius, PlateThickness);
doc.Add.Part(Solid.Revolve(backPlateProfile).Placed(Frame3D.At(0, 0, impellerZ)).Named("ImpellerBackPlate"));

var shroudProfile = Profile2D.Rectangle(r1, 0, r2 - r1, PlateThickness);
doc.Add.Part(Solid.Revolve(shroudProfile).Placed(Frame3D.At(0, 0, shroudZ)).Named("Shroud"));

var radii = new List<double>();
var angles = new List<double>();

for (var k = 0; k <= BladeSteps; k++)
{
    radii.Add(r1 + (r2 - r1) * k / BladeSteps);
}

angles.Add(0.0);

for (var k = 1; k <= BladeSteps; k++)
{
    var sMid = ((double)k - 0.5) / BladeSteps;
    var betaMid = (InletAngle + (OutletAngle - InletAngle) * sMid) * Math.PI / 180.0;
    var rMid = 0.5 * (radii[k] + radii[k - 1]);
    var dTheta = -(radii[k] - radii[k - 1]) / (rMid * Math.Tan(betaMid));
    angles.Add(angles[k - 1] + dTheta);
}

var leftSide = new List<(double X, double Y)>();
var rightSide = new List<(double X, double Y)>();

for (var k = 0; k <= BladeSteps; k++)
{
    var s = (double)k / BladeSteps;
    var kPrev = Math.Max(k - 1, 0);
    var kNext = Math.Min(k + 1, BladeSteps);
    var dx = radii[kNext] * Math.Cos(angles[kNext]) - radii[kPrev] * Math.Cos(angles[kPrev]);
    var dy = radii[kNext] * Math.Sin(angles[kNext]) - radii[kPrev] * Math.Sin(angles[kPrev]);
    var length = Math.Sqrt(dx * dx + dy * dy);
    var nx = -dy / length;
    var ny = dx / length;
    var shape = BladeProfile == 1 ? Math.Max(0.15, Math.Sin(Math.PI * Math.Pow(s, 0.6))) : 1.0;
    var half = 0.5 * BladeThickness * shape;
    var cx = radii[k] * Math.Cos(angles[k]);
    var cy = radii[k] * Math.Sin(angles[k]);
    leftSide.Add((cx + nx * half, cy + ny * half));
    rightSide.Add((cx - nx * half, cy - ny * half));
}

for (var i = 0; i < BladeCount; i++)
{
    var rot = 2.0 * Math.PI * i / BladeCount;
    var cosR = Math.Cos(rot);
    var sinR = Math.Sin(rot);
    var outline = new List<(double X, double Y)>();

    for (var k = 0; k <= BladeSteps; k++)
    {
        var p = leftSide[k];
        outline.Add((p.X * cosR - p.Y * sinR, p.X * sinR + p.Y * cosR));
    }

    for (var k = BladeSteps; k >= 0; k--)
    {
        var q = rightSide[k];
        outline.Add((q.X * cosR - q.Y * sinR, q.X * sinR + q.Y * cosR));
    }

    doc.Add.Part(Solid.Extrude(outline, bladeHeight).AtZ(bladeZ).Named("Blade" + (i + 1)));
}

var outputPath = Path.GetFullPath("CentrifugalFan.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";
}