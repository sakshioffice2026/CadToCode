using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Interfaces;

public interface IGeometryExtractor
{
    bool CanHandle(SourceFileType fileType);
    Task<ExtractedGeometry> ExtractAsync(Stream fileStream, CancellationToken cancellationToken = default);
}