namespace CadToCode.Models.Options;

public class GeminiEmbeddingOptions
{
    public const string SectionName = "GeminiEmbeddingOptions";

    public bool Enabled { get; set; } = true;

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";

    public string Model { get; set; } = "gemini-embedding-001";

    public int OutputDimensionality { get; set; } = 768;

    public int MaxInputChars { get; set; } = 8000;

    public int MaxBatchSize { get; set; } = 100;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;

    public int RetryDelayMilliseconds { get; set; } = 1500;
}