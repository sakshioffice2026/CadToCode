namespace CadToCode.Models.Options;

public class LlmOptions
{
    public const string SectionName = "LlmOptions";

    public bool Enabled { get; set; } = true;

    public string ModelPath { get; set; } = "Models/Phi-3-mini-4k-instruct-q4.gguf";

    public uint ContextSize { get; set; } = 4096;

    public int Threads { get; set; } = 0;

    public int MaxTokens { get; set; } = 512;

    public double Temperature { get; set; } = 0.1;

    public double TopP { get; set; } = 0.9;

    public int InferenceTimeoutSeconds { get; set; } = 180;

    public int MaxRetries { get; set; } = 1;

    public double MinConfidence { get; set; } = 0.5;

    public string VectorStorePath { get; set; } = "App_Data/patterns.json";

    public int EmbeddingDimensions { get; set; } = 64;

    public int TopKPatterns { get; set; } = 3;

    public double MinSimilarity { get; set; } = 0.6;

    public bool SeedBuiltInPatterns { get; set; } = true;
}