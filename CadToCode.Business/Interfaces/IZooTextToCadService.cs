using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IZooTextToCadService
{
    bool IsConfigured { get; }

    Task<ZooActionResultDto> SubmitAsync(
        ZooGenerateRequestDto request,
        CancellationToken cancellationToken = default);

    Task<ZooActionResultDto> GetStatusAsync(
        string id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ZooJobDto>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<(byte[] Content, string FileName)?> GetFileAsync(
        string id,
        string fileName,
        CancellationToken cancellationToken = default);
}
