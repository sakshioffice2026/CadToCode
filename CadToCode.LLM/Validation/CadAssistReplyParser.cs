using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Validation;

public sealed class CadAssistParsedReply
{
    public string Text { get; set; } = string.Empty;

    public List<CadAssistCodeBlockDto> CodeBlocks { get; set; } = new();
}

public static class CadAssistReplyParser
{
    private const int MaxCodeBlocks = 4;

    private static readonly Regex Fence = new(
        @"```[ \t]*([A-Za-z0-9#+_\-]*)[ \t]*\r?\n(.*?)(?:```|\z)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex LeadingToken = new(
        @"^\s*(?:<\|[^|>\r\n]{1,40}\|>(?:[ \t]*(?:assistant|system|user)[ \t]*(?:\r?\n|$))?|###[ \t]*Assistant:[ \t]*\r?\n?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlankLines = new(@"(\r?\n){3,}", RegexOptions.Compiled);

    public static CadAssistParsedReply Parse(string? raw, IEnumerable<string>? stopMarkers = null)
    {
        var result = new CadAssistParsedReply();
        var text = Strip(raw, stopMarkers);

        if (text.Length == 0)
        {
            return result;
        }

        var index = 0;

        foreach (Match match in Fence.Matches(text))
        {
            var code = match.Groups[2].Value.Trim('\r', '\n').TrimEnd();

            if (code.Trim().Length == 0 || result.CodeBlocks.Count >= MaxCodeBlocks)
            {
                continue;
            }

            index++;

            result.CodeBlocks.Add(new CadAssistCodeBlockDto
            {
                Language = NormalizeLanguage(match.Groups[1].Value),
                Title = index == 1 ? "STEP script" : "Script " + index,
                Code = code,
                LineCount = CountLines(code)
            });
        }

        if (result.CodeBlocks.Count == 0 && LooksLikeScript(text))
        {
            var code = text.Trim();

            result.CodeBlocks.Add(new CadAssistCodeBlockDto
            {
                Language = "csharp",
                Title = "STEP script",
                Code = code,
                LineCount = CountLines(code)
            });

            return result;
        }

        var prose = Fence.Replace(text, string.Empty);
        prose = BlankLines.Replace(prose, "\n\n").Trim();
        result.Text = prose;

        return result;
    }

    private static string Strip(string? raw, IEnumerable<string>? stopMarkers)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.TrimStart();

        while (true)
        {
            var stripped = LeadingToken.Replace(text, string.Empty, 1);

            if (stripped.Length == text.Length)
            {
                break;
            }

            text = stripped.TrimStart();
        }

        if (stopMarkers is not null)
        {
            foreach (var marker in stopMarkers)
            {
                if (string.IsNullOrEmpty(marker))
                {
                    continue;
                }

                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    text = text[..at];
                }
            }
        }

        return text.Trim();
    }

    private static bool LooksLikeScript(string text)
    {
        return text.Contains("new StepDocument(", StringComparison.Ordinal)
            && text.Contains("using CadToCode.Step", StringComparison.Ordinal);
    }

    private static string NormalizeLanguage(string? language)
    {
        var value = (language ?? string.Empty).Trim().ToLowerInvariant();

        return value switch
        {
            "" or "cs" or "c#" or "csharp" or "csx" => "csharp",
            _ => value
        };
    }

    private static int CountLines(string code)
    {
        var count = 1;

        foreach (var ch in code)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }
}