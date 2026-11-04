using CadToCode.Business.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public class DefinitionLearningService : IDefinitionLearningService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IGeometryExtractor> _extractors;
    private readonly IPatternMemoryService _memory;
    private readonly ILogger<DefinitionLearningService> _logger;

    public DefinitionLearningService(
        IUnitOfWork unitOfWork,
        IFileStorageService storage,
        IEnumerable<IGeometryExtractor> extractors,
        IPatternMemoryService memory,
        ILogger<DefinitionLearningService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _extractors = extractors;
        _memory = memory;
        _logger = logger;
    }

    public async Task<int> ConfirmAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return 0;
        }

        var components = (await _unitOfWork.Components.GetByProjectIdAsync(projectId, cancellationToken))
            .Where(c => IsLearnable(c.ComponentType))
            .OrderBy(c => c.SequenceIndex)
            .ToList();

        if (components.Count == 0)
        {
            return 0;
        }

        ExtractedGeometry geometry;

        try
        {
            geometry = await ExtractGeometryAsync(projectId, project.SourceFileType, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geometry could not be read while saving definitions for project {ProjectId}.", projectId);
            return 0;
        }

        var learned = 0;

        foreach (var component in components)
        {
            try
            {
                _memory.Learn(geometry, ToExtracted(component), userConfirmed: true);
                learned++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Component {ComponentId} could not be saved to pattern memory.", component.Id);
            }
        }

        return learned;
    }

    private async Task<ExtractedGeometry> ExtractGeometryAsync(
        int projectId,
        SourceFileType fileType,
        CancellationToken cancellationToken)
    {
        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException("The stored file path is missing.");
        }

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(fileType))
            ?? throw new InvalidOperationException($"No extractor is available for {fileType} files.");

        await using var stream = await _storage.OpenReadAsync(path, cancellationToken)
            ?? throw new FileNotFoundException("The stored file could not be opened.");

        return await extractor.ExtractAsync(stream, cancellationToken);
    }

    private static bool IsLearnable(ComponentType type)
    {
        return type is not (ComponentType.Line or ComponentType.Arc or ComponentType.Circle or ComponentType.Hatch);
    }

    private static ExtractedComponent ToExtracted(ComponentDto component)
    {
        return new ExtractedComponent
        {
            ComponentType = component.ComponentType,
            Name = component.Name,
            SequenceIndex = component.SequenceIndex,
            Layer = component.Layer,
            Height = component.Height,
            Width = component.Width,
            Diameter = component.Diameter,
            TopDiameter = component.TopDiameter,
            BottomDiameter = component.BottomDiameter,
            Angle = component.Angle,
            HasHatch = component.HasHatch,
            Confidence = 1.00m
        };
    }
}