namespace CadToCode.Models.Dtos;

public class CadAssistGaRequest
{
    public int ProjectId { get; set; }
    public string? SessionId { get; set; }
    public string EquipmentKey { get; set; } = "centrifugal-fan";
    public string Prompt { get; set; } = string.Empty;
    public string Title { get; set; } = "CENTRIFUGAL FAN - GENERAL ARRANGEMENT";
    public string DrawingNumber { get; set; } = "GA-001";
    public string Material { get; set; } = "MILD STEEL";
    public Dictionary<string, double> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class CadAssistGaPartDto
{
    public int Balloon { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string Material { get; set; } = string.Empty;
}

public class CadAssistGaResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int JobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public List<CadAssistGaPartDto> Parts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long ElapsedMs { get; set; }
}
