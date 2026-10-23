namespace CadToCode.Models.Options;

public class ZooOptions
{
    public const string SectionName = "ZooOptions";

    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.zoo.dev";
    public string[] OutputFormats { get; set; } = { "step", "gltf", "glb", "stl", "obj" };
    public string DefaultOutputFormat { get; set; } = "step";
    public int MaxPromptChars { get; set; } = 2000;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxHistory { get; set; } = 30;
    public string StorageRoot { get; set; } = string.Empty;
}
