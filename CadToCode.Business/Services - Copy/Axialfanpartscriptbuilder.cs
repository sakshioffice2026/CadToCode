using System.Globalization;
using CadToCode.Business.Interfaces;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds one script per axial fan part: Casing, Hub and Blades. The fan is not rendered from a catalog template,
/// so each part script is built directly from the SI values the axial fan mapper produced.
/// The Blades part is a single representative blade body; <see cref="PartScript.Quantity"/> carries the blade count.
/// The full set of values is first validated by <see cref="AxialFanScriptComposer"/>, so a part script is only
/// produced for a geometry the assembly composer would also accept.
/// </summary>
public sealed class AxialFanPartScriptBuilder : IPartScriptBuilder
{
    public const string Id = "axial-fan-part-script-builder";

    private const string FileBaseName = "AxialFan";
    private const string CasingKey = "Casing";
    private const string HubKey = "Hub";
    private const string BladesKey = "Blades";
    private const string BladeBodyName = "Blade";

    /// <summary>Part key that requests one STEP script holding every part of the fan as separate solids.</summary>
    public const string AssemblyKey = "Assembly";

    public string BuilderId => Id;

    public bool CanBuild(EquipmentManifest manifest)
    {
        return manifest is not null
            && manifest.Parts.Count > 0
            && manifest.TemplateKeys.Count == 0
            && string.Equals(manifest.MapperId, AxialFanTemplateMapper.Id, StringComparison.OrdinalIgnoreCase);
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
            return PartScriptResult.Fail("Manifest '" + manifest.Id + "' is not an axial fan manifest this builder can build.");
        }

        if (!mapping.Succeeded)
        {
            return PartScriptResult.Fail(mapping.GetBlockers().ToArray());
        }

        if (!string.Equals(mapping.MapperId, AxialFanTemplateMapper.Id, StringComparison.OrdinalIgnoreCase))
        {
            return PartScriptResult.Fail("The mapper result comes from '" + mapping.MapperId + "', not '" + AxialFanTemplateMapper.Id + "'.");
        }

        var values = mapping.AsDictionary();
        var assembly = AxialFanScriptComposer.Compose(values);

        if (!assembly.Success)
        {
            return PartScriptResult.Fail(assembly.Errors.ToArray());
        }

        if (request.PartKeys.Any(IsAssemblyKey))
        {
            var assemblySafetyError = TemplateScriptComposer.CheckSafety(assembly.Script);

            if (assemblySafetyError is not null)
            {
                return PartScriptResult.Fail("Assembly: " + assemblySafetyError);
            }

            return new PartScriptResult
            {
                Success = true,
                ManifestId = manifest.Id,
                Parts = new[]
                {
                    new PartScript
                    {
                        PartKey = AssemblyKey,
                        Title = "Full assembly",
                        BodyName = AssemblyKey,
                        Quantity = 1,
                        IsRequired = true,
                        Script = assembly.Script,
                        FileBaseName = assembly.FileBaseName,
                        AppliedValues = assembly.AppliedValues
                    }
                },
                MissingRequiredPartKeys = Array.Empty<string>()
            };
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
                errors.Add("Part '" + part.Key + "' is not a known axial fan part (Casing, Hub or Blades).");
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

    private static bool IsAssemblyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var compact = key.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);

