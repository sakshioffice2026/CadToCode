using System.Globalization;
using System.Text;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Templates;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Services;

public static class CadAssistReplyActions
{
    public const string UseDefaults = "use-defaults";
    public const string GenerateAssembly = "generate-assembly";
    public const string GenerateComponentPrefix = "generate-component:";
}

public static class CadAssistReplyBuilder
{
    public static CadAssistMessageDto BuildNoTemplate()
    {
        var text = new StringBuilder();
        text.AppendLine("I do not have a drawing to work from yet.");
        text.AppendLine();
        text.AppendLine("Upload a DXF or an image/blueprint and I will match it to a template, list its components, and ask you for the dimensions I need. I will not guess dimensions from an image.");
        text.AppendLine();
        text.AppendLine("New to the equipment? Ask me first, for example `What is an axial fan?`, and I will explain what it is, where it is used, its key components and the parameters that define it.");

        return Message(text.ToString(), "needs-upload");
    }

    public static CadAssistMessageDto BuildMatchReply(CadAssistSessionState state)
    {
        var schema = state.Schema;

        if (schema is null)
        {
            return BuildNoTemplate();
        }

        var text = new StringBuilder();
        var score = Math.Clamp(state.MatchScore, 0.0, 1.0);

        text.AppendLine("I analysed **" + (state.SourceFileName ?? "your drawing") + "** and matched it to the **" +
                        schema.Title + "** template (match confidence " +
                        score.ToString("P0", CultureInfo.InvariantCulture) + ").");
        text.AppendLine();

        AppendStructure(text, state, schema);

        if (state.SourceIsImage)
        {
            text.AppendLine();
            text.AppendLine("This is an image, so I have not taken any dimensions from it. Every value below must come from you or from the defaults.");
        }

        text.AppendLine();
        AppendParameterTable(text, state.MissingParameters());
        text.AppendLine();
        AppendParameterInvitation(text, state.MissingParameters());

        var message = Message(text.ToString(), "awaiting-parameters");
        message.Actions.Add(new CadAssistActionDto
        {
            Kind = CadAssistReplyActions.UseDefaults,
            Label = "Use defaults"
        });

        return message;
    }

    public static CadAssistMessageDto BuildParametersAck(
        CadAssistSessionState state,
        IReadOnlyDictionary<string, double> accepted,
        IReadOnlyList<string> rejected)
    {
        var schema = state.Schema;

        if (schema is null)
        {
            return BuildNoTemplate();
        }

        var text = new StringBuilder();

        if (accepted.Count > 0)
        {
            text.AppendLine("Recorded:");
            text.AppendLine();

            foreach (var pair in accepted)
            {
                var def = schema.FindParameter(pair.Key);
                text.AppendLine("- " + (def?.Label ?? pair.Key) + " = " + Format(pair.Value) + " " + (def?.Unit ?? string.Empty));
            }

            text.AppendLine();
        }

        if (rejected.Count > 0)
        {
            text.AppendLine("I could not use these:");
            text.AppendLine();

            foreach (var error in rejected)
            {
                text.AppendLine("- " + error);
            }

            text.AppendLine();
        }

        var missing = state.MissingParameters();

        if (missing.Count > 0)
        {
            text.AppendLine("Still using defaults for " + missing.Count.ToString(CultureInfo.InvariantCulture) + " parameter(s):");
            text.AppendLine();
            AppendParameterTable(text, missing);
            text.AppendLine();
            text.AppendLine("Send more values, or reply **use defaults** to accept the rest and move on.");

            var pending = Message(text.ToString(), "awaiting-parameters");
            pending.Actions.Add(new CadAssistActionDto
            {
                Kind = CadAssistReplyActions.UseDefaults,
                Label = "Use defaults for the rest"
            });

            return pending;
        }

        text.AppendLine("All parameters are set.");
        text.AppendLine();
        AppendScopeChoice(text, state, schema);

        return WithScopeActions(Message(text.ToString(), "awaiting-scope"), state, schema);
    }

    public static CadAssistMessageDto BuildScopePrompt(CadAssistSessionState state)
    {
        var schema = state.Schema;

        if (schema is null)
        {
            return BuildNoTemplate();
        }

        var text = new StringBuilder();

        if (state.DefaultsAccepted)
        {
            text.AppendLine("Defaults accepted for every parameter you did not set.");
            text.AppendLine();
        }

        AppendScopeChoice(text, state, schema);

        return WithScopeActions(Message(text.ToString(), "awaiting-scope"), state, schema);
    }

