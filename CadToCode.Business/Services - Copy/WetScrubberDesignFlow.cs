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
/// Designs an industrial wet scrubber (spray tower) from gas flow. Values are read from the message, converted to SI,
/// checked by the calculator, mapped to template values and built as one script per part.
/// Only the gas flow is required; every other input falls back to the rule pack inside the calculator.
/// </summary>
public sealed class WetScrubberDesignFlow : IEquipmentDesignFlow
{
    public const string ManifestId = "wet-scrubber";

    private const string AssemblyKey = "Assembly";

    private static readonly Regex QuantityRegex = new(
        @"(?<![\w.])(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>m\s*(?:3|\u00B3)\s*/\s*(?:s|sec|h|hr|min)|cmh|cfm|l\s*/\s*m\s*(?:3|\u00B3)|l\s*/\s*s|lps|m\s*/\s*s|mm|cm|sec|s|m)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex LiquidGasRatioRegex = new(
        @"(?:l\s*/\s*g|liquid\s*(?:to|/)?\s*gas)\s*(?:ratio)?\D{0,12}?(?<n>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly string[] SummaryKeys =
    {
        WetScrubberCalculator.Keys.VesselInsideDiameterM,
        WetScrubberCalculator.Keys.ShellHeightM,
        WetScrubberCalculator.Keys.ConeHeightM,
        WetScrubberCalculator.Keys.DrainDiameterM,
        WetScrubberCalculator.Keys.DrainLengthM,
        WetScrubberCalculator.Keys.StackDiameterM,
        WetScrubberCalculator.Keys.StackHeightM,
        WetScrubberCalculator.Keys.InletDiameterM,
        WetScrubberCalculator.Keys.InletLengthM,
        WetScrubberCalculator.Keys.InletElevationM,
        WetScrubberCalculator.Keys.WallThicknessUsedM,
        WetScrubberCalculator.Keys.GasVelocityUsedMs,
        WetScrubberCalculator.Keys.ResidenceTimeS,
        WetScrubberCalculator.Keys.LiquidFlowLs
    };

    private readonly IEquipmentManifestRegistry _manifests;
    private readonly IEquipmentCalculatorRegistry _calculators;
    private readonly ICalculatorTemplateMapperRegistry _mappers;
    private readonly IReadOnlyList<IPartScriptBuilder> _builders;

    public WetScrubberDesignFlow(
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
            return Blocked(values, "This flow only designs wet scrubbers.");
        }

        if (!_manifests.TryGet(ManifestId, out var manifest))
        {
            return Blocked(values, "The wet scrubber definition is not loaded.");
        }

        if (!_calculators.TryGet(EquipmentType.WetScrubber, out var calculator))
        {
            return Blocked(values, "The wet scrubber calculator is not registered, so I cannot size the scrubber.");
        }

        if (!_mappers.TryGetFor(manifest, out var mapper))
        {
            return Blocked(values, "The wet scrubber template mapper is not registered.");
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
            text.AppendLine("To size the scrubber I still need:");
            text.AppendLine();

            foreach (var key in missing)
            {
                text.AppendLine("- " + Line(key));
            }

            text.AppendLine();
            text.AppendLine("For example: `gas flow 2 m3/s`. Optional: `superficial velocity 2 m/s, residence time 2 s, wall 8 mm, L/G ratio 2 l/m3`.");
            text.Append("Or say `use defaults` and I will fill the wet scrubber with its standard engineering defaults (2 m3/s gas flow, 1.2 m/s gas velocity, 2 s residence time, 2 l/m3 liquid to gas ratio).");

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
            text.Append("Please correct them and I will size the scrubber again.");

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
            text.AppendLine("The scrubber could not be sized:");
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
            text.Append("Sizing is validated against the wet scrubber rule pack. Choose what to do next: view the component breakdown, generate individual part scripts (vessel shell with conical bottom, top cover, gas outlet stack, drain pipe, side gas inlet), or generate the full assembly STEP file.");

            return Turn(values, text, EquipmentDesignTurn.StatusCalculated, warnings: warnings);
        }

        var builder = _builders.FirstOrDefault(b => b.CanBuild(manifest));

        if (builder is null)
        {
            return Turn(
                values,
                new StringBuilder("No part script builder is available for the wet scrubber."),
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
            text.AppendLine("Built the full wet scrubber as one assembly STEP file (" + parts.Parts[0].FileBaseName
                            + ".step) holding every part: vessel shell with conical bottom, top cover, gas outlet stack, drain pipe and side gas inlet.");
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

            text.AppendLine();
            text.Append("Say `generate assembly` for one combined STEP file.");
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
                    found[WetScrubberCalculator.Keys.AirflowM3s] = number;
                    break;

                case "m3/h":
                case "m\u00B3/h":
                case "m3/hr":
                case "m\u00B3/hr":
                case "cmh":
                    found[WetScrubberCalculator.Keys.AirflowM3s] = number / 3600.0;
                    break;

                case "m3/min":
                case "m\u00B3/min":
                    found[WetScrubberCalculator.Keys.AirflowM3s] = number / 60.0;
                    break;

                case "cfm":
                    found[WetScrubberCalculator.Keys.AirflowM3s] = number * 0.00047194745;
                    break;

                case "l/s":
                case "lps":
                    found[WetScrubberCalculator.Keys.AirflowM3s] = number / 1000.0;
                    break;

                case "l/m3":
                case "l/m\u00B3":
                    found[WetScrubberCalculator.Keys.LiquidToGasRatioLm3] = number;
                    break;

                case "m/s":
                    if (label.Contains("inlet", StringComparison.Ordinal))
                    {
                        found[WetScrubberCalculator.Keys.InletVelocityMs] = number;
                    }
                    else if (label.Contains("stack", StringComparison.Ordinal)
                             || label.Contains("outlet", StringComparison.Ordinal))
                    {
                        found[WetScrubberCalculator.Keys.StackVelocityMs] = number;
                    }
                    else
                    {
                        found[WetScrubberCalculator.Keys.GasVelocityMs] = number;
                    }

                    break;

                case "s":
                case "sec":
                    if (label.Contains("resid", StringComparison.Ordinal)
                        || label.Contains("contact", StringComparison.Ordinal))
                    {
                        found[WetScrubberCalculator.Keys.GasResidenceTimeS] = number;
                    }

                    break;

                case "mm":
                case "cm":
                case "m":
                    var meters = unit == "mm" ? number / 1000.0 : unit == "cm" ? number / 100.0 : number;

                    if (label.Contains("thick", StringComparison.Ordinal)
                        || label.Contains("wall", StringComparison.Ordinal))
                    {
                        found[WetScrubberCalculator.Keys.WallThicknessM] = meters;
                    }

                    break;
            }
        }

        if (!found.ContainsKey(WetScrubberCalculator.Keys.LiquidToGasRatioLm3))
        {
            var ratio = LiquidGasRatioRegex.Match(message);

            if (ratio.Success && TryParseNumber(ratio.Groups["n"].Value, out var lg) && lg > 0.0)
            {
                found[WetScrubberCalculator.Keys.LiquidToGasRatioLm3] = lg;
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
        var required = EquipmentRegistry.TryGet(EquipmentType.WetScrubber, out var metadata)
            ? metadata.RequiredInputs.Select(i => i.Key).ToList()
            : new List<string> { WetScrubberCalculator.Keys.AirflowM3s };

        return required.Where(k => !values.ContainsKey(k)).ToList();
    }

    private static Dictionary<string, double> CalculatorInputs(IReadOnlyDictionary<string, double> values)
    {
        var keys = new[]
        {
            WetScrubberCalculator.Keys.AirflowM3s,
            WetScrubberCalculator.Keys.GasVelocityMs,
            WetScrubberCalculator.Keys.GasResidenceTimeS,
            WetScrubberCalculator.Keys.InletVelocityMs,
            WetScrubberCalculator.Keys.StackVelocityMs,
            WetScrubberCalculator.Keys.LiquidToGasRatioLm3,
            WetScrubberCalculator.Keys.WallThicknessM
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
        if (EquipmentRegistry.TryGet(EquipmentType.WetScrubber, out var metadata))
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
        if (EquipmentRegistry.TryGet(EquipmentType.WetScrubber, out var metadata))
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