using System.Globalization;
using System.Text.Json;
using CadToCode.Business.Interfaces;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public class AiComponentClassifier : IAiComponentClassifier
{
    private const double AxisToleranceDegrees = 3.0;

    private readonly ILLMGenerationService _llm;
    private readonly IPatternMemoryService _memory;
    private readonly IGeometryNormalizer _normalizer;
    private readonly ILogger<AiComponentClassifier> _logger;

    public AiComponentClassifier(
        ILLMGenerationService llm,
        IPatternMemoryService memory,
        IGeometryNormalizer normalizer,
        ILogger<AiComponentClassifier> logger)
    {
        _llm = llm;
        _memory = memory;
        _normalizer = normalizer;
        _logger = logger;
    }

    public async Task<List<ExtractedComponent>> ExtractComponentsAsync(
        ExtractedGeometry geometry,
        CancellationToken cancellationToken = default)
    {
        var deterministic = _normalizer.Normalize(geometry);

        if (!_llm.IsAvailable)
        {
            _logger.LogInformation("AI model is not available. Using the rule-based result.");
            return deterministic;
        }

        try
        {
            var matches = _memory.Search(geometry);
            var request = BuildRequest(geometry, deterministic, matches);

            var result = await _llm.ClassifyAsync(request, cancellationToken);

            if (!result.Success)
            {
                _logger.LogInformation("AI classification was not used: {Error}", result.Error);
                return deterministic;
            }

            if (!IsSupportedType(result.ComponentType))
            {
                _logger.LogInformation("AI returned unsupported type {Type}. Using the rule-based result.", result.ComponentType);
                return deterministic;
            }

            if (result.ComponentType is ComponentType.Cylinder or ComponentType.Cone
                && deterministic.Any(c => c.ComponentType == result.ComponentType))
            {
                return deterministic;
            }

            return new List<ExtractedComponent> { BuildComponent(result, geometry, matches) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI classification failed. Using the rule-based result.");
            return deterministic;
        }
    }

    private static ComponentClassificationRequest BuildRequest(
        ExtractedGeometry geometry,
        List<ExtractedComponent> deterministic,
        IReadOnlyList<PatternMatch> matches)
    {
        var scale = ScaleToMillimeter(geometry.DetectedUnit);
        var extents = ComputeExtents(geometry, scale);
        var (vertical, horizontal, slanted) = CountOrientations(geometry);

        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "Drawing units: {0}. All dimensions are in millimeters. Overall size: {1:0.#} wide x {2:0.#} high. "
            + "Entities: {3} lines ({4} vertical, {5} horizontal, {6} slanted), {7} arcs, {8} circles, {9} hatches, {10} dimensions, {11} texts. "
            + "File version: {12}.",
            geometry.DetectedUnit,
            extents.Width,
            extents.Height,
            geometry.Lines.Count,
            vertical,
            horizontal,
            slanted,
            geometry.Arcs.Count,
            geometry.Circles.Count,
            geometry.Hatches.Count,
            geometry.Dimensions.Count,
            geometry.Texts.Count,
            geometry.SourceVersion ?? "unknown");

        var texts = geometry.Texts
            .Select(t => t.Text)
            .Concat(geometry.Dimensions.Select(d => d.Text ?? string.Empty))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var layers = geometry.Lines.Select(l => l.Layer)
            .Concat(geometry.Arcs.Select(a => a.Layer))
            .Concat(geometry.Circles.Select(c => c.Layer))
            .Concat(geometry.Hatches.Select(h => h.Layer))
            .Concat(geometry.Texts.Select(t => t.Layer))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new ComponentClassificationRequest
        {
            DrawingSummary = summary,
            Texts = texts,
            Layers = layers,
            SimilarPatterns = matches
                .Select(m => new ClassificationPatternHint
                {
                    ComponentType = m.Pattern.ComponentType,
                    Description = m.Pattern.Description,
                    Similarity = m.Similarity
                })
                .ToList(),
            DeterministicCandidate = deterministic.FirstOrDefault()
        };
    }

    private static ExtractedComponent BuildComponent(
        ComponentClassificationResult result,
        ExtractedGeometry geometry,
        IReadOnlyList<PatternMatch> matches)
    {
        var scale = ScaleToMillimeter(geometry.DetectedUnit);
        var extents = ComputeExtents(geometry, scale);

        var component = new ExtractedComponent
        {
            ComponentType = result.ComponentType,
            Name = result.Name,
            SequenceIndex = 1,
            Layer = null,
            Height = result.Height,
            Width = result.Width,
            Diameter = result.Diameter,
            TopDiameter = result.TopDiameter,
            BottomDiameter = result.BottomDiameter,
            Angle = result.Angle,
            HasHatch = geometry.Hatches.Count > 0,
            Confidence = result.Confidence
        };

        FillMissingFromExtents(component, extents);

        component.RawDataJson = JsonSerializer.Serialize(new
        {
            Source = "AI",
            result.Reasoning,
            Patterns = matches.Select(m => new
            {
                Type = m.Pattern.ComponentType.ToString(),
                Similarity = Math.Round(m.Similarity, 3)
            })
        });

        return component;
    }

    private static void FillMissingFromExtents(ExtractedComponent component, (double Width, double Height) extents)
    {
        if (extents.Width <= 0 || extents.Height <= 0)
        {
            return;
        }

        var width = ToDecimal(extents.Width);
        var height = ToDecimal(extents.Height);

        switch (component.ComponentType)
        {
            case ComponentType.Door:
            case ComponentType.Window:
            case ComponentType.Duct:
                component.Width ??= width;
                component.Height ??= height;
                break;

            case ComponentType.Cyclone:
                component.Height ??= height;
                break;

            case ComponentType.AxialFan:
                component.Diameter ??= Math.Min(width, height);
                break;
        }
    }

    private static bool IsSupportedType(ComponentType type)
    {
        return type is not (ComponentType.Line or ComponentType.Arc or ComponentType.Circle or ComponentType.Hatch);
    }

    private static (int Vertical, int Horizontal, int Slanted) CountOrientations(ExtractedGeometry geometry)
    {
        var vertical = 0;
        var horizontal = 0;
        var slanted = 0;

        foreach (var line in geometry.Lines)
        {
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;

            if (double.IsNaN(dx) || double.IsNaN(dy) || (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12))
            {
                continue;
            }

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI % 180.0;
            if (angle < 0)
            {
                angle += 180.0;
            }

            if (angle <= AxisToleranceDegrees || angle >= 180.0 - AxisToleranceDegrees)
            {
                horizontal++;
            }
            else if (Math.Abs(angle - 90.0) <= AxisToleranceDegrees)
            {
                vertical++;
            }
            else
            {
                slanted++;
            }
        }

        return (vertical, horizontal, slanted);
    }

    private static (double Width, double Height) ComputeExtents(ExtractedGeometry geometry, double scale)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var any = false;

        void Include(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                return;
            }

            any = true;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var line in geometry.Lines)
        {
            Include(line.Start.X, line.Start.Y);
            Include(line.End.X, line.End.Y);
        }

        foreach (var arc in geometry.Arcs)
        {
            Include(arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius);
            Include(arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius);
        }

        foreach (var circle in geometry.Circles)
        {
            Include(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius);
            Include(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius);
        }

        foreach (var hatch in geometry.Hatches)
        {
            foreach (var path in hatch.BoundaryPaths)
            {
                foreach (var point in path)
                {
                    Include(point.X, point.Y);
                }
            }
        }

        if (!any)
        {
            return (0, 0);
        }

        return ((maxX - minX) * scale, (maxY - minY) * scale);
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

    private static decimal ToDecimal(double value)
    {
        return (decimal)Math.Round(value, 4);
    }
}