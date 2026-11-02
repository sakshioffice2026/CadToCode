using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IPartDxfExportService
{
    Task<PartDxfPackageDto> BuildPartFilesAsync(PartDxfRequestDto request, CancellationToken cancellationToken = default);

    Task<PartDxfFileDto?> BuildPartFileAsync(int projectId, PartDxfSpecDto spec, CancellationToken cancellationToken = default);

    Task<byte[]?> BuildPackageZipAsync(PartDxfRequestDto request, CancellationToken cancellationToken = default);
}