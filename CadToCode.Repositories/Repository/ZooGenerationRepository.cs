using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Models.Dtos;
using CadToCode.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CadToCode.Repositories.Repository;

public sealed class ZooGenerationRepository : IZooGenerationRepository
{
    private const int MaxErrorLength = 1000;

    private readonly ApplicationDbContext _context;

    public ZooGenerationRepository(ApplicationDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task AddAsync(
        ZooJobDto job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        _context.ZooGenerations.Add(new ZooGeneration
        {
            Id = Guid.Parse(job.Id),
            Prompt = job.Prompt,
            OutputFormat = job.OutputFormat,
            Status = job.Status,
            Error = Truncate(job.Error),
            Kcl = job.Kcl,
            CreatedAt = job.CreatedAt
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ZooJobDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.ZooGenerations
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var files = await _context.ZooGenerationFiles
            .AsNoTracking()
            .Where(f => f.GenerationId == id)
            .OrderBy(f => f.FileName)
            .Select(f => new ZooOutputFileDto { Name = f.FileName, SizeBytes = f.SizeBytes })
            .ToListAsync(cancellationToken);

        return ToDto(entity, files);
    }

    public async Task UpdateAsync(
        ZooJobDto job,
        IReadOnlyList<ZooFileContent>? files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var id = Guid.Parse(job.Id);

        var entity = await _context.ZooGenerations
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (entity is null)
        {
            return;
        }

        entity.Status = job.Status;
        entity.Error = Truncate(job.Error);
        entity.Kcl = job.Kcl;
        entity.UpdatedAt = DateTime.UtcNow;

        if (files is not null)
        {
            var existing = await _context.ZooGenerationFiles
                .Where(f => f.GenerationId == id)
                .ToListAsync(cancellationToken);

            if (existing.Count > 0)
            {
                _context.ZooGenerationFiles.RemoveRange(existing);
                await _context.SaveChangesAsync(cancellationToken);
            }

            foreach (var file in files)
            {
                _context.ZooGenerationFiles.Add(new ZooGenerationFile
                {
                    GenerationId = id,
                    FileName = file.Name,
                    SizeBytes = file.Content.LongLength,
                    Content = file.Content,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ZooJobDto>> ListRecentAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.ZooGenerations
            .AsNoTracking()
            .OrderByDescending(g => g.CreatedAt)
            .Take(Math.Max(1, take))
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Array.Empty<ZooJobDto>();
        }

        var ids = entities.Select(e => e.Id).ToList();

        var files = await _context.ZooGenerationFiles
            .AsNoTracking()
            .Where(f => ids.Contains(f.GenerationId))
            .Select(f => new { f.GenerationId, f.FileName, f.SizeBytes })
            .ToListAsync(cancellationToken);

        var byGeneration = files
            .GroupBy(f => f.GenerationId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(f => f.FileName)
                    .Select(f => new ZooOutputFileDto { Name = f.FileName, SizeBytes = f.SizeBytes })
                    .ToList());

        return entities
            .Select(e => ToDto(
                e,
                byGeneration.TryGetValue(e.Id, out var list) ? list : new List<ZooOutputFileDto>()))
            .ToList();
    }

    public async Task<(byte[] Content, string FileName)?> GetFileAsync(
        Guid id,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var file = await _context.ZooGenerationFiles
            .AsNoTracking()
            .Where(f => f.GenerationId == id && f.FileName == fileName)
            .Select(f => new { f.FileName, f.Content })
            .FirstOrDefaultAsync(cancellationToken);

        if (file is null)
        {
            return null;
        }

        return (file.Content, file.FileName);
    }

    private static ZooJobDto ToDto(ZooGeneration entity, List<ZooOutputFileDto> files)
    {
        return new ZooJobDto
        {
            Id = entity.Id.ToString("D"),
            Prompt = entity.Prompt,
            OutputFormat = entity.OutputFormat,
            Status = entity.Status,
            Error = entity.Error,
            Kcl = entity.Kcl,
            CreatedAt = DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc),
            Files = files
        };
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
    }
}
