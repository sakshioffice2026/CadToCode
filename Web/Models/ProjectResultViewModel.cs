using CadToCode.Models.Dtos;

namespace Web.Models;

public class ProjectResultViewModel
{
    public ProjectResultDto Project { get; set; } = new();

    public string GeneratedCode { get; set; } = string.Empty;

    public bool IsImageSource => Project.SourceFileType == CadToCode.Models.Enums.SourceFileType.Image;
}