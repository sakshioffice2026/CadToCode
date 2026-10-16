using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Core.Domain;
using CadToCode.Core.Units;
using CadToCode.LLM.Templates;

namespace CadToCode.LLM.Services;

public sealed class ParsedIntent
{
    public Dictionary<string, double> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ComponentKeys { get; } = new();
    public List<string> UnrecognizedAssignments { get; } = new();
    public string? UnmatchedComponentText { get; set; }

    /// <summary>Equipment named in the text, or Unknown. Never defaults to a pipe.</summary>
    public EquipmentType Equipment { get; set; } = EquipmentType.Unknown;

    /// <summary>Engineering inputs found in the text, already converted to SI and keyed by calculator input key.</summary>
    public Dictionary<string, double> EngineeringInputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasEngineeringInputs => EngineeringInputs.Count > 0;

    public bool UseDefaults { get; set; }
    public bool Confirm { get; set; }
    public bool FullAssembly { get; set; }
    public bool Regenerate { get; set; }
    public bool Reset { get; set; }
    public bool ShowParameters { get; set; }
    public bool HasGenerateCue { get; set; }

    public bool HasParameters => Updates.Count > 0;
    public bool HasScope => FullAssembly || ComponentKeys.Count > 0;

    public bool HasAnyAction =>
        HasParameters || HasScope || UseDefaults || Confirm || Regenerate || Reset || ShowParameters || HasEngineeringInputs;
}

