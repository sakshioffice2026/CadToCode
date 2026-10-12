using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IAssemblyTopologyMapper
{
    AssemblyTree Map(ExtractedGeometry geometry, IReadOnlyList<AssemblyNode> nodes);
}