    public static CadAssistMessageDto BuildGenerated(CadAssistSessionState state, ComposedScriptResult result)
    {
        var schema = state.Schema;
        var text = new StringBuilder();

        text.AppendLine(result.IsFullAssembly
            ? "Generated the **entire " + (schema?.Title ?? "assembly") + "**."
            : "Generated **" + string.Join(", ", result.IncludedScriptNames) + "** from the " + (schema?.Title ?? "template") + ".");
        text.AppendLine();

        AppendAppliedValues(text, state, schema, result);

        text.AppendLine();
        text.AppendLine("The script only changes `const double` values in the template, so nothing was invented. Refine with a message such as `wall 10`, `just the drain`, or `entire assembly`, and I will regenerate without a new upload.");

        var message = Message(text.ToString(), "generated");

        var block = new CadAssistCodeBlockDto
        {
            Title = result.FileBaseName + ".cs",
            Code = result.Script,
            LineCount = result.Script.Split('\n').Length
        };

        message.CodeBlocks.Add(block);
        message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Run, Label = "Run", TargetCodeBlockId = block.Id });
        message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.DownloadStep, Label = "Download STEP", TargetCodeBlockId = block.Id });
        message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Copy, Label = "Copy", TargetCodeBlockId = block.Id });

        return message;
    }

    public static CadAssistMessageDto BuildErrors(IReadOnlyList<string> errors)
    {
        var text = new StringBuilder();
        text.AppendLine("I could not generate that yet:");
        text.AppendLine();

        foreach (var error in errors)
        {
            text.AppendLine("- " + error);
        }

        text.AppendLine();
        text.AppendLine("Adjust the values and I will try again.");

        return Message(text.ToString(), "error");
    }

    public static CadAssistMessageDto BuildUnknownComponent(CadAssistSessionState state, string requested)
    {
        var schema = state.Schema;
        var text = new StringBuilder();

        text.AppendLine("The **" + (schema?.Title ?? "matched") + "** template has no component called \"" + requested + "\".");

        if (schema is not null)
        {
            text.AppendLine();
            text.AppendLine("Available components:");
            text.AppendLine();

            foreach (var component in schema.Components)
            {
                text.AppendLine("- " + component.Title);
            }

            text.AppendLine();
            text.AppendLine("Pick one of these, or ask for the **entire assembly**.");
        }

        var message = Message(text.ToString(), "awaiting-scope");

        if (schema is not null)
        {
            WithScopeActions(message, state, schema);
        }

        return message;
    }

    public static CadAssistMessageDto BuildUnknownParameters(CadAssistSessionState state, IReadOnlyList<string> labels)
    {
        var schema = state.Schema;
        var text = new StringBuilder();

        text.AppendLine("I do not recognise: " + string.Join(", ", labels) + ".");

        if (schema is not null)
        {
            text.AppendLine();
            text.AppendLine("No problem. These are the parameters the " + schema.Title + " template understands:");
            text.AppendLine();
            AppendParameterTable(text, schema.Parameters);
        }

        return Message(text.ToString(), "awaiting-parameters");
    }

    public static CadAssistMessageDto BuildStatus(CadAssistSessionState state)
    {
        var schema = state.Schema;

        if (schema is null)
        {
            return BuildNoTemplate();
        }

        var text = new StringBuilder();
        text.AppendLine("Current setup for the **" + schema.Title + "**:");
        text.AppendLine();
        text.AppendLine("| Parameter | Value | Source |");
        text.AppendLine("| --- | --- | --- |");

        foreach (var def in schema.Parameters)
        {
            var hasValue = state.Values.TryGetValue(def.Key, out var stored);
            var value = hasValue ? stored!.Value : def.Default;
            var source = hasValue ? SourceLabel(stored!.Source) : "Default";

            text.AppendLine("| " + def.Label + " | " + Format(value) + " " + def.Unit + " | " + source + " |");
        }

        text.AppendLine();
        text.AppendLine(state.FullAssembly
            ? "Scope: entire assembly."
            : state.ScopeComponentKeys.Count > 0
                ? "Scope: " + string.Join(", ", state.ScopeComponentKeys) + "."
                : "Scope: not chosen yet.");

        return Message(text.ToString(), "status");
    }

    private static void AppendStructure(StringBuilder text, CadAssistSessionState state, TemplateParameterSchema schema)
    {
        text.AppendLine("**Detected structure**");
        text.AppendLine();

        if (state.DetectedComponents.Count > 0)
        {
            foreach (var detected in state.DetectedComponents)
            {
                var mapped = schema.FindComponent(detected.MappedComponentKey ?? string.Empty);

                text.AppendLine(mapped is null
                    ? "- " + detected.Label + " (no matching template component)"
                    : "- " + detected.Label + " → " + mapped.Title);
            }

            text.AppendLine();
        }

        text.AppendLine("**Template components**");
        text.AppendLine();

        foreach (var component in schema.Components)
        {
            text.AppendLine("- " + component.Title);
        }
    }

    private static void AppendParameterTable(StringBuilder text, IEnumerable<TemplateParameterDefinition> parameters)
    {
        text.AppendLine("| Parameter | Meaning | Default | Allowed range |");
        text.AppendLine("| --- | --- | --- | --- |");

        foreach (var def in parameters)
        {
            text.AppendLine(
                "| `" + def.Key + "` | " + def.Label + " | " + Format(def.Default) + " " + def.Unit +
                " | " + Format(def.Min) + " to " + Format(def.Max) + " " + def.Unit + " |");
        }
    }

    private static void AppendParameterInvitation(StringBuilder text, IReadOnlyList<TemplateParameterDefinition> missing)
    {
        text.AppendLine("Please give me the dimensions you want, for example:");
        text.AppendLine();

        var sample = missing.Take(2).Select(p => p.Key + " " + Format(p.Default)).ToList();

        text.AppendLine("`" + (sample.Count > 0 ? string.Join(", ", sample) : "Radius 600") + "`");
        text.AppendLine();
        text.AppendLine("Or reply **use defaults** to accept every default value shown above. Defaults are only starting values, so confirm them against your customer's datasheet or design requirements.");

        var first = missing.FirstOrDefault();

        if (first is not null)
        {
            text.AppendLine();
            text.AppendLine("Not sure what a dimension means or what a sensible value is? Ask me, for example `what is " + first.Label + "?`, and I will explain it in plain words.");
        }
    }

    private static void AppendScopeChoice(StringBuilder text, CadAssistSessionState state, TemplateParameterSchema schema)
    {
        text.AppendLine("What should I generate?");
        text.AppendLine();
        text.AppendLine("- **Entire assembly**");

        foreach (var component in schema.Components)
        {
            var keys = string.Join(", ", component.ParameterKeys);
            text.AppendLine("- **" + component.Title + "** only (uses " + keys + ")");
        }

        text.AppendLine();
        text.AppendLine("Reply with something like `entire assembly` or `just the " + SampleAlias(schema) + "`.");
    }

    private static void AppendAppliedValues(
        StringBuilder text,
        CadAssistSessionState state,
        TemplateParameterSchema? schema,
        ComposedScriptResult result)
    {
        if (schema is null)
        {
            return;
        }

        var relevant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (result.IsFullAssembly)
        {
            foreach (var def in schema.Parameters)
            {
                relevant.Add(def.Key);
            }
        }
        else
        {
            foreach (var component in schema.Components.Where(c =>
                         c.ScriptNames.Any(n => result.IncludedScriptNames.Contains(n, StringComparer.OrdinalIgnoreCase))))
            {
                foreach (var key in component.ParameterKeys)
                {
                    relevant.Add(key);
                }
            }
        }

        text.AppendLine("| Parameter | Value | Source |");
        text.AppendLine("| --- | --- | --- |");

        foreach (var def in schema.Parameters.Where(p => relevant.Contains(p.Key)))
        {
            var value = result.AppliedValues.TryGetValue(def.Key, out var applied) ? applied : def.Default;
            var source = state.Values.TryGetValue(def.Key, out var stored) ? SourceLabel(stored.Source) : "Default";

            text.AppendLine("| " + def.Label + " | " + Format(value) + " " + def.Unit + " | " + source + " |");
        }
    }

    private static CadAssistMessageDto WithScopeActions(
        CadAssistMessageDto message,
        CadAssistSessionState state,
        TemplateParameterSchema schema)
    {
        message.Actions.Add(new CadAssistActionDto
        {
            Kind = CadAssistReplyActions.GenerateAssembly,
            Label = "Entire assembly"
        });

        foreach (var component in schema.Components)
        {
            message.Actions.Add(new CadAssistActionDto
            {
                Kind = CadAssistReplyActions.GenerateComponentPrefix + component.Key,
                Label = component.Title
            });
        }

        return message;
    }

    private static string SampleAlias(TemplateParameterSchema schema)
    {
        var component = schema.Components.LastOrDefault();
        return component?.Aliases.FirstOrDefault() ?? component?.Key ?? "component";
    }

    private static string SourceLabel(ParameterSource source)
    {
        return source switch
        {
            ParameterSource.User => "You",
            ParameterSource.DxfVerified => "DXF",
            ParameterSource.ImageUnverified => "Image (unconfirmed)",
            _ => "Default"
        };
    }

    private static string Format(double value)
    {
        return TemplateParameterCatalog.Format(value);
    }

    private static CadAssistMessageDto Message(string content, string status)
    {
        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = content.TrimEnd(),
            Status = status
        };
    }
}