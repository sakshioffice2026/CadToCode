using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Business.Interfaces;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Prompts;
using CadToCode.LLM.Services;
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
    private const int MaxComponentNotes = 50;

    private static readonly ConcurrentDictionary<int, (AssemblyGenerationResult Result, DateTime StoredUtc)> AssemblyCache = new();

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IGeometryExtractor> _extractors;
    private readonly IComponentDecomposer _decomposer;
    private readonly IAssemblyTopologyMapper _topologyMapper;
    private readonly IPatternMemoryService _memory;
    private readonly ILLMGenerationService _llm;
    private readonly IDrawingProcessingService _drawingProcessing;
    private readonly IStepScriptRunner _stepRunner;
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
        IDrawingProcessingService drawingProcessing,
        IStepScriptRunner stepRunner,
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
        _drawingProcessing = drawingProcessing;
        _stepRunner = stepRunner;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record ImagePipelineOutcome(string? Script, IReadOnlyList<string> Warnings, string? Error);

    private const string PipelineSidecarSuffix = ".pipeline.json";

    private static readonly ConcurrentDictionary<int, ImagePipelineOutcome> PipelineCache = new();

    private sealed class PipelineSidecar
    {
        public string? Script { get; set; }

        public List<string> Warnings { get; set; } = new();
    }

    private async Task<ImagePipelineOutcome?> ReadPipelineSidecarAsync(string storedPath, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _storage.ReadTextAsync(storedPath + PipelineSidecarSuffix, cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<PipelineSidecar>(json);

            if (saved is null || string.IsNullOrWhiteSpace(saved.Script))
            {
                return null;
            }

            return new ImagePipelineOutcome(saved.Script, saved.Warnings ?? new List<string>(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The saved image analysis could not be read.");
            return null;
        }
    }

    private async Task WritePipelineSidecarAsync(string storedPath, ImagePipelineOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(new PipelineSidecar
            {
                Script = outcome.Script,
                Warnings = outcome.Warnings.ToList()
            });

            await _storage.WriteTextAsync(storedPath + PipelineSidecarSuffix, json, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The image analysis could not be saved for reuse.");
        }
    }

    private async Task<ImagePipelineOutcome> RunImagePipelineAsync(
        int projectId,
        string title,
        string? originalFileName,
        KeyDimensionsDto? keyDimensions,
        CancellationToken cancellationToken)
    {
        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new ImagePipelineOutcome(null, Array.Empty<string>(), "The stored file path is missing.");
        }

        var useCache = keyDimensions is not { HasAny: true };

        if (useCache)
        {
            if (PipelineCache.TryGetValue(projectId, out var memoryHit))
            {
                return memoryHit;
            }

            var diskHit = await ReadPipelineSidecarAsync(path, cancellationToken);

            if (diskHit is not null)
            {
                PipelineCache[projectId] = diskHit;
                return diskHit;
            }
        }

        await using var stream = await _storage.OpenReadAsync(path, cancellationToken);

        if (stream is null)
        {
            return new ImagePipelineOutcome(null, Array.Empty<string>(), "The stored file could not be opened.");
        }

        var fileName = string.IsNullOrWhiteSpace(originalFileName) ? Path.GetFileName(path) : originalFileName;

        var processed = await _drawingProcessing.ProcessAsync(stream, fileName, title, 6000, cancellationToken, keyDimensions);

        if (!processed.Success)
        {
            return new ImagePipelineOutcome(null, processed.Warnings, processed.Error);
        }

        var script = processed.Mode == DrawingProcessingMode.TemplateInjection
            ? (processed.InjectedScript ?? processed.TemplateScript)
            : null;

        if (string.IsNullOrWhiteSpace(script))
        {
            return new ImagePipelineOutcome(
                null,
                processed.Warnings,
                "No verified template matched this image, so no geometry was generated. Pixel guessing is disabled for images. Upload a DXF file, or add a clearer drawing with dimension labels.");
        }

        var warnings = processed.Warnings.ToList();

        if (!string.IsNullOrWhiteSpace(processed.TemplateScript)
            && !string.Equals(script, processed.TemplateScript, StringComparison.Ordinal))
        {
            var check = await _stepRunner.RunAsync(script, cancellationToken);

            if (!check.Success)
            {
                warnings.Add("The adjusted template did not build (" + (check.Message ?? "unknown error") + "). The unmodified template is shown instead.");
                script = processed.TemplateScript;
            }
            else if (!check.IsConnected)
            {
                warnings.Add("The adjusted template produced parts that float apart (" + string.Join(", ", check.IsolatedParts) + "). The unmodified template is shown instead.");
                script = processed.TemplateScript;
            }
        }

        var finished = new ImagePipelineOutcome(script, warnings, null);

        if (useCache)
        {
            PipelineCache[projectId] = finished;
            await WritePipelineSidecarAsync(path, finished, cancellationToken);
        }

        return finished;
    }

    private async Task<AssemblyGenerationResult> BuildImageAssemblyAsync(
        AssemblyGenerationResult result,
        ProjectResultDto project,
        KeyDimensionsDto? keyDimensions,
        CancellationToken cancellationToken)
    {
        result.ClassName = AssemblyPromptTemplates.ToIdentifier(project.Title, "Assembly");

        var outcome = await RunImagePipelineAsync(project.Id, project.Title, project.OriginalFileName, keyDimensions, cancellationToken);

        result.Warnings.AddRange(outcome.Warnings);

        if (outcome.Script is null)
        {
            result.Success = false;
            result.Error = outcome.Error ?? "No STEP script could be built from this image.";
            return result;
        }

        result.Success = true;
        result.Source = AssemblyCodeSource.Deterministic;
        result.Code = outcome.Script;
        result.Confidence = 1.00m;
        result.FallbackReason = "Template-based, approximate. Built from a verified template with OCR and user-supplied values. Pixel geometry is not used for images.";

        return result;
    }

    public async Task<string> GenerateAsync(int projectId, CancellationToken cancellationToken = default, KeyDimensionsDto? keyDimensions = null)
    {
        var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            return "// The project was not found.";
        }

        if (project.SourceFileType == SourceFileType.Image)
        {
            try
            {
                var outcome = await RunImagePipelineAsync(projectId, project.Title, project.OriginalFileName, keyDimensions, cancellationToken);

                return outcome.Script
                    ?? "// " + (outcome.Error ?? "No STEP script could be built from this image.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image pipeline failed for project {ProjectId}.", projectId);
                return "// The image could not be processed, so no STEP script was generated.";
            }
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
            _logger.LogWarning(ex, "Drawing geometry could not be read for project {ProjectId}.", projectId);
            return "// The drawing could not be read, so no STEP script could be generated.";
        }

        if (geometry.PrimitiveCount == 0 && geometry.Texts.Count == 0 && geometry.Dimensions.Count == 0)
        {
            return "// No drawing entities were found for this project.";
        }

        var components = await _unitOfWork.Components.GetByProjectIdAsync(projectId, cancellationToken);
        var notes = DescribeComponents(components);
        var scriptName = AssemblyPromptTemplates.ToIdentifier(project.Title, "Drawing");

        try
        {
            var recognition = Analyze(geometry).Recognition;

            if (recognition.HasFeatures)
            {
                return ScriptEmitter.Emit(
                    recognition,
                    scriptName,
                    new ScriptEmitterOptions
                    {
                        Scale = ScaleToMillimeter(geometry.DetectedUnit),
                        OriginX = geometry.Bounds?.MinX ?? 0.0,
                        OriginY = geometry.Bounds?.MinY ?? 0.0,
                        Notes = notes
                    });
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Feature recognition failed for project {ProjectId}. Using the contour script.", projectId);
        }

        return AssemblyCodeBuilder.BuildGeometryScript(geometry, scriptName, notes);
    }

    public static GeometryFeatureContext DescribeFeatures(ExtractedGeometry geometry)
    {
        if (geometry is null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        var (profiles, recognition) = Analyze(geometry);
        var scale = ScaleToMillimeter(geometry.DetectedUnit);

        var profileLines = new List<string>();

        for (var i = 0; i < profiles.Profiles.Count; i++)
        {
            var profile = profiles.Profiles[i];
            var b = profile.Bounds;

            profileLines.Add(
                "Profile" + (i + 1).ToString(CultureInfo.InvariantCulture) + ": "
                + FormatNumber((b.MaxX - b.MinX) * scale) + " x " + FormatNumber((b.MaxY - b.MinY) * scale)
                + ", " + (profile.IsCircle ? "circle" : profile.HasArcs ? "lines and arcs" : "straight lines")
                + ", " + profile.HoleCount + (profile.HoleCount == 1 ? " hole" : " holes")
                + ", area " + FormatNumber(profile.Area * scale * scale));
        }

        if (profiles.OpenChains.Count > 0 || profiles.AxisEntities.Count > 0)
        {
            profileLines.Add(
                profiles.OpenChains.Count + " open chains, " + profiles.AxisEntities.Count + " centrelines");
        }

        var featureLines = new List<string>();
        var parameterLines = new List<string>();

        foreach (var feature in recognition.Features)
        {
            featureLines.Add(DescribeFeature(feature, scale));

            foreach (var parameter in feature.Parameters)
            {
                parameterLines.Add(DescribeParameter(parameter.Name, parameter.Value * scale, parameter.IsAssumed));
            }

            for (var h = 0; h < feature.Holes.Count; h++)
            {
                parameterLines.Add(DescribeParameter(
                    char.ToLowerInvariant(feature.Name[0]) + feature.Name[1..] + "Hole"
                    + (h + 1).ToString(CultureInfo.InvariantCulture) + "Diameter",
                    feature.Holes[h].Diameter * scale,
                    false));
            }
        }

        var kinds = recognition.Features
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key)
            .Select(g => g.Key + " x" + g.Count().ToString(CultureInfo.InvariantCulture))
            .ToList();

        return new GeometryFeatureContext
        {
            Profiles = profileLines,
            Features = featureLines,
            Parameters = parameterLines,
            KindCounts = kinds
        };
    }

    private static (ProfileBuildResult Profiles, FeatureRecognitionResult Recognition) Analyze(ExtractedGeometry geometry)
    {
        var profiles = ProfileBuilder.Build(DrawingEntity.FromGeometry(geometry));
        var recognition = FeatureRecognizer.Recognize(profiles);

        if (!recognition.HasFeatures && geometry.Hatches.Count > 0)
        {
            var withHatches = ProfileBuilder.Build(DrawingEntity.FromGeometry(geometry, includeHatchBoundaries: true));
            var hatchRecognition = FeatureRecognizer.Recognize(withHatches);

            if (hatchRecognition.HasFeatures)
            {
                return (withHatches, hatchRecognition);
            }
        }

        return (profiles, recognition);
    }

    private static string DescribeFeature(RecognizedFeature feature, double scale)
    {
        var text = feature.Name + ": ";

        switch (feature.Kind)
        {
            case RecognizedFeatureKind.Extrusion:
                {
                    var b = feature.Profile!.Bounds;

                    text += "extrusion of a closed profile " + FormatNumber((b.MaxX - b.MinX) * scale) + " x "
                        + FormatNumber((b.MaxY - b.MinY) * scale) + " mm";

                    if (feature.Holes.Count > 0)
                    {
                        text += ", " + feature.Holes.Count + " round holes (diameters "
                            + string.Join(", ", feature.Holes.Select(h => FormatNumber(h.Diameter * scale))) + ")";
                    }

                    if (feature.OtherHoleCount > 0)
                    {
                        text += ", " + feature.OtherHoleCount + " other cut-outs";
                    }

                    return text + ", depth parameter " + feature.DepthParameter!.Name;
                }

            case RecognizedFeatureKind.Cylinder:
                {
                    var diameter = feature.Profile!.Outer.CircleArc!.Radius * 2.0 * scale;

                    return text + "cylinder of diameter " + FormatNumber(diameter) + " mm, height parameter " + feature.DepthParameter!.Name;
                }

            case RecognizedFeatureKind.Revolve:
                {
                    var b = feature.Profile!.Bounds;

                    return text + "revolve of a half-section, max radius " + FormatNumber(b.MaxX * scale)
                        + " mm, axial length " + FormatNumber((b.MaxY - b.MinY) * scale) + " mm";
                }

            default:
                {
                    var length = feature.Centerline.Sum(s => s.Length) * scale;

                    return text + "pipe along a centreline of " + feature.Centerline.Count + " segments, length " + FormatNumber(length)
                        + " mm, outer diameter " + FormatNumber(feature.OuterDiameterParameter!.Value * scale)
                        + " mm, wall " + FormatNumber(feature.WallParameter!.Value * scale)
                        + (feature.WallParameter.IsAssumed ? " mm (assumed)" : " mm");
                }
        }
    }

    private static string DescribeParameter(string name, double value, bool isAssumed)
    {
        return name + " = " + FormatNumber(value) + (isAssumed ? " (assumed)" : " (measured)");
    }

    private static double ScaleToMillimeter(DrawingUnit unit)
    {
        return unit switch
        {
            DrawingUnit.Centimeter => 10.0,
            DrawingUnit.Meter => 1000.0,
            DrawingUnit.Inch => 25.4,
            DrawingUnit.Foot => 304.8,
            _ => 1.0
        };
    }

    public async Task<AssemblyGenerationResult> GenerateAssemblyAsync(int projectId, bool useAi, CancellationToken cancellationToken = default, KeyDimensionsDto? keyDimensions = null)
    {
        var hasKeyDimensions = keyDimensions is { HasAny: true };

        if (!useAi && !hasKeyDimensions && AssemblyCache.TryGetValue(projectId, out var cached))
        {
            return cached.Result;
        }

        var built = await BuildAssemblyAsync(projectId, useAi, keyDimensions, cancellationToken);

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

    private async Task<AssemblyGenerationResult> BuildAssemblyAsync(int projectId, bool useAi, KeyDimensionsDto? keyDimensions, CancellationToken cancellationToken)
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

            if (project.SourceFileType == SourceFileType.Image)
            {
                return await BuildImageAssemblyAsync(result, project, keyDimensions, cancellationToken);
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
                    DrawingTitle = project.Title,
                    Texts = geometry.Texts
                        .Select(t => t.Text)
                        .Concat(geometry.Dimensions.Select(d => d.Text ?? string.Empty))
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList(),
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
            result.Success = true;
            result.Source = AssemblyCodeSource.Ai;
            result.Code = AssemblyCodeBuilder.Build(tree, analysis, className);
            result.Confidence = analysis.Confidence;
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

    private static List<string> DescribeComponents(IEnumerable<ComponentDto> components)
    {
        var notes = new List<string>();

        foreach (var component in components
                     .Where(c => IsSupported(c.ComponentType))
                     .OrderBy(c => c.SequenceIndex)
                     .Take(MaxComponentNotes))
        {
            var parts = new List<string>();

            AddPart(parts, "Width", component.Width);
            AddPart(parts, "Height", component.Height);
            AddPart(parts, "Diameter", component.Diameter);
            AddPart(parts, "TopDiameter", component.TopDiameter);
            AddPart(parts, "BottomDiameter", component.BottomDiameter);
            AddPart(parts, "Angle", component.Angle);

            if (component.Profile is not null)
            {
                parts.Add("BoundingWidth " + FormatNumber(component.Profile.BoundingWidth));
                parts.Add("BoundingHeight " + FormatNumber(component.Profile.BoundingHeight));
            }

            if (component.HasHatch)
            {
                parts.Add("Hatched");
            }

            notes.Add(parts.Count == 0
                ? component.ComponentType.ToString()
                : component.ComponentType + ": " + string.Join(", ", parts));
        }

        return notes;
    }

    private static void AddPart(List<string> parts, string name, decimal? value)
    {
        if (value is null)
        {
            return;
        }

        parts.Add(name + " " + value.Value.ToString("0.####", CultureInfo.InvariantCulture));
    }

    private static string FormatNumber(double value)
    {
        return Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
    }
}