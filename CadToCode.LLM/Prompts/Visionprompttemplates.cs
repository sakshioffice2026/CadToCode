namespace CadToCode.LLM.Prompts;

public static class VisionPromptTemplates
{
    public const int MaxHintChars = 200;

    public const string SystemPrompt =
        "You are a mechanical drawing reader. Reply with one raw JSON object and nothing else. Be extremely brief.";

    public const string Instructions =
        "Describe this engineering drawing as one JSON object with the keys equipmentType, summary, views, components, dimensions, uncertainties. "
        + "equipmentType: exactly one of axial fan, centrifugal fan, cyclone separator, industrial wet scrubber, pressure vessel, piping layout, unknown. "
        + "Use axial fan for any fan with an impeller, hub and blades inside a short cylindrical casing. "
        + "Use centrifugal fan for any fan or blower with a scroll (spiral) casing, a side inlet and a drum or disc impeller with blades around its rim. "
        + "Use cyclone separator only for a drawing with a conical hopper and tangential inlet. "
        + "Use unknown when unsure. "
        + "summary: under 8 words. "
        + "views: each visible view (front, side, top, section, detail) as name and description. "
        + "components: each visible part as name, description, quantity (0 if unknown) and view. "
        + "dimensions: ONLY numbers printed on the drawing, each as label, value, unit (mm, cm, m, in, deg or count), component, view and evidence (the exact printed text containing that number). Never use a view name as a label. If no numbers are printed, return an empty list. "
        + "uncertainties: short notes or an empty list. "
        + "Name the real equipment and parts you can see. Every description is under 5 words. Never repeat yourself. "
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