public static class CadAssistIntentParser
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Number = @"-?\d+(?:[.,]\d+)?";

    private static readonly Regex ResetRegex = Make(@"\b(start over|restart|reset|new drawing|clear everything)\b");

    private static readonly Regex DefaultsRegex = Make(
        @"\b(use|using|accept|go with|proceed with|keep|apply|take)\s+(the\s+)?(all\s+)?defaults?\b|" +
        @"\bdefaults?\s+(are\s+)?(fine|ok|okay|good)\b|^\s*defaults?\s*[.!]?\s*$|\bdefault values?\b");

    private static readonly Regex ConfirmRegex = Make(
        @"^\s*(yes|yep|yeah|ok|okay|confirm|confirmed|proceed|continue|go ahead|looks good|sounds good|sure|do it)\b");

    private static readonly Regex FullAssemblyRegex = Make(
        @"\b(entire|whole|full|complete|all)\s+(the\s+)?(assembly|model|thing|parts|components)\b|\beverything\b|\bassembly\b");

    private static readonly Regex RegenerateRegex = Make(@"\b(regenerate|re-generate|again|redo|rebuild)\b");

    private static readonly Regex ShowRegex = Make(
        @"\b(show|list|display|what are|what's|what is)\b.*\b(parameters|dimensions|values|defaults|settings)\b");

    private static readonly Regex CueRegex = Make(
        @"\b(generate|build|make|create|give|write|produce|code|script|export|just|only)\b");

    private static readonly Regex OnlyRegex = Make(
        @"\b(?:just|only)\s+(?:the\s+)?(?<what>[a-z][a-z \-]{1,40}?)(?=$|[.,;!?]|\band\b)");

    private static readonly Regex AssignmentRegex = Make(
        @"(?<label>[A-Za-z][A-Za-z ]{1,24}?)\s*[=:]\s*(?<num>" + Number + ")");

    public static ParsedIntent Parse(
        string? text,
        TemplateParameterSchema? schema,
        bool awaitingScope = false,
        EquipmentType contextEquipment = EquipmentType.Unknown)
    {
        var intent = new ParsedIntent();

        if (string.IsNullOrWhiteSpace(text))
        {
            return intent;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        var consumed = new List<(int Start, int End)>();

        if (schema is not null)
        {
            ParseParameters(normalized, schema, intent, consumed);
        }

        var named = EquipmentRegistry.Resolve(normalized);
        intent.Equipment = named != EquipmentType.Unknown ? named : contextEquipment;
        ParseEngineeringInputs(normalized, intent, consumed);

        var masked = Mask(normalized, consumed);

        intent.Reset = IsMatch(ResetRegex, masked);
        intent.UseDefaults = IsMatch(DefaultsRegex, masked);
        intent.Confirm = IsMatch(ConfirmRegex, masked);
        intent.Regenerate = IsMatch(RegenerateRegex, masked);
        intent.ShowParameters = IsMatch(ShowRegex, masked);
        intent.HasGenerateCue = IsMatch(CueRegex, masked);
        intent.FullAssembly = IsMatch(FullAssemblyRegex, masked);

        if (schema is not null && !intent.FullAssembly && (intent.HasGenerateCue || awaitingScope))
        {
            ParseComponents(masked, schema, intent);
        }

        if (!intent.FullAssembly && intent.ComponentKeys.Count == 0)
        {
            var only = SafeMatch(OnlyRegex, masked);

            if (only is not null && only.Success)
            {
                intent.UnmatchedComponentText = only.Groups["what"].Value.Trim();
            }
        }

        foreach (Match match in SafeMatches(AssignmentRegex, masked))
        {
            var label = match.Groups["label"].Value.Trim();

            if (label.Length > 0)
            {
                intent.UnrecognizedAssignments.Add(label + "=" + match.Groups["num"].Value);
            }
        }

        return intent;
    }

    private static readonly Regex FlowRegex = Make(
        @"(?<![\d.,])(?<num>" + Number + @")\s*(?<unit>m3/s|m³/s|m3/hr|m3/h|m³/h|cmh|cfm|l/s|lps)(?![A-Za-z])");

    private static readonly Regex PressureRegex = Make(
        @"(?<![\d.,])(?<num>" + Number + @")\s*(?<unit>kpa|mm\s?wg|mmh2o|in\.?\s?wg|inwg|psi|bar|pa)(?![A-Za-z])");

    private static readonly Regex SpeedRegex = Make(
        @"(?<![\d.,])(?<num>" + Number + @")\s*(?<unit>rpm|rad/s)(?![A-Za-z])");

    private static readonly Regex DensityRegex = Make(
        @"(?<![\d.,])(?<num>" + Number + @")\s*(?<unit>kg/m3|kg/m³)");

    private static readonly Regex EfficiencyRegex = Make(
        @"efficiency\s*(?:of|is|=|:|at)?\s*(?<num>" + Number + @")\s*(?<pct>%)?");

    private static void ParseEngineeringInputs(
        string text,
        ParsedIntent intent,
        List<(int Start, int End)> consumed)
    {
        var equipment = intent.Equipment;

        if (equipment == EquipmentType.Unknown)
        {
            return;
        }

        var flowKey = equipment switch
        {
            EquipmentType.AxialFan or EquipmentType.Cyclone or EquipmentType.WetScrubber => "airflow_m3s",
            EquipmentType.PipingLayout => "flow_rate_m3s",
            _ => null
        };

        var pressureKey = equipment switch
        {
            EquipmentType.AxialFan => "static_pressure_pa",
            EquipmentType.PressureVessel or EquipmentType.PipingLayout => "design_pressure_pa",
            _ => null
        };

        var isFan = equipment == EquipmentType.AxialFan;

        ApplyEngineering(text, intent, consumed, FlowRegex, flowKey, m => ConvertFlow(m));
        ApplyEngineering(text, intent, consumed, PressureRegex, pressureKey, m => ConvertPressure(m));
        ApplyEngineering(text, intent, consumed, SpeedRegex, isFan ? "speed_rad_s" : null, m => ConvertSpeed(m));
        ApplyEngineering(text, intent, consumed, DensityRegex, isFan ? "air_density_kgm3" : null, m => ReadNumber(m));
        ApplyEngineering(text, intent, consumed, EfficiencyRegex, isFan ? "static_efficiency" : null, m => ConvertEfficiency(m));
    }

    private static void ApplyEngineering(
        string text,
        ParsedIntent intent,
        List<(int Start, int End)> consumed,
        Regex regex,
        string? key,
        Func<Match, double?> convert)
    {
        if (key is null)
        {
            return;
        }

        foreach (var match in SafeMatches(regex, text))
        {
            if (Overlaps(consumed, match.Index, match.Index + match.Length))
            {
                continue;
            }

            var value = convert(match);

            if (value is null || !double.IsFinite(value.Value) || value.Value <= 0)
            {
                continue;
            }

            intent.EngineeringInputs[key] = value.Value;
            consumed.Add((match.Index, match.Index + match.Length));
        }
    }

    private static double? ReadNumber(Match match)
    {
        var raw = match.Groups["num"].Value.Replace(',', '.');

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ConvertFlow(Match match)
    {
        var n = ReadNumber(match);

        if (n is null)
        {
            return null;
        }

        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "m3/s" or "m³/s" => n.Value,
            "m3/h" or "m³/h" or "m3/hr" or "cmh" => UnitConversions.CubicMetersPerHourToCubicMetersPerSecond(n.Value),
            "cfm" => UnitConversions.CfmToCubicMetersPerSecond(n.Value),
            "l/s" or "lps" => UnitConversions.LitersPerSecondToCubicMetersPerSecond(n.Value),
            _ => null
        };
    }

    private static double? ConvertPressure(Match match)
    {
        var n = ReadNumber(match);

        if (n is null)
        {
            return null;
        }

        var unit = Regex.Replace(match.Groups["unit"].Value.ToLowerInvariant(), @"[\s.]", string.Empty);

        return unit switch
        {
            "pa" => n.Value,
            "kpa" => UnitConversions.KilopascalsToPascals(n.Value),
            "mmwg" or "mmh2o" => UnitConversions.MmWaterGaugeToPascals(n.Value),
            "inwg" => UnitConversions.InchesWaterGaugeToPascals(n.Value),
            "psi" => UnitConversions.PsiToPascals(n.Value),
            "bar" => UnitConversions.BarToPascals(n.Value),
            _ => null
        };
    }

    private static double? ConvertSpeed(Match match)
    {
        var n = ReadNumber(match);

        if (n is null)
        {
            return null;
        }

        return string.Equals(match.Groups["unit"].Value, "rpm", StringComparison.OrdinalIgnoreCase)
            ? UnitConversions.RpmToRadiansPerSecond(n.Value)
            : n.Value;
    }

    private static double? ConvertEfficiency(Match match)
    {
        var n = ReadNumber(match);

        if (n is null)
        {
            return null;
        }

        var fraction = match.Groups["pct"].Success || n.Value > 1.0 ? n.Value / 100.0 : n.Value;

        return fraction is > 0 and <= 1.0 ? fraction : null;
    }

    private static void ParseParameters(
        string text,
        TemplateParameterSchema schema,
        ParsedIntent intent,
        List<(int Start, int End)> consumed)
    {
        var entries = BuildAliasEntries(schema);

        foreach (var entry in entries)
        {
            var alias = Regex.Escape(entry.Alias);

            var forward = Make(
                @"(?<![A-Za-z])" + alias + @"(?![A-Za-z])\s*(?:=|:|\bto\b|\bof\b|\bas\b|\bis\b|\bat\b)?\s*" +
                "(?<num>" + Number + @")\s*(?<unit>mm|cm|m|in|deg|degrees|°)?(?![A-Za-z])");

            foreach (Match match in SafeMatches(forward, text))
            {
                if (Overlaps(consumed, match.Index, match.Index + match.Length))
                {
                    continue;
                }

                if (TryReadValue(match, entry.Parameter, out var value))
                {
                    intent.Updates[entry.Parameter.Key] = value;
                    consumed.Add((match.Index, match.Index + match.Length));
                }
            }
        }

        foreach (var entry in entries.Where(e => e.Alias.Length >= 3))
        {
            var alias = Regex.Escape(entry.Alias);

            var reverse = Make(
                "(?<num>" + Number + @")\s*(?<unit>mm|cm|m|in|deg|degrees|°)\s+(?:thick\s+)?" +
                alias + @"(?![A-Za-z])");

            foreach (Match match in SafeMatches(reverse, text))
            {
                if (Overlaps(consumed, match.Index, match.Index + match.Length))
                {
                    continue;
                }

                if (TryReadValue(match, entry.Parameter, out var value))
                {
                    intent.Updates[entry.Parameter.Key] = value;
                    consumed.Add((match.Index, match.Index + match.Length));
                }
            }
        }
    }

    private static void ParseComponents(string masked, TemplateParameterSchema schema, ParsedIntent intent)
    {
        var working = masked;

        var candidates = schema.Components
            .SelectMany(c => ComponentNames(c).Select(n => (Component: c, Name: n)))
            .OrderByDescending(x => x.Name.Length)
            .ToList();

        foreach (var candidate in candidates)
        {
            var pattern = Make(@"(?<![A-Za-z])" + Regex.Escape(candidate.Name) + @"(?![A-Za-z])");
            var match = SafeMatch(pattern, working);

            if (match is null || !match.Success)
            {
                continue;
            }

            if (!intent.ComponentKeys.Contains(candidate.Component.Key, StringComparer.OrdinalIgnoreCase))
            {
                intent.ComponentKeys.Add(candidate.Component.Key);
            }

            working = working.Remove(match.Index, match.Length).Insert(match.Index, new string(' ', match.Length));
        }
    }

    private static IEnumerable<string> ComponentNames(TemplateComponentDefinition component)
    {
        yield return component.Key;
        yield return component.Title;

        foreach (var alias in component.Aliases)
        {
            yield return alias;
        }
    }

    private static List<AliasEntry> BuildAliasEntries(TemplateParameterSchema schema)
    {
        var entries = new List<AliasEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in schema.Parameters)
        {
            var names = new List<string>
            {
                parameter.Key,
                Regex.Replace(parameter.Key, "(?<=[a-z])(?=[A-Z])", " "),
                parameter.Label
            };

            names.AddRange(parameter.Aliases);

            foreach (var name in names)
            {
                var clean = name.Trim().ToLowerInvariant();

                if (clean.Length == 0 || !seen.Add(clean))
                {
                    continue;
                }

                entries.Add(new AliasEntry(clean, parameter));
            }
        }

        return entries.OrderByDescending(e => e.Alias.Length).ToList();
    }

    private static bool TryReadValue(Match match, TemplateParameterDefinition parameter, out double value)
    {
        value = 0;

        var raw = match.Groups["num"].Value.Replace(',', '.');

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value.ToLowerInvariant() : string.Empty;

        if (string.Equals(parameter.Unit, "mm", StringComparison.OrdinalIgnoreCase))
        {
            parsed = unit switch
            {
                "cm" => parsed * 10.0,
                "m" => parsed * 1000.0,
                "in" => parsed * 25.4,
                _ => parsed
            };
        }

        value = parsed;
        return true;
    }

    private static bool Overlaps(List<(int Start, int End)> spans, int start, int end)
    {
        return spans.Any(s => start < s.End && end > s.Start);
    }

    private static string Mask(string text, List<(int Start, int End)> spans)
    {
        if (spans.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text);

        foreach (var span in spans)
        {
            for (var i = span.Start; i < span.End && i < builder.Length; i++)
            {
                builder[i] = ' ';
            }
        }

        return builder.ToString();
    }

    private static Regex Make(string pattern)
    {
        return new Regex(pattern, Options | RegexOptions.Compiled, Timeout);
    }

    private static bool IsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Match? SafeMatch(Regex regex, string text)
    {
        try
        {
            return regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static IEnumerable<Match> SafeMatches(Regex regex, string text)
    {
        try
        {
            return regex.Matches(text).Cast<Match>().ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<Match>();
        }
    }

    private sealed record AliasEntry(string Alias, TemplateParameterDefinition Parameter);
}