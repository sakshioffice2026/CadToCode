using CadToCode.Models.Dtos;

namespace Web.Models;

public class ProjectResultViewModel
{
    public ProjectResultDto Project { get; set; } = new();

    public string GeneratedCode { get; set; } = string.Empty;

    public AssemblyGenerationResult Assembly { get; set; } = new();

    public KeyDimensionsDto KeyDimensions { get; set; } = new();

    public bool IsImageSource => Project.SourceFileType == CadToCode.Models.Enums.SourceFileType.Image;

    public bool HasAssembly => Assembly.Success && Assembly.Tree.Nodes.Count > 0;

    public bool UsedAi => Assembly.Source == AssemblyCodeSource.Ai;
}