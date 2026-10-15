using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.LLM.Templates;

namespace CadToCode.LLM.Vision;

public sealed record MappedParameter(
    string Key,
    double Value,
    ParameterSource Source,
    string VisionLabel,
    string Evidence,
    bool Corroborated);

public sealed record UnmappedDimension(VisionDimension Dimension, string Reason);

public sealed class VisionParameterMapping
{
    public List<MappedParameter> Mapped { get; } = new();

    public List<UnmappedDimension> Unmapped { get; } = new();

    public List<string> Notes { get; } = new();
}

public static class VisionParameterMapper
{
    private const int MinScore = 60;
    private const double Tolerance = 1e-6;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "the", "a", "an", "and", "dimension", "size", "value", "mm", "in"
    };

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "diameter", "length", "width", "height", "thickness", "radius", "wall", "angle", "count", "outer", "inner"
    };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dia"] = "diameter",
        ["diam"] = "diameter",
        ["\u00D8"] = "diameter",
        ["\u03C6"] = "diameter",
        ["qty"] = "count",
        ["number"] = "count",
        ["nos"] = "count",
        ["num"] = "count",
        ["thk"] = "thickness",
        ["len"] = "length",
        ["ht"] = "height",
        ["outside"] = "outer",
        ["external"] = "outer",
        ["od"] = "outer",
        ["inside"] = "inner",
        ["internal"] = "inner",
        ["id"] = "inner"
    };

    public static VisionParameterMapping Map(
        TemplateParameterSchema schema,
        VisionInspectionResult vision,
        string? ocrText = null)
    {
        var mapping = new VisionParameterMapping();
        var ocrNumbers = ExtractNumbers(ocrText);
        var accepted = new Dictionary<string, List<(VisionDimension Dimension, double Value)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dimension in vision.Dimensions)
        {
            var candidates = Score(schema, dimension);

            if (candidates.Count == 0)
            {
                mapping.Unmapped.Add(new UnmappedDimension(dimension, "No template parameter matches this label."));
                continue;
            }

            var top = candidates[0];

            if (candidates.Count > 1 && candidates[1].Score == top.Score)
            {
                mapping.Unmapped.Add(new UnmappedDimension(dimension, "The label matches more than one parameter, so it needs your confirmation."));
                continue;
            }

            if (!TryConvert(top.Parameter, dimension, out var value, out var convertError))
            {
                mapping.Unmapped.Add(new UnmappedDimension(dimension, convertError));
                continue;
            }

            var rangeError = TemplateParameterCatalog.ValidateValue(schema, top.Parameter.Key, value);

            if (rangeError is not null)
            {
                mapping.Unmapped.Add(new UnmappedDimension(dimension, rangeError));
                continue;
            }

            if (!accepted.TryGetValue(top.Parameter.Key, out var list))
            {
                list = new List<(VisionDimension, double)>();
                accepted[top.Parameter.Key] = list;
            }

            list.Add((dimension, value));
        }

        foreach (var pair in accepted)
        {
            var first = pair.Value[0];
            var conflict = pair.Value.Any(x => Math.Abs(x.Value - first.Value) > Tolerance);

            if (conflict)
            {
                foreach (var item in pair.Value)
                {
                    mapping.Unmapped.Add(new UnmappedDimension(item.Dimension, "The image shows different values for " + pair.Key + ", so none was used."));
                }

                continue;
            }

            var corroborated = ocrNumbers.Any(n =>
                Math.Abs(n - first.Dimension.Value) < Tolerance || Math.Abs(n - first.Value) < Tolerance);

            mapping.Mapped.Add(new MappedParameter(
                pair.Key,
                first.Value,
                ParameterSource.ImageUnverified,
                first.Dimension.Label,
                first.Dimension.Evidence ?? string.Empty,
                corroborated));
        }

        if (mapping.Mapped.Count > 0)
        {
            mapping.Notes.Add("Values read from an image are unverified. Please confirm each one before generating the STEP file.");
        }

        return mapping;
    }

    private static List<(TemplateParameterDefinition Parameter, int Score)> Score(
        TemplateParameterSchema schema,
        VisionDimension dimension)
    {
        var dimensionTokens = Tokenize(dimension.Label);
        var dimensionNormalized = Normalize(dimension.Label);
        var componentNormalized = Normalize(dimension.Component);
        var results = new List<(TemplateParameterDefinition, int)>();

        foreach (var parameter in schema.Parameters)
        {
            var best = 0;
            var variants = new List<string> { parameter.Key, parameter.Label };
            variants.AddRange(parameter.Aliases);

            foreach (var variant in variants)
            {
                var normalized = Normalize(variant);

                if (normalized.Length == 0)
                {
                    continue;
                }

                if (normalized == dimensionNormalized)
                {
                    best = Math.Max(best, 100);
                    continue;
                }

                var tokens = Tokenize(variant);

                if (tokens.Count == 0)
                {
                    continue;
                }

                var common = tokens.Count(t => dimensionTokens.Contains(t));
                var hasSpecificToken = tokens.Any(t => !GenericWords.Contains(t));

                if (common == tokens.Count && (tokens.Count >= 2 || hasSpecificToken))
                {
                    best = Math.Max(best, 80);
                }
                else if (tokens.Count >= 2 && common >= 2 && common >= tokens.Count - 1)
                {
                    best = Math.Max(best, 60);
                }
            }

            if (best >= MinScore && componentNormalized.Length > 0 && ComponentOwnsParameter(schema, parameter.Key, componentNormalized))
            {
                best += 10;
            }

            if (best >= MinScore)
            {
                results.Add((parameter, best));
            }
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }

    private static bool ComponentOwnsParameter(TemplateParameterSchema schema, string parameterKey, string componentNormalized)
    {
        foreach (var component in schema.Components)
        {
            var names = new List<string> { component.Title, component.Key };
            names.AddRange(component.Aliases);

            var matches = names
                .Select(Normalize)
                .Any(n => n.Length > 0 && (n == componentNormalized || componentNormalized.Contains(n) || n.Contains(componentNormalized)));

            if (matches && component.ParameterKeys.Any(k => string.Equals(k, parameterKey, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryConvert(
        TemplateParameterDefinition parameter,
        VisionDimension dimension,
        out double value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        var unit = (parameter.Unit ?? string.Empty).Trim().ToLowerInvariant();

        if (unit == "deg")
        {
            if (!dimension.IsAngle)
            {
                error = "Unit mismatch: " + parameter.Key + " is an angle in degrees.";
                return false;
            }

            value = dimension.Value;
        }
        else if (parameter.IsInteger || unit == "count" || unit.Length == 0)
        {
            if (!dimension.IsCount && Math.Abs(dimension.Value - Math.Round(dimension.Value)) > Tolerance)
            {
                error = "Unit mismatch: " + parameter.Key + " is a whole-number count.";
                return false;
            }

            if (dimension.IsAngle)
            {
                error = "Unit mismatch: " + parameter.Key + " is not an angle.";
                return false;
            }

            value = Math.Round(dimension.Value);
        }
        else
        {
            if (!dimension.TryGetMillimeters(out var mm))
            {
                error = "Unit mismatch: " + parameter.Key + " needs a length in millimeters.";
                return false;
            }

            value = Math.Round(mm, 4);
        }

        if (parameter.IsInteger)
        {
            value = Math.Round(value);
        }

        return true;
    }

    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
        {
            return set;
        }

        var spaced = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");

        foreach (var raw in Regex.Split(spaced.ToLowerInvariant(), "[^a-z0-9\u00F8\u03C6]+"))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var token = Synonyms.TryGetValue(raw, out var mapped) ? mapped : raw;

            if (token.Length > 3 && token.EndsWith('s'))
            {
                token = token[..^1];
            }

            if (token.Length == 0 || StopWords.Contains(token))
            {
                continue;
            }

            set.Add(token);
        }

        return set;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static List<double> ExtractNumbers(string? text)
    {
        var numbers = new List<double>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return numbers;
        }

        foreach (Match match in Regex.Matches(text, @"-?\d+(?:[.,]\d+)?"))
        {
            var normalized = match.Value.Replace(',', '.');

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                numbers.Add(n);
            }
        }

        return numbers;
    }
}