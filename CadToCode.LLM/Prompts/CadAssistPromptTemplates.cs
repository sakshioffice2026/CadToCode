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

    private static readonly Regex SpecialToken = new(@"<\|[^|>\r\n]{1,40}\|>", RegexOptions.Compiled);
    private static readonly Regex FencedBlock = new(@"```[^\r\n]*\r?\n.*?(?:```|\z)", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ActionIntent = new(
        @"\b(generate|create|make|write|build|modify|change|update|edit|add|remove|delete|fix|replace|regenerate|rewrite|increase|decrease|resize|script|code)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuestionIntent = new(
        @"^\s*(explain|what|why|how|which|describe|list|tell|summari[sz]e|show|is|are|does|do|can|where|who)\b|\?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string QuestionSystemPrompt =
        "You are CadAssist, an offline engineering assistant inside CadToCode. "
        + "Answer the question using only the drawing data below, including its 2D profiles, detected features and named parameters. "
        + "All measurements are computed by code, are final and are in millimeters. "
        + "Never invent a dimension or a part that is not in the data. "
        + "Answer in plain text, in at most five short sentences, and do not write any code.";

    public const string DefaultSystemPrompt =
        "You are CadAssist, an offline engineering assistant inside CadToCode. "
        + "You turn CAD drawing data into parametric C# STEP scripts and refine those scripts when the user asks for changes. "
        + "All measurements in the drawing data are computed by code and are final. Never invent or change a dimension unless the user explicitly asks for that change. "
        + "All dimensions are in millimeters. "
        + "Answer in one or two short sentences, then give the complete script in a single fenced block that starts with ```csharp and ends with ```. "
        + "When the user only asks a question, answer in plain text without code. "
        + "Script rules: start with these using lines: System, System.Collections.Generic, System.IO, System.Linq, CadToCode.Step. "
        + "Then declare every size and every assumed depth as a named const double (for example const double Thickness = 10;), "
        + "reusing the parameter names from the drawing data when they are given; then var doc = new StepDocument(\"Name\"); then add solids; "
        + "finish with var outputPath = Path.GetFullPath(\"Name.step\"); doc.Save(outputPath); Console.WriteLine(\"STEP saved to \" + outputPath + \" (\" + doc.SolidCount + \" solids)\"); "
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
                sb.AppendLine("Attached file: " + name + " (binary or image, content is not readable by this offline model)");
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

        var remaining = Math.Max(800, total - system.Length - 300);

        var user = Truncate(userText, (int)(remaining * 0.35));
        var geometry = Truncate(Sanitize(geometryContext, int.MaxValue), Math.Min(options.MaxGeometryChars, (int)(remaining * 0.35)));

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