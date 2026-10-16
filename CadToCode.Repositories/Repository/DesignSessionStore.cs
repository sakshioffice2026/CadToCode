using System.Text.Json;
using CadToCode.Core.Services;
using CadToCode.Database;
using Microsoft.EntityFrameworkCore;
using DomainSession = CadToCode.Core.Domain.DesignSession;
using EntitySession = CadToCode.Database.Entities.DesignSession;

namespace CadToCode.Repositories.Repository;

public sealed class DesignSessionStore : IDesignSessionStore
{
    private readonly ApplicationDbContext _context;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public DesignSessionStore(ApplicationDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<DomainSession?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.DesignSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : Deserialize(entity);
    }

    public async Task<DomainSession> CreateAsync(
        DomainSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entity = new EntitySession
        {
            Id = session.Id == Guid.Empty ? Guid.NewGuid() : session.Id,
            UserId = "system",
            EquipmentType = session.EquipmentType.ToString(),
            State = session.State.ToString(),
            InputsJson = JsonSerializer.Serialize(session, JsonOptions),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.DesignSessions.Add(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Deserialize(entity);
    }

    public async Task UpdateAsync(
        DomainSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entity = await _context.DesignSessions
            .FirstOrDefaultAsync(x => x.Id == session.Id, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            throw new KeyNotFoundException(
                $"Design session '{session.Id}' was not found.");
        }

        entity.EquipmentType = session.EquipmentType.ToString();
        entity.State = session.State.ToString();
        entity.InputsJson = JsonSerializer.Serialize(session, JsonOptions);
        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.DesignSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
            return false;

        _context.DesignSessions.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<IReadOnlyList<DomainSession>> ListByUserAsync(
        string userId,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var entities = await _context.DesignSessions
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(Deserialize).ToList();
    }

    private static DomainSession Deserialize(EntitySession entity)
    {
        if (string.IsNullOrWhiteSpace(entity.InputsJson))
        {
            throw new InvalidOperationException(
                $"Design session '{entity.Id}' has no serialized domain session.");
        }

        return JsonSerializer.Deserialize<DomainSession>(
                   entity.InputsJson, JsonOptions)
               ?? throw new InvalidOperationException(
                   $"Could not deserialize design session '{entity.Id}'.");
    }
}