namespace CadToCode.Models.Options;

public class VisionOptions
{
    public const string SectionName = "VisionOptions";

    public bool Enabled { get; set; } = true;

    public string ModelPath { get; set; } = "Models/vision/vision-model.gguf";

    public string MmprojPath { get; set; } = "Models/vision/vision-mmproj.gguf";

    public uint ContextSize { get; set; } = 4096;

    public int Threads { get; set; } = 0;

    public int GpuLayerCount { get; set; } = 0;

    public int MaxImageEdgePixels { get; set; } = 1024;

    public int JpegQuality { get; set; } = 85;

    public int MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    public int MaxTokens { get; set; } = 768;

    public double Temperature { get; set; } = 0.1;

    public double TopP { get; set; } = 0.9;

    public int InferenceTimeoutSeconds { get; set; } = 240;

    public int IdleUnloadMinutes { get; set; } = 10;

    public int MaxComponents { get; set; } = 40;

    public int MaxDimensions { get; set; } = 60;
}