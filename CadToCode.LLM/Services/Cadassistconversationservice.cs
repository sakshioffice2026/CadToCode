using System.Text;
using System.Text.RegularExpressions;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Templates;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Services;

public sealed class CadAssistUploadInput
{
    public int ProjectId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public bool IsImage { get; init; }
    public string? Prompt { get; init; }
    public string? DrawingType { get; init; }
    public ProjectResultDto? Project { get; init; }
    public AssemblyTree? Tree { get; init; }
    public IReadOnlyList<CadAssistAttachmentDto> Attachments { get; init; } = Array.Empty<CadAssistAttachmentDto>();
    public IReadOnlyList<string> ComponentLabels { get; init; } = Array.Empty<string>();

    /// <summary>Dimensions read exactly from a DXF. Ignored for images.</summary>
    public IReadOnlyDictionary<string, double>? DxfVerifiedValues { get; init; }
}

public interface ICadAssistConversationService
{
    bool HasActiveTemplate(int projectId, string sessionId);

    Task<CadAssistMessageDto> HandleUploadAsync(CadAssistUploadInput input, CancellationToken cancellationToken = default);

    Task<CadAssistMessageDto> HandleMessageAsync(
        int projectId,
        string sessionId,
        string? prompt,
        CancellationToken cancellationToken = default);
}

public sealed class CadAssistConversationService : ICadAssistConversationService
{
    private const int MaxDetectedComponents = 12;

