using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Repositories.Repository;

public class ProjectRepository : IProjectRepository
{
    private const int MaxErrorMessageLength = 1000;

    private readonly ApplicationDbContext _context;

    public ProjectRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<int> AddAsync(ProjectResultDto project, string storedFilePath, CancellationToken cancellationToken = default)
    {
        var entity = new Project
        {
            Title = project.Title,
            SourceFileType = project.SourceFileType.ToString(),
            OriginalFileName = project.OriginalFileName,
            StoredFilePath = storedFilePath,
            FileSizeBytes = project.FileSizeBytes,
            DetectedUnit = project.DetectedUnit?.ToString(),
            Unit = project.Unit.ToString(),
            ScaleMmPerPixel = project.ScaleMmPerPixel,
            Status = project.Status.ToString(),
            ErrorMessage = Truncate(project.ErrorMessage),
            CreatedAt = DateTime.UtcNow
        };

        _context.Projects.Add(entity);

        // Flush here only to obtain the generated Id; the surrounding transaction stays open.
        await _context.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<ProjectResultDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects
            .AsNoTracking()
            .Include(p => p.DrawingComponents)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var dto = ToDto(entity);
        dto.Components = entity.DrawingComponents
            .OrderBy(c => c.SequenceIndex)
            .Select(ToComponentDto)
            .ToList();

        return dto;
    }

    public async Task<string?> GetStoredFilePathAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Projects
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.StoredFilePath)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<ProjectResultDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDto).ToList();
    }

    public async Task<List<ProjectHistoryItemDto>> ListHistoryAsync(int? take = null, CancellationToken cancellationToken = default)
    {
        IQueryable<Project> query = _context.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id);

        if (take.HasValue && take.Value > 0)
        {
            query = query.Take(take.Value);
        }

        var rows = await query
            .Select(p => new
            {
                p.Id,
                p.Title,
                p.OriginalFileName,
                p.SourceFileType,
                p.FileSizeBytes,
                p.Status,
                p.ErrorMessage,
                ComponentCount = p.DrawingComponents.Count,
                p.CreatedAt,
                p.LastOpenedAt
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ProjectHistoryItemDto
        {
            Id = r.Id,
            Title = r.Title,
            OriginalFileName = r.OriginalFileName,
            SourceFileType = Enum.Parse<SourceFileType>(r.SourceFileType, true),
            FileSizeBytes = r.FileSizeBytes,
            Status = Enum.Parse<ProjectStatus>(r.Status, true),
            ErrorMessage = r.ErrorMessage,
            ComponentCount = r.ComponentCount,
            CreatedAt = r.CreatedAt,
            LastOpenedAt = r.LastOpenedAt
        }).ToList();
    }

    public async Task<bool> RenameAsync(int id, string title, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Title = title;
        return true;
    }

    public async Task TouchLastOpenedAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.LastOpenedAt = DateTime.UtcNow;
    }

    public async Task UpdateStatusAsync(int id, ProjectStatus status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = status.ToString();
        entity.ErrorMessage = status == ProjectStatus.Failed ? Truncate(errorMessage) : null;
    }

    public async Task UpdateDetectedUnitAsync(int id, DrawingUnit detectedUnit, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.DetectedUnit = detectedUnit.ToString();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        _context.Projects.Remove(entity);
    }

    public async Task SaveDesignStateAsync(int id, string designStateJson, int schemaVersion, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.DesignStateJson = designStateJson;
        entity.DesignStateSchemaVersion = schemaVersion;
        entity.DesignStateUpdatedAt = DateTime.UtcNow;
    }

    public async Task<string?> GetDesignStateJsonAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Projects
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.DesignStateJson)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= MaxErrorMessageLength ? value : value[..MaxErrorMessageLength];
    }

    private static ProjectResultDto ToDto(Project entity)
    {
        return new ProjectResultDto
        {
            Id = entity.Id,
            Title = entity.Title,
            SourceFileType = Enum.Parse<SourceFileType>(entity.SourceFileType, true),
            OriginalFileName = entity.OriginalFileName,
            FileSizeBytes = entity.FileSizeBytes,
            DetectedUnit = string.IsNullOrWhiteSpace(entity.DetectedUnit)
                ? null
                : Enum.Parse<DrawingUnit>(entity.DetectedUnit, true),
            Unit = Enum.Parse<DrawingUnit>(entity.Unit, true),
            ScaleMmPerPixel = entity.ScaleMmPerPixel,
            Status = Enum.Parse<ProjectStatus>(entity.Status, true),
            ErrorMessage = entity.ErrorMessage,
            CreatedAt = entity.CreatedAt,
            LastOpenedAt = entity.LastOpenedAt
        };
    }

    private static ComponentDto ToComponentDto(DrawingComponent entity)
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
            Confidence = entity.Confidence,
            CreatedAt = entity.CreatedAt
        };
    }
}