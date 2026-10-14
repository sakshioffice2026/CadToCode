namespace CadToCode.Repositories.Interfaces;

public interface IUnitOfWork : IAsyncDisposable
{
    IProjectRepository Projects { get; }

    IDrawingComponentRepository Components { get; }

    ICadLibraryRepository CadLibrary { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}