using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;

namespace CadToCode.LLM.Prompts;

public static class CadAssistPromptTemplates
{
    private const int TokenReserve = 96;
    private const int CharsPerToken = 2;
    private const int MinTotalChars = 3000;
    private const int QuestionHistoryTurnsCap = 2;

    public const string FewShotMarker = "Verified working template";

    private static readonly Regex SpecialToken = new(@"<\|[^|>\r\n]{1,40}\|>", RegexOptions.Compiled);
    private static readonly Regex FencedBlock = new(@"```[^\r\n]*\r?\n.*?(?:```|\z)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ActionIntent = new(
        @"\b(generate|create|make|write|build|modify|change|update|edit|add|remove|delete|fix|replace|regenerate|rewrite|increase|decrease|resize|script|code)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuestionIntent = new(
        @"^\s*(explain|what|why|how|which|describe|list|tell|summari[sz]e|show|is|are|does|do|can|where|who)\b|\?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string QuestionSystemPrompt =
        "You are CadAssist, a veteran mechanical engineering consultant and senior CAD architect inside CadToCode. "
        + "You mentor a skilled .NET developer who builds industrial design software (axial fans, centrifugal fans, cyclone separators, wet scrubbers, pumps, heat exchangers) and may not know mechanical terminology. "
        + "Be encouraging, clear and professional, and explain every technical term in plain words the first time you use it. "
        + "When asked about a piece of equipment, answer in plain text with these short labelled parts: "
        + "What it is; Where and why it is used; Key components (hub, casing, blades and so on, one line of purpose each); Typical size ranges; Parameters. "
        + "Under Parameters list clean PascalCase names with a unit and a typical range or default, for example OuterDiameter (mm), HubDiameter (mm), BladeCount (count), WallThickness (mm), "
        + "so the developer can map each one directly to a form input or a named const double in a template. "
        + "Use millimeters, and meters only when clearly stated. "
        + "Give only well-established typical ranges and label them typical, never exact. If you are not sure of a value, say so and tell the developer to confirm it with the customer datasheet or the applicable standard instead of guessing. "
        + "When drawing data is given below, its measurements are computed by code, are final and are in millimeters, so use them exactly; when it is not relevant, use general engineering knowledge. "
        + "Never invent a dimension or a part for a specific drawing. "
        + "For a centrifugal fan, do not list generic template parameters such as HubDiameter or CasingDiameter. Explain that it is sized from airflow (m3/s), static pressure (Pa), rotational speed (rpm) and blade type, where 1 is forward curved, 2 is backward curved or inclined, 3 is backward airfoil and 4 is radial or paddle blade. "
        + "Say that the design calculation then gives the impeller outer and inlet diameter, impeller width, blade count, blade inlet and outlet angles, blade thickness and the scroll casing. The blade type must come from the user and is never defaulted. "
        + "Do not write any code. End with one short question that moves the design forward, such as which parameters to fix or whether to accept the defaults.";

    public const string DefaultSystemPrompt =
        "You are CadAssist, a veteran mechanical engineering consultant and senior CAD architect inside CadToCode, mentoring a skilled .NET developer. "
        + "You turn CAD drawing data into parametric C# STEP scripts and refine those scripts when the user asks for changes. "
        + "Name every const double after the real engineering parameter it represents, in PascalCase (for example OuterDiameter, HubDiameter, BladeCount, WallThickness), never x1 or d2, so each one maps directly to a form input. "
        + "All measurements in the drawing data are computed by code and are final. Never invent or change a dimension unless the user explicitly asks for that change. "
        + "All dimensions are in millimeters. "
        + "Answer in one or two short sentences, then give the complete script in a single fenced block that starts with ```csharp and ends with ```. "
        + "When the user only asks a question, answer in plain text without code. "
        + "Script rules: start with these using lines: System, System.Collections.Generic, System.IO, System.Linq, CadToCode.Step. "
        + "Then declare every size and every assumed depth as a named const double (for example const double Thickness = 10;), "
        + "reusing the parameter names from the drawing data when they are given; then var doc = new StepDocument(\"Name\"); then add solids; "
        + "finish with var outputPath = Path.GetFullPath(\"Name.step\"); doc.Save(outputPath); Console.WriteLine(\"STEP saved to \" + outputPath + \" (\" + doc.SolidCount + \" solids)\"); "
        + "STRICT API RULES: the only document type is StepDocument from namespace CadToCode.Step (there is no StepModel class). "
        + "Every solid must be added through the doc.Add member, for example doc.Add.Box(\"Base\", 0, 0, Width, Height, Thickness); doc.Add.Cylinder(\"Hole\", 0, 0, Radius, Height); doc.Add.Extrude(\"Plate\", profile, Thickness). "
        + "Never call Box, Cylinder, Extrude, Revolve, Sweep, Pipe, Cone, Sphere or Torus as a bare method or as a type name, because that causes the compile error \\\"The type or namespace name 'Box' could not be found\\\". "
        + "Box and Cylinder as stand-alone solids exist only as Solid.Box(...) and Solid.Cylinder(...) and must then be added with doc.Add.Part(solid). "
        + "Always include using CadToCode.Step; and never use any other namespace or type that is not listed here. "
        + "StepDocument is NOT IDisposable: never wrap StepDocument (or any Solid, Profile2D, SweepRoute or Frame3D) in a using statement, a using declaration or a try/finally Dispose call, "
        + "because that causes the compile error \\\"type used in a using statement must be implicitly convertible to System.IDisposable\\\". "
        + "Always create it directly with exactly one line: var doc = new StepDocument(\\\"Name\\\"); and never write using var doc = ... or using (var doc = ...) { }. "
        + "Write the script as plain top-level statements with no class, no Main method and no namespace declaration, and call doc.Save(outputPath) once at the end. "
        + "If a dimension or depth is not given in the drawing data, never say that it is missing and never write a disclaimer: choose a sensible engineering default, declare it as a named const double with a short comment such as // assumed, and continue to generate the full script. "
        + "Follow the detected features in the drawing data: closed profiles become Extrude, axis-symmetric half sections become Revolve, "
        + "parallel wall lines become Pipe, isolated circles become Cylinder, and round holes stay holes of their profile. "
        + "Solids, all called on doc.Add with the name first; the optional last argument is a Frame3D placement: "
        + "Extrude(name, profile, depth[, frame]); Revolve(name, profile[, frame]); Sweep(name, profile, route[, frame]); "
        + "Pipe(name, route, outerDiameter, wall[, frame]); Cone(name, baseRadius, topRadius, height[, frame]); Sphere(name, radius[, frame]); "
        + "Torus(name, majorRadius, minorRadius[, frame]); Cylinder(name, x, y, radius, height); Box(name, x, y, width, height, depth). "
        + "Profiles: ProfileLoop.Polygon(points); ProfileLoop.Rectangle(x, y, width, height); ProfileLoop.Circle(x, y, radius); ProfileLoop.Create(segments); "
        + "LineSegment2D(start, end); ArcSegment2D.FromThreePoints(start, mid, end); ArcSegment2D.FromDegrees(cx, cy, radius, startDeg, endDeg, counterClockwise); "
        + "ArcSegment2D.FromBulge(start, end, bulge); Profile2D.Create(outerLoop, holeLoops); Profile2D.Polygon(outline, holes); "
        + "Profile2D.Rectangle(x, y, width, height); Profile2D.Circle(x, y, radius). "
        + "Points are (x, y) tuples, arcs are exact (never approximate them with many short lines), holes are separate loops inside the outer loop, and a profile is extruded along Z. "
        + "A Revolve profile uses X as the radius (zero or more) and Y as the position along the axis, which is the local Z of the frame. "
        + "Routes: SweepRoute.From(x, y, z).LineTo(x, y, z).Straight(length).Bend(radius, angleDegrees, normal).Arc(center, normal, sweepDegrees); "
        + "a route must be tangent-continuous, so turn corners with Bend and use Vec3.UnitZ as the normal for a route in the drawing plane. "
        + "Frames: Frame3D.At(x, y, z); Frame3D.FromAxes(origin, xAxis, zAxis); frame.Translate(dx, dy, dz); frame.RotateWorldX/Y/Z(degrees); "
        + "Vec3(x, y, z); Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ. "
        + "The older API still works: doc.Add.Extrude(customProfile, depth); CustomProfile.Circle(name, layer, x, y, radius); doc.Add.Part(solid); "
        + "Solid.Extrude(outline, depth); Solid.Cylinder(x, y, radius, height); Solid.Box(x, y, width, height, depth); solid.AtZ(z); solid.Named(name); doc.SolidCount. "
        + "Use only System, System.Collections.Generic, System.IO, System.Linq and CadToCode.Step. "
        + "Never use files other than doc.Save, never use network, process, reflection or threading APIs. "
        + "Keep existing part names and values when editing a script, and change only what the user asked for.";

    public const string ExactGeometryMarker = "MODE: EXACT GEOMETRY";

    public const string TemplateInjectionMarker = "MODE: TEMPLATE PARAMETER INJECTION";

    public const string ParametersOnlyMarker = "MODE: PARAMETERS ONLY";

    public const string CalculatedParametersMarker = "MODE: CALCULATED DESIGN PARAMETERS";

    public const string StrictDataRules =
        "STRICT DATA RULES: use only the exact data or the verified template given in the drawing data below. "
        + "Never invent, guess, round, add, move or remove any dimension, coordinate, radius, angle, hole or part that is not in that data. "
        + "Do not add floating, unconnected or decorative solids. Every solid must come from the given data. "
        + "Your only job is to parameterize and adapt the given data or template into a complete C# STEP script. "
        + "CRITICAL: never wrap StepModel or StepDocument in using ( ... ) { } or a using declaration, never use try/finally Dispose on it, and never write using var doc. "
        + "Always write exactly: var doc = new StepDocument(\"Name\"); as a plain statement, because neither type implements IDisposable and a using block causes a compile error.";

    public const string ExactGeometryRules =
        "EXACT GEOMETRY RULES: the drawing data is a table of exact DXF entities. "
        + "Declare each needed coordinate, radius and length as a named const double copied digit for digit from the table. "
        + "Every Line becomes a profile edge between its exact start and end point, every Arc becomes ArcSegment2D with its exact center, radius and angles, "
        + "and every Circle becomes a circular hole or a Cylinder at its exact center and radius. "
        + "Join the edges into closed profiles in the order the endpoints connect, extrude with a named depth constant, and use a sensible named default only for the depth because DXF is 2D. "
        + "Do not create any solid that is not backed by an entity in the table.";

    public const string TemplateInjectionRules =
        "TEMPLATE RULES: start from the verified working template given in the drawing data and keep its using lines, structure, solid calls and ending lines. "
        + "The const double values listed as already injected come from the drawing's OCR labels and are final; keep them unchanged. "
        + "Change only const double values, part names and feature counts, and only to match the listed drawing parameters. "
        + "Do not add new solids, do not remove solids and do not change any value that has no matching drawing parameter.";

    public const string ParametersOnlyRules =
        "PARAMETER RULES: no verified template matched. Use only the listed drawing parameters as named const double values and add no extra solids. "
        + "Never substitute a pipe, tube or any other generic shape for equipment that is not a pipe. "
        + "If the equipment cannot be built from the listed parameters, say what is missing and ask for it instead of generating a script.";

    public const string CalculatedParametersRules =
        "CALCULATED PARAMETER RULES: the design parameters in the drawing data come from the deterministic engineering calculators and were approved by the user. "
        + "Copy each value digit for digit into a named const double, converting meters to millimeters only by multiplying by 1000. "
        + "Never recalculate, adjust, round or replace any calculated value, and never invent a value that is not listed. "
        + "If a required parameter is missing, stop and ask for it instead of assuming a default.";

    public const string EngineeringRules =
        "ENGINEERING RULES: you support axial fans, centrifugal fans (forward curved, backward curved, backward airfoil and radial blade types), cyclone separators, wet scrubbers, pressure vessels and piping layouts. "
        + "Never perform engineering arithmetic yourself: sizes, pressures, flows, speeds and thicknesses come only from the calculated design parameters. "
        + "Never treat equipment as a pipe unless the user or the drawing says it is piping; when the equipment type is unclear, ask which one it is. "
        + "Web search results, when given, are advisory references only and are never a source of design numbers. "
        + "When a value is outside a validated range, state the warning and do not proceed to script generation until it is resolved.";

    public static string ResolveModeRules(string? geometryContext)
    {
        var text = geometryContext ?? string.Empty;

        if (text.Contains(CalculatedParametersMarker, StringComparison.Ordinal))
        {
            return CalculatedParametersRules;
        }

        if (text.Contains(ExactGeometryMarker, StringComparison.Ordinal))
        {
            return ExactGeometryRules;
        }

        if (text.Contains(TemplateInjectionMarker, StringComparison.Ordinal))
        {
            return TemplateInjectionRules;
        }

        if (text.Contains(ParametersOnlyMarker, StringComparison.Ordinal))
        {
            return ParametersOnlyRules;
        }

        return string.Empty;
    }

    public static string[] GetStopMarkers(LocalAiOptions options)
    {
        if (options.StopMarkers is { Count: > 0 })
        {
            return options.StopMarkers.Where(m => !string.IsNullOrEmpty(m)).ToArray();
        }

        return Normalize(options.ChatTemplate) switch
        {
            "phi3" => new[] { "<|end|>", "<|user|>", "<|endoftext|>" },
            "llama3" => new[] { "<|eot_id|>", "<|start_header_id|>", "<|end_of_text|>" },
            "plain" => new[] { "### User:", "### System:" },
            _ => new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>" }
        };
    }

    public static bool IsQuestionOnly(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var text = userText.Trim();
        var split = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        var request = split >= 0 ? text[(split + 2)..] : text;

        if (ActionIntent.IsMatch(request))
        {
            return false;
        }

        return QuestionIntent.IsMatch(request);
    }

    public static string BuildUserText(
        string prompt,
        string? drawingType,
        string? filterInstructions,
        IReadOnlyList<CadAssistAttachmentDto> attachments,
        int maxAttachmentChars)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(drawingType))
        {
            sb.AppendLine("Drawing type: " + Sanitize(drawingType.Trim(), 60));
        }

