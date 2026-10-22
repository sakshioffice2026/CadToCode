using System.Globalization;
using CadToCode.Business.Interfaces;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds one script per centrifugal fan part: ScrollCasing, InletPlate, BackPlate, Hub, ImpellerBackPlate,
/// Shroud and Blades. The fan is not rendered from a catalog template, so each part script is built directly from
/// the SI values the centrifugal fan mapper produced. Every part is positioned where it sits in the assembly.
/// The Blades part is a single representative blade body; <see cref="PartScript.Quantity"/> carries the blade count.
/// The full set of values is first validated by <see cref="CentrifugalFanScriptComposer"/>, so a part script is only
/// produced for a geometry the assembly composer would also accept.
/// </summary>
public sealed class CentrifugalFanPartScriptBuilder : IPartScriptBuilder
{
    public const string Id = "centrifugal-fan-part-script-builder";

    private const string FileBaseName = "CentrifugalFan";
    private const string ScrollCasingKey = "ScrollCasing";
    private const string InletPlateKey = "InletPlate";
    private const string BackPlateKey = "BackPlate";
    private const string HubKey = "Hub";
    private const string ImpellerBackPlateKey = "ImpellerBackPlate";
    private const string ShroudKey = "Shroud";
    private const string BladesKey = "Blades";
    private const string BladeBodyName = "Blade";

    public string BuilderId => Id;

    public bool CanBuild(EquipmentManifest manifest)
    {
        return manifest is not null
            && manifest.Parts.Count > 0
            && manifest.TemplateKeys.Count == 0
            && string.Equals(manifest.MapperId, CentrifugalFanTemplateMapper.Id, StringComparison.OrdinalIgnoreCase);
    }

    public PartScriptResult Build(PartScriptRequest request)
    {
        if (request is null)
        {
            return PartScriptResult.Fail("The part script request is missing.");
        }

        if (request.Manifest is null)
        {
            return PartScriptResult.Fail("An equipment manifest is required to build part scripts.");
        }

        if (request.Mapping is null)
        {
            return PartScriptResult.Fail("A mapper result is required to build part scripts.");
        }

        var manifest = request.Manifest;
        var mapping = request.Mapping;

        if (!CanBuild(manifest))
        {
            return PartScriptResult.Fail("Manifest '" + manifest.Id + "' is not a centrifugal fan manifest this builder can build.");
        }

        if (!mapping.Succeeded)
        {
            return PartScriptResult.Fail(mapping.GetBlockers().ToArray());
        }

        if (!string.Equals(mapping.MapperId, CentrifugalFanTemplateMapper.Id, StringComparison.OrdinalIgnoreCase))
        {
            return PartScriptResult.Fail("The mapper result comes from '" + mapping.MapperId + "', not '" + CentrifugalFanTemplateMapper.Id + "'.");
        }

        var values = mapping.AsDictionary();
        var assembly = CentrifugalFanScriptComposer.Compose(values);

        if (!assembly.Success)
        {
            return PartScriptResult.Fail(assembly.Errors.ToArray());
        }

        var errors = new List<string>();
        var selected = new List<ManifestPart>();

        if (request.PartKeys.Count == 0)
        {
            selected.AddRange(manifest.Parts);
        }
        else
        {
            foreach (var key in request.PartKeys)
            {
                var part = TemplateScriptComposer.FindPart(manifest, key);

                if (part is null)
                {
                    errors.Add("Part '" + key + "' does not exist in " + manifest.DisplayName + ".");
                    continue;
                }

                if (!selected.Contains(part))
                {
                    selected.Add(part);
                }
            }
        }

        if (errors.Count > 0)
        {
            return PartScriptResult.Fail(errors.ToArray());
        }

        var built = new List<PartScript>();

        foreach (var part in selected)
        {
            var script = BuildPart(part, values);

            if (script is null)
            {
                errors.Add("Part '" + part.Key + "' is not a known centrifugal fan part (ScrollCasing, InletPlate, BackPlate, Hub, ImpellerBackPlate, Shroud or Blades).");
                continue;
            }

            var safetyError = TemplateScriptComposer.CheckSafety(script.Script);

            if (safetyError is not null)
            {
                errors.Add("Part '" + part.Key + "': " + safetyError);
                continue;
            }

            built.Add(script);
        }

        if (errors.Count > 0)
        {
            return PartScriptResult.Fail(errors.ToArray());
        }

        var missingRequired = manifest.Parts
            .Where(p => p.IsRequired && !selected.Contains(p))
            .Select(p => p.Key)
            .ToList();

        return new PartScriptResult
        {
            Success = true,
            ManifestId = manifest.Id,
            Parts = built,
            MissingRequiredPartKeys = missingRequired
        };
    }

