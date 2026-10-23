using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Repositories.Interfaces;

public interface IProjectRepository
{
    Task<int> AddAsync(ProjectResultDto project, string storedFilePath, CancellationToken cancellationToken = default);

    Task<ProjectResultDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<string?> GetStoredFilePathAsync(int id, CancellationToken cancellationToken = default);

    Task<List<ProjectResultDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<List<ProjectHistoryItemDto>> ListHistoryAsync(int? take = null, CancellationToken cancellationToken = default);

    Task<bool> RenameAsync(int id, string title, CancellationToken cancellationToken = default);

    Task TouchLastOpenedAsync(int id, CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(int id, ProjectStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);

    Task UpdateDetectedUnitAsync(int id, DrawingUnit detectedUnit, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}