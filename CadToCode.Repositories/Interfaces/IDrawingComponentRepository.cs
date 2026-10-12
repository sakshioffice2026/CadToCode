using CadToCode.Models.Dtos;

namespace CadToCode.Repositories.Interfaces;

public interface IDrawingComponentRepository
{
    Task AddRangeAsync(int projectId, IEnumerable<ExtractedComponent> components, CancellationToken cancellationToken = default);

    Task<List<ComponentDto>> GetByProjectIdAsync(int projectId, CancellationToken cancellationToken = default);

    Task<ComponentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task UpdateAsync(ComponentDto component, CancellationToken cancellationToken = default);

    Task ReplaceNonEditedAsync(int projectId, IEnumerable<ExtractedComponent> components, CancellationToken cancellationToken = default);

    Task DeleteByProjectAsync(int projectId, CancellationToken cancellationToken = default);
}