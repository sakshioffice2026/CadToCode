namespace CadToCode.Models.Dtos;

public class PartDxfSpecDto
{
    public string BlockName { get; set; } = string.Empty;
    public string? Material { get; set; }
    public decimal? Thickness { get; set; }
}

public class PartDxfRequestDto
{
    public int ProjectId { get; set; }
    public List<PartDxfSpecDto> Specs { get; set; } = new();
}

public class PartDxfFileDto
{
    public string BlockName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public class PartDxfPackageDto
{
    public int ProjectId { get; set; }
    public string ProjectTitle { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<PartDxfFileDto> Files { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}