using System.Text.Json;
using CadToCode.Core.Domain;
using CadToCode.Core.Services;
using CadToCode.Core.Workflow;
using CadToCode.Database;
using Microsoft.EntityFrameworkCore;
using CoreSession = CadToCode.Core.Domain.DesignSession;
using SessionEntity = CadToCode.Database.Entities.DesignSession;

namespace CadToCode.Repositories.Repository;

public sealed class DesignSessionStore : IDesignSessionStore
{
    private const int MaxReasonLength = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _context;

    public DesignSessionStore(ApplicationDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<CoreSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DesignSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<CoreSession> CreateAsync(CoreSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Id == Guid.Empty)
            session.Id = Guid.NewGuid();

        var entity = new SessionEntity();
        Apply(entity, session);
        entity.Id = session.Id;

        _context.DesignSessions.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDomain(entity);
    }

    public async Task UpdateAsync(CoreSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entity = await _context.DesignSessions
            .FirstOrDefaultAsync(s => s.Id == session.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Design session '{session.Id}' was not found.");

        Apply(entity, session);
        entity.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.DesignSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (entity is null)
            return false;

        _context.DesignSessions.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CoreSession>> ListByUserAsync(
        string userId,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Array.Empty<CoreSession>();

        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, 200);

        var entities = await _context.DesignSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDomain).ToList();
    }

    private static void Apply(SessionEntity entity, CoreSession session)
    {
        entity.UserId = session.UserId;
        entity.EquipmentType = session.EquipmentType.ToString();
        entity.State = session.State.ToString();
        entity.InputsJson = session.Inputs is null
            ? null
            : JsonSerializer.Serialize(session.Inputs, JsonOptions);
        entity.ResultJson = session.Result is null
            ? null
            : JsonSerializer.Serialize(session.Result, JsonOptions);
        entity.RejectionReason = Truncate(session.RejectionReason, MaxReasonLength);
        entity.CreatedAt = session.CreatedUtc == default ? DateTime.UtcNow : session.CreatedUtc;
        entity.UpdatedAt = session.UpdatedUtc == default ? DateTime.UtcNow : session.UpdatedUtc;
    }

    private static CoreSession ToDomain(SessionEntity entity)
    {
        return new CoreSession
        {
            Id = entity.Id,
            UserId = entity.UserId,
            EquipmentType = Enum.Parse<EquipmentType>(entity.EquipmentType, ignoreCase: true),
            State = Enum.Parse<DesignWorkflowState>(entity.State, ignoreCase: true),
            Inputs = string.IsNullOrWhiteSpace(entity.InputsJson)
                ? new Dictionary<string, double>()
                : JsonSerializer.Deserialize<Dictionary<string, double>>(entity.InputsJson, JsonOptions)
                  ?? new Dictionary<string, double>(),
            Result = string.IsNullOrWhiteSpace(entity.ResultJson)
                ? null
                : JsonSerializer.Deserialize<CalculationResult>(entity.ResultJson, JsonOptions),
            RejectionReason = entity.RejectionReason,
            CreatedUtc = DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc),
            UpdatedUtc = DateTime.SpecifyKind(entity.UpdatedAt, DateTimeKind.Utc)
        };
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
