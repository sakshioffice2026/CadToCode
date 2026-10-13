using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IProjectProcessingService
{
    Task<UploadResultDto> ProcessUploadAsync(Stream fileStream, string originalFileName, CancellationToken cancellationToken = default);

    Task<ProjectResultDto?> GetProjectAsync(int projectId, CancellationToken cancellationToken = default);

    Task<List<ProjectHistoryItemDto>> GetHistoryAsync(int? take = null, CancellationToken cancellationToken = default);

    Task MarkOpenedAsync(int projectId, CancellationToken cancellationToken = default);

    Task<bool> DeleteProjectAsync(int projectId, CancellationToken cancellationToken = default);

    Task<bool> UpdateComponentAsync(ComponentDto component, CancellationToken cancellationToken = default);

    Task<Stream?> OpenSourceFileAsync(int projectId, CancellationToken cancellationToken = default);
}