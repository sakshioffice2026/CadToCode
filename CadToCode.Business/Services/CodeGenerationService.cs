using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Business.Interfaces;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Prompts;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using CadToCode.Repositories.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public class CodeGenerationService : ICodeGenerationService
{
    private static readonly JsonSerializerOptions TreeJsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private const int MaxCachedAssemblies = 20;

    private static readonly ConcurrentDictionary<int, (AssemblyGenerationResult Result, DateTime StoredUtc)> AssemblyCache = new();

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IGeometryExtractor> _extractors;
    private readonly IComponentDecomposer _decomposer;
    private readonly IAssemblyTopologyMapper _topologyMapper;
    private readonly IPatternMemoryService _memory;
    private readonly ILLMGenerationService _llm;
    private readonly LlmOptions _options;
    private readonly ILogger<CodeGenerationService> _logger;

    public CodeGenerationService(
        IUnitOfWork unitOfWork,
        IFileStorageService storage,
        IEnumerable<IGeometryExtractor> extractors,
        IComponentDecomposer decomposer,
        IAssemblyTopologyMapper topologyMapper,
        IPatternMemoryService memory,
        ILLMGenerationService llm,
        IOptions<LlmOptions> options,
        ILogger<CodeGenerationService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _extractors = extractors;
        _decomposer = decomposer;
        _topologyMapper = topologyMapper;
        _memory = memory;
        _llm = llm;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var components = await _unitOfWork.Components.GetByProjectIdAsync(projectId, cancellationToken);

        var supported = components
            .Where(c => IsSupported(c.ComponentType))
            .OrderBy(c => c.SequenceIndex)
            .ToList();

        if (supported.Count == 0)
        {
            return "// No supported components were found for this project.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("// All dimensions in millimeters, all angles in degrees.");
        builder.AppendLine();

        var typeCounts = supported
            .GroupBy(c => c.ComponentType)
            .ToDictionary(g => g.Key, g => g.Count());

        var typeIndexes = new Dictionary<ComponentType, int>();

        for (var i = 0; i < supported.Count; i++)
        {
            var component = supported[i];

            typeIndexes.TryGetValue(component.ComponentType, out var index);
            index++;
            typeIndexes[component.ComponentType] = index;

            var className = typeCounts[component.ComponentType] > 1
                ? $"{component.ComponentType}{index}"
                : component.ComponentType.ToString();

            AppendClass(builder, className, component);

            if (i < supported.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public async Task<AssemblyGenerationResult> GenerateAssemblyAsync(int projectId, bool useAi, CancellationToken cancellationToken = default)
    {
        if (!useAi && AssemblyCache.TryGetValue(projectId, out var cached))
        {
            return cached.Result;
        }

        var built = await BuildAssemblyAsync(projectId, useAi, cancellationToken);

        if (built.Success)
        {
            StoreInCache(projectId, built);
        }

        return built;
    }

    private static void StoreInCache(int projectId, AssemblyGenerationResult result)
    {
        AssemblyCache[projectId] = (result, DateTime.UtcNow);

        while (AssemblyCache.Count > MaxCachedAssemblies)
        {
            var oldest = AssemblyCache.OrderBy(p => p.Value.StoredUtc).Select(p => p.Key).FirstOrDefault();
            if (!AssemblyCache.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private async Task<AssemblyGenerationResult> BuildAssemblyAsync(int projectId, bool useAi, CancellationToken cancellationToken)
    {
        var result = new AssemblyGenerationResult { ProjectId = projectId };

        ProjectResultDto? project;
        ExtractedGeometry geometry;

        try
        {
            project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
            if (project is null)
            {
                result.Error = "The project was not found.";
                return result;
            }

            geometry = await ExtractGeometryAsync(projectId, project.SourceFileType, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assembly geometry could not be read for project {ProjectId}.", projectId);
            result.Error = "The drawing could not be read for assembly analysis.";
            return result;
        }

        var className = AssemblyPromptTemplates.ToIdentifier(project.Title, "Assembly");
        result.ClassName = className;

        AssemblyTree tree;

        try
        {
            var nodes = _decomposer.Decompose(geometry);
            tree = _topologyMapper.Map(geometry, nodes);
            tree.Unit = geometry.DetectedUnit;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assembly decomposition failed for project {ProjectId}.", projectId);
            result.Error = "The drawing could not be decomposed into sub-components.";
            return result;
        }

        result.Tree = tree;
        result.Warnings.AddRange(tree.Warnings);

        if (tree.Nodes.Count == 0)
        {
            return UseFallback(result, null, "No sub-components were detected in the drawing.");
        }

        if (!useAi)
        {
            return UseFallback(result, null, "it has not been run for this drawing yet.");
        }

        if (!_llm.IsAvailable)
        {
            return UseFallback(result, null, "The local AI model is not available.");
        }

        try
        {
            var matches = _memory.Search(geometry);
            var hints = matches
                .Select(m => new ClassificationPatternHint
                {
                    ComponentType = m.Pattern.ComponentType,
                    Description = m.Pattern.Description,
                    Similarity = m.Similarity
                })
                .ToList();

            var minConfidence = (decimal)_options.MinConfidence;

            var analysis = await _llm.AnalyzeAssemblyAsync(
                new AssemblyAnalysisRequest
                {
                    Tree = tree,
                    TreeJson = JsonSerializer.Serialize(tree, TreeJsonOptions),
                    SimilarPatterns = hints
                },
                cancellationToken);

            if (!analysis.Success || analysis.Confidence < minConfidence)
            {
                _logger.LogWarning(
                    "AI analysis not used for project {ProjectId}: {Error}. Raw response: {Raw}",
                    projectId,
                    analysis.Error,
                    analysis.RawResponse);

                return UseFallback(result, null, analysis.Error ?? "The AI analysis confidence was too low.");
            }

            result.Analysis = analysis;

            var synthesis = await _llm.SynthesizeCodeAsync(
                new CodeSynthesisRequest
                {
                    Tree = tree,
                    Analysis = analysis,
                    ClassName = className,
                    SimilarPatterns = hints
                },
                cancellationToken);

            if (!synthesis.Success
                || synthesis.Confidence < minConfidence
                || string.IsNullOrWhiteSpace(synthesis.Code))
            {
                _logger.LogWarning(
                    "AI code not used for project {ProjectId}: {Error}. Raw response: {Raw}",
                    projectId,
                    synthesis.Error,
                    synthesis.RawResponse);

                return UseFallback(result, analysis, synthesis.Error ?? "The AI code confidence was too low.");
            }

            result.Success = true;
            result.Source = AssemblyCodeSource.Ai;
            result.Code = synthesis.Code;
            result.Confidence = Math.Min(analysis.Confidence, synthesis.Confidence);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI assembly generation failed for project {ProjectId}. Using the deterministic result.", projectId);
            return UseFallback(result, null, "The AI generation failed unexpectedly.");
        }
    }

    private AssemblyGenerationResult UseFallback(
        AssemblyGenerationResult result,
        AssemblyAnalysisResult? analysis,
        string reason)
    {
        result.Success = true;
        result.Source = AssemblyCodeSource.Deterministic;
        result.Analysis = analysis ?? result.Analysis;
        result.Code = AssemblyCodeBuilder.Build(result.Tree, analysis, result.ClassName);
        result.Confidence = 1.00m;
        result.FallbackReason = reason;
        return result;
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

    private static bool IsSupported(ComponentType type)
    {
        return type is not (ComponentType.Line or ComponentType.Arc or ComponentType.Circle or ComponentType.Hatch);
    }

    private static void AppendClass(StringBuilder builder, string className, ComponentDto component)
    {
        builder.AppendLine($"public class {className}");
        builder.AppendLine("{");

        switch (component.ComponentType)
        {
            case ComponentType.Cylinder:
                AppendProperty(builder, "Height", component.Height);
                AppendProperty(builder, "Diameter", component.Diameter);
                break;

            case ComponentType.Cone:
                AppendProperty(builder, "Angle", component.Angle);
                AppendProperty(builder, "TopDiameter", component.TopDiameter);
                AppendProperty(builder, "BottomDiameter", component.BottomDiameter);
                AppendProperty(builder, "Height", component.Height);
                break;

            case ComponentType.Door:
            case ComponentType.Window:
            case ComponentType.Duct:
                AppendProperty(builder, "Width", component.Width);
                AppendProperty(builder, "Height", component.Height);
                break;

            case ComponentType.AxialFan:
                AppendProperty(builder, "Diameter", component.Diameter);
                break;

            case ComponentType.Cyclone:
                AppendProperty(builder, "Height", component.Height);
                AppendProperty(builder, "Diameter", component.Diameter);
                AppendProperty(builder, "TopDiameter", component.TopDiameter);
                AppendProperty(builder, "BottomDiameter", component.BottomDiameter);
                AppendProperty(builder, "Angle", component.Angle);
                break;
        }

        builder.AppendLine($"    public bool HasHatch {{ get; set; }} = {(component.HasHatch ? "true" : "false")};");
        builder.AppendLine("}");
    }

    private static void AppendProperty(StringBuilder builder, string name, decimal? value)
    {
        if (value is null)
        {
            return;
        }

        var text = value.Value.ToString("0.####", CultureInfo.InvariantCulture);
        builder.AppendLine($"    public double {name} {{ get; set; }} = {text};");
    }
}