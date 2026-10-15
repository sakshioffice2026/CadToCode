using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CadToCode.LLM.Templates;

public sealed class ParameterValue
{
    public double Value { get; init; }
    public ParameterSource Source { get; init; } = ParameterSource.User;

    public ParameterValue()
    {
    }

    public ParameterValue(double value, ParameterSource source)
    {
        Value = value;
        Source = source;
    }
}

public sealed class ComposeRequest
{
    public string TemplateKey { get; init; } = string.Empty;

    /// <summary>Component keys to generate. Empty means the entire assembly.</summary>
    public IReadOnlyList<string> ComponentKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, ParameterValue> Values { get; init; }
        = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ComposedScriptResult
{
    public bool Success { get; init; }
    public string Script { get; init; } = string.Empty;
    public string FileBaseName { get; init; } = string.Empty;
    public bool IsFullAssembly { get; init; }
    public IReadOnlyList<string> IncludedScriptNames { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, double> AppliedValues { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public static ComposedScriptResult Fail(params string[] errors)
    {
        return new ComposedScriptResult { Success = false, Errors = errors };
    }
}

public static class TemplateScriptComposer
{
    private static readonly Regex ConstRegex = new(
        @"^(?<head>[ \t]*const[ \t]+(?<type>double|int)[ \t]+(?<name>\w+)[ \t]*=[ \t]*)(?<val>[^;\r\n]+);",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AddNameRegex = new(
        @"^doc\.Add\.\w+\(\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex DocNameRegex = new(
        @"new\s+StepDocument\(\s*""(?<name>[^""]+)""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex SavePathRegex = new(
        @"Path\.GetFullPath\(\s*""(?<name>[^""]+)\.step""\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex UsingStatementRegex = new(
        @"^[ \t]*using[ \t]*\(|^[ \t]*using[ \t]+var[ \t]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static ComposedScriptResult Compose(ComposeRequest request)
    {
        var schema = TemplateParameterCatalog.Find(request.TemplateKey);
        var template = BuiltInCadTemplates.Find(request.TemplateKey);

        if (schema is null || template is null)
        {
            return ComposedScriptResult.Fail("Template '" + request.TemplateKey + "' is not available.");
        }

        var errors = new List<string>();
        var values = schema.BuildDefaults();

        foreach (var pair in request.Values)
        {
            if (schema.FindParameter(pair.Key) is null)
            {
                errors.Add("Unknown parameter '" + pair.Key + "' for " + schema.Title + ".");
                continue;
            }

            if (pair.Value.Source == ParameterSource.ImageUnverified)
            {
                errors.Add("Parameter '" + pair.Key + "' came from an image and has not been confirmed by the user.");
                continue;
            }

            values[schema.FindParameter(pair.Key)!.Key] = pair.Value.Value;
        }

        if (errors.Count > 0)
        {
            return ComposedScriptResult.Fail(errors.ToArray());
        }

        var validation = TemplateParameterCatalog.ValidateSet(schema, values);

        if (validation.Count > 0)
        {
            return ComposedScriptResult.Fail(validation.ToArray());
        }

        var selected = new List<TemplateComponentDefinition>();

        foreach (var key in request.ComponentKeys)
        {
            var component = ResolveComponent(schema, key);

            if (component is null)
            {
                errors.Add("Component '" + key + "' does not exist in " + schema.Title + ".");
                continue;
            }

            if (!selected.Contains(component))
            {
                selected.Add(component);
            }
        }

        if (errors.Count > 0)
        {
            return ComposedScriptResult.Fail(errors.ToArray());
        }

        var fullAssembly = selected.Count == 0 || selected.Count == schema.Components.Count;
        var wantedNames = fullAssembly
            ? schema.Components.SelectMany(c => c.ScriptNames).ToList()
            : selected.SelectMany(c => c.ScriptNames).ToList();

        var script = template.Script;
        var docStart = script.IndexOf("var doc = new StepDocument(", StringComparison.Ordinal);

        if (docStart < 0)
        {
            return ComposedScriptResult.Fail("Template script has no StepDocument declaration.");
        }

        var docEnd = script.IndexOf(';', docStart);
        var outputStart = script.IndexOf("var outputPath", docEnd + 1, StringComparison.Ordinal);

        if (docEnd < 0 || outputStart < 0)
        {
            return ComposedScriptResult.Fail("Template script structure is not recognised.");
        }

        var header = script.Substring(0, docStart);
        var docStatement = script.Substring(docStart, docEnd - docStart + 1);
        var body = script.Substring(docEnd + 1, outputStart - docEnd - 1);
        var trailer = script.Substring(outputStart);

        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        header = ConstRegex.Replace(header, match =>
        {
            var name = match.Groups["name"].Value;

            if (!values.TryGetValue(name, out var value))
            {
                return match.Value;
            }

            replaced.Add(name);

            var literal = match.Groups["type"].Value == "int"
                ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.################", CultureInfo.InvariantCulture);

            return match.Groups["head"].Value + literal + ";";
        });

        var missing = values.Keys.Where(k => !replaced.Contains(k)).ToList();

        if (missing.Count > 0)
        {
            return ComposedScriptResult.Fail(
                "Template script has no const declaration for: " + string.Join(", ", missing) + ".");
        }

        var statements = SplitStatements(body);
        var kept = new List<string>();
        var includedNames = new List<string>();

        foreach (var statement in statements)
        {
            var addMatch = AddNameRegex.Match(statement);

            if (!addMatch.Success)
            {
                kept.Add(statement);
                continue;
            }

            var name = addMatch.Groups["name"].Value;

            if (wantedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(statement);
                includedNames.Add(name);
            }
        }

        var notFound = wantedNames
            .Where(n => !includedNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (notFound.Count > 0)
        {
            return ComposedScriptResult.Fail(
                "Template script has no statement for: " + string.Join(", ", notFound) + ".");
        }

        var suffix = fullAssembly ? string.Empty : "_" + string.Join("_", includedNames);
        var baseName = DocNameRegex.Match(docStatement).Groups["name"].Value;

        docStatement = DocNameRegex.Replace(docStatement, m =>
            "new StepDocument(\"" + m.Groups["name"].Value + suffix + "\")");

        trailer = SavePathRegex.Replace(trailer, m =>
            "Path.GetFullPath(\"" + m.Groups["name"].Value + suffix + ".step\")");

        var builder = new StringBuilder();
        builder.Append(header.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(docStatement);

        foreach (var statement in kept)
        {
            builder.AppendLine(statement);
        }

        builder.AppendLine();
        builder.Append(trailer.TrimEnd());
        builder.AppendLine();

        var composed = builder.ToString();

        var safetyError = CheckSafety(composed);

        if (safetyError is not null)
        {
            return ComposedScriptResult.Fail(safetyError);
        }

        return new ComposedScriptResult
        {
            Success = true,
            Script = composed,
            FileBaseName = baseName + suffix,
            IsFullAssembly = fullAssembly,
            IncludedScriptNames = includedNames,
            AppliedValues = values
        };
    }

    public static TemplateComponentDefinition? ResolveComponent(TemplateParameterSchema schema, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var needle = text.Trim();

        var exact = schema.Components.FirstOrDefault(c =>
            string.Equals(c.Key, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Title, needle, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Contains(needle, StringComparer.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        return schema.Components
            .Select(c => new
            {
                Component = c,
                Score = c.Aliases
                    .Where(a => needle.Contains(a, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Length)
                    .DefaultIfEmpty(0)
                    .Max()
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Component)
            .FirstOrDefault();
    }

    public static string? CheckSafety(string script)
    {
        if (UsingStatementRegex.IsMatch(script))
        {
            return "Generated script wraps an object in a using block; StepDocument and StepModel must never be disposed that way.";
        }

        if (!script.Contains("doc.Save(", StringComparison.Ordinal))
        {
            return "Generated script never saves the STEP document.";
        }

        if (!script.Contains("new StepDocument(", StringComparison.Ordinal))
        {
            return "Generated script does not create a StepDocument.";
        }

        return null;
    }

    private static List<string> SplitStatements(string body)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var paren = 0;
        var brace = 0;
        var inString = false;
        var started = false;
        var isControl = false;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (!started)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                started = true;
                isControl = StartsWithControlKeyword(body, i);
            }

            current.Append(c);

            if (inString)
            {
                if (c == '\\' && i + 1 < body.Length)
                {
                    current.Append(body[++i]);
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
                    break;
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
            }

            var endsControl = isControl && c == '}' && brace == 0 && paren == 0;
            var endsSimple = !isControl && c == ';' && brace == 0 && paren == 0;

            if (endsControl || endsSimple)
            {
                result.Add(current.ToString().TrimEnd());
                current.Clear();
                started = false;
                isControl = false;
            }
        }

        var rest = current.ToString().Trim();

        if (rest.Length > 0)
        {
            result.Add(rest);
        }

        return result;
    }

    private static bool StartsWithControlKeyword(string text, int index)
    {
        foreach (var keyword in new[] { "for", "foreach", "while", "if" })
        {
            if (string.CompareOrdinal(text, index, keyword, 0, keyword.Length) != 0)
            {
                continue;
            }

            var next = index + keyword.Length;

            if (next >= text.Length)
            {
                continue;
            }

            if (char.IsWhiteSpace(text[next]) || text[next] == '(')
            {
                return true;
            }
        }

        return false;
    }
}