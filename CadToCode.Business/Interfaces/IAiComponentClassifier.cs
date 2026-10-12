using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IAiComponentClassifier
{
    Task<List<ExtractedComponent>> ExtractComponentsAsync(
        ExtractedGeometry geometry,
        CancellationToken cancellationToken = default);
}