using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Repositories.Repository;

public class DrawingComponentRepository : IDrawingComponentRepository
{
    private readonly ApplicationDbContext _context;

    public DrawingComponentRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task AddRangeAsync(int projectId, IEnumerable<ExtractedComponent> components, CancellationToken cancellationToken = default)
    {
        var entities = components.Select(c => ToEntity(projectId, c)).ToList();
        if (entities.Count > 0)
        {
            _context.DrawingComponents.AddRange(entities);
        }

        return Task.CompletedTask;
    }

    public async Task<List<ComponentDto>> GetByProjectIdAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var entities = await _context.DrawingComponents
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.SequenceIndex)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDto).ToList();
    }

    public async Task<ComponentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DrawingComponents
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        return entity is null ? null : ToDto(entity);
    }

    public async Task UpdateAsync(ComponentDto component, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DrawingComponents
            .FirstOrDefaultAsync(c => c.Id == component.Id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.Name = component.Name;
        entity.Height = component.Height;
        entity.Width = component.Width;
        entity.Diameter = component.Diameter;
        entity.TopDiameter = component.TopDiameter;
        entity.BottomDiameter = component.BottomDiameter;
        entity.Angle = component.Angle;
        entity.HasHatch = component.HasHatch;
        entity.IsManuallyEdited = true;
    }

    public async Task ReplaceNonEditedAsync(int projectId, IEnumerable<ExtractedComponent> components, CancellationToken cancellationToken = default)
    {
        var existing = await _context.DrawingComponents
            .Where(c => c.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        var editedIndexes = existing
            .Where(c => c.IsManuallyEdited)
            .Select(c => c.SequenceIndex)
            .ToHashSet();

        _context.DrawingComponents.RemoveRange(existing.Where(c => !c.IsManuallyEdited));

        var newEntities = components
            .Where(c => !editedIndexes.Contains(c.SequenceIndex))
            .Select(c => ToEntity(projectId, c))
            .ToList();

        _context.DrawingComponents.AddRange(newEntities);
    }

    public async Task DeleteByProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var existing = await _context.DrawingComponents
            .Where(c => c.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        _context.DrawingComponents.RemoveRange(existing);
    }

    private static DrawingComponent ToEntity(int projectId, ExtractedComponent component)
    {
        return new DrawingComponent
        {
            ProjectId = projectId,
            ComponentType = component.ComponentType.ToString(),
            Name = component.Name,
            SequenceIndex = component.SequenceIndex,
            Layer = component.Layer,
            Height = component.Height,
            Width = component.Width,
            Diameter = component.Diameter,
            TopDiameter = component.TopDiameter,
            BottomDiameter = component.BottomDiameter,
            Angle = component.Angle,
            HasHatch = component.HasHatch,
            IsManuallyEdited = false,
            Confidence = component.Confidence,
            RawDataJson = component.RawDataJson,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static ComponentDto ToDto(DrawingComponent entity)
    {
        return new ComponentDto
        {
            Id = entity.Id,
            ProjectId = entity.ProjectId,
            ComponentType = Enum.Parse<ComponentType>(entity.ComponentType, true),
            Name = entity.Name,
            SequenceIndex = entity.SequenceIndex,
            Layer = entity.Layer,
            Height = entity.Height,
            Width = entity.Width,
            Diameter = entity.Diameter,
            TopDiameter = entity.TopDiameter,
            BottomDiameter = entity.BottomDiameter,
            Angle = entity.Angle,
            HasHatch = entity.HasHatch,
            IsManuallyEdited = entity.IsManuallyEdited,
            Profile = StructuralProfile.FromRawJson(entity.RawDataJson),
            Confidence = entity.Confidence,
            CreatedAt = entity.CreatedAt
        };
    }
}