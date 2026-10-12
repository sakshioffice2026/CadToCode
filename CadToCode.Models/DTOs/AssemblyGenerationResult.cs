namespace CadToCode.Models.Dtos;

public enum AssemblyCodeSource
{
    Deterministic = 0,
    Ai = 1
}

public class AssemblyGenerationResult
{
    public bool Success { get; set; }
    public int ProjectId { get; set; }
    public AssemblyTree Tree { get; set; } = new();
    public AssemblyAnalysisResult? Analysis { get; set; }
    public string ClassName { get; set; } = "Assembly";
    public string Code { get; set; } = string.Empty;
    public AssemblyCodeSource Source { get; set; } = AssemblyCodeSource.Deterministic;
    public decimal Confidence { get; set; }
    public string? FallbackReason { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();
}