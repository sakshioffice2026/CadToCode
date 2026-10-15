namespace CadToCode.LLM.Prompts;

public static class VisionPromptTemplates
{
    public const int MaxHintChars = 200;

    public const string SystemPrompt =
        "You are a mechanical drawing reader inside CadToCode. "
        + "You inspect one engineering drawing or blueprint image and report only what is visibly drawn or printed on it. "
        + "Reply with one JSON object and nothing else.";

    public const string Instructions =
        "Inspect the drawing and fill this JSON object: "
        + "equipmentType, summary, views, components, dimensions, uncertainties. "
        + "Rules: "
        + "1. equipmentType is the kind of equipment you believe the drawing shows (for example axial fan, cyclone separator, pipe tee), or an empty string if unclear. "
        + "2. summary is one short plain sentence about the drawing. "
        + "3. views lists each view you can see (front, side, top, section, detail) with a short description. "
        + "4. components lists each distinct part you can see, with a short description, a quantity (0 if unknown) and the view it appears in (empty string if unclear). "
        + "5. dimensions lists ONLY numbers that are printed on the drawing. Never estimate a size from the picture and never guess a missing number. "
        + "For each dimension give a descriptive label, the printed value as a number, the unit (mm, cm, m, in, deg or count), the component it belongs to, the view, "
        + "and evidence, which is the exact text you read next to it (for example the text 250 or R125). "
        + "If the unit is not printed use mm only when the drawing is clearly metric, otherwise omit the dimension and add a note to uncertainties. "
        + "6. uncertainties lists anything blurry, cut off, ambiguous or unreadable, and every dimension you are not sure about. "
        + "7. Do not invent parts, views or dimensions. An empty list is a correct answer. "
        + "8. Output valid JSON only, with no markdown and no extra text.";

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
root ::= "{" ws "\"equipmentType\"" ws ":" ws short "," ws "\"summary\"" ws ":" ws text "," ws "\"views\"" ws ":" ws viewlist "," ws "\"components\"" ws ":" ws componentlist "," ws "\"dimensions\"" ws ":" ws dimensionlist "," ws "\"uncertainties\"" ws ":" ws strlist ws "}"
view ::= "{" ws "\"name\"" ws ":" ws short "," ws "\"description\"" ws ":" ws text ws "}"
viewlist ::= "[" ws (view ("," ws view){0,3})? ws "]"
component ::= "{" ws "\"name\"" ws ":" ws short "," ws "\"description\"" ws ":" ws text "," ws "\"quantity\"" ws ":" ws int "," ws "\"view\"" ws ":" ws short ws "}"
componentlist ::= "[" ws (component ("," ws component){0,11})? ws "]"
dimension ::= "{" ws "\"label\"" ws ":" ws short "," ws "\"value\"" ws ":" ws number "," ws "\"unit\"" ws ":" ws unit "," ws "\"component\"" ws ":" ws short "," ws "\"view\"" ws ":" ws short "," ws "\"evidence\"" ws ":" ws short ws "}"
dimensionlist ::= "[" ws (dimension ("," ws dimension){0,14})? ws "]"
strlist ::= "[" ws (text ("," ws text){0,3})? ws "]"
unit ::= "\"mm\"" | "\"cm\"" | "\"m\"" | "\"in\"" | "\"deg\"" | "\"count\""
int ::= [0-9]{1,4}
number ::= "-"? ([0-9] | [1-9] [0-9]{0,8}) ("." [0-9]{1,4})?
char ::= [^"\\\x7F\x00-\x1F] | "\\" ["\\bfnrt]
short ::= "\"" char{0,40} "\""
text ::= "\"" char{0,100} "\""
ws ::= | " " | "\n" [ \t]{0,20}
"""";
}