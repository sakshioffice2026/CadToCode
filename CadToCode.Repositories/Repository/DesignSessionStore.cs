using CadToCode.Core.Domain;
using CadToCode.Core.Services;
using CadToCode.Core.Workflow;
using CadToCode.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CadToCode.Repositories
{
    public class DesignSessionStore : IDesignSessionStore
    {
        private readonly ApplicationDbContext _context;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public DesignSessionStore(ApplicationDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<DesignSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var entity = await _context.DesignSessions
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

            return entity is null ? null : ToDomain(entity);
        }

        public async Task<DesignSession> SaveAsync(DesignSession session, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);

            var entity = new SessionEntity { Id = session.Id };
            Apply(entity, session);
            await _context.DesignSessions.AddAsync(entity, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return ToDomain(entity);
        }

        public async Task UpdateAsync(DesignSession session, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);

            var entity = await _context.DesignSessions
                .SingleOrDefaultAsync(x => x.Id == session.Id, cancellationToken)
                ?? throw new KeyNotFoundException($"Design session '{session.Id}' was not found.");

            Apply(entity, session);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var entity = await _context.DesignSessions
                .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

            if (entity is null)
                return false;

            _context.DesignSessions.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<IReadOnlyList<DesignSession>> ListByUserAsync(
            string userId,
            int skip = 0,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return Array.Empty<DesignSession>();

            var entities = await _context.DesignSessions
                .AsNoTracking()
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.UpdatedAt)
                .Skip(Math.Max(0, skip))
                .Take(Math.Clamp(take, 1, 200))
                .ToListAsync(cancellationToken);

            return entities.Select(ToDomain).ToArray();
        }

        private static void Apply(SessionEntity entity, DesignSession session)
        {
            entity.UserId = session.OwnerId;
            entity.EquipmentType = session.EquipmentType.ToString();
            entity.State = session.State.ToString();
            entity.InputsJson = JsonSerializer.Serialize(session.Inputs, JsonOptions);
            entity.ResultJson = JsonSerializer.Serialize(new StoredSessionDetails(
                session.Title,
                session.SourceProjectId,
                session.Revisions,
                session.Approvals), JsonOptions);
            entity.RejectionReason = null;
            entity.CreatedAt = session.CreatedUtc.UtcDateTime;
            entity.UpdatedAt = session.UpdatedUtc.UtcDateTime;
        }

        private static DesignSession ToDomain(SessionEntity entity)
        {
            var equipmentType = Enum.Parse<EquipmentType>(entity.EquipmentType, ignoreCase: true);
            var state = Enum.Parse<DesignWorkflowState>(entity.State, ignoreCase: true);
            var inputs = string.IsNullOrWhiteSpace(entity.InputsJson)
                ? new Dictionary<string, double>()
                : JsonSerializer.Deserialize<Dictionary<string, double>>(entity.InputsJson, JsonOptions)
                  ?? new Dictionary<string, double>();

            var details = string.IsNullOrWhiteSpace(entity.ResultJson)
                ? null
                : JsonSerializer.Deserialize<StoredSessionDetails>(entity.ResultJson, JsonOptions);

            return DesignSession.Restore(
                id: entity.Id,
                ownerId: entity.UserId,
                title: details?.Title ?? "Untitled design",
                equipmentType: equipmentType,
                state: state,
                createdUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc)),
                updatedUtc: new DateTimeOffset(DateTime.SpecifyKind(entity.UpdatedAt, DateTimeKind.Utc)),
                sourceProjectId: details?.SourceProjectId,
                inputs: inputs,
                revisions: details?.Revisions ?? Array.Empty<DesignRevision>(),
                approvals: details?.Approvals ?? Array.Empty<StageApproval>());
        }

        private sealed record StoredSessionDetails(
            string Title,
            int? SourceProjectId,
            IReadOnlyList<DesignRevision> Revisions,
            IReadOnlyList<StageApproval> Approvals);
    }
}