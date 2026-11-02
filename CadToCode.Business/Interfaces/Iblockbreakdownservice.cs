using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IBlockBreakdownService
{
    Task<ManufacturingBreakdownDto> BuildBreakdownAsync(int projectId, CancellationToken cancellationToken = default);
}