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
/// Designs a centrifugal fan from airflow, static pressure, speed and blade type. Values are read from the message,
/// converted to SI, checked by the calculator, mapped to script values and built as one script per part.
/// Nothing is defaulted: the blade type must come from the user. Blade thickness is calculated and may be overridden.
/// </summary>
public sealed class CentrifugalFanDesignFlow : IEquipmentDesignFlow
{
    public const string ManifestId = "centrifugal-fan";

    private static readonly Regex QuantityRegex = new(
        @"(?<![\w.])(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>m\s*(?:3|\u00B3)\s*/\s*(?:s|sec|h|hr|min)|cmh|cfm|l\s*/\s*s|lps|kpa|mbar|mmwg|mm\s*wg|mm\s*h2o|inwg|in\s*wg|pa|rpm|rad\s*/\s*s|rps|hz|mm|cm|%|m)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex DensityRegex = new(
        @"densit\w*\D{0,12}?(?<n>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex EfficiencyRegex = new(
        @"eff\w*\D{0,12}?(?<n>\d+(?:[.,]\d+)?)\s*(?<pct>%)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex BladeTypeRegex = new(
        @"blade\s*type\D{0,6}?(?<n>[1-4])(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Dictionary<string, (string Name, string Unit, string Example)> InputInfo =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [CentrifugalFanCalculator.Keys.BladeType] = (
                "Blade type (forward curved, backward curved, backward airfoil or radial)",
                string.Empty,
                "backward curved blades"),
            [CentrifugalFanTemplateMapper.BladeThicknessM] = ("Blade thickness", "m", "blade thickness 3 mm")
        };

    private static readonly string[] SummaryKeys =
    {
        CentrifugalFanCalculator.Keys.ImpellerOuterDiameterM,
        CentrifugalFanCalculator.Keys.ImpellerInletDiameterM,
        CentrifugalFanCalculator.Keys.ImpellerWidthM,
        CentrifugalFanCalculator.Keys.BladeCount,
        CentrifugalFanCalculator.Keys.BladeInletAngleDeg,
        CentrifugalFanCalculator.Keys.BladeOutletAngleDeg,
        CentrifugalFanCalculator.Keys.TipSpeedMs,
        CentrifugalFanCalculator.Keys.SpecificSpeed,
        CentrifugalFanCalculator.Keys.TotalPressurePa,
        CentrifugalFanCalculator.Keys.CasingBaseDiameterM,
        CentrifugalFanCalculator.Keys.CasingWidthM,
        CentrifugalFanCalculator.Keys.OutletHeightM,
        CentrifugalFanCalculator.Keys.ShaftPowerW
    };

    private readonly IEquipmentManifestRegistry _manifests;
    private readonly IEquipmentCalculatorRegistry _calculators;
    private readonly ICalculatorTemplateMapperRegistry _mappers;
    private readonly IReadOnlyList<IPartScriptBuilder> _builders;

    public CentrifugalFanDesignFlow(
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
            return Blocked(values, "This flow only designs centrifugal fans.");
        }

        if (!_manifests.TryGet(ManifestId, out var manifest))
        {
            return Blocked(values, "The centrifugal fan definition is not loaded.");
        }

        if (!_calculators.TryGet(EquipmentType.CentrifugalFan, out var calculator))
        {
            return Blocked(values, "The centrifugal fan calculator is not registered, so I cannot size the fan.");
        }

        if (!_mappers.TryGetFor(manifest, out var mapper))
        {
            return Blocked(values, "The centrifugal fan script mapper is not registered.");
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
            text.AppendLine("To size the fan I still need:");
            text.AppendLine();

            foreach (var key in missing)
            {
                text.AppendLine("- " + Line(key));
            }

            text.AppendLine();
            text.Append("For example: `airflow 5 m3/s, static pressure 1200 Pa, speed 1450 rpm, backward curved blades`.");

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
            text.Append("Please correct them and I will size the fan again.");

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
            text.AppendLine("The fan could not be sized:");
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

        AppendSummary(text, values, result, warnings);

        if (!result.IsCadReady)
        {
            text.AppendLine();
            text.Append("The design has critical warnings, so I will not build CAD parts from it. Change the duty or the blade type and I will size it again.");

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
                text.AppendLine("- " + Describe(key) + " (" + UnitOf(key) + "), for example `" + ExampleOf(key) + "`");
            }

            return Turn(values, text, EquipmentDesignTurn.StatusCalculated, missingUser, warnings);
        }

        var userSupplied = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in mapper.RequiredUserInputKeys)
        {
            userSupplied[key] = values[key];
        }

        if (values.TryGetValue(CentrifugalFanTemplateMapper.BladeThicknessM, out var thicknessOverride))
        {
            userSupplied[CentrifugalFanTemplateMapper.BladeThicknessM] = thicknessOverride;
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
            text.Append("Everything needed is set. Say `generate` and I will build one script per part (scroll casing, inlet plate, back plate, hub, impeller back plate, shroud and blades).");

            return Turn(values, text, EquipmentDesignTurn.StatusCalculated, warnings: warnings);
        }

        var builder = _builders.FirstOrDefault(b => b.CanBuild(manifest));

        if (builder is null)
        {
            return Turn(
                values,
                new StringBuilder("No part script builder is available for the centrifugal fan."),
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

        text.AppendLine();
        text.AppendLine("Built " + parts.Parts.Count.ToString(CultureInfo.InvariantCulture) + " part script(s), one body each:");
        text.AppendLine();

        foreach (var part in parts.Parts)
        {
            text.AppendLine("- " + part.Title + " x" + part.Quantity.ToString(CultureInfo.InvariantCulture)
                            + " (" + part.FileBaseName + ".step)");
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
                    found[CentrifugalFanCalculator.Keys.AirflowM3s] = number;
                    break;

                case "m3/h":
                case "m\u00B3/h":
                case "m3/hr":
                case "m\u00B3/hr":
                case "cmh":
                    found[CentrifugalFanCalculator.Keys.AirflowM3s] = number / 3600.0;
                    break;

                case "m3/min":
                case "m\u00B3/min":
                    found[CentrifugalFanCalculator.Keys.AirflowM3s] = number / 60.0;
                    break;

                case "cfm":
                    found[CentrifugalFanCalculator.Keys.AirflowM3s] = number * 0.00047194745;
                    break;

                case "l/s":
                case "lps":
                    found[CentrifugalFanCalculator.Keys.AirflowM3s] = number / 1000.0;
                    break;

                case "pa":
                    found[CentrifugalFanCalculator.Keys.StaticPressurePa] = number;
                    break;

                case "kpa":
                    found[CentrifugalFanCalculator.Keys.StaticPressurePa] = number * 1000.0;
                    break;

                case "mbar":
                    found[CentrifugalFanCalculator.Keys.StaticPressurePa] = number * 100.0;
                    break;

                case "mmwg":
                case "mmh2o":
                    found[CentrifugalFanCalculator.Keys.StaticPressurePa] = number * 9.80665;
                    break;

                case "inwg":
                    found[CentrifugalFanCalculator.Keys.StaticPressurePa] = number * 249.0889;
                    break;

                case "rpm":
                    found[CentrifugalFanCalculator.Keys.SpeedRadS] = number * 2.0 * Math.PI / 60.0;
                    break;

                case "rad/s":
                    found[CentrifugalFanCalculator.Keys.SpeedRadS] = number;
                    break;

                case "rps":
                case "hz":
                    found[CentrifugalFanCalculator.Keys.SpeedRadS] = number * 2.0 * Math.PI;
                    break;

                case "mm":
                case "cm":
                case "m":
                    var meters = unit == "mm" ? number / 1000.0 : unit == "cm" ? number / 100.0 : number;

                    if (label.Contains("thick", StringComparison.Ordinal))
                    {
                        found[CentrifugalFanTemplateMapper.BladeThicknessM] = meters;
                    }

                    break;

                case "%":
                    if (label.Contains("eff", StringComparison.Ordinal))
                    {
                        found[CentrifugalFanCalculator.Keys.StaticEfficiency] = number / 100.0;
                    }

                    break;
            }
        }

        var density = DensityRegex.Match(message);

        if (density.Success && TryParseNumber(density.Groups["n"].Value, out var rho) && rho > 0.0 && rho < 20.0)
        {
            found[CentrifugalFanCalculator.Keys.AirDensityKgM3] = rho;
        }

        var efficiency = EfficiencyRegex.Match(message);

        if (efficiency.Success && TryParseNumber(efficiency.Groups["n"].Value, out var eff) && eff > 0.0)
        {
            found[CentrifugalFanCalculator.Keys.StaticEfficiency] = eff > 1.0 ? eff / 100.0 : eff;
        }

        if (CentrifugalBladeTypes.TryResolve(message, out var resolved))
        {
            found[CentrifugalFanCalculator.Keys.BladeType] = CentrifugalBladeTypes.ToCode(resolved);
        }

        var typeMatch = BladeTypeRegex.Match(message);

        if (typeMatch.Success
            && int.TryParse(typeMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            && CentrifugalBladeTypes.TryFromCode(code, out _))
        {
            found[CentrifugalFanCalculator.Keys.BladeType] = code;
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
        var required = EquipmentRegistry.TryGet(EquipmentType.CentrifugalFan, out var metadata)
            ? metadata.RequiredInputs.Select(i => i.Key).ToList()
            : new List<string>
            {
                CentrifugalFanCalculator.Keys.AirflowM3s,
                CentrifugalFanCalculator.Keys.StaticPressurePa,
                CentrifugalFanCalculator.Keys.SpeedRadS,
                CentrifugalFanCalculator.Keys.BladeType
            };

        return required.Where(k => !values.ContainsKey(k)).ToList();
    }

    private static Dictionary<string, double> CalculatorInputs(IReadOnlyDictionary<string, double> values)
    {
        var keys = new[]
        {
            CentrifugalFanCalculator.Keys.AirflowM3s,
            CentrifugalFanCalculator.Keys.StaticPressurePa,
            CentrifugalFanCalculator.Keys.SpeedRadS,
            CentrifugalFanCalculator.Keys.BladeType,
            CentrifugalFanCalculator.Keys.AirDensityKgM3,
            CentrifugalFanCalculator.Keys.StaticEfficiency
        };

        return keys
            .Where(values.ContainsKey)
            .ToDictionary(k => k, k => values[k], StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendSummary(
        StringBuilder text,
        IReadOnlyDictionary<string, double> values,
        CalculationResult result,
        IReadOnlyList<string> warnings)
    {
        text.AppendLine("Calculated design:");
        text.AppendLine();

        if (values.TryGetValue(CentrifugalFanCalculator.Keys.BladeType, out var bladeCode)
            && CentrifugalBladeTypes.TryFromCode(bladeCode, out var bladeType))
        {
            text.AppendLine("- Blade type: " + CentrifugalBladeTypes.DisplayName(bladeType));
        }

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
        if (string.Equals(key, CentrifugalFanCalculator.Keys.BladeType, StringComparison.OrdinalIgnoreCase)
            && CentrifugalBladeTypes.TryFromCode(value, out var bladeType))
        {
            return CentrifugalBladeTypes.DisplayName(bladeType);
        }

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

        if (EquipmentRegistry.TryGet(EquipmentType.CentrifugalFan, out var metadata))
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

        if (EquipmentRegistry.TryGet(EquipmentType.CentrifugalFan, out var metadata))
        {
            var descriptor = metadata.Inputs.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

            if (descriptor is not null)
            {
                return descriptor.SiUnit;
            }
        }

        return string.Empty;
    }

    private static string ExampleOf(string key)
    {
        return InputInfo.TryGetValue(key, out var info) ? info.Example : key;
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