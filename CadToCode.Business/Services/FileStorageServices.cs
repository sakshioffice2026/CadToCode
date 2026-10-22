using CadToCode.Business.Interfaces;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public class FileStorageService : IFileStorageService
{
    private readonly UploadOptions _options;
    private readonly string _root;

    public FileStorageService(IOptions<UploadOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.StorageRoot))
        {
            throw new InvalidOperationException("UploadOptions:StorageRoot is not configured.");
        }

        _root = Path.GetFullPath(_options.StorageRoot);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream fileStream, string originalFileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(originalFileName)?.ToLowerInvariant() ?? string.Empty;

        if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed.");
        }

        var maxBytes = (long)_options.MaxFileSizeMb * 1024 * 1024;
        var relativePath = Path.Combine(DateTime.UtcNow.ToString("yyyyMM"), $"{Guid.NewGuid():N}{extension}");
        var fullPath = ResolveSafePath(relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        long total = 0;
        var buffer = new byte[81920];

        try
        {
            await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);

            int read;
            while ((read = await fileStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidOperationException($"File exceeds the {_options.MaxFileSizeMb} MB limit.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            throw;
        }

        return relativePath.Replace('\\', '/');
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveSafePath(relativePath);

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveSafePath(relativePath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public async Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveSafePath(relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveSafePath(relativePath);

        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullPath, cancellationToken);
    }

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Path is required.", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Invalid file path.");
        }

        return fullPath;
    }
}