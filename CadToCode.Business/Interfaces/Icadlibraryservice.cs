using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface ICadLibraryService
{
    Task<CadLibraryIndexViewModel> ListAsync(
        string? searchText,
        string? category,
        string? fileType,
        CancellationToken cancellationToken = default);

    Task<CadLibraryItemDto?> GetAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<(byte[] Content, string FileName)?> GetFileAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<CadLibraryResultDto> AddAsync(
        CadLibraryUploadRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CadLibraryResultDto> UpdateAsync(
        CadLibraryUpdateRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CadLibraryResultDto> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<CadLibraryResultDto> SyncAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<CadLibrarySyncResultDto> SyncAllAsync(
        bool pendingAndFailedOnly,
        CancellationToken cancellationToken = default);
}