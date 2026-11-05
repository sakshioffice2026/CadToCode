namespace CadToCode.Models.Dtos;

public class CadAssistStepRequest
{
    public int ProjectId { get; set; }
    public string? SessionId { get; set; }
    public int JobId { get; set; }

    /// <summary>Extrusion thickness in drawing units (mm). Null or non-positive means "use the default".</summary>
    public double? Thickness { get; set; }
}

public class CadAssistStepPartDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DrawingNumber { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public double Thickness { get; set; }
}

public class CadAssistStepResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int JobId { get; set; }
    public double Thickness { get; set; }
    public bool UsedDefaultThickness { get; set; }
    public List<CadAssistStepPartDto> Parts { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? ZipDownloadUrl { get; set; }
    public long ElapsedMs { get; set; }
}