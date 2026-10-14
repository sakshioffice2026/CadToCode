namespace CadToCode.Models.Options;

public class QdrantOptions
{
    public const string SectionName = "QdrantOptions";

    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 6334;

    public bool UseHttps { get; set; } = false;

    public string? ApiKey { get; set; }

    public string CollectionName { get; set; } = "cad_templates";

    public int VectorSize { get; set; } = 768;

    public int TopK { get; set; } = 3;

    public double MinScore { get; set; } = 0.55;

    public int TimeoutSeconds { get; set; } = 30;

    public bool AutoCreateCollection { get; set; } = true;
}