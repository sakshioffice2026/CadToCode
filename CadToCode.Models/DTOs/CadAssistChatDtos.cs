using System.Text.Json.Serialization;

namespace CadToCode.Models.Dtos;

public static class CadAssistRole
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

public static class CadAssistActionKind
{
    public const string Run = "run";
    public const string Apply = "apply";
    public const string Copy = "copy";
    public const string DownloadStep = "download-step";
    public const string Regenerate = "regenerate";
}

public class CadAssistAttachmentDto
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? TextContent { get; set; }
}

public class CadAssistCodeBlockDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Language { get; set; } = "csharp";
    public string Title { get; set; } = "STEP script";
    public string Code { get; set; } = string.Empty;
    public int LineCount { get; set; }
}

public class CadAssistActionDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = CadAssistActionKind.Copy;
    public string Label { get; set; } = string.Empty;
    public string? TargetCodeBlockId { get; set; }
}

public class CadAssistComponentDto
{
    public string Label { get; set; } = string.Empty;
    public string? MappedComponent { get; set; }
}

public class CadAssistViewDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CadAssistParameterDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Unit { get; set; } = "mm";
    public double Default { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double? Value { get; set; }
    public string Source { get; set; } = "Default";
    public string? Evidence { get; set; }
    public bool Verified { get; set; } = true;
}

public class CadAssistMessageDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = CadAssistRole.Assistant;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? Status { get; set; }
    public string? Model { get; set; }
    public long ElapsedMs { get; set; }
    public string? Stage { get; set; }
    public string? TemplateTitle { get; set; }
    public List<string> Attachments { get; set; } = new();
    public List<CadAssistCodeBlockDto> CodeBlocks { get; set; } = new();
    public List<CadAssistActionDto> Actions { get; set; } = new();
    public List<CadAssistComponentDto> Breakdown { get; set; } = new();
    public List<CadAssistParameterDto> Parameters { get; set; } = new();
    public List<CadAssistViewDto> Views { get; set; } = new();
    public List<string> Uncertainties { get; set; } = new();

    [JsonIgnore]
    public string? ModelContent { get; set; }
}

public class CadAssistChatRequest
{
    public int ProjectId { get; set; }
    public string? SessionId { get; set; }
    public string? Prompt { get; set; }
    public string? DrawingType { get; set; }
    public string? FilterInstructions { get; set; }
    public string? CurrentCode { get; set; }
    public List<CadAssistAttachmentDto> Attachments { get; set; } = new();
}

public class CadAssistChatResponse
{
    public bool Success { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public CadAssistMessageDto? Message { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
    public string? Stage { get; set; }
    public long ElapsedMs { get; set; }
}

public class CadAssistHistoryResponse
{
    public string SessionId { get; set; } = string.Empty;
    public List<CadAssistMessageDto> Messages { get; set; } = new();
    public string? EditorCode { get; set; }
}

public class CadAssistSnapshotDto
{
    public List<CadAssistMessageDto> Messages { get; set; } = new();
    public List<string?> ModelContents { get; set; } = new();
    public string? EditorCode { get; set; }
}

public class CadAssistStatusDto
{
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public class LocalAiGenerationRequest
{
    public int ProjectId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? DrawingType { get; set; }
    public string? FilterInstructions { get; set; }
    public string GeometryContext { get; set; } = string.Empty;
    public string? CurrentCode { get; set; }
    public List<CadAssistAttachmentDto> Attachments { get; set; } = new();
}

public class LocalAiGenerationResult
{
    public bool Success { get; set; }
    public string Reply { get; set; } = string.Empty;
    public List<CadAssistCodeBlockDto> CodeBlocks { get; set; } = new();
    public CadAssistMessageDto? Message { get; set; }
    public string? Error { get; set; }
    public string? Model { get; set; }
    public long ElapsedMs { get; set; }
}