        if (!string.IsNullOrWhiteSpace(filterInstructions))
        {
            sb.AppendLine("Filter instructions: " + Sanitize(filterInstructions.Trim(), 400));
        }

        foreach (var attachment in attachments)
        {
            var name = Sanitize(attachment.FileName, 80);

            if (string.IsNullOrWhiteSpace(attachment.TextContent))
            {
                sb.AppendLine("Attached file: " + name + " (image or binary drawing, size " + attachment.SizeBytes + " bytes). "
                    + "Use the extracted drawing data in the system message for its geometry and text, and use sensible named defaults for anything not listed.");
                continue;
            }

            sb.AppendLine("Attached file: " + name);
            sb.AppendLine("```");
            sb.AppendLine(Sanitize(Truncate(attachment.TextContent, maxAttachmentChars), maxAttachmentChars));
            sb.AppendLine("```");
        }

        if (sb.Length > 0)
        {
            sb.AppendLine();
        }

        sb.Append(string.IsNullOrWhiteSpace(prompt)
            ? "Generate the STEP script for this drawing."
            : Sanitize(prompt.Trim(), int.MaxValue));

        return sb.ToString();
    }

    public static string Build(
        LocalAiOptions options,
        string geometryContext,
        string? currentCode,
        IReadOnlyList<CadAssistMessageDto> history,
        string userText)
    {
        var questionOnly = IsQuestionOnly(userText);

        var total = (int)Math.Max(MinTotalChars, ((long)options.ContextSize - options.MaxTokens - TokenReserve) * CharsPerToken);

        var system = !string.IsNullOrWhiteSpace(options.SystemPromptOverride)
            ? options.SystemPromptOverride!
            : questionOnly ? QuestionSystemPrompt : DefaultSystemPrompt;

        if (!questionOnly)
        {
            var modeRules = ResolveModeRules(geometryContext);

            system = system + " " + StrictDataRules + " " + EngineeringRules
                     + (modeRules.Length > 0 ? " " + modeRules : string.Empty);
        }
        else
        {
            system = system + " " + EngineeringRules;
        }

        var remaining = Math.Max(800, total - system.Length - 300);

        var user = Truncate(userText, (int)(remaining * 0.35));
        var geometryShare = (geometryContext ?? string.Empty).Contains(FewShotMarker, StringComparison.Ordinal) ? 0.6 : 0.35;
        var geometry = Truncate(Sanitize(geometryContext, int.MaxValue), Math.Min(options.MaxGeometryChars, (int)(remaining * geometryShare)));

        var hasCode = !questionOnly && !string.IsNullOrWhiteSpace(currentCode);
        var code = hasCode
            ? Truncate(Sanitize(currentCode!, int.MaxValue), Math.Min(options.MaxCurrentCodeChars, (int)(remaining * 0.30)))
            : string.Empty;

        var systemBlock = new StringBuilder(system);
        systemBlock.AppendLine().AppendLine();
        systemBlock.AppendLine("Drawing data (final, computed by code):");
        systemBlock.AppendLine(geometry);

        if (hasCode)
        {
            systemBlock.AppendLine();
            systemBlock.AppendLine("Script currently in the editor:");
            systemBlock.AppendLine("```csharp");
            systemBlock.AppendLine(code);
            systemBlock.AppendLine("```");
        }

        var historyBudget = Math.Max(0, remaining - geometry.Length - code.Length - user.Length);
        var turns = SelectHistory(history, historyBudget, questionOnly ? QuestionHistoryTurnsCap : int.MaxValue);

        return Render(Normalize(options.ChatTemplate), systemBlock.ToString().TrimEnd(), turns, user);
    }

    private static List<(string Role, string Text)> SelectHistory(IReadOnlyList<CadAssistMessageDto> history, int budget, int maxMessages)
    {
        var picked = new List<(string Role, string Text)>();
        var used = 0;
        var lastAssistantIndex = -1;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == CadAssistRole.Assistant)
            {
                lastAssistantIndex = i;
                break;
            }
        }

        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (picked.Count >= maxMessages)
            {
                break;
            }

            var message = history[i];

            if (message.Role != CadAssistRole.User && message.Role != CadAssistRole.Assistant)
            {
                continue;
            }

            var text = message.ModelContent ?? message.Content;

            if (message.Role == CadAssistRole.Assistant && i != lastAssistantIndex)
            {
                text = FencedBlock.Replace(text, "[script omitted]");
            }

            text = Sanitize(text, int.MaxValue).Trim();

            if (text.Length == 0)
            {
                continue;
            }

            if (used + text.Length > budget)
            {
                break;
            }

            used += text.Length;
            picked.Add((message.Role, text));
        }

        picked.Reverse();

        while (picked.Count > 0 && picked[0].Role != CadAssistRole.User)
        {
            picked.RemoveAt(0);
        }

        return picked;
    }

    private static string Render(string template, string system, List<(string Role, string Text)> turns, string user)
    {
        var sb = new StringBuilder();

        switch (template)
        {
            case "phi3":
                sb.Append("<|system|>\n").Append(system).Append("<|end|>\n");
                foreach (var (role, text) in turns)
                {
                    sb.Append(role == CadAssistRole.User ? "<|user|>\n" : "<|assistant|>\n").Append(text).Append("<|end|>\n");
                }
                sb.Append("<|user|>\n").Append(user).Append("<|end|>\n<|assistant|>\n");
                break;

            case "llama3":
                sb.Append("<|start_header_id|>system<|end_header_id|>\n\n").Append(system).Append("<|eot_id|>");
                foreach (var (role, text) in turns)
                {
                    sb.Append("<|start_header_id|>").Append(role == CadAssistRole.User ? "user" : "assistant")
                      .Append("<|end_header_id|>\n\n").Append(text).Append("<|eot_id|>");
                }
                sb.Append("<|start_header_id|>user<|end_header_id|>\n\n").Append(user).Append("<|eot_id|>");
                sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
                break;

            case "plain":
                sb.Append("### System:\n").Append(system).Append("\n\n");
                foreach (var (role, text) in turns)
                {
                    sb.Append(role == CadAssistRole.User ? "### User:\n" : "### Assistant:\n").Append(text).Append("\n\n");
                }
                sb.Append("### User:\n").Append(user).Append("\n\n### Assistant:\n");
                break;

            default:
                sb.Append("<|im_start|>system\n").Append(system).Append("<|im_end|>\n");
                foreach (var (role, text) in turns)
                {
                    sb.Append("<|im_start|>").Append(role == CadAssistRole.User ? "user" : "assistant")
                      .Append('\n').Append(text).Append("<|im_end|>\n");
                }
                sb.Append("<|im_start|>user\n").Append(user).Append("<|im_end|>\n<|im_start|>assistant\n");
                break;
        }

        return sb.ToString();
    }

    private static string Normalize(string? template)
    {
        return string.IsNullOrWhiteSpace(template) ? "chatml" : template.Trim().ToLowerInvariant();
    }

    private static string Sanitize(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = SpecialToken.Replace(value, string.Empty);
        return cleaned.Length > maxChars ? cleaned[..maxChars] : cleaned;
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0)
        {
            return string.Empty;
        }

        return value.Length <= maxChars ? value : value[..maxChars] + "\n// ...truncated";
    }
}