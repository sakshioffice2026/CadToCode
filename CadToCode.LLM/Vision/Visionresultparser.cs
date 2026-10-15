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
    private const double MaxLengthMm = 100000.0;
    private const double MaxCount = 1000.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static VisionInspectionResult? Parse(string? raw, VisionOptions options, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The vision model returned no text.";
            return null;
        }

        var json = ExtractJsonObject(raw);

        if (json is null)
        {
            error = "The vision output had no complete JSON object.";
            return null;
        }

        VisionInspectionResult? result;

        try
        {
            result = JsonSerializer.Deserialize<VisionInspectionResult>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "The vision output was not valid JSON: " + ex.Message;
            return null;
        }

        if (result is null)
        {
            error = "The vision output could not be read.";
            return null;
        }

        Sanitize(result, options);

        if (!result.HasContent)
        {
            error = "The vision output had no usable content.";
            return null;
        }

        return result;
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

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');

        if (start < 0)
        {
            return null;
        }

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

        return null;
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