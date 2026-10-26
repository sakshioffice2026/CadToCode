namespace CadToCode.LLM.Prompts;

public static class VisionPromptTemplates
{
    public const int MaxHintChars = 200;

    public const string SystemPrompt =
        "You are a mechanical drawing reader. Reply with one raw JSON object and nothing else. Be extremely brief.";

    public const string Instructions =
        "Describe this engineering drawing as one JSON object with keys equipmentType, summary, views, components, dimensions, uncertainties. "
        + "equipmentType: one of axial fan, centrifugal fan, cyclone separator, industrial wet scrubber, pressure vessel, piping layout, unknown. "
        + "Cyclone separator: tall barrel over a cone with a bottom outlet and a side inlet. Fans need visible blades. Use unknown when unsure. "
        + "summary: under 6 words. views: name and description. components: name, description, quantity (0 if unknown), view. "
        + "dimensions: ONLY printed numbers, each as label, value, unit (mm, cm, m, in, deg or count), component, view, evidence (the printed text). Empty list if none. "
        + "uncertainties: empty list or short notes. Descriptions under 4 words. "
        + "Raw JSON only: start with { and end with }, no markdown, no code fences.";

    public static string BuildUserPrompt(string? userHint)
    {
        var hint = CleanHint(userHint);

        return string.IsNullOrEmpty(hint)
            ? Instructions
            : Instructions + " The user says this drawing is about: " + hint + ". Treat that as a hint only and do not copy numbers from it.";
    }

    public static string CleanHint(string? userHint)
    {
        if (string.IsNullOrWhiteSpace(userHint))
        {
            return string.Empty;
        }

        var chars = userHint
            .Where(c => !char.IsControl(c))
            .Take(MaxHintChars)
            .ToArray();

        return new string(chars).Trim();
    }

    public const string JsonGrammar = """"
root ::= "{" ws "\"equipmentType\"" ws ":" ws equipment "," ws "\"summary\"" ws ":" ws desc "," ws "\"views\"" ws ":" ws viewlist "," ws "\"components\"" ws ":" ws componentlist "," ws "\"dimensions\"" ws ":" ws dimensionlist "," ws "\"uncertainties\"" ws ":" ws strlist ws "}"
view ::= "{" ws "\"name\"" ws ":" ws name "," ws "\"description\"" ws ":" ws desc ws "}"
viewlist ::= "[" ws view ("," ws view){0,3} ws "]"
component ::= "{" ws "\"name\"" ws ":" ws name "," ws "\"description\"" ws ":" ws desc "," ws "\"quantity\"" ws ":" ws int "," ws "\"view\"" ws ":" ws short ws "}"
componentlist ::= "[" ws component ("," ws component){0,11} ws "]"
dimension ::= "{" ws "\"label\"" ws ":" ws name "," ws "\"value\"" ws ":" ws number "," ws "\"unit\"" ws ":" ws unit "," ws "\"component\"" ws ":" ws short "," ws "\"view\"" ws ":" ws short "," ws "\"evidence\"" ws ":" ws name ws "}"
dimensionlist ::= "[" ws (dimension ("," ws dimension){0,14})? ws "]"
strlist ::= "[" ws (desc ("," ws desc){0,2})? ws "]"
equipment ::= "\"axial fan\"" | "\"centrifugal fan\"" | "\"cyclone separator\"" | "\"industrial wet scrubber\"" | "\"pressure vessel\"" | "\"piping layout\"" | "\"unknown\""
unit ::= "\"mm\"" | "\"cm\"" | "\"m\"" | "\"in\"" | "\"deg\"" | "\"count\""
int ::= [0-9]{1,4}
number ::= "-"? ([0-9] | [1-9] [0-9]{0,8}) ("." [0-9]{1,4})?
char ::= [^"\\\x7F\x00-\x1F] | "\\" ["\\bfnrt]
name ::= "\"" char{1,30} "\""
short ::= "\"" char{0,30} "\""
desc ::= "\"" char{1,40} "\""
ws ::= " "?
"""";
}