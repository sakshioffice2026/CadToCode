using System.Text.Json;

namespace CadToCode.Models.Dtos;

public class StructuralProfile
{
    public double BoundingWidth { get; set; }
    public double BoundingHeight { get; set; }
    public int SegmentCount { get; set; }
    public int VertexCount { get; set; }
    public int InnerCutoutsCount { get; set; }
    public int NotchCount { get; set; }
    public int ClosedLoopCount { get; set; }
    public bool IsClosed { get; set; }

    public string ToRawJson(string source = "Profile")
    {
        return JsonSerializer.Serialize(new { Source = source, Profile = this });
    }

    public static StructuralProfile? FromRawJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Profile", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonSerializer.Deserialize<StructuralProfile>(element.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
