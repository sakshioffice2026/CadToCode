using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Templates;

namespace CadToCode.Business.Services;

/// <summary>
/// Designs a dry gas-solid cyclone separator from gas flow. Values are read from the message, converted to SI,
/// checked by the cyclone calculator, mapped to template values by the cyclone mapper and built as one script per part
/// (body, top plate, vortex finder, dust outlet and tangential inlet).
/// Only the gas flow is required; every other input falls back to the cyclone rule pack inside the calculator.
/// </summary>
public sealed class CycloneDesignFlow : IEquipmentDesignFlow
{
    public const string ManifestId = "cyclone-separator";

    private const string AssemblyKey = "Assembly";

    private static readonly Regex QuantityRegex = new(
        @"(?<![\w.])(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>m\s*(?:3|\u00B3)\s*/\s*(?:s|sec|h|hr|min)|cmh|cfm|l\s*/\s*s|lps|m\s*/\s*s|\u00B5m|um|microns|micron|mm|cm|kg\s*/\s*m\s*(?:3|\u00B3)|m)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex GasDensityRegex = new(
        @"gas\s*densit\w*\D{0,12}?(?<n>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ParticleDensityRegex = new(
        @"(?:particle|dust|solid)\s*densit\w*\D{0,12}?(?<n>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly string[] SummaryKeys =
    {
        CycloneCalculator.Keys.BarrelDiameterM,
        CycloneCalculator.Keys.BarrelHeightM,
        CycloneCalculator.Keys.ConeHeightM,
        CycloneCalculator.Keys.DustOutletDiameterM,
        CycloneCalculator.Keys.DustOutletLengthM,
        CycloneCalculator.Keys.VortexFinderDiameterM,
        CycloneCalculator.Keys.VortexFinderDepthM,
        CycloneCalculator.Keys.VortexFinderHeightM,
        CycloneCalculator.Keys.InletHeightM,
        CycloneCalculator.Keys.InletWidthM,
        CycloneCalculator.Keys.InletLengthM,
        CycloneCalculator.Keys.WallThicknessUsedM,
        CycloneCalculator.Keys.InletVelocityUsedMs,
        CycloneCalculator.Keys.PressureDropPa,
        CycloneCalculator.Keys.D50EstimatedM
    };

    private static readonly Dictionary<string, (string Name, string Unit)> InputInfo =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CycloneCalculator.Keys.AirflowM3s] = ("Gas flow", "m3/s"),
            [CycloneCalculator.Keys.InletVelocityMs] = ("Inlet velocity", "m/s"),
            [CycloneCalculator.Keys.CutDiameterM] = ("Target cut diameter", "m"),
            [CycloneCalculator.Keys.GasDensityKgM3] = ("Gas density", "kg/m3"),
            [CycloneCalculator.Keys.ParticleDensityKgM3] = ("Particle density", "kg/m3"),
            [CycloneCalculator.Keys.WallThicknessM] = ("Wall thickness", "m")
        };

    private readonly IEquipmentManifestRegistry _manifests;
    private readonly IEquipmentCalculatorRegistry _calculators;
    private readonly ICalculatorTemplateMapperRegistry _mappers;
    private readonly IReadOnlyList<IPartScriptBuilder> _builders;

    public CycloneDesignFlow(
        IEquipmentManifestRegistry manifests,
        IEquipmentCalculatorRegistry calculators,
        ICalculatorTemplateMapperRegistry mappers,
        IEnumerable<IPartScriptBuilder> builders)
    {
        _manifests = manifests;
        _calculators = calculators;
        _mappers = mappers;
        _builders = builders.ToList();
    }

    public bool CanHandle(string? manifestId)
    {
        return string.Equals(manifestId?.Trim(), ManifestId, StringComparison.OrdinalIgnoreCase);
    }

    public EquipmentDesignTurn Handle(EquipmentDesignRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var values = new Dictionary<string, double>(request.Values, StringComparer.OrdinalIgnoreCase);

        if (!CanHandle(request.ManifestId))
        {
            return Blocked(values, "This flow only designs cyclone separators.");
        }

        if (!_manifests.TryGet(ManifestId, out var manifest))
        {
            return Blocked(values, "The cyclone separator definition is not loaded.");
        }

        if (!_calculators.TryGet(EquipmentType.Cyclone, out var calculator))
        {
            return Blocked(values, "The cyclone calculator is not registered, so I cannot size the cyclone.");
        }

        if (!_mappers.TryGetFor(manifest, out var mapper))
        {
            return Blocked(values, "The cyclone template mapper is not registered.");
        }

        var read = ReadValues(request.Text);

        foreach (var pair in read)
        {
            values[pair.Key] = pair.Value;
        }

        var text = new StringBuilder();

        if (read.Count > 0)
        {
            text.AppendLine("I read:");
            text.AppendLine();

            foreach (var pair in read)
            {
                text.AppendLine("- " + Describe(pair.Key) + " = " + FormatValue(pair.Key, pair.Value));
            }

            text.AppendLine();
        }

        var missing = MissingCalculatorKeys(values);

        if (missing.Count > 0)
        {
            text.AppendLine("To size the cyclone I still need:");
            text.AppendLine();

            foreach (var key in missing)
            {
                text.AppendLine("- " + Line(key));
            }

            text.AppendLine();
            text.AppendLine("For example: `gas flow 1.5 m3/s`. Optional: `inlet velocity 20 m/s, cut diameter 10 um, gas density 1.2 kg/m3, particle density 2500 kg/m3, wall 4 mm`.");
            text.Append("Or say `use defaults` and I will fill the cyclone with its standard engineering defaults (1 m3/s gas flow, 20 m/s inlet velocity).");

            return Turn(values, text, EquipmentDesignTurn.StatusNeedInputs, missing);
        }

        var inputs = new EquipmentInputs(CalculatorInputs(values));
        var validation = calculator.Validate(inputs);

        if (!validation.IsValid)
        {
            text.AppendLine("These values cannot be used:");
            text.AppendLine();

            foreach (var issue in validation.BlockingIssues)
            {
                text.AppendLine("- " + issue.Message);
            }

            text.AppendLine();
            text.Append("Please correct them and I will size the cyclone again.");

            return Turn(
                values,
                text,
                EquipmentDesignTurn.StatusNeedInputs,
                validation.MissingRequiredKeys,
                errors: validation.BlockingIssues.Select(i => i.Message).ToList());
        }

        var result = calculator.Calculate(inputs);

        if (!result.Succeeded)
        {
            text.AppendLine("The cyclone could not be sized:");
            text.AppendLine();

            foreach (var error in result.Errors)
            {
                text.AppendLine("- " + error);
            }

            return Turn(values, text, EquipmentDesignTurn.StatusBlocked, errors: result.Errors.ToList());
        }

        var warnings = result.Warnings
            .Where(w => w.Severity != WarningSeverity.Info)
            .Select(w => (w.Severity == WarningSeverity.Critical ? "Critical: " : "Caution: ") + w.Message)
            .ToList();

        AppendSummary(text, result, warnings);

        if (!result.IsCadReady)
        {
            text.AppendLine();
            text.Append("The design has critical warnings, so I will not build CAD parts from it. Change the duty and I will size it again.");

            return Turn(values, text, EquipmentDesignTurn.StatusBlocked, warnings: warnings);
        }

        var missingUser = mapper.RequiredUserInputKeys
            .Where(k => !values.ContainsKey(k))
            .ToList();

        if (missingUser.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("The calculator does not produce everything the CAD script needs. I need these from you:");
            text.AppendLine();

            foreach (var key in missingUser)
            {
                text.AppendLine("- " + Describe(key) + " (" + UnitOf(key) + ")");
            }

            return Turn(values, text, EquipmentDesignTurn.StatusCalculated, missingUser, warnings);
        }

        var userSupplied = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in mapper.RequiredUserInputKeys)
        {
            userSupplied[key] = values[key];
        }

        var mapping = mapper.Map(result, manifest, userSupplied);

        if (!mapping.Succeeded)
        {
            var blockers = mapping.GetBlockers();

            text.AppendLine();
            text.AppendLine("The design could not be mapped to CAD values:");
            text.AppendLine();

            foreach (var blocker in blockers)
            {
                text.AppendLine("- " + blocker);
            }

            return Turn(values, text, EquipmentDesignTurn.StatusBlocked, warnings: warnings, errors: blockers.ToList());
        }

        if (!request.GenerateRequested)
        {
            text.AppendLine();
            text.Append("Sizing is validated against the cyclone rule pack. Choose what to do next: view the component breakdown, generate individual part scripts (body, top plate, vortex finder, dust outlet, tangential inlet), or generate the full assembly STEP file.");

            return Turn(values, text, EquipmentDesignTurn.StatusCalculated, warnings: warnings);
        }

        var builder = _builders.FirstOrDefault(b => b.CanBuild(manifest));

        if (builder is null)
        {
            return Turn(
                values,
                new StringBuilder("No part script builder is available for the cyclone separator."),
                EquipmentDesignTurn.StatusBlocked,
                warnings: warnings);
        }

        var built = builder.Build(new PartScriptRequest
        {
            Manifest = manifest,
            Mapping = mapping,
            PartKeys = request.PartKeys
        });

        if (!built.Success)
        {
            text.AppendLine();
            text.AppendLine("The parts could not be built:");
            text.AppendLine();

            foreach (var error in built.Errors)
            {
                text.AppendLine("- " + error);
            }

            return Turn(values, text, EquipmentDesignTurn.StatusBlocked, warnings: warnings, errors: built.Errors.ToList());
        }

        var parts = new ComposedPartsResult
        {
            Success = true,
            ManifestId = manifest.Id,
            MissingRequiredPartKeys = built.MissingRequiredPartKeys,
            Parts = built.Parts
                .Select(p => new ComposedPartScript
                {
                    PartKey = p.PartKey,
                    Title = p.Title,
                    Quantity = p.Quantity,
                    IsRequired = p.IsRequired,
                    BodyName = p.BodyName,
                    Script = p.Script,
                    FileBaseName = p.FileBaseName,
                    AppliedValues = p.AppliedValues
                })
                .ToList()
        };

        var isAssembly = parts.Parts.Count == 1
                         && string.Equals(parts.Parts[0].PartKey, AssemblyKey, StringComparison.OrdinalIgnoreCase);

        text.AppendLine();

        if (isAssembly)
        {
            text.AppendLine("Built the full cyclone separator as one assembly STEP file (" + parts.Parts[0].FileBaseName
                            + ".step) holding every part: body with cone, top plate, vortex finder, dust outlet and tangential inlet.");
        }
        else
        {
            text.AppendLine("Built " + parts.Parts.Count.ToString(CultureInfo.InvariantCulture) + " part script(s), one body each:");
            text.AppendLine();

            foreach (var part in parts.Parts)
            {
                text.AppendLine("- " + part.Title + " x" + part.Quantity.ToString(CultureInfo.InvariantCulture)
                                + " (" + part.FileBaseName + ".step)");
            }
        }

        if (parts.MissingRequiredPartKeys.Count > 0)
        {
            text.AppendLine();
            text.Append("Still missing required parts: " + string.Join(", ", parts.MissingRequiredPartKeys) + ". The set is incomplete.");
        }

        return new EquipmentDesignTurn
        {
            Values = values,
            Reply = text.ToString().TrimEnd(),
            Status = EquipmentDesignTurn.StatusGenerated,
            Warnings = warnings,
            Parts = parts
        };
    }

    private static Dictionary<string, double> ReadValues(string? message)
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(message))
        {
            return found;
        }

        foreach (Match match in QuantityRegex.Matches(message))
        {
            if (!TryParseNumber(match.Groups["num"].Value, out var number))
            {
                continue;
            }

            var unit = Regex.Replace(match.Groups["unit"].Value.ToLowerInvariant(), @"\s+", string.Empty);
            var label = LabelBefore(message, match.Index);

            switch (unit)
            {
                case "m3/s":
                case "m\u00B3/s":
                case "m3/sec":
                case "m\u00B3/sec":
                    found[CycloneCalculator.Keys.AirflowM3s] = number;
                    break;

                case "m3/h":
                case "m\u00B3/h":
                case "m3/hr":
                case "m\u00B3/hr":
                case "cmh":
                    found[CycloneCalculator.Keys.AirflowM3s] = number / 3600.0;
                    break;

                case "m3/min":
                case "m\u00B3/min":
                    found[CycloneCalculator.Keys.AirflowM3s] = number / 60.0;
                    break;

                case "cfm":
                    found[CycloneCalculator.Keys.AirflowM3s] = number * 0.00047194745;
                    break;

                case "l/s":
                case "lps":
                    found[CycloneCalculator.Keys.AirflowM3s] = number / 1000.0;
                    break;

                case "m/s":
                    found[CycloneCalculator.Keys.InletVelocityMs] = number;
                    break;

                case "\u00B5m":
                case "um":
                case "micron":
                case "microns":
                    found[CycloneCalculator.Keys.CutDiameterM] = number * 1e-6;
                    break;

                case "kg/m3":
                case "kg/m\u00B3":
                    if (label.Contains("particle", StringComparison.Ordinal)
                        || label.Contains("dust", StringComparison.Ordinal)
                        || label.Contains("solid", StringComparison.Ordinal))
                    {
                        found[CycloneCalculator.Keys.ParticleDensityKgM3] = number;
                    }
                    else if (label.Contains("gas", StringComparison.Ordinal)
                             || label.Contains("air", StringComparison.Ordinal))
                    {
                        found[CycloneCalculator.Keys.GasDensityKgM3] = number;
                    }

                    break;

                case "mm":
                case "cm":
                case "m":
                    var meters = unit == "mm" ? number / 1000.0 : unit == "cm" ? number / 100.0 : number;

                    if (label.Contains("thick", StringComparison.Ordinal)
                        || label.Contains("wall", StringComparison.Ordinal))
                    {
                        found[CycloneCalculator.Keys.WallThicknessM] = meters;
                    }
                    else if (label.Contains("cut", StringComparison.Ordinal)
                             || label.Contains("d50", StringComparison.Ordinal))
                    {
                        found[CycloneCalculator.Keys.CutDiameterM] = meters;
                    }

                    break;
            }
        }

        if (!found.ContainsKey(CycloneCalculator.Keys.GasDensityKgM3))
        {
            var density = GasDensityRegex.Match(message);

            if (density.Success && TryParseNumber(density.Groups["n"].Value, out var gas) && gas > 0.0)
            {
                found[CycloneCalculator.Keys.GasDensityKgM3] = gas;
            }
        }

        if (!found.ContainsKey(CycloneCalculator.Keys.ParticleDensityKgM3))
        {
            var density = ParticleDensityRegex.Match(message);

            if (density.Success && TryParseNumber(density.Groups["n"].Value, out var particle) && particle > 0.0)
            {
                found[CycloneCalculator.Keys.ParticleDensityKgM3] = particle;
            }
        }

        return found;
    }

    private static string LabelBefore(string text, int index)
    {
        var start = Math.Max(0, index - 32);
        var before = text.Substring(start, index - start).ToLowerInvariant();
        var cut = before.LastIndexOfAny(new[] { ',', ';', '\n' });

        return cut >= 0 ? before[(cut + 1)..] : before;
    }

    private static bool TryParseNumber(string raw, out double value)
    {
        var text = raw.Trim();

        if (text.Contains(',') && !text.Contains('.'))
        {
            var after = text[(text.IndexOf(',') + 1)..];
            text = after.Length == 3 ? text.Replace(",", string.Empty) : text.Replace(',', '.');
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static List<string> MissingCalculatorKeys(IReadOnlyDictionary<string, double> values)
    {
        var required = EquipmentRegistry.TryGet(EquipmentType.Cyclone, out var metadata)
            ? metadata.RequiredInputs.Select(i => i.Key).ToList()
            : new List<string> { CycloneCalculator.Keys.AirflowM3s };

        return required.Where(k => !values.ContainsKey(k)).ToList();
    }

    private static Dictionary<string, double> CalculatorInputs(IReadOnlyDictionary<string, double> values)
    {
        var keys = new[]
        {
            CycloneCalculator.Keys.AirflowM3s,
            CycloneCalculator.Keys.InletVelocityMs,
            CycloneCalculator.Keys.CutDiameterM,
            CycloneCalculator.Keys.GasDensityKgM3,
            CycloneCalculator.Keys.GasViscosityPas,
            CycloneCalculator.Keys.ParticleDensityKgM3,
            CycloneCalculator.Keys.WallThicknessM
        };

        return keys
            .Where(values.ContainsKey)
            .ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendSummary(
        StringBuilder text,
        CalculationResult result,
        IReadOnlyList<string> warnings)
    {
        text.AppendLine("Calculated design:");
        text.AppendLine();

        foreach (var key in SummaryKeys)
        {
            var value = result.Find(key);

            if (value is null)
            {
                continue;
            }

            text.AppendLine("- " + value.DisplayName + ": " + Format(value.Value)
                            + (string.IsNullOrWhiteSpace(value.SiUnit) || value.SiUnit == "-" ? string.Empty : " " + value.SiUnit));
        }

        if (warnings.Count > 0)
        {
            text.AppendLine();

            foreach (var warning in warnings)
            {
                text.AppendLine("- " + warning);
            }
        }
    }

    private static string Line(string key)
    {
        var unit = UnitOf(key);

        return string.IsNullOrWhiteSpace(unit) || unit == "-"
            ? Describe(key)
            : Describe(key) + " (" + unit + ")";
    }

    private static string FormatValue(string key, double value)
    {
        var unit = UnitOf(key);

        return string.IsNullOrWhiteSpace(unit) || unit == "-"
            ? Format(value)
            : Format(value) + " " + unit;
    }

    private static string Describe(string key)
    {
        if (InputInfo.TryGetValue(key, out var info))
        {
            return info.Name;
        }

        if (EquipmentRegistry.TryGet(EquipmentType.Cyclone, out var metadata))
        {
            var descriptor = metadata.Inputs.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

            if (descriptor is not null)
            {
                return descriptor.DisplayName;
            }
        }

        return key;
    }

    private static string UnitOf(string key)
    {
        if (InputInfo.TryGetValue(key, out var info))
        {
            return info.Unit;
        }

        if (EquipmentRegistry.TryGet(EquipmentType.Cyclone, out var metadata))
        {
            var descriptor = metadata.Inputs.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

            if (descriptor is not null)
            {
                return descriptor.SiUnit;
            }
        }

        return string.Empty;
    }

    private static string Format(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static EquipmentDesignTurn Blocked(IReadOnlyDictionary<string, double> values, string message)
    {
        return new EquipmentDesignTurn
        {
            Values = values,
            Reply = message,
            Status = EquipmentDesignTurn.StatusBlocked,
            Errors = new[] { message }
        };
    }

    private static EquipmentDesignTurn Turn(
        IReadOnlyDictionary<string, double> values,
        StringBuilder text,
        string status,
        IEnumerable<string>? missing = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? errors = null)
    {
        return new EquipmentDesignTurn
        {
            Values = values,
            Reply = text.ToString().TrimEnd(),
            Status = status,
            MissingKeys = (missing ?? Array.Empty<string>()).ToList(),
            Warnings = warnings ?? Array.Empty<string>(),
            Errors = errors ?? Array.Empty<string>()
        };
    }
}