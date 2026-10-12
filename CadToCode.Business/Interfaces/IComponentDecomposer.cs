using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IComponentDecomposer
{
    List<AssemblyNode> Decompose(ExtractedGeometry geometry);
}