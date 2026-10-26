using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IProjectProcessingService
{
    Task<UploadResultDto> ProcessUploadAsync(Stream fileStream, string originalFileName, CancellationToken cancellationToken = default);

    Task<ProjectResultDto?> GetProjectAsync(int projectId, CancellationToken cancellationToken = default);

    Task<List<ProjectHistoryItemDto>> GetHistoryAsync(int? take = null, CancellationToken cancellationToken = default);

    Task<bool> RenameProjectAsync(int projectId, string title, CancellationToken cancellationToken = default);

    Task MarkOpenedAsync(int projectId, CancellationToken cancellationToken = default);

    Task<bool> DeleteProjectAsync(int projectId, CancellationToken cancellationToken = default);

    Task<bool> UpdateComponentAsync(ComponentDto component, CancellationToken cancellationToken = default);

    Task<Stream?> OpenSourceFileAsync(int projectId, CancellationToken cancellationToken = default);

    Task<bool> SaveChatSnapshotAsync(int projectId, string json, CancellationToken cancellationToken = default);

    Task<string?> LoadChatSnapshotAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>Persists the design state to MySQL and mirrors it to the Qdrant point payload. Backend only.</summary>
    Task<bool> SaveDesignStateAsync(int projectId, DesignStateDocument state, CancellationToken cancellationToken = default);

    /// <summary>Loads the stored design state (Qdrant first, MySQL fallback). Returns null when none exists.</summary>
    Task<DesignStateDocument?> LoadDesignStateAsync(int projectId, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the design state from the current project components, keeping inputs, calculation and edit history.</summary>
    Task<DesignStateDocument?> RefreshDesignStateAsync(int projectId, CancellationToken cancellationToken = default);
}