using CadToCode.Business.Interfaces;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds one script per part for equipment rendered through catalog templates (for example the cyclone).
/// The mapper result is validated against the template first; nothing is defaulted from the template.
/// Each part script builds exactly one body whose name equals the part key.
/// </summary>
public sealed class TemplatePartScriptBuilder : IPartScriptBuilder
{
    public const string Id = "template-part-script-builder";

    public const string AssemblyKey = "Assembly";

    public string BuilderId => Id;

    public bool CanBuild(EquipmentManifest manifest)
    {
        if (manifest is null || manifest.Parts.Count == 0 || manifest.TemplateKeys.Count == 0)
        {
            return false;
        }

        var hasFallback = manifest.TemplateKeys.Count == 1;

        return manifest.Parts.All(p => !string.IsNullOrWhiteSpace(p.TemplateKey) || hasFallback);
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
            return PartScriptResult.Fail("Manifest '" + manifest.Id + "' has parts this builder cannot build from a catalog template.");
        }

        if (!mapping.Succeeded)
        {
            return PartScriptResult.Fail(mapping.GetBlockers().ToArray());
        }

        if (string.IsNullOrWhiteSpace(mapping.TemplateKey))
        {
            return PartScriptResult.Fail("The mapper result does not target a catalog template.");
        }

        var fallbackTemplate = manifest.TemplateKeys.Count == 1 ? manifest.TemplateKeys[0] : null;
        var templateErrors = new List<string>();

        foreach (var part in SelectParts(manifest, request.PartKeys))
        {
            var partTemplate = !string.IsNullOrWhiteSpace(part.TemplateKey) ? part.TemplateKey!.Trim() : fallbackTemplate;

            if (!string.Equals(partTemplate, mapping.TemplateKey, StringComparison.OrdinalIgnoreCase))
            {
                templateErrors.Add(
                    "Part '" + part.Key + "' uses template '" + partTemplate + "' but the mapper result targets '" + mapping.TemplateKey + "'.");
            }
        }

        if (templateErrors.Count > 0)
        {
            return PartScriptResult.Fail(templateErrors.ToArray());
        }

        var validation = TemplateParameterCatalog.ValidateMapped(mapping);

        if (!validation.IsValid)
        {
            var problems = new List<string>(validation.Errors);

            problems.AddRange(validation.UncoveredKeys.Select(k => "Template parameter '" + k + "' was not provided by the mapper."));
            problems.AddRange(validation.UnknownKeys.Select(k => "Mapped value '" + k + "' is not a parameter of template '" + mapping.TemplateKey + "'."));

            return PartScriptResult.Fail(problems.ToArray());
        }

        var values = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in mapping.Parameters)
        {
            values[parameter.Key] = new ParameterValue(parameter.Value, ToSource(parameter.Origin));
        }

        if (request.PartKeys.Any(IsAssemblyKey))
        {
            var assembly = TemplateScriptComposer.Compose(new ComposeRequest
            {
                TemplateKey = mapping.TemplateKey,
                Values = values
            });

            if (!assembly.Success)
            {
                return PartScriptResult.Fail(assembly.Errors.ToArray());
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

        var composed = TemplateScriptComposer.ComposeParts(new ComposePartsRequest
        {
            Manifest = manifest,
            TemplateKey = fallbackTemplate,
            PartKeys = request.PartKeys,
            Values = values
        });

        if (!composed.Success)
        {
            return PartScriptResult.Fail(composed.Errors.ToArray());
        }

        return new PartScriptResult
        {
            Success = true,
            ManifestId = manifest.Id,
            Parts = composed.Parts
                .Select(p => new PartScript
                {
                    PartKey = p.PartKey,
                    Title = p.Title,
                    BodyName = p.BodyName,
                    Quantity = p.Quantity,
                    IsRequired = p.IsRequired,
                    Script = p.Script,
                    FileBaseName = p.FileBaseName,
                    AppliedValues = p.AppliedValues
                })
                .ToList(),
            MissingRequiredPartKeys = composed.MissingRequiredPartKeys
        };
    }

    private static bool IsAssemblyKey(string? key)
    {
        var compact = new string((key ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());

        return string.Equals(compact, AssemblyKey, StringComparison.OrdinalIgnoreCase)
               || string.Equals(compact, "FullAssembly", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ManifestPart> SelectParts(EquipmentManifest manifest, IReadOnlyList<string> partKeys)
    {
        if (partKeys.Count == 0)
        {
            return manifest.Parts.ToList();
        }

        var selected = new List<ManifestPart>();

        foreach (var key in partKeys)
        {
            var part = TemplateScriptComposer.FindPart(manifest, key);

            if (part is not null && !selected.Contains(part))
            {
                selected.Add(part);
            }
        }

        return selected;
    }

    private static ParameterSource ToSource(ValueOrigin origin)
    {
        return origin switch
        {
            ValueOrigin.Default => ParameterSource.Default,
            ValueOrigin.DxfVerified => ParameterSource.DxfVerified,
            ValueOrigin.ImageUnverified => ParameterSource.ImageUnverified,
            _ => ParameterSource.User
        };
    }
}