namespace CadToCode.Models.Dtos;

public class UploadResultDto
{
    public bool Success { get; set; }
    public int? ProjectId { get; set; }
    public string? ErrorMessage { get; set; }
}