using System.Text.RegularExpressions;

namespace CadToCode.LLM.Services;

/// <summary>
/// Classifies conversational requests for manifest-backed equipment before legacy template matching.
/// This classifier does not generate CAD; the conversation service should dispatch design requests
/// through EquipmentDesignFlowDispatcher and use equipment/parts replies for informational requests.
/// </summary>
public static class CadAssistEquipmentIntentResolver
{
    private static readonly Regex PartsRegex = new(
        @"\b(parts?|components?|subtypes?|types|classifications|what makes up|list the)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex GenerateRegex = new(
        @"\b(generate|create|write|build|make|export|produce)\b.{0,100}\b(step|stp|cad|script|code|assembly|part|model)\b|\b(step|stp)\s+file\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex IdentifyRegex = new(
        @"\b(what is this|identify|inspect|recognize|recognise|what equipment|which equipment)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public static CadAssistEquipmentIntent Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CadAssistEquipmentIntent.Unknown;
        }

        if (IsMatch(GenerateRegex, text))
        {
            return CadAssistEquipmentIntent.GenerateCad;
        }

        if (IsMatch(PartsRegex, text))
        {
            return CadAssistEquipmentIntent.ListParts;
        }

        if (IsMatch(IdentifyRegex, text))
        {
            return CadAssistEquipmentIntent.IdentifyEquipment;
        }

        return CadAssistEquipmentIntent.DesignOrFollowUp;
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

public enum CadAssistEquipmentIntent
{
    Unknown = 0,
    IdentifyEquipment = 1,
    ListParts = 2,
    DesignOrFollowUp = 3,
    GenerateCad = 4
}
