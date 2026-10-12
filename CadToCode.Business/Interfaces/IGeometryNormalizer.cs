using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IGeometryNormalizer
{
    List<ExtractedComponent> Normalize(ExtractedGeometry geometry);
}