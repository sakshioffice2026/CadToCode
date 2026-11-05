namespace CadToCode.Models.Dtos;

public class CadAssistManufacturingRequest
{
    public int ProjectId { get; set; }
    public string? SessionId { get; set; }
    public int JobId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public List<string> PartKeys { get; set; } = new();
    public bool IncludeAllParts { get; set; } = true;
    public string? GaFileName { get; set; }
}

public class CadAssistManufacturingPartDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DrawingNumber { get; set; } = string.Empty;
    public int EntityCount { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public class CadAssistManufacturingResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int JobId { get; set; }
    public string GaFileName { get; set; } = string.Empty;
    public List<CadAssistManufacturingPartDto> Parts { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string? ZipDownloadUrl { get; set; }
    public long ElapsedMs { get; set; }
}