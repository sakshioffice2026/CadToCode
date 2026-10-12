namespace CadToCode.Models.Dtos;

public class AssemblyAnalysisRequest
{
    public AssemblyTree Tree { get; set; } = new();
    public string TreeJson { get; set; } = string.Empty;
    public List<ClassificationPatternHint> SimilarPatterns { get; set; } = new();
}

public class NodeSemanticName
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public decimal Confidence { get; set; }
}

public class RelationshipInsight
{
    public string FromId { get; set; } = string.Empty;
    public string ToId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public class AssemblyAnalysisResult
{
    public bool Success { get; set; }
    public string AssemblyName { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public List<NodeSemanticName> Nodes { get; set; } = new();
    public List<RelationshipInsight> Relationships { get; set; } = new();
    public decimal Confidence { get; set; }
    public string? Error { get; set; }
    public string? RawResponse { get; set; }
}

public class CodeSynthesisRequest
{
    public AssemblyTree Tree { get; set; } = new();
    public AssemblyAnalysisResult? Analysis { get; set; }
    public string ClassName { get; set; } = "Assembly";
    public List<ClassificationPatternHint> SimilarPatterns { get; set; } = new();
}

public class CodeSynthesisResult
{
    public bool Success { get; set; }
    public string Code { get; set; } = string.Empty;
    public decimal Confidence { get; set; }
    public string? Error { get; set; }
    public string? RawResponse { get; set; }
}