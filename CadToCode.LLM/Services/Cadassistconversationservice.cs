using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Templates;
using CadToCode.LLM.Vision;
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

    /// <summary>Breakdown read by the vision model from an image. Values stay unverified.</summary>
    public VisionInspectionResult? Vision { get; init; }

    /// <summary>OCR text of the image, used only to corroborate vision values.</summary>
    public string? OcrText { get; init; }
}

public interface ICadAssistConversationService
{
    bool HasActiveTemplate(int projectId, string sessionId);

    /// <summary>True when a manifest-driven equipment design is in progress for this session.</summary>
    bool IsDesignActive(int projectId, string sessionId);

    /// <summary>
    /// True when this prompt belongs to a manifest-driven equipment flow: either a design is already in
    /// progress, or the prompt names equipment that a registered flow designs.
    /// </summary>
    bool CanRouteToDesign(int projectId, string sessionId, string? prompt);

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
    private const int MaxUnmappedNotes = 8;

    private static readonly Regex SwitchRegex = new(
        @"\b(this is|it is|it's|its|switch to|change (?:the )?template to|use (?:the )?(?:\w+ )?template|treat (?:it )?as|actually)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ExplicitGenerateRegex = new(
        @"\b(write|generate|create|build|make|give|produce|export|get|need|want)\b.*(?<![A-Za-z])(c#|csharp|c sharp|script|code|step|stp)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex PlainValueRegex = new(
        @"(?<![A-Za-z])(?<noun>[a-z][a-z \-]{1,30}?)\s+(?:of|with|at|size|sized)\s+(?<num>-?\d+(?:[.,]\d+)?)\s*(?<unit>mm|cm|m|in)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly ICadAssistSessionStore _store;
    private readonly ICadTemplateMatcher _matcher;
    private readonly IChatMemoryStore _memory;
    private readonly ILlmParameterExtractor _extractor;
    private readonly IEquipmentManifestRegistry? _manifests;

    private const double MinImageEquipmentConfidence = 0.85;

    private static readonly HashSet<string> EquipmentGateCodes = new(StringComparer.Ordinal)
    {
        "GATE_AMBIGUOUS_EQUIPMENT",
        "GATE_IDENTIFY_EQUIPMENT",
        "GATE_CONFIRM_EQUIPMENT"
    };

    private static readonly Regex GenerateVerbRegex = new(
        @"\b(generate|regenerate|export|produce)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex AssemblyRequestRegex = new(
        @"\bassembl(?:y|ed|ies)\b|\b(?:single|one|combined|merged|unified)\s+(?:step\s+)?(?:file|model)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    // Each equipment type keeps its own conversation state. The store outlives a single request.
    // The fallback is used only when no store is registered in dependency injection.
    private static readonly EquipmentConversationStore SharedFallbackStore = new();

    private static readonly Regex UseDefaultsRegex = new(
        @"\b(use|apply|accept|take|go with|go for|load|fill)\b.{0,24}\bdefaults?\b|\bdefault\s+(values|dimensions|settings)\b|^\s*defaults?\s*[.!]?\s*$|\bstandard\s+(values|defaults)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ConfirmRegex = new(
        @"^\s*(yes|yep|yeah|y|correct|right|confirmed?|that'?s right|that is right|it is|it's|ok|okay|sure)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex GenericFanWordRegex = new(
        @"\b(fan|fans|blower|blowers|ventilator)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex SpecificFanWordRegex = new(
        @"\b(axial|propeller|tube\s*axial|tubeaxial|vane\s*axial|vaneaxial|centrifugal|radial|scroll|squirrel|sirocco|plug|forward|backward|airfoil|aerofoil|paddle)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex BreakdownRegex = new(
        @"\b(breakdown|break down|components?|parts?\s+list|list\s+(?:the\s+)?parts|what\s+parts|view\s+(?:the\s+)?(?:parts|components?)|show\s+(?:me\s+)?(?:the\s+)?(?:parts|components?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex PartScriptsRegex = new(
        @"\b(individual|separate|each|per)\s+(part|parts|component|components)\b|\bpart\s+scripts?\b|\bscripts?\s+(?:for|of)\s+(?:each|every|all)\s+parts?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ChangeValuesRegex = new(
        @"\b(change|modify|edit|update|adjust|revise|redo|resize)\b.{0,24}\b(value|values|input|inputs|dimension|dimensions|duty|parameter|parameters)\b|^\s*(change|modify)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex WordSplitRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private const string GenerateHintMarker = "Everything needed is set.";

    private const string NextActionQuestion = "What would you like to do next?";

    private readonly IReadOnlyList<IEquipmentDesignFlow> _designFlows;
    private readonly IEquipmentConversationStore _equipment;

    public CadAssistConversationService(
        ICadAssistSessionStore store,
        ICadTemplateMatcher matcher,
        IChatMemoryStore memory,
        ILlmParameterExtractor extractor,
        IEquipmentManifestRegistry? manifests = null,
        IEnumerable<IEquipmentDesignFlow>? designFlows = null,
        IEquipmentConversationStore? equipmentStore = null)
    {
        _store = store;
        _matcher = matcher;
        _memory = memory;
        _extractor = extractor;
        _manifests = manifests;
        _designFlows = designFlows?.ToList() ?? new List<IEquipmentDesignFlow>();
        _equipment = equipmentStore ?? SharedFallbackStore;
    }

    public bool HasActiveTemplate(int projectId, string sessionId)
    {
        return _store.Get(projectId, sessionId).HasTemplate;
    }

    public bool IsDesignActive(int projectId, string sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
               && (_equipment.IsActive(projectId, sessionId)
                   || _equipment.IsAwaitingEquipmentType(projectId, sessionId));
    }

    public bool CanRouteToDesign(int projectId, string sessionId, string? prompt)
    {
        if (_designFlows.Count == 0 || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (IsDesignActive(projectId, sessionId))
        {
            return true;
        }

        var route = ResolveDesignRoute(prompt.Trim());

        if (route.Manifest is not null)
        {
            return true;
        }

        return route.Clarify.Count > 0 && !_store.Get(projectId, sessionId).HasTemplate;
    }

    public async Task<CadAssistMessageDto> HandleUploadAsync(
        CadAssistUploadInput input,
        CancellationToken cancellationToken = default)
    {
        // An image never goes straight to template matching or sizing. The equipment type is confirmed first.
        var identification = BeginImageIdentification(input);

        if (identification is not null)
        {
            _store.Update(input.ProjectId, input.SessionId, s =>
            {
                s.SourceFileName = input.FileName;
                s.SourceIsImage = input.IsImage;

                if (input.Vision is not null)
                {
                    s.SetVisionData(
                        input.Vision.Views.Select(v => new DetectedViewInfo { Name = v.Name, Description = v.Description }),
                        input.Vision.Uncertainties.Concat(input.Vision.Warnings));
                }

                s.TemplateKey = null;
                s.TemplateTitle = null;
                s.Stage = CadAssistStage.Uploaded;
            });

            AppendUser(input.ProjectId, input.SessionId, "Uploaded " + input.FileName +
                (string.IsNullOrWhiteSpace(input.Prompt) ? string.Empty : ": " + input.Prompt.Trim()));

            _memory.Append(input.ProjectId, input.SessionId, identification);
            return identification;
        }

        var match = await _matcher.MatchAsync(
            input.Project,
            input.Tree,
            input.Prompt,
            input.DrawingType,
            input.Attachments,
            cancellationToken);

        var gatePause = EvaluateEquipmentGate(input, match?.Score);

        if (gatePause is not null)
        {
            _store.Update(input.ProjectId, input.SessionId, s =>
            {
                s.SourceFileName = input.FileName;
                s.SourceIsImage = input.IsImage;

                if (input.IsImage && input.Vision is not null)
                {
                    s.SetVisionData(
                        input.Vision.Views.Select(v => new DetectedViewInfo { Name = v.Name, Description = v.Description }),
                        input.Vision.Uncertainties.Concat(input.Vision.Warnings));
                }

                s.TemplateKey = null;
                s.TemplateTitle = null;
                s.Stage = CadAssistStage.Uploaded;
            });

            AppendUser(input.ProjectId, input.SessionId, "Uploaded " + input.FileName +
                (string.IsNullOrWhiteSpace(input.Prompt) ? string.Empty : ": " + input.Prompt.Trim()));

            _memory.Append(input.ProjectId, input.SessionId, gatePause);
            return gatePause;
        }

        var requestedEquipment = ResolveRequestedEquipment(input);
        var unrecognisedEquipment = GetUnrecognisedVisionEquipment(input, requestedEquipment);

        var template = GuardTemplate(ResolveTemplate(match), requestedEquipment, unrecognisedEquipment)
                       ?? GuardTemplate(
                           FindTemplateByText(input.Prompt + " " + input.DrawingType + " " + Path.GetFileNameWithoutExtension(input.FileName)),
                           requestedEquipment,
                           unrecognisedEquipment);

        var score = match?.Score ?? 0.0;
        var accepted = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();

        var snapshot = _store.Update(input.ProjectId, input.SessionId, s =>
        {
            s.SourceFileName = input.FileName;
            s.SourceIsImage = input.IsImage;

            if (input.IsImage && input.Vision is not null)
            {
                s.SetVisionData(
                    input.Vision.Views.Select(v => new DetectedViewInfo { Name = v.Name, Description = v.Description }),
                    input.Vision.Uncertainties.Concat(input.Vision.Warnings));
            }

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
            else if (input.Vision is not null)
            {
                ApplyVisionValues(s, schema, input.Vision, input.OcrText, score);
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
            reply = BuildNoMatch(input.FileName, requestedEquipment, unrecognisedEquipment);
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
            _equipment.Reset(projectId, sessionId);

            var cleared = CadAssistReplyBuilder.BuildNoTemplate();
            cleared.Content = "Session cleared.\n\n" + cleared.Content;
            _memory.Append(projectId, sessionId, cleared);
            return cleared;
        }

        var current = _store.Get(projectId, sessionId);

        if (TryRouteEquipmentDesign(projectId, sessionId, text, current, out var designReply))
        {
            _memory.Append(projectId, sessionId, designReply);
            return designReply;
        }

        CadAssistMessageDto reply;

        if (!current.HasTemplate)
        {
            var messageEquipment = EquipmentRegistry.Resolve(text);
            var template = GuardTemplate(FindTemplateByText(text), messageEquipment);

            if (template is null)
            {
                reply = BuildNoMatch(current.SourceFileName, messageEquipment);
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

        if (quick.HasAnyAction || quick.UnrecognizedAssignments.Count > 0 || IsExplicitGenerateRequest(text))
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

        var explicitGenerate = IsExplicitGenerateRequest(prompt)
                               || (intent.HasGenerateCue && (intent.FullAssembly || intent.ComponentKeys.Count > 0));

        if (explicitGenerate && !intent.HasParameters)
        {
            InferPlainValue(prompt, schema, intent);
        }

        ApplyUpdates(s, schema, intent, accepted, rejected);

        foreach (var label in intent.UnrecognizedAssignments)
        {
            rejected.Add("Unknown parameter '" + label + "' for the " + schema.Title + " template.");
        }

        if (explicitGenerate)
        {
            if (rejected.Count > 0)
            {
                return CadAssistReplyBuilder.BuildParametersAck(s, accepted, rejected);
            }

            s.AcceptDefaults();
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

        if (explicitGenerate && !s.FullAssembly && s.ScopeComponentKeys.Count == 0)
        {
            s.SelectScope(true, null);
        }

        if (intent.ShowParameters && !intent.HasParameters && !intent.HasScope && !explicitGenerate)
        {
            return CadAssistReplyBuilder.BuildStatus(s);
        }

        var hasScope = s.FullAssembly || s.ScopeComponentKeys.Count > 0;
        var ready = IsReady(s, hasScope);

        if (!intent.HasAnyAction && !explicitGenerate)
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
        PrefixAck(generated, schema, accepted, rejected);
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

    private static void ApplyVisionValues(
        CadAssistSessionState s,
        TemplateParameterSchema schema,
        VisionInspectionResult vision,
        string? ocrText,
        double matchScore)
    {
        var mapping = VisionParameterMapper.Map(schema, vision, ocrText, matchScore);

        foreach (var warning in mapping.Warnings)
        {
            if (!s.VisionUncertainties.Contains(warning, StringComparer.OrdinalIgnoreCase))
            {
                s.VisionUncertainties.Add(warning);
            }
        }

        foreach (var item in mapping.Mapped)
        {
            s.SetValue(item.Key, item.Value, ParameterSource.ImageUnverified);
            s.SetEvidence(item.Key, new ParameterEvidence(item.VisionLabel, item.Evidence, item.Corroborated));
        }

        foreach (var item in mapping.Unmapped.Take(MaxUnmappedNotes))
        {
            var d = item.Dimension;

            s.VisionUncertainties.Add(
                "Not used: " + d.Label + " " + d.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " " + d.Unit + " (" + item.Reason + ")");
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

        if (input.Vision is not null)
        {
            labels.AddRange(input.Vision.Components.Select(c => c.Name));
        }

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

        if (ordered.Count > 1
            && ordered[0].Score == ordered[1].Score
            && !string.Equals(ordered[0].Template.Category, ordered[1].Template.Category, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ordered[0].Template;
    }

    private static bool IsExplicitGenerateRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return ExplicitGenerateRegex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static void InferPlainValue(string text, TemplateParameterSchema schema, ParsedIntent intent)
    {
        Match match;

        try
        {
            match = PlainValueRegex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return;
        }

        if (!match.Success)
        {
            return;
        }

        var noun = match.Groups["noun"].Value.Trim().ToLowerInvariant();

        var names = new List<string> { schema.Title.ToLowerInvariant() };

        foreach (var component in schema.Components)
        {
            names.Add(component.Key.ToLowerInvariant());
            names.Add(component.Title.ToLowerInvariant());
            names.AddRange(component.Aliases.Select(a => a.ToLowerInvariant()));
        }

        var nounMatches = names.Any(n => n.Length >= 3 && (noun == n || noun.EndsWith(" " + n, StringComparison.Ordinal)));

        if (!nounMatches)
        {
            return;
        }

        var candidates = schema.Parameters
            .Where(p => string.Equals(p.Unit, "mm", StringComparison.OrdinalIgnoreCase)
                        && !p.IsInteger
                        && p.Aliases.Any(a => a.Equals("diameter", StringComparison.OrdinalIgnoreCase)
                                              || a.Equals("dia", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count != 1)
        {
            return;
        }

        if (!double.TryParse(
                match.Groups["num"].Value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return;
        }

        value = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "cm" => value * 10.0,
            "m" => value * 1000.0,
            "in" => value * 25.4,
            _ => value
        };

        intent.Updates[candidates[0].Key] = value;
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

    private CadAssistMessageDto? EvaluateEquipmentGate(CadAssistUploadInput input, double? matchScore)
    {
        if (_manifests is null || !input.IsImage)
        {
            return null;
        }

        var userText = string.Join(
            " ",
            new[] { input.Prompt, input.DrawingType, Path.GetFileNameWithoutExtension(input.FileName) }
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        var allText = string.Join(
            " ",
            new[] { input.Vision?.EquipmentType, input.Vision?.Summary, userText }
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        var resolution = _manifests.ResolveDetailed(allText);

        if (!resolution.IsResolved && !resolution.IsAmbiguous)
        {
            return null;
        }

        var namedByUser = _manifests.Resolve(userText) is not null;

        double? confidence = null;

        if (!namedByUser && matchScore.HasValue && matchScore.Value > 0)
        {
            confidence = matchScore.Value > 1.0 ? matchScore.Value / 100.0 : matchScore.Value;
        }

        var decision = DesignGate.Evaluate(new GateContext
        {
            Manifest = resolution.Manifest,
            Resolution = resolution,
            AvailableManifests = _manifests.All,
            MatchConfidence = confidence,
            MinMatchConfidence = MinImageEquipmentConfidence,
            CalculatorRegistered = true
        });

        var question = decision.Questions.FirstOrDefault(q => EquipmentGateCodes.Contains(q.Code));

        if (question is null)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(question.Prompt);

        if (question.Choices.Count > 0)
        {
            text.AppendLine();

            foreach (var choice in question.Choices)
            {
                text.AppendLine("- " + choice);
            }
        }

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "equipment-confirm"
        };
    }

    private static EquipmentType ResolveRequestedEquipment(CadAssistUploadInput input)
    {
        var fromVision = EquipmentRegistry.Resolve(input.Vision?.EquipmentType);

        if (fromVision != EquipmentType.Unknown)
        {
            return fromVision;
        }

        return EquipmentRegistry.Resolve(
            input.Prompt + " " + input.DrawingType + " " + Path.GetFileNameWithoutExtension(input.FileName));
    }

    private static readonly string[] PipingVocabulary =
    {
        "pipe", "piping", "tube", "tee", "elbow", "flange", "duct", "reducer", "manifold", "bend"
    };

    private static string? GetUnrecognisedVisionEquipment(CadAssistUploadInput input, EquipmentType requested)
    {
        if (requested != EquipmentType.Unknown)
        {
            return null;
        }

        var raw = input.Vision?.EquipmentType;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = new string(raw.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '/').ToArray()).Trim();

        if (name.Length == 0)
        {
            return null;
        }

        var words = name.ToLowerInvariant()
            .Split(new[] { ' ', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w);

        if (words.Any(w => PipingVocabulary.Contains(w, StringComparer.Ordinal)))
        {
            return null;
        }

        return name.Length > 60 ? name[..60] : name;
    }

    private static CadTemplateDefinition? GuardTemplate(
        CadTemplateDefinition? template,
        EquipmentType requested,
        string? unrecognisedEquipment = null)
    {
        if (template is not null
            && unrecognisedEquipment is not null
            && ClassifyTemplate(template.Category, template.Title) == EquipmentType.PipingLayout)
        {
            return null;
        }

        if (template is null || requested == EquipmentType.Unknown)
        {
            return template;
        }

        var templateType = ClassifyTemplate(template.Category, template.Title);

        return templateType == EquipmentType.Unknown || templateType == requested ? template : null;
    }

    private static EquipmentType ClassifyTemplate(string? category, string? title)
    {
        var text = Normalize((category ?? string.Empty) + " " + (title ?? string.Empty));

        if (text.Contains("scrubber", StringComparison.Ordinal))
        {
            return EquipmentType.WetScrubber;
        }

        if (text.Contains("cyclone", StringComparison.Ordinal))
        {
            return EquipmentType.Cyclone;
        }

        if (text.Contains("centrifugal", StringComparison.Ordinal)
            || text.Contains("blower", StringComparison.Ordinal)
            || text.Contains("scroll", StringComparison.Ordinal)
            || text.Contains("squirrel", StringComparison.Ordinal)
            || text.Contains("radial", StringComparison.Ordinal))
        {
            return EquipmentType.CentrifugalFan;
        }

        if (text.Contains("fan", StringComparison.Ordinal))
        {
            return EquipmentType.AxialFan;
        }

        if (text.Contains("pressure vessel", StringComparison.Ordinal))
        {
            return EquipmentType.PressureVessel;
        }

        if (text.Contains("pipe", StringComparison.Ordinal)
            || text.Contains("elbow", StringComparison.Ordinal)
            || text.Contains("flange", StringComparison.Ordinal))
        {
            return EquipmentType.PipingLayout;
        }

        return EquipmentType.Unknown;
    }

    private CadAssistMessageDto BuildNoMatch(
        string? fileName,
        EquipmentType equipment = EquipmentType.Unknown,
        string? unrecognisedEquipment = null)
    {
        var text = new StringBuilder();

        if (equipment == EquipmentType.Unknown && !string.IsNullOrWhiteSpace(unrecognisedEquipment))
        {
            text.AppendLine("This looks like a **" + unrecognisedEquipment + "**. There is no matching CAD template for it, "
                            + "and I will not substitute a pipe or any other shape.");
            text.AppendLine();
            text.AppendLine("Tell me the equipment dimensions or pick one of the templates below and I will continue.");
            text.AppendLine();

            foreach (var template in BuiltInCadTemplates.All)
            {
                text.AppendLine("- " + template.Title);
            }

            AppendDesignableEquipment(text);

            return new CadAssistMessageDto
            {
                Role = CadAssistRole.Assistant,
                Content = text.ToString().TrimEnd(),
                Status = "equipment-no-template"
            };
        }

        if (equipment != EquipmentType.Unknown && TryFindDesignManifest(equipment, out var designManifest))
        {
            text.AppendLine("This is a **" + designManifest.DisplayName + "**. I design it from calculator inputs "
                            + "instead of a dimension template, and I will not substitute another shape.");
            text.AppendLine();

            if (EquipmentRegistry.TryGet(equipment, out var designMetadata) && designMetadata.RequiredInputs.Any())
            {
                text.AppendLine("To size it I need:");
                text.AppendLine();

                foreach (var input in designMetadata.RequiredInputs)
                {
                    text.AppendLine("- " + input.DisplayName + " (" + input.SiUnit + ")");
                }
            }
            else
            {
                text.AppendLine("Tell me the duty (for example airflow, pressure and speed) and I will continue.");
            }

            return new CadAssistMessageDto
            {
                Role = CadAssistRole.Assistant,
                Content = text.ToString().TrimEnd(),
                Status = "equipment-design-ready"
            };
        }

        if (equipment != EquipmentType.Unknown && EquipmentRegistry.TryGet(equipment, out var metadata))
        {
            text.AppendLine("This looks like a **" + metadata.DisplayName + "**. There is no matching CAD template for it, "
                            + "and I will not substitute a pipe or any other shape.");
            text.AppendLine();

            if (metadata.CalculatorStatus == CalculatorStatus.NotImplemented)
            {
                text.AppendLine("The deterministic calculator for this equipment is not implemented yet.");
            }
            else
            {
                text.AppendLine("To size it I need:");
                text.AppendLine();

                foreach (var input in metadata.RequiredInputs)
                {
                    text.AppendLine("- " + input.DisplayName + " (" + input.SiUnit + ")");
                }
            }

            return new CadAssistMessageDto
            {
                Role = CadAssistRole.Assistant,
                Content = text.ToString().TrimEnd(),
                Status = "equipment-no-template"
            };
        }

        text.AppendLine("I could not match " + (string.IsNullOrWhiteSpace(fileName) ? "this drawing" : "**" + fileName + "**") +
                        " to a template with enough confidence, so I will not guess.");
        text.AppendLine();
        text.AppendLine("Templates I can work from:");
        text.AppendLine();

        foreach (var template in BuiltInCadTemplates.All)
        {
            text.AppendLine("- " + template.Title);
        }

        AppendDesignableEquipment(text);

        text.AppendLine();
        text.AppendLine("Tell me which one this is (for example `this is a cyclone separator`) and I will list the dimensions I need.");

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "no-match"
        };
    }

    private bool TryRouteEquipmentDesign(
        int projectId,
        string sessionId,
        string text,
        CadAssistSessionState current,
        out CadAssistMessageDto reply)
    {
        reply = null!;

        if (_designFlows.Count == 0 || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var session = _equipment.Get(projectId, sessionId);

        // Step 1: an image is waiting for the user to name the equipment type.
        if (session.Stage == EquipmentSessionStage.AwaitingEquipmentType)
        {
            var answered = AnswerEquipmentQuestion(projectId, sessionId, text, session, out reply);

            if (answered)
            {
                return true;
            }

            if (reply is not null)
            {
                return true;
            }

            // The answer names equipment this assistant does not design from calculator inputs.
            // Leave the question and let the template pipeline handle it.
            _equipment.Update(projectId, sessionId, s =>
            {
                s.Stage = EquipmentSessionStage.None;
                s.PendingSourceFileName = null;
                s.ProposedManifestId = null;
                s.OfferedManifestIds = new List<string>();
            });

            return false;
        }

        var activeManifestId = session.Stage == EquipmentSessionStage.Active ? session.ActiveManifestId : null;

        var route = ResolveDesignRoute(text);
        var manifest = route.Manifest;
        var flow = route.Flow;

        // While one equipment is being designed, a loose word such as "fan" must not switch it.
        // Only a clear equipment name that differs from the active equipment switches the conversation.
        if (activeManifestId is not null && manifest is not null
            && !string.Equals(manifest.Id, activeManifestId, StringComparison.OrdinalIgnoreCase)
            && !NamesEquipmentClearly(text, manifest))
        {
            manifest = null;
            flow = null;
        }

        // A message that names different equipment with no design flow (for example a pressure vessel)
        // must not be answered by the active design of another equipment.
        if (manifest is null && activeManifestId is not null && _manifests is not null)
        {
            var named = _manifests.ResolveDetailed(text);

            if (named.IsResolved
                && named.Manifest is not null
                && !string.Equals(named.Manifest.Id, activeManifestId, StringComparison.OrdinalIgnoreCase)
                && NamesEquipmentClearly(text, named.Manifest)
                && FindFlow(named.Manifest.Id) is null)
            {
                _equipment.Update(projectId, sessionId, s => s.Discard(activeManifestId));
                return false;
            }
        }

        // A follow-up such as "5000 m3/h" or "use defaults" continues the equipment already being designed.
        if (manifest is null
            && activeManifestId is not null
            && _manifests is not null
            && _manifests.TryGet(activeManifestId, out var activeManifest))
        {
            manifest = activeManifest;
            flow = FindFlow(activeManifest.Id);
        }

        if (manifest is null || flow is null)
        {
            if (route.Clarify.Count > 0 && activeManifestId is null && !current.HasTemplate)
            {
                reply = BuildDesignClarification(route.Clarify);
                return true;
            }

            return false;
        }

        var intent = CadAssistEquipmentIntentResolver.Resolve(text);

        if (intent == CadAssistEquipmentIntent.ListParts)
        {
            reply = BuildPartsReply(manifest);
            return true;
        }

        if (intent == CadAssistEquipmentIntent.IdentifyEquipment)
        {
            reply = BuildEquipmentSummaryReply(manifest);
            return true;
        }

        var turnReply = RunEquipmentTurn(projectId, sessionId, manifest, flow, text, intent);

        if (turnReply is null)
        {
            return false;
        }

        reply = turnReply;
        return true;
    }

    /// <summary>
    /// One turn of the guided workflow for one equipment type: collect values, apply defaults on request,
    /// calculate, pause with the "what next" menu, then generate exactly what the user asks for.
    /// </summary>
    private CadAssistMessageDto? RunEquipmentTurn(
        int projectId,
        string sessionId,
        EquipmentManifest manifest,
        IEquipmentDesignFlow flow,
        string text,
        CadAssistEquipmentIntent intent)
    {
        // Each equipment has its own state. Switching equipment never reuses another equipment's values.
        var snapshot = _equipment.Update(projectId, sessionId, s => s.Activate(manifest.Id));
        var state = snapshot.States[manifest.Id];

        var action = ResolveNextAction(text, state);
        var useDefaults = !string.IsNullOrWhiteSpace(text)
                          && (SafeIsMatch(UseDefaultsRegex, text) || CadAssistIntentParser.Parse(text, null).UseDefaults);

        var partKeys = FindRequestedPartKeys(manifest, text);
        var assemblyRequested = partKeys.Count == 0 && SafeIsMatch(AssemblyRequestRegex, text);

        if (action == EquipmentNextAction.GenerateAssembly)
        {
            assemblyRequested = true;
        }

        if (action == EquipmentNextAction.ViewBreakdown && state.CalculationValidated)
        {
            var breakdown = BuildPartsReply(manifest);
            breakdown.Content += "\n\n" + BuildNextActionMenu(manifest);
            breakdown.Status = EquipmentDesignTurn.StatusCalculated;
            return breakdown;
        }

        if (action == EquipmentNextAction.ChangeValues && state.CalculationValidated)
        {
            _equipment.Update(projectId, sessionId, s => s.Activate(manifest.Id).Reopen());

            return new CadAssistMessageDto
            {
                Role = CadAssistRole.Assistant,
                Status = EquipmentDesignTurn.StatusNeedInputs,
                Content = "Tell me the value or values to change, for example `" + ChangeExample(manifest)
                          + "`. Everything else stays as it is, and I will size the "
                          + DisplayNameOf(manifest).ToLowerInvariant() + " again."
            };
        }

        var defaultsNote = string.Empty;
        var values = new Dictionary<string, double>(state.Values, StringComparer.OrdinalIgnoreCase);

        if (useDefaults)
        {
            var defaults = EquipmentStandardDefaults.For(manifest);
            var added = 0;

            var applied = _equipment.Update(projectId, sessionId, s => added = s.Activate(manifest.Id).ApplyDefaults(defaults));
            values = new Dictionary<string, double>(applied.States[manifest.Id].Values, StringComparer.OrdinalIgnoreCase);

            defaultsNote = BuildDefaultsNote(manifest, defaults, applied.States[manifest.Id], added);
        }

        // A bare "generate" before any calculation passed must not skip the sizing step.
        // A named part, an assembly request or an explicit "write code / STEP" request is honoured.
        var explicitGenerate = IsExplicitGenerateRequest(text) || assemblyRequested || partKeys.Count > 0
                               || action is EquipmentNextAction.GeneratePartScripts or EquipmentNextAction.GenerateAssembly;

        var verbGenerate = intent == CadAssistEquipmentIntent.GenerateCad || SafeIsMatch(GenerateVerbRegex, text);

        var generateRequested = explicitGenerate || (verbGenerate && state.CalculationValidated);

        var request = new EquipmentDesignRequest
        {
            ManifestId = manifest.Id,
            Text = text,
            Values = values,
            GenerateRequested = generateRequested,
            PartKeys = assemblyRequested ? new[] { "Assembly" } : partKeys
        };

        var turn = flow.Handle(request);

        if (turn is null)
        {
            _equipment.Update(projectId, sessionId, s => s.Discard(manifest.Id));
            return null;
        }

        var isCalculated = turn.Status == EquipmentDesignTurn.StatusCalculated
                           && turn.MissingKeys.Count == 0
                           && turn.Errors.Count == 0;

        var generatedScope = turn.Parts is { Success: true } built
            ? built.Parts.Select(p => p.PartKey).ToList()
            : new List<string>();

        _equipment.Update(projectId, sessionId, s =>
        {
            var current = s.Activate(manifest.Id);
            current.ReplaceValues(turn.Values);

            if (turn.Generated)
            {
                current.MarkCalculated();
                current.MarkGenerated(generatedScope);
            }
            else if (isCalculated)
            {
                current.MarkCalculated();
                current.MarkMenuShown();
            }
            else
            {
                current.Reopen();
            }
        });

        string? footer = null;

        if (turn.Generated || isCalculated)
        {
            footer = BuildNextActionMenu(manifest);
        }
        else if (turn.Status == EquipmentDesignTurn.StatusNeedInputs)
        {
            footer = "Or say `use defaults` and I will fill the " + DisplayNameOf(manifest).ToLowerInvariant()
                     + " with the standard engineering defaults.";
        }

        var message = BuildDesignReply(turn, footer);

        if (defaultsNote.Length > 0)
        {
            message.Content = defaultsNote + "\n\n" + message.Content;
        }

        return message;
    }

    /// <summary>
    /// Reads the answer to "Which equipment type is this?". Returns true when the equipment was confirmed
    /// and a reply is ready. When false and reply is set, the assistant asks again. When false and reply is null,
    /// the answer names equipment that is not designed from calculator inputs.
    /// </summary>
    private bool AnswerEquipmentQuestion(
        int projectId,
        string sessionId,
        string text,
        EquipmentConversationSession session,
        out CadAssistMessageDto? reply)
    {
        reply = null;

        EquipmentManifest? confirmed = null;

        if (!string.IsNullOrWhiteSpace(session.ProposedManifestId)
            && SafeIsMatch(ConfirmRegex, text)
            && _manifests is not null
            && _manifests.TryGet(session.ProposedManifestId, out var proposed)
            && FindFlow(proposed.Id) is not null
            && !NamesDifferentEquipment(text, proposed))
        {
            confirmed = proposed;
        }

        if (confirmed is null)
        {
            var route = ResolveDesignRoute(text);

            if (route.Manifest is not null && route.Flow is not null)
            {
                confirmed = route.Manifest;
            }
            else if (route.Clarify.Count > 0)
            {
                reply = BuildDesignClarification(route.Clarify);
                return false;
            }
        }

        if (confirmed is null)
        {
            return false;
        }

        var flow = FindFlow(confirmed.Id)!;

        reply = RunEquipmentTurn(
            projectId,
            sessionId,
            confirmed,
            flow,
            string.Empty,
            CadAssistEquipmentIntent.Unknown);

        if (reply is null)
        {
            return false;
        }

        reply.Content = "Got it, this is a **" + DisplayNameOf(confirmed) + "**. I will use its own rule set.\n\n" + reply.Content;
        return true;
    }

    /// <summary>
    /// Builds the "Which equipment type is this?" question for an uploaded image. Returns null only when
    /// there is nothing to ask: no design flows, or the user already named the equipment in their own words.
    /// </summary>
    private CadAssistMessageDto? BeginImageIdentification(CadAssistUploadInput input)
    {
        if (!input.IsImage || _manifests is null || _designFlows.Count == 0)
        {
            return null;
        }

        var designable = _manifests.All
            .Where(candidate => FindFlow(candidate.Id) is not null)
            .ToList();

        if (designable.Count == 0)
        {
            return null;
        }

        var userText = string.Join(
            " ",
            new[] { input.Prompt, input.DrawingType, Path.GetFileNameWithoutExtension(input.FileName) }
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        // The user named the equipment in their own words: that is the confirmation.
        var named = ResolveDesignRoute(userText);

        if (named.Manifest is not null
            && named.Flow is not null
            && NamesEquipmentClearly(userText, named.Manifest))
        {
            var direct = RunEquipmentTurn(
                input.ProjectId,
                input.SessionId,
                named.Manifest,
                named.Flow,
                string.Empty,
                CadAssistEquipmentIntent.Unknown);

            if (direct is not null)
            {
                direct.Content = "You named this as a **" + DisplayNameOf(named.Manifest) + "**, so I will treat it as one.\n\n"
                                 + direct.Content;

                return direct;
            }
        }

        // The image is inspected only to propose a type. Nothing is sized until the user confirms it.
        var detectedText = string.Join(
            " ",
            new[] { input.Vision?.EquipmentType, input.Vision?.Summary }
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        EquipmentManifest? proposal = null;

        if (detectedText.Length > 0)
        {
            var detected = ResolveDesignRoute(detectedText);

            if (detected.Manifest is not null
                && detected.Flow is not null
                && NamesEquipmentClearly(detectedText, detected.Manifest))
            {
                proposal = detected.Manifest;
            }
        }

        _equipment.Update(input.ProjectId, input.SessionId, s =>
            s.BeginIdentification(input.FileName, proposal?.Id, designable.Select(m => m.Id)));

        var text = new StringBuilder();

        if (proposal is not null)
        {
            text.AppendLine("This looks like a **" + DisplayNameOf(proposal) + "**. Is that right?");
            text.AppendLine();
            text.AppendLine("Reply `yes` to confirm, or tell me which one it is:");
        }
        else
        {
            text.AppendLine("Which equipment type is this?");
            text.AppendLine();
            text.AppendLine("Pick one:");
        }

        text.AppendLine();

        foreach (var item in designable)
        {
            text.AppendLine("- " + DisplayNameOf(item));
        }

        text.AppendLine();
        text.Append("I will not size anything until the equipment type is confirmed.");

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "equipment-confirm"
        };
    }

    private EquipmentNextAction ResolveNextAction(string text, EquipmentConversationState state)
    {
        if (!state.CalculationValidated)
        {
            return EquipmentNextAction.None;
        }

        if (SafeIsMatch(ChangeValuesRegex, text))
        {
            return EquipmentNextAction.ChangeValues;
        }

        if (SafeIsMatch(AssemblyRequestRegex, text))
        {
            return EquipmentNextAction.GenerateAssembly;
        }

        if (SafeIsMatch(PartScriptsRegex, text))
        {
            return EquipmentNextAction.GeneratePartScripts;
        }

        if (SafeIsMatch(BreakdownRegex, text) && !SafeIsMatch(GenerateVerbRegex, text))
        {
            return EquipmentNextAction.ViewBreakdown;
        }

        var trimmed = text.Trim();

        return trimmed switch
        {
            "1" => EquipmentNextAction.ViewBreakdown,
            "2" => EquipmentNextAction.GeneratePartScripts,
            "3" => EquipmentNextAction.GenerateAssembly,
            "4" => EquipmentNextAction.ChangeValues,
            _ => EquipmentNextAction.None
        };
    }

    private static string BuildNextActionMenu(EquipmentManifest manifest)
    {
        var firstPart = manifest.Parts.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Title))?.Title
                        ?? manifest.Parts.FirstOrDefault()?.Key
                        ?? "main body";

        var menu = new StringBuilder();

        menu.AppendLine("**" + NextActionQuestion + "**");
        menu.AppendLine();
        menu.AppendLine("1. View the component breakdown (" + manifest.Parts.Count.ToString(CultureInfo.InvariantCulture) + " parts)");
        menu.AppendLine("2. Generate individual part scripts, one body each (for example `generate the " + firstPart.ToLowerInvariant() + "`)");
        menu.AppendLine("3. Generate the full assembly STEP file (`generate the full assembly`)");
        menu.Append("4. Change a value and size it again (for example `" + ChangeExample(manifest) + "`)");

        return menu.ToString();
    }

    private static string ChangeExample(EquipmentManifest manifest)
    {
        var first = manifest.Inputs.FirstOrDefault(i => i.IsRequired) ?? manifest.Inputs.FirstOrDefault();

        if (first is null)
        {
            return "change a value";
        }

        var unit = string.IsNullOrWhiteSpace(first.Unit) || first.Unit == "-" ? string.Empty : " " + first.Unit;
        var alias = first.Aliases.FirstOrDefault() ?? first.DisplayName.ToLowerInvariant();

        return alias + " 3" + unit;
    }

    private static string BuildDefaultsNote(
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double> defaults,
        EquipmentConversationState state,
        int added)
    {
        var text = new StringBuilder();

        text.AppendLine("Using the standard engineering defaults for the **" + DisplayNameOf(manifest) + "**:");
        text.AppendLine();

        var shown = 0;

        foreach (var pair in defaults.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!state.DefaultedKeys.Contains(pair.Key))
            {
                continue;
            }

            var input = manifest.Inputs.FirstOrDefault(i => string.Equals(i.Key, pair.Key, StringComparison.OrdinalIgnoreCase));
            var label = input is not null && !string.IsNullOrWhiteSpace(input.DisplayName) ? input.DisplayName : pair.Key;
            var unit = input is null || string.IsNullOrWhiteSpace(input.Unit) || input.Unit == "-" ? string.Empty : " " + input.Unit;

            text.AppendLine("- " + label + " = " + pair.Value.ToString("0.######", CultureInfo.InvariantCulture) + unit);
            shown++;
        }

        if (shown == 0)
        {
            text.AppendLine("- You already gave every value, so nothing needed a default.");
        }

        text.AppendLine();
        text.Append("These are starting values. Give me your real duty values at any time and I will size it again.");

        return text.ToString();
    }

    /// <summary>
    /// True when the text names the equipment itself (its name or a specific alias), not just a loose word such as "fan".
    /// </summary>
    private static bool NamesEquipmentClearly(string text, EquipmentManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (string.Equals(manifest.Id, "axial-fan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(manifest.Id, "centrifugal-fan", StringComparison.OrdinalIgnoreCase))
        {
            if (!SafeIsMatch(SpecificFanWordRegex, text))
            {
                return false;
            }
        }

        var type = EquipmentRegistry.Resolve(text);

        if (type != EquipmentType.Unknown)
        {
            return string.Equals(type.ToString(), manifest.CalculatorId, StringComparison.OrdinalIgnoreCase);
        }

        var padded = " " + string.Join(" ", WordSplitRegex.Split(text.ToLowerInvariant()).Where(w => w.Length > 0)) + " ";

        var names = new List<string> { manifest.Id.Replace('-', ' '), manifest.DisplayName };
        names.AddRange(manifest.Aliases.Where(a => a.Trim().Contains(' ') || a.Trim().Length >= 6));

        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => " " + string.Join(" ", WordSplitRegex.Split(n.ToLowerInvariant()).Where(w => w.Length > 0)) + " ")
            .Any(n => n.Trim().Length > 0 && padded.Contains(n, StringComparison.Ordinal));
    }

    private bool NamesDifferentEquipment(string text, EquipmentManifest proposed)
    {
        if (_manifests is null)
        {
            return false;
        }

        var resolved = _manifests.ResolveDetailed(text);

        return resolved.IsResolved
               && resolved.Manifest is not null
               && !string.Equals(resolved.Manifest.Id, proposed.Id, StringComparison.OrdinalIgnoreCase)
               && NamesEquipmentClearly(text, resolved.Manifest);
    }

    private DesignRoute ResolveDesignRoute(string text)
    {
        if (_manifests is null || _designFlows.Count == 0)
        {
            return DesignRoute.None;
        }

        var resolution = _manifests.ResolveDetailed(text);
        var manifest = resolution.IsResolved ? resolution.Manifest : null;

        if (manifest is null)
        {
            var type = EquipmentRegistry.Resolve(text);

            if (type != EquipmentType.Unknown)
            {
                manifest = _manifests.All.FirstOrDefault(candidate =>
                    string.Equals(candidate.CalculatorId, type.ToString(), StringComparison.OrdinalIgnoreCase));
            }
        }

        // A loose word such as "fan" or "blower" must never default to the axial fan.
        // When the text does not say which kind of fan, ask.
        if (manifest is not null
            && string.Equals(manifest.Id, "axial-fan", StringComparison.OrdinalIgnoreCase)
            && SafeIsMatch(GenericFanWordRegex, text)
            && !SafeIsMatch(SpecificFanWordRegex, text)
            && EquipmentRegistry.Resolve(text) == EquipmentType.Unknown)
        {
            var fans = _manifests.All
                .Where(candidate => (string.Equals(candidate.Id, "axial-fan", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(candidate.Id, "centrifugal-fan", StringComparison.OrdinalIgnoreCase))
                                    && FindFlow(candidate.Id) is not null)
                .ToList();

            if (fans.Count > 1)
            {
                return new DesignRoute(null, null, fans);
            }
        }

        if (manifest is not null)
        {
            // Equipment with no registered flow stays on the legacy template pipeline.
            var flow = FindFlow(manifest.Id);

            return flow is null
                ? DesignRoute.None
                : new DesignRoute(manifest, flow, Array.Empty<EquipmentManifest>());
        }

        if (resolution.IsAmbiguous
            && resolution.Candidates.Any(candidate => FindFlow(candidate.Id) is not null))
        {
            return new DesignRoute(null, null, resolution.Candidates);
        }

        return DesignRoute.None;
    }

    private IEquipmentDesignFlow? FindFlow(string? manifestId)
    {
        return _designFlows.FirstOrDefault(candidate => candidate.CanHandle(manifestId));
    }

    private bool TryFindDesignManifest(EquipmentType equipment, out EquipmentManifest manifest)
    {
        manifest = null!;

        if (_manifests is null)
        {
            return false;
        }

        var found = _manifests.All.FirstOrDefault(candidate =>
            string.Equals(candidate.CalculatorId, equipment.ToString(), StringComparison.OrdinalIgnoreCase)
            && FindFlow(candidate.Id) is not null);

        if (found is null)
        {
            return false;
        }

        manifest = found;
        return true;
    }

    private void AppendDesignableEquipment(StringBuilder text)
    {
        if (_manifests is null || _designFlows.Count == 0)
        {
            return;
        }

        var designable = _manifests.All
            .Where(candidate => FindFlow(candidate.Id) is not null)
            .ToList();

        if (designable.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("Equipment I design from calculator inputs:");
        text.AppendLine();

        foreach (var item in designable)
        {
            text.AppendLine("- " + DisplayNameOf(item));
        }
    }

    private static string DisplayNameOf(EquipmentManifest manifest)
    {
        return string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.Id : manifest.DisplayName;
    }

    private static bool SafeIsMatch(Regex regex, string text)
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

    private static IReadOnlyList<string> FindRequestedPartKeys(EquipmentManifest manifest, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || manifest.Parts.Count == 0)
        {
            return Array.Empty<string>();
        }

        // An empty list means the flow builds every part the manifest declares.
        var words = new HashSet<string>(
            WordSplitRegex.Split(text.ToLowerInvariant()).Where(w => w.Length > 0),
            StringComparer.Ordinal);

        return manifest.Parts
            .Where(part => ContainsPhrase(text, part.Key)
                           || ContainsPhrase(text, part.Title)
                           || ContainsAllWords(words, part.Title)
                           || ContainsAllWords(words, SplitCamelCase(part.Key)))
            .Select(part => part.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsAllWords(HashSet<string> words, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var needed = WordSplitRegex.Split(phrase.ToLowerInvariant())
            .Where(w => w.Length >= 3)
            .ToList();

        return needed.Count > 0 && needed.All(words.Contains);
    }

    private static string SplitCamelCase(string? key)
    {
        return string.IsNullOrWhiteSpace(key)
            ? string.Empty
            : Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    private static bool ContainsPhrase(string text, string? phrase)
    {
        return !string.IsNullOrWhiteSpace(phrase)
               && text.Contains(phrase.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CadAssistMessageDto BuildDesignClarification(IReadOnlyList<EquipmentManifest> candidates)
    {
        var text = new StringBuilder();

        text.AppendLine("That could be more than one kind of equipment, and I will not guess between them:");
        text.AppendLine();

        foreach (var candidate in candidates)
        {
            text.AppendLine("- " + DisplayNameOf(candidate));
        }

        text.AppendLine();
        text.AppendLine("Tell me which one you mean and I will continue.");

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "equipment-ambiguous"
        };
    }

    private static CadAssistMessageDto BuildPartsReply(EquipmentManifest manifest)
    {
        var text = new StringBuilder();
        var name = DisplayNameOf(manifest);

        if (manifest.Parts.Count == 0)
        {
            text.AppendLine("**" + name + "** is exported as a single body.");
        }
        else
        {
            text.AppendLine("**" + name + "** is built from these parts:");
            text.AppendLine();

            foreach (var part in manifest.Parts)
            {
                var title = string.IsNullOrWhiteSpace(part.Title) ? part.Key : part.Title;

                text.AppendLine("- " + title
                                + (part.Quantity > 1 ? " (x" + part.Quantity + ")" : string.Empty)
                                + (part.IsRequired ? string.Empty : " (optional)"));
            }
        }

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "equipment-parts"
        };
    }

    private static CadAssistMessageDto BuildEquipmentSummaryReply(EquipmentManifest manifest)
    {
        var text = new StringBuilder();

        text.AppendLine("**" + DisplayNameOf(manifest) + "**");

        if (!string.IsNullOrWhiteSpace(manifest.Description))
        {
            text.AppendLine();
            text.AppendLine(manifest.Description.Trim());
        }

        return new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = text.ToString().TrimEnd(),
            Status = "equipment-identified"
        };
    }

    private static CadAssistMessageDto BuildDesignReply(EquipmentDesignTurn turn, string? footer = null)
    {
        var reply = turn.Reply?.TrimEnd() ?? string.Empty;

        // The "what next" menu replaces the flow's own "say generate" hint.
        if (footer is not null)
        {
            var hint = reply.LastIndexOf(GenerateHintMarker, StringComparison.Ordinal);

            if (hint >= 0)
            {
                reply = reply[..hint].TrimEnd();
            }
        }

        var text = new StringBuilder(reply);

        AppendDesignNotes(text, "Errors", turn.Errors);
        AppendDesignNotes(text, "Warnings", turn.Warnings);

        var message = new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Status = turn.Status
        };

        if (turn.Parts is { } parts)
        {
            if (parts.Success)
            {
                foreach (var part in parts.Parts.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Script)))
                {
                    var title = string.IsNullOrWhiteSpace(part.Title) ? part.PartKey : part.Title;

                    var block = new CadAssistCodeBlockDto
                    {
                        Title = title + (part.Quantity > 1 ? " (x" + part.Quantity + ")" : string.Empty),
                        Code = part.Script,
                        LineCount = part.Script.Split('\n').Length
                    };

                    message.CodeBlocks.Add(block);
                    message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Run, Label = "Run", TargetCodeBlockId = block.Id });
                    message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.DownloadStep, Label = "Download STEP", TargetCodeBlockId = block.Id });
                    message.Actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Copy, Label = "Copy", TargetCodeBlockId = block.Id });
                }

                AppendDesignNotes(text, "Required parts not built", parts.MissingRequiredPartKeys);
            }
            else
            {
                AppendDesignNotes(text, "Script errors", parts.Errors);
            }
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            if (text.Length > 0)
            {
                text.AppendLine();
                text.AppendLine();
            }

            text.Append(footer);
        }

        message.Content = text.ToString().TrimEnd();
        return message;
    }

    private static void AppendDesignNotes(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        var current = text.ToString();

        var missing = items
            .Where(item => !string.IsNullOrWhiteSpace(item)
                           && current.IndexOf(item, StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.AppendLine();
            text.AppendLine();
        }

        text.AppendLine(heading + ":");

        foreach (var item in missing)
        {
            text.AppendLine("- " + item);
        }
    }

    private sealed record DesignRoute(
        EquipmentManifest? Manifest,
        IEquipmentDesignFlow? Flow,
        IReadOnlyList<EquipmentManifest> Clarify)
    {
        public static readonly DesignRoute None = new(null, null, Array.Empty<EquipmentManifest>());
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

/// <summary>
/// Standard starting values for "use defaults", one set per equipment type. Values are SI and keyed by the
/// calculator and mapper keys. Manifest input defaults come first; the equipment design point fills the rest.
/// Duty values are the worked examples the equipment's own design flow shows the user, or the rulepack design values.
/// Nothing here replaces a value the user gave, and none of it is an engineering approval.
/// </summary>
internal static class EquipmentStandardDefaults
{
    private const double StandardSpeedRpm = 1450.0;

    public static IReadOnlyDictionary<string, double> For(EquipmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in manifest.Inputs)
        {
            if (input.Default.HasValue)
            {
                values[input.Key] = input.Default.Value;
            }
        }

        foreach (var pair in DesignPoint(manifest.Id))
        {
            values.TryAdd(pair.Key, pair.Value);
        }

        return values;
    }

    private static double RpmToRadS(double rpm) => rpm * 2.0 * Math.PI / 60.0;

    private static IReadOnlyDictionary<string, double> DesignPoint(string manifestId)
    {
        var id = (manifestId ?? string.Empty).Trim().ToLowerInvariant();

        return id switch
        {
            "cyclone-separator" => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [CycloneCalculator.Keys.AirflowM3s] = 1.0,
                [CycloneCalculator.Keys.InletVelocityMs] = 20.0
            },

            "wet-scrubber" => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [WetScrubberCalculator.Keys.AirflowM3s] = 2.0,
                [WetScrubberCalculator.Keys.GasVelocityMs] = 1.2,
                [WetScrubberCalculator.Keys.GasResidenceTimeS] = 2.0,
                [WetScrubberCalculator.Keys.InletVelocityMs] = 15.0,
                [WetScrubberCalculator.Keys.StackVelocityMs] = 10.0,
                [WetScrubberCalculator.Keys.LiquidToGasRatioLm3] = 2.0
            },

            "axial-fan" => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [AxialFanCalculator.Keys.AirflowM3s] = 5.0,
                [AxialFanCalculator.Keys.StaticPressurePa] = 450.0,
                [AxialFanCalculator.Keys.SpeedRadS] = RpmToRadS(StandardSpeedRpm),
                [AxialFanTemplateMapper.BladeChordM] = 0.08,
                [AxialFanTemplateMapper.BladeThicknessM] = 0.004,
                [AxialFanTemplateMapper.BladePitchDeg] = 25.0
            },

            "centrifugal-fan" => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [CentrifugalFanCalculator.Keys.AirflowM3s] = 5.0,
                [CentrifugalFanCalculator.Keys.StaticPressurePa] = 1200.0,
                [CentrifugalFanCalculator.Keys.SpeedRadS] = RpmToRadS(StandardSpeedRpm),
                [CentrifugalFanCalculator.Keys.BladeType] = CentrifugalBladeTypes.ToCode(CentrifugalBladeType.BackwardCurved)
            },

            _ => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        };
    }
}