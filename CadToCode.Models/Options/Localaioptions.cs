namespace CadToCode.Models.Options;

public class LocalAiOptions
{
    public const string SectionName = "LocalAiOptions";

    public bool Enabled { get; set; } = true;

    public string ModelPath { get; set; } = "Models/cadtocode-assist.gguf";

    public string ChatTemplate { get; set; } = "chatml";

    public List<string>? StopMarkers { get; set; }

    public uint ContextSize { get; set; } = 8192;

    public int Threads { get; set; } = 0;

    public int GpuLayerCount { get; set; } = 0;

    public int MaxTokens { get; set; } = 1536;

    public double Temperature { get; set; } = 0.1;

    public double TopP { get; set; } = 0.9;

    public int InferenceTimeoutSeconds { get; set; } = 300;

    public int MaxPromptChars { get; set; } = 4000;

    public int MaxGeometryChars { get; set; } = 6000;

    public int MaxCurrentCodeChars { get; set; } = 6000;

    public int MaxAttachments { get; set; } = 5;

    public int MaxAttachmentBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxAttachmentTextChars { get; set; } = 4000;

    public int MaxHistoryTurns { get; set; } = 12;

    public int MaxSessions { get; set; } = 200;

    public int SessionIdleMinutes { get; set; } = 240;

    public bool AutoVerifyCode { get; set; } = true;

    public string? SystemPromptOverride { get; set; }
}