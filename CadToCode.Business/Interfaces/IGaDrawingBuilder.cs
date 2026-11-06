using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public sealed class GaDrawingBuild
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public byte[] DxfContent { get; init; } = Array.Empty<byte>();
    public string FileName { get; init; } = "ga-drawing.dxf";
    public IReadOnlyList<CadAssistGaPartDto> Parts { get; init; } = Array.Empty<CadAssistGaPartDto>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public interface IGaDrawingBuilder
{
    bool CanHandle(string? equipmentKey);

    GaDrawingBuild Build(CadAssistGaRequest request);
}
