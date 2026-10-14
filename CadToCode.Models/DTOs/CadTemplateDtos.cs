namespace CadToCode.Models.Dtos;

public static class CadTemplateSource
{
    public const string Vector = "Qdrant";
    public const string BuiltIn = "BuiltIn";
}

public class CadTemplateDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    public string Script { get; set; } = string.Empty;
}

public class CadTemplateMatch
{
    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Source { get; set; } = CadTemplateSource.BuiltIn;

    public double Score { get; set; }

    public string Script { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<string> MatchedKeywords { get; set; } = new();
}

public class CadTemplateIndexResult
{
    public int Total { get; set; }

    public int Indexed { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public List<string> Errors { get; set; } = new();
}