using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadToCode.LLM.Validation;

public static class JsonExtractor
{
    private static readonly Regex FenceRegex = new("```(?:json|JSON)?", RegexOptions.Compiled);
    private static readonly Regex ObjectRegex = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    public static JsonDocumentOptions ParseOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string WithPrefill(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "{";
        }

        return raw.TrimStart().StartsWith('{') ? raw : "{" + raw;
    }

    public static string? Extract(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = Normalize(raw);
        var scanned = Scan(text);

        if (scanned != null && IsValid(scanned))
        {
            return scanned;
        }

        var match = ObjectRegex.Match(text);
        if (match.Success)
        {
            var candidate = SkipDuplicateBraces(match.Value);
            if (IsValid(candidate))
            {
                return candidate;
            }
        }

        return scanned;
    }

    private static string Normalize(string raw)
    {
        var text = raw.Replace("\uFEFF", string.Empty)
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'');

        text = FenceRegex.Replace(text, string.Empty);

        var marker = text.IndexOf("<|", StringComparison.Ordinal);
        if (marker >= 0)
        {
            text = text[..marker];
        }

        return text;
    }

    private static string SkipDuplicateBraces(string text)
    {
        var start = 0;

        while (true)
        {
            var next = start + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            if (next < text.Length && text[start] == '{' && text[next] == '{')
            {
                start = next;
                continue;
            }

            break;
        }

        return text[start..];
    }

    private static string? Scan(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        while (true)
        {
            var next = start + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            if (next < text.Length && text[next] == '{')
            {
                start = next;
                continue;
            }

            break;
        }

        var builder = new StringBuilder();
        var closers = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    builder.Append(c);
                }
                else if (c == '\\')
                {
                    escaped = true;
                    builder.Append(c);
                }
                else if (c == '"')
                {
                    inString = false;
                    builder.Append(c);
                }
                else if (c == '\n')
                {
                    builder.Append("\\n");
                }
                else if (c == '\r')
                {
                    builder.Append("\\r");
                }
                else if (c == '\t')
                {
                    builder.Append("\\t");
                }
                else
                {
                    builder.Append(c);
                }

                continue;
            }

            builder.Append(c);

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                closers.Push('}');
            }
            else if (c == '[')
            {
                closers.Push(']');
            }
            else if (c == '}' || c == ']')
            {
                if (closers.Count > 0)
                {
                    closers.Pop();
                }

                if (closers.Count == 0)
                {
                    return builder.ToString();
                }
            }
        }

        if (inString)
        {
            builder.Append('"');
        }

        var repaired = builder.ToString().TrimEnd();
        while (repaired.EndsWith(',') || repaired.EndsWith(':'))
        {
            if (repaired.EndsWith(':'))
            {
                repaired += " null";
                break;
            }

            repaired = repaired[..^1].TrimEnd();
        }

        builder.Clear();
        builder.Append(repaired);

        while (closers.Count > 0)
        {
            builder.Append(closers.Pop());
        }

        return builder.ToString();
    }

    private static bool IsValid(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, ParseOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
