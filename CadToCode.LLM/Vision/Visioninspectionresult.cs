using System.Text.Json.Serialization;

namespace CadToCode.LLM.Vision;

public sealed class VisionInspectionResult
{
    [JsonPropertyName("equipmentType")]
    public string? EquipmentType { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("views")]
    public List<VisionView> Views { get; set; } = new();

    [JsonPropertyName("components")]
    public List<VisionComponent> Components { get; set; } = new();

    [JsonPropertyName("dimensions")]
    public List<VisionDimension> Dimensions { get; set; } = new();

    [JsonPropertyName("uncertainties")]
    public List<string> Uncertainties { get; set; } = new();

    [JsonIgnore]
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore]
    public bool HasContent =>
        Views.Count > 0 || Components.Count > 0 || Dimensions.Count > 0 || !string.IsNullOrWhiteSpace(EquipmentType);
}

public sealed class VisionView
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class VisionComponent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("view")]
    public string? View { get; set; }
}

public sealed class VisionDimension
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "mm";

    [JsonPropertyName("component")]
    public string? Component { get; set; }

    [JsonPropertyName("view")]
    public string? View { get; set; }

    [JsonPropertyName("evidence")]
    public string? Evidence { get; set; }

    [JsonIgnore]
    public bool IsAngle =>
        Unit.Equals("deg", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsCount =>
        Unit.Equals("count", StringComparison.OrdinalIgnoreCase);

    public bool TryGetMillimeters(out double millimeters)
    {
        millimeters = 0;

        if (double.IsNaN(Value) || double.IsInfinity(Value))
        {
            return false;
        }

        switch (Unit.Trim().ToLowerInvariant())
        {
            case "mm":
                millimeters = Value;
                return true;
            case "cm":
                millimeters = Value * 10.0;
                return true;
            case "m":
                millimeters = Value * 1000.0;
                return true;
            case "in":
                millimeters = Value * 25.4;
                return true;
            default:
                return false;
        }
    }
}