    private static PartScript? BuildPart(ManifestPart part, IReadOnlyDictionary<string, double> values)
    {
        if (Is(part, ScrollCasingKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.CasingBaseDiameterM,
                CentrifugalFanTemplateMapper.OutletHeightM,
                CentrifugalFanTemplateMapper.CasingWallM,
                CentrifugalFanTemplateMapper.CasingWidthM);

            return Create(part, ScrollCasingKey, part.Quantity, ScrollCasingTemplate, values, used);
        }

        if (Is(part, InletPlateKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.CasingBaseDiameterM,
                CentrifugalFanTemplateMapper.OutletHeightM,
                CentrifugalFanTemplateMapper.CasingWallM,
                CentrifugalFanTemplateMapper.CasingWidthM,
                CentrifugalFanTemplateMapper.CasingInletDiameterM);

            return Create(part, InletPlateKey, part.Quantity, InletPlateTemplate, values, used);
        }

        if (Is(part, BackPlateKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.CasingBaseDiameterM,
                CentrifugalFanTemplateMapper.OutletHeightM,
                CentrifugalFanTemplateMapper.CasingWallM);

            return Create(part, BackPlateKey, part.Quantity, BackPlateTemplate, values, used);
        }

        if (Is(part, HubKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.HubDiameterM,
                CentrifugalFanTemplateMapper.PlateThicknessM,
                CentrifugalFanTemplateMapper.BladeWidthM,
                CentrifugalFanTemplateMapper.BladeInletWidthM,
                CentrifugalFanTemplateMapper.CasingWidthM,
                CentrifugalFanTemplateMapper.CasingWallM);

            return Create(part, HubKey, part.Quantity, HubTemplate, values, used);
        }

        if (Is(part, ImpellerBackPlateKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.ImpellerOuterDiameterM,
                CentrifugalFanTemplateMapper.HubDiameterM,
                CentrifugalFanTemplateMapper.PlateThicknessM,
                CentrifugalFanTemplateMapper.BladeWidthM,
                CentrifugalFanTemplateMapper.BladeInletWidthM,
                CentrifugalFanTemplateMapper.CasingWidthM,
                CentrifugalFanTemplateMapper.CasingWallM);

            return Create(part, ImpellerBackPlateKey, part.Quantity, ImpellerBackPlateTemplate, values, used);
        }

        if (Is(part, ShroudKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.ImpellerOuterDiameterM,
                CentrifugalFanTemplateMapper.ImpellerInletDiameterM,
                CentrifugalFanTemplateMapper.PlateThicknessM,
                CentrifugalFanTemplateMapper.BladeWidthM,
                CentrifugalFanTemplateMapper.BladeInletWidthM,
                CentrifugalFanTemplateMapper.CasingWidthM,
                CentrifugalFanTemplateMapper.CasingWallM);

            return Create(part, ShroudKey, part.Quantity, ShroudTemplate, values, used);
        }

        if (Is(part, BladesKey))
        {
            var used = Pick(
                values,
                CentrifugalFanTemplateMapper.ImpellerOuterDiameterM,
                CentrifugalFanTemplateMapper.ImpellerInletDiameterM,
                CentrifugalFanTemplateMapper.BladeWidthM,
                CentrifugalFanTemplateMapper.BladeInletWidthM,
                CentrifugalFanTemplateMapper.BladeInletAngleDeg,
                CentrifugalFanTemplateMapper.BladeOutletAngleDeg,
                CentrifugalFanTemplateMapper.BladeThicknessM,
                CentrifugalFanTemplateMapper.BladeProfileCode,
                CentrifugalFanTemplateMapper.PlateThicknessM,
                CentrifugalFanTemplateMapper.CasingWidthM,
                CentrifugalFanTemplateMapper.CasingWallM,
                CentrifugalFanTemplateMapper.BladeCount);

            var count = (int)Math.Round(values[CentrifugalFanTemplateMapper.BladeCount]);

            return Create(part, BladeBodyName, count, BladeTemplate, values, used, BladesKey);
        }

        return null;
    }

