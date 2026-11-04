namespace CadToCode.Models.Options;
public class CalloutRule
{
    public string Match { get; set; } = "";
    public double HalfWidth { get; set; }
    public double HalfHeight { get; set; }
    public string? Component { get; set; }
    public bool Snap { get; set; }
    public double SnapMax { get; set; } = 800;
}

public class ComponentSettings
{
    public List<string> IgnoreLayers { get; set; } = new();
    public List<string> IgnoreLayerPatterns { get; set; } = new();
    public List<string> ViewSuffixes { get; set; } = new();
    public List<string> DimensionLayers { get; set; } = new();
    public Dictionary<string, List<string>> Merge { get; set; } = new();
    public int MinEntities { get; set; } = 5;
    public double MatchTolerance { get; set; } = 2.0;
    public double TextMaxDistance { get; set; } = 500;

    public string CalloutRegex { get; set; } = @"DRG\s*NO\.?\s*([A-Za-z0-9\-\.]+)";
    public string LabelRegex { get; set; } = @"^(?!.*(MATCHING|WELD|DESIGN)).*\bFLANGE\b";
    public int MaxLabelLength { get; set; } = 40;
    public double LeaderTolerance { get; set; } = 150;
    public double DefaultHalfSize { get; set; } = 400;
    public List<CalloutRule> Callouts { get; set; } = new();
}