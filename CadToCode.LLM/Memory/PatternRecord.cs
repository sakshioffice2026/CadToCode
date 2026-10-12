using CadToCode.Models.Enums;

namespace CadToCode.LLM.Memory;

public class PatternRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ComponentType ComponentType { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public float[] Vector { get; set; } = Array.Empty<float>();

    public decimal? Height { get; set; }

    public decimal? Width { get; set; }

    public decimal? Diameter { get; set; }

    public decimal? TopDiameter { get; set; }

    public decimal? BottomDiameter { get; set; }

    public decimal? Angle { get; set; }

    public bool IsSeed { get; set; }

    public bool IsUserConfirmed { get; set; }

    public int UseCount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PatternMatch
{
    public PatternRecord Pattern { get; set; } = new();

    public double Similarity { get; set; }
}