    private static readonly Regex SwitchRegex = new(
        @"\b(this is|it is|it's|its|switch to|change (?:the )?template to|use (?:the )?(?:\w+ )?template|treat (?:it )?as|actually)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly ICadAssistSessionStore _store;
    private readonly ICadTemplateMatcher _matcher;
    private readonly IChatMemoryStore _memory;
    private readonly ILlmParameterExtractor _extractor;

    public CadAssistConversationService(
        ICadAssistSessionStore store,
        ICadTemplateMatcher matcher,
        IChatMemoryStore memory,
        ILlmParameterExtractor extractor)
    {
        _store = store;
        _matcher = matcher;
        _memory = memory;
        _extractor = extractor;
    }

    public bool HasActiveTemplate(int projectId, string sessionId)
    {
        return _store.Get(projectId, sessionId).HasTemplate;
    }

    public async Task<CadAssistMessageDto> HandleUploadAsync(
        CadAssistUploadInput input,
        CancellationToken cancellationToken = default)
    {
        var match = await _matcher.MatchAsync(
            input.Project,
            input.Tree,
            input.Prompt,
            input.DrawingType,
            input.Attachments,
            cancellationToken);

        var template = ResolveTemplate(match)
                       ?? FindTemplateByText(input.Prompt + " " + input.DrawingType + " " + Path.GetFileNameWithoutExtension(input.FileName));

        var score = match?.Score ?? 0.0;
        var accepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();

        var snapshot = _store.Update(input.ProjectId, input.SessionId, s =>
        {
            s.SourceFileName = input.FileName;
            s.SourceIsImage = input.IsImage;

            if (template is null)
            {
                s.TemplateKey = null;
                s.TemplateTitle = null;
                s.Stage = CadAssistStage.Uploaded;
                return;
            }

            var schema = TemplateParameterCatalog.Find(template.Key);

            if (schema is null)
            {
                s.TemplateKey = null;
                s.Stage = CadAssistStage.Uploaded;
                return;
            }

            s.SetTemplate(template.Key, template.Title, score, BuildDetectedComponents(input, schema));

            if (!input.IsImage)
            {
                ApplyDxfValues(s, schema, input.DxfVerifiedValues);
            }

            if (!string.IsNullOrWhiteSpace(input.Prompt))
            {
                var intent = CadAssistIntentParser.Parse(input.Prompt, schema);
                ApplyUpdates(s, schema, intent, accepted, rejected);
            }
        });

        AppendUser(input.ProjectId, input.SessionId, "Uploaded " + input.FileName +
            (string.IsNullOrWhiteSpace(input.Prompt) ? string.Empty : ": " + input.Prompt.Trim()));

        CadAssistMessageDto reply;

        if (!snapshot.HasTemplate || snapshot.Schema is null)
        {
            reply = BuildNoMatch(input.FileName);
        }
        else if (snapshot.MissingParameters().Count == 0)
        {
            reply = CadAssistReplyBuilder.BuildScopePrompt(snapshot);
            PrefixAck(reply, snapshot.Schema, accepted, rejected);
        }
        else
        {
            reply = CadAssistReplyBuilder.BuildMatchReply(snapshot);
            PrefixAck(reply, snapshot.Schema, accepted, rejected);
        }

        _memory.Append(input.ProjectId, input.SessionId, reply);
        return reply;
    }

    public async Task<CadAssistMessageDto> HandleMessageAsync(
        int projectId,
        string sessionId,
        string? prompt,
        CancellationToken cancellationToken = default)
    {
        var text = prompt?.Trim() ?? string.Empty;

        AppendUser(projectId, sessionId, text);

        if (CadAssistIntentParser.Parse(text, null).Reset)
        {
            _store.Reset(projectId, sessionId);

            var cleared = CadAssistReplyBuilder.BuildNoTemplate();
            cleared.Content = "Session cleared.\n\n" + cleared.Content;
            _memory.Append(projectId, sessionId, cleared);
            return cleared;
        }

        var current = _store.Get(projectId, sessionId);
        CadAssistMessageDto reply;

        if (!current.HasTemplate)
        {
            var template = FindTemplateByText(text);

            if (template is null)
            {
                reply = BuildNoMatch(current.SourceFileName);
            }
            else
            {
                var snapshot = _store.Update(projectId, sessionId, s =>
                    s.SetTemplate(template.Key, template.Title, 1.0, Array.Empty<DetectedComponentInfo>()));

                reply = CadAssistReplyBuilder.BuildMatchReply(snapshot);
            }
        }
        else
        {
            var llm = await TryLlmFallbackAsync(current, text, cancellationToken);

            CadAssistMessageDto? turn = null;

            _store.Update(projectId, sessionId, s => { turn = ProcessTurn(s, text, llm); });

            reply = turn ?? CadAssistReplyBuilder.BuildNoTemplate();
        }

        _memory.Append(projectId, sessionId, reply);
        return reply;
    }

    private async Task<LlmExtractionResult?> TryLlmFallbackAsync(
        CadAssistSessionState current,
        string text,
        CancellationToken cancellationToken)
    {
        var schema = current.Schema;

        if (schema is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var quick = CadAssistIntentParser.Parse(text, schema, AwaitingScope(current.Stage));

        if (quick.HasAnyAction || quick.UnrecognizedAssignments.Count > 0)
        {
            return null;
        }

        if (!quick.HasGenerateCue && !text.Any(char.IsDigit))
        {
            return null;
        }

        var extracted = await _extractor.ExtractAsync(text, schema, cancellationToken);

        return extracted.Success ? extracted : null;
    }

    private static bool AwaitingScope(CadAssistStage stage)
    {
        return stage is CadAssistStage.ParametersConfirmed
            or CadAssistStage.ScopeSelected
            or CadAssistStage.Generated
            or CadAssistStage.Refining;
    }

    private static CadAssistMessageDto ProcessTurn(CadAssistSessionState s, string prompt, LlmExtractionResult? llm)
    {
        var schema = s.Schema;

        if (schema is null)
        {
            return CadAssistReplyBuilder.BuildNoTemplate();
        }

        if (SwitchRequested(prompt))
        {
            var requested = FindTemplateByText(prompt);

            if (requested is not null && !string.Equals(requested.Key, s.TemplateKey, StringComparison.OrdinalIgnoreCase))
            {
                s.SetTemplate(requested.Key, requested.Title, 1.0, Array.Empty<DetectedComponentInfo>());
                return CadAssistReplyBuilder.BuildMatchReply(s);
            }
        }

        var intent = CadAssistIntentParser.Parse(prompt, schema, AwaitingScope(s.Stage));

        var accepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();

        if (llm is not null && !intent.HasAnyAction)
        {
            MergeLlm(intent, llm, rejected);
        }

        ApplyUpdates(s, schema, intent, accepted, rejected);

        foreach (var label in intent.UnrecognizedAssignments)
        {
            rejected.Add("Unknown parameter '" + label + "' for the " + schema.Title + " template.");
        }

        if (intent.UseDefaults)
        {
            s.AcceptDefaults();
        }

        if (intent.Confirm && !intent.HasParameters && !intent.UseDefaults)
        {
            s.ConfirmParameters();
        }

        if (intent.FullAssembly)
        {
            s.SelectScope(true, null);
        }
        else if (intent.ComponentKeys.Count > 0)
        {
            s.SelectScope(false, intent.ComponentKeys);
        }
        else if (intent.UnmatchedComponentText is not null
                 && intent.HasGenerateCue
                 && !intent.HasParameters
                 && !intent.UseDefaults
                 && !intent.Confirm
                 && !intent.Regenerate)
        {
            return CadAssistReplyBuilder.BuildUnknownComponent(s, intent.UnmatchedComponentText);
        }

        if (intent.ShowParameters && !intent.HasParameters && !intent.HasScope)
        {
            return CadAssistReplyBuilder.BuildStatus(s);
        }

        var hasScope = s.FullAssembly || s.ScopeComponentKeys.Count > 0;
        var ready = IsReady(s, hasScope);

        if (!intent.HasAnyAction)
        {
            return BuildIdleReply(s, hasScope, ready, schema, rejected);
        }

        if (!ready)
        {
            return CadAssistReplyBuilder.BuildParametersAck(s, accepted, rejected);
        }

        if (!hasScope)
        {
            var scopePrompt = CadAssistReplyBuilder.BuildScopePrompt(s);
            PrefixAck(scopePrompt, schema, accepted, rejected);
            return scopePrompt;
        }

        var result = TemplateScriptComposer.Compose(s.BuildComposeRequest());

        if (!result.Success)
        {
            return CadAssistReplyBuilder.BuildErrors(result.Errors);
        }

        s.RecordGenerated(result);

        var generated = CadAssistReplyBuilder.BuildGenerated(s, result);
        PrefixAck(generated, schema, new Dictionary<string, double>(), rejected);
        return generated;
    }

    private static void MergeLlm(ParsedIntent intent, LlmExtractionResult llm, List<string> rejected)
    {
        foreach (var pair in llm.Updates)
        {
            if (!intent.Updates.ContainsKey(pair.Key))
            {
                intent.Updates[pair.Key] = pair.Value;
            }
        }

        foreach (var key in llm.ComponentKeys)
        {
            if (!intent.ComponentKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                intent.ComponentKeys.Add(key);
            }
        }

        if (llm.FullAssembly)
        {
            intent.FullAssembly = true;
        }

        rejected.AddRange(llm.Rejected);
    }

    private static CadAssistMessageDto BuildIdleReply(
        CadAssistSessionState s,
        bool hasScope,
        bool ready,
        TemplateParameterSchema schema,
        IReadOnlyList<string> rejected)
    {
        if (!ready)
        {
            return CadAssistReplyBuilder.BuildParametersAck(s, new Dictionary<string, double>(), rejected);
        }

        if (!hasScope)
        {
            var scopePrompt = CadAssistReplyBuilder.BuildScopePrompt(s);
            PrefixAck(scopePrompt, schema, new Dictionary<string, double>(), rejected);
            return scopePrompt;
        }

        var text = new StringBuilder();
        text.AppendLine("Nothing changed. Tell me a value to change (for example `wall 10`), a part to generate (for example `just the " +
                        (schema.Components.FirstOrDefault()?.Aliases.FirstOrDefault() ?? "shell") +
                        "`), or say `entire assembly`.");

        var message = new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "idle"
        };

        PrefixAck(message, schema, new Dictionary<string, double>(), rejected);
        return message;
    }

    private static bool IsReady(CadAssistSessionState s, bool hasScope)
    {
        if (s.DefaultsAccepted)
        {
            return true;
        }

        if (!hasScope || s.FullAssembly)
        {
            return s.MissingParameters().Count == 0;
        }

        return s.MissingParametersFor(s.ScopeComponentKeys).Count == 0;
    }

    private static void ApplyUpdates(
        CadAssistSessionState s,
        TemplateParameterSchema schema,
        ParsedIntent intent,
        Dictionary<string, double> accepted,
        List<string> rejected)
    {
        if (!intent.HasParameters)
        {
            return;
        }

        var candidateAccepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in intent.Updates)
        {
            var error = TemplateParameterCatalog.ValidateValue(schema, pair.Key, pair.Value);

            if (error is not null)
            {
                rejected.Add(error);
                continue;
            }

            candidateAccepted[pair.Key] = pair.Value;
        }

        if (candidateAccepted.Count == 0)
        {
            return;
        }

        var merged = schema.BuildDefaults();

        foreach (var stored in s.Values)
        {
            merged[stored.Key] = stored.Value.Value;
        }

        foreach (var pair in candidateAccepted)
        {
            merged[pair.Key] = pair.Value;
        }

        var crossErrors = TemplateParameterCatalog.ValidateSet(schema, merged);

        if (crossErrors.Count > 0)
        {
            rejected.AddRange(crossErrors);
            return;
        }

        foreach (var pair in candidateAccepted)
        {
            s.SetValue(pair.Key, pair.Value, ParameterSource.User);
            accepted[pair.Key] = pair.Value;
        }
    }

