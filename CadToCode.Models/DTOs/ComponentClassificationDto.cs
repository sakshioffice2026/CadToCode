using CadToCode.Models.Enums;

namespace CadToCode.Models.Dtos;

public class ComponentClassificationRequest
{
    public string DrawingSummary { get; set; } = string.Empty;
    public List<string> Texts { get; set; } = new();
    public List<string> Layers { get; set; } = new();
    public List<ClassificationPatternHint> SimilarPatterns { get; set; } = new();
    public ExtractedComponent? DeterministicCandidate { get; set; }
}

public class ClassificationPatternHint
{
    public ComponentType ComponentType { get; set; }
    public string Description { get; set; } = string.Empty;
    public double Similarity { get; set; }
}

public class ComponentClassificationResult
{
    public bool Success { get; set; }
    public ComponentType ComponentType { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Height { get; set; }
    public decimal? Width { get; set; }
    public decimal? Diameter { get; set; }
    public decimal? TopDiameter { get; set; }
    public decimal? BottomDiameter { get; set; }
    public decimal? Angle { get; set; }
    public decimal Confidence { get; set; }
    public string? Reasoning { get; set; }
    public string? Error { get; set; }
    public string? RawResponse { get; set; }
}