        return string.Equals(compact, AssemblyKey, StringComparison.OrdinalIgnoreCase)
               || string.Equals(compact, "FullAssembly", StringComparison.OrdinalIgnoreCase);
    }

    private static PartScript? BuildPart(ManifestPart part, IReadOnlyDictionary<string, double> values)
    {
        if (string.Equals(part.Key, CasingKey, StringComparison.OrdinalIgnoreCase))
        {
            var used = Pick(
                values,
                AxialFanTemplateMapper.TipDiameterM,
                AxialFanTemplateMapper.TipClearanceM,
                AxialFanTemplateMapper.CasingWallM,
                AxialFanTemplateMapper.CasingLengthM);

            var script = CasingTemplate
                .Replace("@@TIP@@", Mm(values[AxialFanTemplateMapper.TipDiameterM]))
                .Replace("@@CLR@@", Mm(values[AxialFanTemplateMapper.TipClearanceM]))
                .Replace("@@WALL@@", Mm(values[AxialFanTemplateMapper.CasingWallM]))
                .Replace("@@LEN@@", Mm(values[AxialFanTemplateMapper.CasingLengthM]));

            return Create(part, CasingKey, part.Quantity, script, FileBaseName + "_" + CasingKey, used);
        }

        if (string.Equals(part.Key, HubKey, StringComparison.OrdinalIgnoreCase))
        {
            var used = Pick(
                values,
                AxialFanTemplateMapper.HubDiameterM,
                AxialFanTemplateMapper.CasingLengthM);

            var script = HubTemplate
                .Replace("@@HUB@@", Mm(values[AxialFanTemplateMapper.HubDiameterM]))
                .Replace("@@LEN@@", Mm(values[AxialFanTemplateMapper.CasingLengthM]));

            return Create(part, HubKey, part.Quantity, script, FileBaseName + "_" + HubKey, used);
        }

        if (string.Equals(part.Key, BladesKey, StringComparison.OrdinalIgnoreCase))
        {
            var used = Pick(
                values,
                AxialFanTemplateMapper.TipDiameterM,
                AxialFanTemplateMapper.HubDiameterM,
                AxialFanTemplateMapper.TipClearanceM,
                AxialFanTemplateMapper.CasingLengthM,
                AxialFanTemplateMapper.BladeChordM,
                AxialFanTemplateMapper.BladeThicknessM,
                AxialFanTemplateMapper.BladePitchDeg,
                AxialFanTemplateMapper.BladeCount);

            var script = BladeTemplate
                .Replace("@@TIP@@", Mm(values[AxialFanTemplateMapper.TipDiameterM]))
                .Replace("@@HUB@@", Mm(values[AxialFanTemplateMapper.HubDiameterM]))
                .Replace("@@CLR@@", Mm(values[AxialFanTemplateMapper.TipClearanceM]))
                .Replace("@@LEN@@", Mm(values[AxialFanTemplateMapper.CasingLengthM]))
                .Replace("@@CHORD@@", Mm(values[AxialFanTemplateMapper.BladeChordM]))
                .Replace("@@THK@@", Mm(values[AxialFanTemplateMapper.BladeThicknessM]))
                .Replace("@@PITCH@@", Num(values[AxialFanTemplateMapper.BladePitchDeg]));

            var count = (int)Math.Round(values[AxialFanTemplateMapper.BladeCount]);

            return Create(part, BladeBodyName, count, script, FileBaseName + "_" + BladeBodyName, used);
        }

        return null;
    }

    private static PartScript Create(
        ManifestPart part,
        string bodyName,
        int quantity,
        string script,
        string fileBaseName,
        IReadOnlyDictionary<string, double> used)
    {
        return new PartScript
        {
            PartKey = part.Key,
            Title = part.Title,
            BodyName = bodyName,
            Quantity = quantity,
            IsRequired = part.IsRequired,
            Script = script,
            FileBaseName = fileBaseName,
            AppliedValues = used
        };
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

    private const string CasingTemplate = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double TipDiameter = @@TIP@@;
const double TipClearance = @@CLR@@;
const double CasingWall = @@WALL@@;
const double CasingLength = @@LEN@@;

var doc = new StepDocument("AxialFan_Casing");

doc.Add.Pipe(
    "Casing",
    SweepRoute.From(0, 0, 0).LineTo(0, 0, CasingLength),
    TipDiameter + 2.0 * TipClearance + 2.0 * CasingWall,
    CasingWall);

var outputPath = Path.GetFullPath("AxialFan_Casing.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string HubTemplate = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double HubDiameter = @@HUB@@;
const double CasingLength = @@LEN@@;

var doc = new StepDocument("AxialFan_Hub");
var hubRadius = HubDiameter / 2.0;

doc.Add.Cylinder("Hub", 0, 0, hubRadius, CasingLength);

var outputPath = Path.GetFullPath("AxialFan_Hub.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string BladeTemplate = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double TipDiameter = @@TIP@@;
const double HubDiameter = @@HUB@@;
const double TipClearance = @@CLR@@;
const double CasingLength = @@LEN@@;
const double BladeChord = @@CHORD@@;
const double BladeThickness = @@THK@@;
const double BladePitch = @@PITCH@@;

var doc = new StepDocument("AxialFan_Blade");
var tipRadius = TipDiameter / 2.0;
var hubRadius = HubDiameter / 2.0;
var bladeSpan = tipRadius - TipClearance - hubRadius;
var midZ = CasingLength / 2.0;

var bladeProfile = Profile2D.Rectangle(0, -BladeChord / 2.0, bladeSpan, BladeChord);
var radial = new Vec3(1, 0, 0);
var tangent = new Vec3(0, 1, 0);
var root = radial * hubRadius + new Vec3(0, 0, midZ);
var origin = root - tangent * (BladeThickness / 2.0);

var blade = Solid.Extrude(bladeProfile, BladeThickness)
    .Placed(Frame3D.FromAxes(origin, radial, tangent))
    .RotateAbout(root, radial, BladePitch)
    .Named("Blade");

doc.Add.Part(blade);

var outputPath = Path.GetFullPath("AxialFan_Blade.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";
}