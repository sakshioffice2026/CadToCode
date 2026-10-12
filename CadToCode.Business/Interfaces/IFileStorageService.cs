namespace CadToCode.Business.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveAsync(Stream fileStream, string originalFileName, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}