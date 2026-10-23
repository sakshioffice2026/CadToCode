using CadToCode.Models.Dtos;

namespace CadToCode.Repositories.Interfaces;

public sealed class ZooFileContent
{
    public string Name { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public interface IZooGenerationRepository
{
    Task AddAsync(
        ZooJobDto job,
        CancellationToken cancellationToken = default);

    Task<ZooJobDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        ZooJobDto job,
        IReadOnlyList<ZooFileContent>? files,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ZooJobDto>> ListRecentAsync(
        int take,
        CancellationToken cancellationToken = default);

    Task<(byte[] Content, string FileName)?> GetFileAsync(
        Guid id,
        string fileName,
        CancellationToken cancellationToken = default);
}
