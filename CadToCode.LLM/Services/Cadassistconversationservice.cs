using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;
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
    private readonly IEquipmentDesignDispatcher? _designDispatcher;

    private const double MinImageEquipmentConfidence = 0.85;

    private static readonly HashSet<string> EquipmentGateCodes = new(StringComparer.Ordinal)
    {
        "GATE_AMBIGUOUS_EQUIPMENT",
        "GATE_IDENTIFY_EQUIPMENT",
        "GATE_CONFIRM_EQUIPMENT"
    };

    public CadAssistConversationService(
        ICadAssistSessionStore store,
        ICadTemplateMatcher matcher,
        IChatMemoryStore memory,
        ILlmParameterExtractor extractor,
        IEquipmentManifestRegistry? manifests = null,
        IEquipmentDesignDispatcher? designDispatcher = null)
    {
        _store = store;