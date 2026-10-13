using System.Text.Json.Serialization;

namespace CadToCode.Models.Dtos;

public static class SandboxStatus
{
    public const string Ok = "ok";
    public const string Invalid = "invalid";
    public const string Error = "error";
    public const string Timeout = "timeout";
}

public class SandboxDiagnostic
{
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
}

public class SandboxPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class SandboxBounds
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MinZ { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double MaxZ { get; set; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double Depth => MaxZ - MinZ;
}

public class SandboxSolid
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Z { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Depth { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SandboxPoint>? Outer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<List<SandboxPoint>>? Holes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? X { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Y { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Radius { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Height { get; set; }
}

public class SandboxRunResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = SandboxStatus.Error;
    public string? Message { get; set; }
    public long ElapsedMs { get; set; }
    public int SolidCount { get; set; }
    public int FaceCount { get; set; }
    public bool Truncated { get; set; }
    public string? FileName { get; set; }
    public string? StepContent { get; set; }
    public SandboxBounds? Bounds { get; set; }
    public List<string> Output { get; set; } = new();
    public List<SandboxDiagnostic> Diagnostics { get; set; } = new();
    public List<SandboxSolid> Solids { get; set; } = new();
}