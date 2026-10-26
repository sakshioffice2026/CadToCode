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

    /// <summary>Stores the serialized design state on the project record. Caller commits via the unit of work.</summary>
    Task SaveDesignStateAsync(int id, string designStateJson, int schemaVersion, CancellationToken cancellationToken = default);

    /// <summary>Reads only the serialized design state column. Returns null when none is stored.</summary>
    Task<string?> GetDesignStateJsonAsync(int id, CancellationToken cancellationToken = default);
}