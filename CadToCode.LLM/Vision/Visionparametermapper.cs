using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Core.Domain;
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

    public List<string> Warnings { get; } = new();
}

public static class VisionParameterMapper
{
    private const int MinScore = 60;
    private const double Tolerance = 1e-6;
    private const double LowConfidenceThreshold = 0.80;
    private const int SimpleTemplateMaxParameters = 6;
    private const int ComplexComponentCount = 4;
    private const string TruncationMarker = "cut off and repaired";

    private static readonly HashSet<string> NeutralEquipmentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "assembly", "unit", "equipment", "industrial", "machine", "plant", "drawing", "blueprint", "cad"
    };

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
        string? ocrText = null,
        double matchScore = 1.0)
    {
        var mapping = new VisionParameterMapping();

        AddMismatchWarnings(mapping, schema, vision, matchScore);

        var blockReason = GetEquipmentBlockReason(schema, vision);

        if (blockReason is not null)
        {
            foreach (var blocked in vision.Dimensions)
            {
                mapping.Unmapped.Add(new UnmappedDimension(blocked, blockReason));
            }

            mapping.Warnings.Add(blockReason + " No values were applied.");
            return mapping;
        }

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

    private static void AddMismatchWarnings(
        VisionParameterMapping mapping,
        TemplateParameterSchema schema,
        VisionInspectionResult vision,
        double matchScore)
    {
        var reasons = new List<string>();

        if (vision.Warnings.Any(w => w.Contains(TruncationMarker, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("the vision output was cut off, so parts of the drawing may be missing");
        }

        var confidence = matchScore > 1.0 ? matchScore / 100.0 : matchScore;

        if (confidence < LowConfidenceThreshold)
        {
            reasons.Add("the match confidence is only "
                        + (confidence * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%");
        }

        var equipment = vision.EquipmentType;

        if (!string.IsNullOrWhiteSpace(equipment) && !SharesVocabulary(schema, equipment))
        {
            reasons.Add("the drawing looks like '" + equipment.Trim() + "'");
        }

        if (schema.Parameters.Count <= SimpleTemplateMaxParameters
            && vision.Components.Count >= ComplexComponentCount)
        {
            reasons.Add("the drawing shows " + vision.Components.Count
                        + " distinct parts but this template is simple");
        }

        if (reasons.Count == 0)
        {
            return;
        }

        mapping.Warnings.Add(
            "Possible template mismatch with " + schema.Title + ": " + string.Join("; ", reasons)
            + ". If this is wrong, tell me the real equipment, for example `this is a wet scrubber`.");
    }

    private static string? GetEquipmentBlockReason(TemplateParameterSchema schema, VisionInspectionResult vision)
    {
        var equipment = vision.EquipmentType;

        if (string.IsNullOrWhiteSpace(equipment))
        {
            return null;
        }

        var schemaType = ResolveSchemaEquipment(schema);
        var visionType = EquipmentRegistry.Resolve(equipment);

        if (visionType != EquipmentType.Unknown && schemaType != EquipmentType.Unknown && visionType != schemaType)
        {
            return "The drawing shows '" + equipment.Trim() + "' but the template " + schema.Title
                   + " is for a different equipment type.";
        }

        if (schemaType == EquipmentType.PipingLayout && !SharesVocabulary(schema, equipment))
        {
            return "The drawing shows '" + equipment.Trim() + "', which is not piping, so the pipe template "
                   + schema.Title + " was not used.";
        }

        return null;
    }

    private static EquipmentType ResolveSchemaEquipment(TemplateParameterSchema schema)
    {
        var key = schema.TemplateKey ?? string.Empty;

        if (key.Contains("scrubber", StringComparison.OrdinalIgnoreCase))
        {
            return EquipmentType.WetScrubber;
        }

        if (key.Contains("cyclone", StringComparison.OrdinalIgnoreCase))
        {
            return EquipmentType.Cyclone;
        }

        if (key.Contains("pipe", StringComparison.OrdinalIgnoreCase))
        {
            return EquipmentType.PipingLayout;
        }

        return EquipmentType.Unknown;
    }

    private static bool SharesVocabulary(TemplateParameterSchema schema, string equipment)
    {
        var equipmentTokens = Tokenize(equipment)
            .Where(t => t.Length >= 3 && !NeutralEquipmentWords.Contains(t))
            .ToList();

        if (equipmentTokens.Count == 0)
        {
            return true;
        }

        var vocabulary = Tokenize(schema.Title);

        foreach (var component in schema.Components)
        {
            vocabulary.UnionWith(Tokenize(component.Key));
            vocabulary.UnionWith(Tokenize(component.Title));

            foreach (var alias in component.Aliases)
            {
                vocabulary.UnionWith(Tokenize(alias));
            }
        }

        return equipmentTokens.Any(vocabulary.Contains);
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