    private static void ApplyDxfValues(
        CadAssistSessionState s,
        TemplateParameterSchema schema,
        IReadOnlyDictionary<string, double>? values)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var candidate = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in values)
        {
            var def = schema.FindParameter(pair.Key);

            if (def is null || TemplateParameterCatalog.ValidateValue(schema, def.Key, pair.Value) is not null)
            {
                continue;
            }

            candidate[def.Key] = pair.Value;
        }

        if (candidate.Count == 0)
        {
            return;
        }

        var merged = schema.BuildDefaults();

        foreach (var pair in candidate)
        {
            merged[pair.Key] = pair.Value;
        }

        if (TemplateParameterCatalog.ValidateSet(schema, merged).Count > 0)
        {
            return;
        }

        foreach (var pair in candidate)
        {
            s.Values[pair.Key] = new ParameterValue(pair.Value, ParameterSource.DxfVerified);
        }
    }

    private static List<DetectedComponentInfo> BuildDetectedComponents(CadAssistUploadInput input, TemplateParameterSchema schema)
    {
        var labels = new List<string>();

        if (input.Tree is not null)
        {
            labels.AddRange(input.Tree.Assemblies.Select(a => a.AssemblyName));
        }

        if (input.Project is not null)
        {
            labels.AddRange(input.Project.Components.Select(c => c.Name));
        }

        labels.AddRange(input.ComponentLabels);

        return labels
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxDetectedComponents)
            .Select(l => new DetectedComponentInfo
            {
                Label = l,
                MappedComponentKey = TemplateScriptComposer.ResolveComponent(schema, l)?.Key
            })
            .ToList();
    }

    private static CadTemplateDefinition? ResolveTemplate(CadTemplateMatch? match)
    {
        if (match is null)
        {
            return null;
        }

        var byTitle = BuiltInCadTemplates.All.FirstOrDefault(t =>
            string.Equals(t.Title, match.Title, StringComparison.OrdinalIgnoreCase));

        if (byTitle is not null)
        {
            return byTitle;
        }

        var byCategory = BuiltInCadTemplates.All
            .Where(t => string.Equals(t.Category, match.Category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byCategory.Count == 1 ? byCategory[0] : FindTemplateByText(match.Title + " " + match.Category);
    }

    private static CadTemplateDefinition? FindTemplateByText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var padded = " " + Normalize(text) + " ";
        var scored = new List<(CadTemplateDefinition Template, int Score)>();

        foreach (var template in BuiltInCadTemplates.All)
        {
            var score = 0;
            var category = Normalize(template.Category);
            var title = Normalize(template.Title);

            if (title.Length > 0 && padded.Contains(" " + title + " ", StringComparison.Ordinal))
            {
                score += 3;
            }

            if (category.Length > 0 && padded.Contains(" " + category + " ", StringComparison.Ordinal))
            {
                score += 3;
            }

            foreach (var tag in template.Tags)
            {
                var term = Normalize(tag);

                if (term.Length == 0 || term == category)
                {
                    continue;
                }

                if (padded.Contains(" " + term + " ", StringComparison.Ordinal))
                {
                    score += 1;
                }
            }

            if (score >= 3)
            {
                scored.Add((template, score));
            }
        }

        if (scored.Count == 0)
        {
            return null;
        }

        var ordered = scored.OrderByDescending(x => x.Score).ToList();

        if (ordered.Count > 1 && ordered[0].Score == ordered[1].Score)
        {
            return null;
        }

        return ordered[0].Template;
    }

    private static bool SwitchRequested(string text)
    {
        try
        {
            return SwitchRegex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : NonAlphaNumeric.Replace(value.ToLowerInvariant(), " ").Trim();
    }

    private static void PrefixAck(
        CadAssistMessageDto message,
        TemplateParameterSchema schema,
        IReadOnlyDictionary<string, double> accepted,
        IReadOnlyList<string> rejected)
    {
        if (accepted.Count == 0 && rejected.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();

        if (accepted.Count > 0)
        {
            text.AppendLine("Recorded:");
            text.AppendLine();

            foreach (var pair in accepted)
            {
                var def = schema.FindParameter(pair.Key);

                text.AppendLine("- " + (def?.Label ?? pair.Key) + " = " +
                                TemplateParameterCatalog.Format(pair.Value) + " " + (def?.Unit ?? string.Empty));
            }

            text.AppendLine();
        }

        if (rejected.Count > 0)
        {
            text.AppendLine("I could not use:");
            text.AppendLine();

            foreach (var error in rejected)
            {
                text.AppendLine("- " + error);
            }

            text.AppendLine();
        }

        text.Append(message.Content);
        message.Content = text.ToString();
    }

    private static CadAssistMessageDto BuildNoMatch(string? fileName)
    {
        var text = new StringBuilder();

        text.AppendLine("I could not match " + (string.IsNullOrWhiteSpace(fileName) ? "this drawing" : "**" + fileName + "**") +
                        " to a template with enough confidence, so I will not guess.");
        text.AppendLine();
        text.AppendLine("Templates I can work from:");
        text.AppendLine();

        foreach (var template in BuiltInCadTemplates.All)
        {
            text.AppendLine("- " + template.Title);
        }

        text.AppendLine();
        text.AppendLine("Tell me which one this is (for example `this is a cyclone separator`) and I will list the dimensions I need.");

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "no-match"
        };
    }

    private void AppendUser(int projectId, string sessionId, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _memory.Append(projectId, sessionId, new CadAssistMessageDto
        {
            Role = CadAssistRole.User,
            Content = content
        });
    }
}