using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Models.Options;

namespace CadToCode.LLM.Vision;

public static class VisionResultParser
{
    private const int MaxTextChars = 300;
    private const int MaxViews = 20;
    private const int MaxUncertainties = 30;
    private const int MaxRepairAttempts = 800;
    private const double MaxLengthMm = 100000.0;
    private const double MaxCount = 1000.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static VisionInspectionResult? Parse(string? raw, VisionOptions options, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The vision model returned no text.";
            return null;
        }

        var json = ExtractJsonObject(Normalize(raw), out var repaired);

        if (json is null)
        {
            error = "The vision output had no JSON object that could be read or repaired.";
            return null;
        }

        var result = DeserializeTolerant(json);

        if (result is null)
        {
            error = "The vision output could not be read.";
            return null;
        }

        Sanitize(result, options);

        if (repaired)
        {
            result.Warnings.Add("The vision output was cut off and repaired; trailing items may be missing.");
        }

        if (!result.HasContent)
        {
            error = "The vision output had no usable content.";
            return null;
        }

        return result;
    }

    private static VisionInspectionResult? DeserializeTolerant(string json)
    {
        try
        {
            var strict = JsonSerializer.Deserialize<VisionInspectionResult>(json, JsonOptions);

            if (strict is not null)
            {
                return strict;
            }
        }
        catch (JsonException)
        {
        }

        return DeserializeLoose(json);
    }

    private static VisionInspectionResult? DeserializeLoose(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new VisionInspectionResult();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name.Trim().ToLowerInvariant())
                {
                    case "equipmenttype":
                        result.EquipmentType = AsText(prop.Value);
                        break;
                    case "summary":
                        result.Summary = AsText(prop.Value);
                        break;
                    case "views":
                        result.Views = ReadList<VisionView>(prop.Value);
                        break;
                    case "components":
                        result.Components = ReadList<VisionComponent>(prop.Value);
                        break;
                    case "dimensions":
                        result.Dimensions = ReadList<VisionDimension>(prop.Value);
                        break;
                    case "uncertainties":
                        result.Uncertainties = ReadStrings(prop.Value);
                        break;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? AsText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static List<T> ReadList<T>(JsonElement element) where T : class
    {
        var list = new List<T>();

        if (element.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in element.EnumerateArray())
        {
            try
            {
                var value = item.Deserialize<T>(JsonOptions);

                if (value is not null)
                {
                    list.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }

    private static List<string> ReadStrings(JsonElement element)
    {
        var list = new List<string>();

        if (element.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in element.EnumerateArray())
        {
            var text = AsText(item);

            if (!string.IsNullOrWhiteSpace(text))
            {
                list.Add(text);
            }
        }

        return list;
    }

    private static void Sanitize(VisionInspectionResult result, VisionOptions options)
    {
        result.EquipmentType = NullIfEmpty(Clean(result.EquipmentType));
        result.Summary = NullIfEmpty(Clean(result.Summary));

        result.Warnings = new List<string>();

        result.Views = (result.Views ?? new List<VisionView>())
            .Where(v => v is not null)
            .Select(v => new VisionView
            {
                Name = Clean(v.Name),
                Description = NullIfEmpty(Clean(v.Description))
            })
            .Where(v => v.Name.Length > 0)
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(MaxViews)
            .ToList();

        result.Components = (result.Components ?? new List<VisionComponent>())
            .Where(c => c is not null)
            .Select(c => new VisionComponent
            {
                Name = Clean(c.Name),
                Description = NullIfEmpty(Clean(c.Description)),
                Quantity = c.Quantity is > 0 and <= 1000 ? c.Quantity : null,
                View = NullIfEmpty(Clean(c.View))
            })
            .Where(c => c.Name.Length > 0)
            .GroupBy(c => c.Name + "|" + c.View, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(Math.Max(1, options.MaxComponents))
            .ToList();

        var dimensions = new List<VisionDimension>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;

        foreach (var d in result.Dimensions ?? new List<VisionDimension>())
        {
            if (d is null)
            {
                dropped++;
                continue;
            }

            var label = Clean(d.Label);
            var evidence = Clean(d.Evidence);
            var unit = NormalizeUnit(d.Unit);

            if (label.Length == 0 || evidence.Length == 0 || unit is null)
            {
                dropped++;
                continue;
            }

            var value = d.Value;

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                dropped++;
                continue;
            }

            var candidate = new VisionDimension
            {
                Label = label,
                Value = value,
                Unit = unit,
                Component = NullIfEmpty(Clean(d.Component)),
                View = NullIfEmpty(Clean(d.View)),
                Evidence = evidence
            };

            if (!IsValueInRange(candidate))
            {
                dropped++;
                continue;
            }

            if (candidate.IsCount)
            {
                candidate.Value = Math.Round(candidate.Value);
            }

            var key = candidate.Label + "|" + candidate.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "|" + candidate.Unit + "|" + candidate.Component;

            if (!seen.Add(key))
            {
                continue;
            }

            dimensions.Add(candidate);

            if (dimensions.Count >= Math.Max(1, options.MaxDimensions))
            {
                break;
            }
        }

        result.Dimensions = dimensions;

        if (dropped > 0)
        {
            result.Warnings.Add(dropped + " dimension(s) from the image were dropped because the unit, value or printed evidence was missing or invalid.");
        }

        result.Uncertainties = (result.Uncertainties ?? new List<string>())
            .Select(Clean)
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxUncertainties)
            .ToList();
    }

    private static bool IsValueInRange(VisionDimension d)
    {
        if (d.IsAngle)
        {
            return d.Value >= 0 && d.Value <= 360.0;
        }

        if (d.IsCount)
        {
            return d.Value >= 1 && d.Value <= MaxCount;
        }

        return d.TryGetMillimeters(out var mm) && mm > 0 && mm <= MaxLengthMm;
    }

    private static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        switch (unit.Trim().ToLowerInvariant())
        {
            case "mm":
            case "millimeter":
            case "millimeters":
            case "millimetre":
            case "millimetres":
                return "mm";
            case "cm":
            case "centimeter":
            case "centimeters":
            case "centimetre":
            case "centimetres":
                return "cm";
            case "m":
            case "meter":
            case "meters":
            case "metre":
            case "metres":
                return "m";
            case "in":
            case "inch":
            case "inches":
            case "\"":
                return "in";
            case "deg":
            case "degree":
            case "degrees":
            case "\u00B0":
                return "deg";
            case "count":
            case "pcs":
            case "pc":
            case "nos":
            case "no":
            case "qty":
            case "off":
                return "count";
            default:
                return null;
        }
    }

    private static string? ExtractJsonObject(string raw, out bool repaired)
    {
        repaired = false;

        var first = raw.IndexOf('{');

        if (first < 0)
        {
            return null;
        }

        first = SkipDuplicateBraces(raw, first);
        raw = EscapeControlCharsInStrings(raw.Substring(first));

        var start = 0;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return raw.Substring(start, i - start + 1);
                }
            }
        }

        var fixedJson = RepairTruncated(raw, start);

        if (fixedJson is not null)
        {
            repaired = true;
        }

        return fixedJson;
    }

    private static string? RepairTruncated(string raw, int start)
    {
        var stack = new List<char>();
        var cuts = new List<(int Length, string Closers)>();
        var inString = false;
        var escaped = false;
        var broken = false;

        for (var i = start; i < raw.Length && !broken; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;

                    var close = FindStringEnd(raw, i);

                    if (close > 0)
                    {
                        cuts.Add((close + 1, BuildClosers(stack)));
                    }

                    break;
                case '{':
                    stack.Add('}');
                    cuts.Add((i + 1, BuildClosers(stack)));
                    break;
                case '[':
                    stack.Add(']');
                    cuts.Add((i + 1, BuildClosers(stack)));
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0 || stack[^1] != c)
                    {
                        broken = true;
                        break;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    cuts.Add((i + 1, BuildClosers(stack)));
                    break;
                case ',':
                    if (stack.Count > 0)
                    {
                        cuts.Add((i, BuildClosers(stack)));
                    }

                    break;
            }
        }

        var attempts = 0;

        for (var k = cuts.Count - 1; k >= 0 && attempts < MaxRepairAttempts; k--, attempts++)
        {
            var (length, closers) = cuts[k];

            var candidate = raw.Substring(start, length - start).TrimEnd() + closers;

            if (IsValidJsonObject(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Normalize(string raw)
    {
        var text = raw.Replace("\uFEFF", string.Empty)
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty);

        var marker = text.IndexOf("<|", StringComparison.Ordinal);

        if (marker >= 0)
        {
            text = text.Substring(0, marker);
        }

        return text;
    }

    private static int SkipDuplicateBraces(string raw, int start)
    {
        while (true)
        {
            var next = start + 1;

            while (next < raw.Length && char.IsWhiteSpace(raw[next]))
            {
                next++;
            }

            if (next < raw.Length && raw[next] == '{')
            {
                start = next;
                continue;
            }

            return start;
        }
    }

    private static int FindStringEnd(string text, int openIndex)
    {
        var escaped = false;

        for (var j = openIndex + 1; j < text.Length; j++)
        {
            var c = text[j];

            if (escaped)
            {
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                return j;
            }
        }

        return -1;
    }

    private static string EscapeControlCharsInStrings(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                }

                sb.Append(c);
                continue;
            }

            if (escaped)
            {
                escaped = false;
                sb.Append(c);
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                sb.Append(c);
            }
            else if (c == '"')
            {
                inString = false;
                sb.Append(c);
            }
            else if (c == '\n')
            {
                sb.Append("\\n");
            }
            else if (c == '\r')
            {
                sb.Append("\\r");
            }
            else if (c == '\t')
            {
                sb.Append("\\t");
            }
            else if (c < 0x20)
            {
                sb.Append("\\u").Append(((int)c).ToString("x4"));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string BuildClosers(List<char> stack)
    {
        var sb = new StringBuilder(stack.Count);

        for (var i = stack.Count - 1; i >= 0; i--)
        {
            sb.Append(stack[i]);
        }

        return sb.ToString();
    }

    private static bool IsValidJsonObject(string candidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate, DocumentOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, MaxTextChars));
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;

            if (sb.Length >= MaxTextChars)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string? NullIfEmpty(string text)
    {
        return text.Length == 0 ? null : text;
    }
}