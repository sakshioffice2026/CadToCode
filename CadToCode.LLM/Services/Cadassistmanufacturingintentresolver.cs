using System.Text.RegularExpressions;

namespace CadToCode.LLM.Services;

/// <summary>
/// Detects when a chat message asks for manufacturing (fabrication, shop, per-part) drawings
/// from a GA DXF, and extracts any part names the user limited the request to.
/// </summary>
public static class CadAssistManufacturingIntentResolver
{
    private static readonly Regex RequestRegex = new(
        @"\b(generate|create|make|build|produce|prepare|give|get|export|extract|split|need|want)\b.{0,80}\b(manufacturing|fabrication|shop|workshop|production|detail|detailed|part|parts|component|components|individual|per[\s-]?part)\b.{0,40}\b(drawings?|drgs?|dxfs?|sheets?)\b"
        + @"|\b(manufacturing|fabrication|shop|workshop|production)\s+(drawings?|drgs?)\b"
        + @"|\b(drawings?|drgs?)\s+(for|of)\s+(each|every|all)\s+(parts?|components?)\b"
        + @"|\b(split|break|separate)\b.{0,40}\b(ga|general\s+arrangement)\b.{0,40}\b(parts?|components?|drawings?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex AllPartsRegex = new(
        @"\b(all|every|each|complete|full|whole)\b.{0,20}\b(parts?|components?)\b|\bwhole\s+(ga|drawing|assembly)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex OnlyPartsRegex = new(
        @"\b(?:only|just|for|of)\s+(?:the\s+)?(?<names>[A-Za-z0-9][A-Za-z0-9 _\-,/&]{1,120}?)\s*(?:parts?|components?|drawings?)?\s*(?:$|[.!?])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "ga", "the", "this", "that", "my", "our", "drawing", "drawings", "dxf", "file", "files",
        "part", "parts", "component", "components", "manufacturing", "fabrication", "shop", "each", "every", "all"
    };

    public static bool IsManufacturingRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return IsMatch(RequestRegex, text);
    }

    public static bool WantsAllParts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (IsMatch(AllPartsRegex, text))
        {
            return true;
        }

        return ExtractPartNames(text).Count == 0;
    }

    public static List<string> ExtractPartNames(string? text)
    {
        var names = new List<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return names;
        }

        Match match;

        try
        {
            match = OnlyPartsRegex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return names;
        }

        if (!match.Success)
        {
            return names;
        }

        var raw = match.Groups["names"].Value;

        foreach (var piece in Regex.Split(raw, @"\s*(?:,|/|&|\band\b)\s*", RegexOptions.IgnoreCase))
        {
            var name = piece.Trim();

            if (name.Length < 2 || Noise.Contains(name))
            {
                continue;
            }

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
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
}