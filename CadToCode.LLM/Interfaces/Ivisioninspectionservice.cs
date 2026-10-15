using CadToCode.LLM.Vision;

namespace CadToCode.LLM.Interfaces;

public interface IVisionInspectionService
{
    bool IsAvailable { get; }

    string ModelName { get; }

    Task<VisionInspectionResult?> InspectAsync(
        byte[] jpegBytes,
        string? userHint = null,
        CancellationToken cancellationToken = default);
}