    private static bool Is(ManifestPart part, string key)
    {
        return string.Equals(part.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    private static PartScript Create(
        ManifestPart part,
        string bodyName,
        int quantity,
        string template,
        IReadOnlyDictionary<string, double> values,
        IReadOnlyDictionary<string, double> used,
        string? fileKey = null)
    {
        var fileBase = FileBaseName + "_" + (fileKey ?? bodyName);

        return new PartScript
        {
            PartKey = part.Key,
            Title = part.Title,
            BodyName = bodyName,
            Quantity = quantity,
            IsRequired = part.IsRequired,
            Script = Fill(template, values, fileBase),
            FileBaseName = fileBase,
            AppliedValues = used
        };
    }

    private static string Fill(string template, IReadOnlyDictionary<string, double> values, string fileBase)
    {
        return template
            .Replace("@@FILE@@", fileBase)
            .Replace("@@D2@@", Mm(values[CentrifugalFanTemplateMapper.ImpellerOuterDiameterM]))
            .Replace("@@D1@@", Mm(values[CentrifugalFanTemplateMapper.ImpellerInletDiameterM]))
            .Replace("@@B2@@", Mm(values[CentrifugalFanTemplateMapper.BladeWidthM]))
            .Replace("@@B1@@", Mm(values[CentrifugalFanTemplateMapper.BladeInletWidthM]))
            .Replace("@@HUB@@", Mm(values[CentrifugalFanTemplateMapper.HubDiameterM]))
            .Replace("@@BETA1@@", Num(values[CentrifugalFanTemplateMapper.BladeInletAngleDeg]))
            .Replace("@@BETA2@@", Num(values[CentrifugalFanTemplateMapper.BladeOutletAngleDeg]))
            .Replace("@@THK@@", Mm(values[CentrifugalFanTemplateMapper.BladeThicknessM]))
            .Replace("@@PROFILE@@", ((int)Math.Round(values[CentrifugalFanTemplateMapper.BladeProfileCode])).ToString(CultureInfo.InvariantCulture))
            .Replace("@@PLATE@@", Mm(values[CentrifugalFanTemplateMapper.PlateThicknessM]))
            .Replace("@@D3@@", Mm(values[CentrifugalFanTemplateMapper.CasingBaseDiameterM]))
            .Replace("@@WIDTH@@", Mm(values[CentrifugalFanTemplateMapper.CasingWidthM]))
            .Replace("@@WALL@@", Mm(values[CentrifugalFanTemplateMapper.CasingWallM]))
            .Replace("@@HOUT@@", Mm(values[CentrifugalFanTemplateMapper.OutletHeightM]))
            .Replace("@@DIN@@", Mm(values[CentrifugalFanTemplateMapper.CasingInletDiameterM]))
            .Replace("@@COUNT@@", ((int)Math.Round(values[CentrifugalFanTemplateMapper.BladeCount])).ToString(CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, double> Pick(IReadOnlyDictionary<string, double> values, params string[] keys)
    {
        return keys.ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
    }

    private static string Mm(double meters) => Num(meters * 1000.0);

    private static string Num(double value)
    {
        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".0";
    }

    private const string Header = """
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
""";

    private const string Derived = """
var doc = new StepDocument("@@FILE@@");

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
""";

    private const string Spirals = """
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
""";

    private const string BladeOutlines = """
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

var outline = new List<(double X, double Y)>();

for (var k = 0; k <= BladeSteps; k++)
{
    outline.Add(leftSide[k]);
}

for (var k = BladeSteps; k >= 0; k--)
{
    outline.Add(rightSide[k]);
}
""";

    private const string Footer = """
var outputPath = Path.GetFullPath("@@FILE@@.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string BackPlateBody = """
doc.Add.Part(Solid.Extrude(outerSpiral, CasingWall).AtZ(0.0).Named("BackPlate"));
""";

    private const string ScrollCasingBody = """
doc.Add.Part(Solid.Extrude(wallOutline, CasingWidth).AtZ(CasingWall).Named("ScrollCasing"));
""";

    private const string InletPlateBody = """
var inletProfile = Profile2D.Create(
    ProfileLoop.Polygon(outerSpiral),
    new[] { ProfileLoop.Circle(0, 0, CasingInletDiameter / 2.0) });

doc.Add.Part(Solid.Extrude(inletProfile, CasingWall).Placed(Frame3D.At(0, 0, casingTopZ)).Named("InletPlate"));
""";

    private const string HubBody = """
doc.Add.Part(Solid.Cylinder(0, 0, hubRadius, PlateThickness + bladeHeight).AtZ(impellerZ).Named("Hub"));
""";

    private const string ImpellerBackPlateBody = """
var backPlateProfile = Profile2D.Rectangle(hubRadius, 0, r2 - hubRadius, PlateThickness);
doc.Add.Part(Solid.Revolve(backPlateProfile).Placed(Frame3D.At(0, 0, impellerZ)).Named("ImpellerBackPlate"));
""";

    private const string ShroudBody = """
var shroudProfile = Profile2D.Rectangle(r1, 0, r2 - r1, PlateThickness);
doc.Add.Part(Solid.Revolve(shroudProfile).Placed(Frame3D.At(0, 0, shroudZ)).Named("Shroud"));
""";

    private const string BladeBody = """
doc.Add.Part(Solid.Extrude(outline, bladeHeight).AtZ(bladeZ).Named("Blade"));
""";

    private const string ScrollCasingTemplate = Header + "\n\n" + Derived + "\n\n" + Spirals + "\n\n" + ScrollCasingBody + "\n\n" + Footer;

    private const string BackPlateTemplate = Header + "\n\n" + Derived + "\n\n" + Spirals + "\n\n" + BackPlateBody + "\n\n" + Footer;

    private const string InletPlateTemplate = Header + "\n\n" + Derived + "\n\n" + Spirals + "\n\n" + InletPlateBody + "\n\n" + Footer;

    private const string HubTemplate = Header + "\n\n" + Derived + "\n\n" + HubBody + "\n\n" + Footer;

    private const string ImpellerBackPlateTemplate = Header + "\n\n" + Derived + "\n\n" + ImpellerBackPlateBody + "\n\n" + Footer;

    private const string ShroudTemplate = Header + "\n\n" + Derived + "\n\n" + ShroudBody + "\n\n" + Footer;

    private const string BladeTemplate = Header + "\n\n" + Derived + "\n\n" + BladeOutlines + "\n\n" + BladeBody + "\n\n" + Footer;
}