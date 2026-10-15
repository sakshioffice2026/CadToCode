using CadToCode.Core.Domain;

namespace CadToCode.Core.Services;

/// <summary>
/// Persistence abstraction for <see cref="DesignSession"/>.
/// Defined in Core so Core carries no database dependency;
/// implemented in CadToCode.Repositories.
/// </summary>
public interface IDesignSessionStore
{
    Task<DesignSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<DesignSession> CreateAsync(DesignSession session, CancellationToken cancellationToken = default);

    Task UpdateAsync(DesignSession session, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DesignSession>> ListByUserAsync(
        string userId,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default);
}
