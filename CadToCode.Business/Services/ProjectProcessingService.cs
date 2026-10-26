using CadToCode.Business.Interfaces;
using CadToCode.LLM.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public class ProjectProcessingService : IProjectProcessingService
{
    private const int MaxTitleLength = 200;
    private const int MaxFileNameLength = 260;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IGeometryExtractor> _extractors;
    private readonly IAiComponentClassifier _classifier;
    private readonly ICodeGenerationService _codeGeneration;
    private readonly IProjectHistoryIndex _historyIndex;
    private readonly ILogger<ProjectProcessingService> _logger;

    public ProjectProcessingService(
        IUnitOfWork unitOfWork,
        IFileStorageService storage,
        IEnumerable<IGeometryExtractor> extractors,
        IAiComponentClassifier classifier,
        ICodeGenerationService codeGeneration,
        IProjectHistoryIndex historyIndex,
        ILogger<ProjectProcessingService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _extractors = extractors;
        _classifier = classifier;
        _codeGeneration = codeGeneration;
        _historyIndex = historyIndex;
        _logger = logger;
    }

    public async Task<UploadResultDto> ProcessUploadAsync(Stream fileStream, string originalFileName, CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(originalFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new UploadResultDto { Success = false, ErrorMessage = "A file is required." };
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var fileType = extension == ".dxf" ? SourceFileType.Dxf : SourceFileType.Image;

        string storedPath;
        try
        {
            storedPath = await _storage.SaveAsync(fileStream, fileName, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return new UploadResultDto { Success = false, ErrorMessage = ex.Message };
        }

        var fileSize = await GetStoredFileSizeAsync(storedPath, cancellationToken);

        UploadResultDto result;

        try
        {
            await _unitOfWork.BeginTransactionAsync(cancellationToken);

            var title = Path.GetFileNameWithoutExtension(fileName);
            var projectId = await _unitOfWork.Projects.AddAsync(
                new ProjectResultDto
                {
                    Title = Truncate(title, MaxTitleLength),
                    SourceFileType = fileType,
                    OriginalFileName = Truncate(fileName, MaxFileNameLength),
                    FileSizeBytes = fileSize,
                    Unit = DrawingUnit.Millimeter,
                    Status = ProjectStatus.Processing
                },
                storedPath,
                cancellationToken);

            string? errorMessage = null;

            try
            {
                if (fileType != SourceFileType.Image)
                {
                    await ExtractAndSaveAsync(projectId, fileType, storedPath, title, cancellationToken);
                }

                await _unitOfWork.Projects.UpdateStatusAsync(projectId, ProjectStatus.Processed, null, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extraction failed for project {ProjectId}.", projectId);
                errorMessage = ex.Message;
                await _unitOfWork.Projects.UpdateStatusAsync(projectId, ProjectStatus.Failed, errorMessage, cancellationToken);
            }

            await _unitOfWork.CommitAsync(cancellationToken);

            result = new UploadResultDto
            {
                Success = errorMessage is null,
                ProjectId = projectId,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload processing failed for file {FileName}.", fileName);

            await _unitOfWork.RollbackAsync(CancellationToken.None);
            await _storage.DeleteAsync(storedPath, CancellationToken.None);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new UploadResultDto { Success = false, ErrorMessage = "The upload could not be processed." };
        }

        if (result.Success && result.ProjectId.HasValue)
        {
            await RunAssemblyAnalysisAsync(result.ProjectId.Value, cancellationToken);
        }

        if (result.ProjectId.HasValue)
        {
            await SyncHistoryIndexAsync(result.ProjectId.Value, cancellationToken);
        }

        return result;
    }

    public Task<ProjectResultDto?> GetProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
    }

    public async Task<List<ProjectHistoryItemDto>> GetHistoryAsync(int? take = null, CancellationToken cancellationToken = default)
    {
        if (_historyIndex.IsConfigured)
        {
            try
            {
                var indexed = await _historyIndex.ListAsync(take, cancellationToken);
                if (indexed.Count > 0)
                {
                    var existing = (await _unitOfWork.Projects.ListHistoryAsync(null, cancellationToken))
                        .Select(r => r.Id)
                        .ToHashSet();

                    indexed = indexed.Where(i => existing.Contains(i.Id)).ToList();

                    if (indexed.Count > 0)
                    {
                        return indexed;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Project history could not be read from Qdrant. Falling back to MySQL.");
            }
        }

        var rows = await _unitOfWork.Projects.ListHistoryAsync(take, cancellationToken);

        if (_historyIndex.IsConfigured && rows.Count > 0)
        {
            await BackfillHistoryIndexAsync(take, cancellationToken);
        }

        return rows;
    }

    public async Task<bool> RenameProjectAsync(int projectId, string title, CancellationToken cancellationToken = default)
    {
        var clean = (title ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return false;
        }

        clean = Truncate(clean, MaxTitleLength);

        var renamed = await _unitOfWork.Projects.RenameAsync(projectId, clean, cancellationToken);
        if (!renamed)
        {
            return false;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await SyncHistoryIndexAsync(projectId, cancellationToken);

        return true;
    }

    public async Task MarkOpenedAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await _unitOfWork.Projects.TouchLastOpenedAsync(projectId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var storedPath = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (storedPath is null)
        {
            return false;
        }

        await _unitOfWork.Projects.DeleteAsync(projectId, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await RemoveFromHistoryIndexAsync(projectId, cancellationToken);

        try
        {
            await _storage.DeleteAsync(storedPath, cancellationToken);
            await _storage.DeleteAsync(storedPath + ChatSnapshotSuffix, cancellationToken);
            await _storage.DeleteAsync(storedPath + ".pipeline.json", cancellationToken);
            await _storage.DeleteAsync(storedPath + ".script.json", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Project {ProjectId} was removed but its stored file could not be deleted.", projectId);
        }

        return true;
    }

    public async Task<bool> UpdateComponentAsync(ComponentDto component, CancellationToken cancellationToken = default)
    {
        var existing = await _unitOfWork.Components.GetByIdAsync(component.Id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        await _unitOfWork.Components.UpdateAsync(component, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<Stream?> OpenSourceFileAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return await _storage.OpenReadAsync(path, cancellationToken);
    }

    private const string ChatSnapshotSuffix = ".chat.json";

    public async Task<bool> SaveChatSnapshotAsync(int projectId, string json, CancellationToken cancellationToken = default)
    {
        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        await _storage.WriteTextAsync(path + ChatSnapshotSuffix, json, cancellationToken);
        return true;
    }

    public async Task<string?> LoadChatSnapshotAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return await _storage.ReadTextAsync(path + ChatSnapshotSuffix, cancellationToken);
    }

    private async Task<long> GetStoredFileSizeAsync(string storedPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _storage.OpenReadAsync(storedPath, cancellationToken);
            return stream is { CanSeek: true } ? stream.Length : 0L;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the size of stored file {StoredPath}.", storedPath);
            return 0L;
        }
    }

    private async Task SyncHistoryIndexAsync(int projectId, CancellationToken cancellationToken)
    {
        if (!_historyIndex.IsConfigured)
        {
            return;
        }

        try
        {
            var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
            if (project is null)
            {
                await _historyIndex.DeleteAsync(projectId, cancellationToken);
                return;
            }

            await _historyIndex.UpsertAsync(
                new ProjectHistoryItemDto
                {
                    Id = project.Id,
                    Title = project.Title,
                    OriginalFileName = project.OriginalFileName,
                    SourceFileType = project.SourceFileType,
                    FileSizeBytes = project.FileSizeBytes,
                    Status = project.Status,
                    ErrorMessage = project.ErrorMessage,
                    ComponentCount = project.Components.Count,
                    CreatedAt = project.CreatedAt,
                    LastOpenedAt = project.LastOpenedAt
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Project {ProjectId} could not be synced to the Qdrant history index.", projectId);
        }
    }

    private async Task BackfillHistoryIndexAsync(int? take, CancellationToken cancellationToken)
    {
        try
        {
            var all = await _unitOfWork.Projects.ListHistoryAsync(null, cancellationToken);
            await _historyIndex.UpsertManyAsync(all, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Project history could not be backfilled into Qdrant.");
        }
    }

    private async Task RemoveFromHistoryIndexAsync(int projectId, CancellationToken cancellationToken)
    {
        if (!_historyIndex.IsConfigured)
        {
            return;
        }

        try
        {
            await _historyIndex.DeleteAsync(projectId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Project {ProjectId} could not be removed from the Qdrant history index.", projectId);
        }
    }

    private async Task RunAssemblyAnalysisAsync(int projectId, CancellationToken cancellationToken)
    {
        try
        {
            await _codeGeneration.GenerateAssemblyAsync(projectId, true, cancellationToken);
            await _codeGeneration.GenerateAsync(projectId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assembly analysis failed for project {ProjectId}. The rule-based result will be shown.", projectId);
        }
    }

    private async Task ExtractAndSaveAsync(int projectId, SourceFileType fileType, string storedPath, string title, CancellationToken cancellationToken)
    {
        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(fileType))
            ?? throw new InvalidOperationException($"No extractor is available for {fileType} files.");

        await using var stream = await _storage.OpenReadAsync(storedPath, cancellationToken)
            ?? throw new FileNotFoundException("The stored file could not be opened.");

        var geometry = await extractor.ExtractAsync(stream, cancellationToken);

        await _unitOfWork.Projects.UpdateDetectedUnitAsync(projectId, geometry.DetectedUnit, cancellationToken);

        var components = await _classifier.ExtractComponentsAsync(geometry, cancellationToken);
        if (components.Count == 0)
        {
            throw new InvalidOperationException("No component could be recognized in the drawing.");
        }

        ApplyTitleHint(components, title);

        await _unitOfWork.Components.AddRangeAsync(projectId, components, cancellationToken);
    }

    private static void ApplyTitleHint(List<ExtractedComponent> components, string title)
    {
        var resolved = ProfileKeywordResolver.Resolve(new string?[] { title });
        if (resolved is null)
        {
            return;
        }

        foreach (var component in components.Where(c => c.ComponentType is ComponentType.CustomProfile or ComponentType.Generic))
        {
            component.ComponentType = resolved.Value;
            component.Name = resolved.Value + " profile";
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}