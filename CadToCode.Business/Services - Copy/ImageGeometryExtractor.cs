using System.Globalization;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CadToCode.Business.Services;

/// <summary>
/// Read-only proposal for the raster ingestion stage. It extracts local OCR annotations,
/// detects line/curve/loop features, estimates a pixel-to-drawing-unit scale when a
/// dimension can be associated with a nearby feature, then maps the result into the
/// same ExtractedGeometry DTO consumed by the existing classifier and code generator.
/// </summary>
public sealed class ImageGeometryExtractor : IGeometryExtractor
{
    private const int MaxWorkingDimension = 1200;
    private const long MaxPixels = 40_000_000;
    private const double MinCalloutAssociationConfidence = 0.40;

    private readonly IImageTextRecognizer? _textRecognizer;
    private readonly ILogger<ImageGeometryExtractor>? _logger;

    // Optional parameters keep the extractor usable in existing tests/tools that construct
    // it directly. Production DI should register IImageTextRecognizer to enable OCR.
    public ImageGeometryExtractor(
        IImageTextRecognizer? textRecognizer = null,
        ILogger<ImageGeometryExtractor>? logger = null)
    {
        _textRecognizer = textRecognizer;
        _logger = logger;
    }

    public bool CanHandle(SourceFileType fileType) => fileType == SourceFileType.Image;

    public async Task<ExtractedGeometry> ExtractAsync(
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        await using var memory = new MemoryStream();
        await fileStream.CopyToAsync(memory, cancellationToken);
        var imageBytes = memory.ToArray();
        if (imageBytes.Length == 0)
        {
            throw new InvalidOperationException("The uploaded image is empty.");
        }

        ImageInfo info;
        try
        {
            using var identifyStream = new MemoryStream(imageBytes, writable: false);
            info = Image.Identify(identifyStream)
                ?? throw new InvalidOperationException("The image format could not be identified.");
        }
        catch (Exception ex) when (ex is UnknownImageFormatException
                                   or InvalidImageContentException
                                   or NotSupportedException)
        {
            throw new InvalidOperationException("The image could not be read.", ex);
        }

        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
        {
            throw new InvalidOperationException("The image resolution is invalid or too large.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RecognizedWord> words = Array.Empty<RecognizedWord>();
        if (_textRecognizer is not null)
        {
            try
            {
                words = await _textRecognizer.RecognizeAsync(imageBytes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing OCR runtime should reduce annotation detail, not prevent line analysis.
                _logger?.LogWarning(ex, "OCR failed for the uploaded image; proceeding with visual features only.");
            }
        }

        var annotations = ImageAnnotationParser.Parse(words);

        using var image = Image.Load<Rgba32>(imageBytes);
        var originalWidth = image.Width;
        var originalHeight = image.Height;
        var workingWidth = originalWidth;
        var workingHeight = originalHeight;
        var longest = Math.Max(workingWidth, workingHeight);

        if (longest > MaxWorkingDimension)
        {
            var ratio = (double)MaxWorkingDimension / longest;
            workingWidth = Math.Max(1, (int)Math.Round(workingWidth * ratio));
            workingHeight = Math.Max(1, (int)Math.Round(workingHeight * ratio));
            image.Mutate(context => context.Resize(workingWidth, workingHeight));
        }

        var scaleX = workingWidth / (double)originalWidth;
        var scaleY = workingHeight / (double)originalHeight;
        var gray = ImageFeatureExtractor.ToGray(image);
        var textRegions = words
            .Where(w => w.Width > 0 && w.Height > 0)
            .Select(w => new PixelRegion(
                Math.Clamp((int)Math.Floor(w.X * scaleX), 0, workingWidth - 1),
                Math.Clamp((int)Math.Floor(w.Y * scaleY), 0, workingHeight - 1),
                Math.Max(1, (int)Math.Ceiling(w.Width * scaleX)),
                Math.Max(1, (int)Math.Ceiling(w.Height * scaleY))))
            .ToList();

        var features = ImageFeatureExtractor.Analyze(
            gray,
            workingWidth,
            workingHeight,
            textRegions,
            cancellationToken);

        var warnings = new List<string>();
        if (words.Count == 0)
        {
            warnings.Add("No text was recognized. Geometry is based on visual features only.");
        }

        var pixelsPerUnit = EstimatePixelsPerUnit(features, annotations, scaleX, scaleY, warnings);
        if (pixelsPerUnit is null)
        {
            // Preserve the detected geometry rather than substituting a canned shape. A scale
            // of 1 keeps coordinates numerically usable, but is explicitly marked uncalibrated.
            pixelsPerUnit = 1.0;
            warnings.Add("No reliable dimension-to-feature match was found. Coordinates remain in pixel-equivalent units; verify scale before manufacturing.");
        }

        var output = new ExtractedGeometry
        {
            DetectedUnit = annotations.DetectedUnit ?? DrawingUnit.Millimeter,
            Warnings = warnings
        };

        GeometryPoint ToPoint(double px, double py)
        {
            // Center the origin and flip image Y so the geometry uses a conventional upward Y axis.
            var x = (px - workingWidth / 2.0) / pixelsPerUnit.Value;
            var y = (workingHeight / 2.0 - py) / pixelsPerUnit.Value;
            return new GeometryPoint(Round(x), Round(y));
        }

        foreach (var segment in features.Segments)
        {
            if (segment.Length < 2.0)
            {
                continue;
            }

            output.Lines.Add(new GeometryLine
            {
                Start = ToPoint(segment.X1, segment.Y1),
                End = ToPoint(segment.X2, segment.Y2),
                Layer = "image/edge"
            });
        }

        foreach (var loop in features.Loops)
        {
            if (loop.Points.Count < 3)
            {
                continue;
            }

            var polyline = new GeometryPolyline { IsClosed = true, Layer = "image/outline" };
            foreach (var point in loop.Points)
            {
                polyline.Vertices.Add(new GeometryVertex(
                    ToPoint(point.X, point.Y).X,
                    ToPoint(point.X, point.Y).Y));
            }

            output.Polylines.Add(polyline);
        }

        foreach (var circle in features.Circles)
        {
            if (circle.Radius <= 1.0)
            {
                continue;
            }

            output.Circles.Add(new GeometryCircle
            {
                Center = ToPoint(circle.CenterX, circle.CenterY),
                Radius = Round(circle.Radius / pixelsPerUnit.Value),
                Layer = "image/circle"
            });
        }

        foreach (var arc in features.Arcs)
        {
            if (arc.Radius <= 1.0)
            {
                continue;
            }

            output.Arcs.Add(new GeometryArc
            {
                Center = ToPoint(arc.CenterX, arc.CenterY),
                Radius = Round(arc.Radius / pixelsPerUnit.Value),
                StartAngle = NormalizeDegrees(360.0 - arc.StartDegrees),
                EndAngle = NormalizeDegrees(360.0 - arc.EndDegrees),
                Layer = "image/arc"
            });
        }

        AddTextAndDimensions(output, words, annotations, scaleX, scaleY, ToPoint, pixelsPerUnit.Value);
        output.Bounds = ComputeBounds(output);
        output.Warnings.AddRange(features.Notes.Where(n => !string.IsNullOrWhiteSpace(n)));

        if (output.PrimitiveCount == 0 && output.Texts.Count == 0 && output.Dimensions.Count == 0)
        {
            throw new InvalidOperationException("No usable drawing geometry or dimension text was detected in the image.");
        }

        return output;
    }

    private static double? EstimatePixelsPerUnit(
        ImageFeatures features,
        ImageAnnotationResult annotations,
        double scaleX,
        double scaleY,
        List<string> warnings)
    {
        var estimates = new List<(double Scale, double Weight)>();
        var diagonal = Math.Sqrt((double)features.Width * features.Width + (double)features.Height * features.Height);

        foreach (var callout in annotations.Callouts)
        {
            if (callout.Value is not > 0 || callout.Confidence < MinCalloutAssociationConfidence)
            {
                continue;
            }

            var cx = callout.CenterX * scaleX;
            var cy = callout.CenterY * scaleY;

            if (callout.Kind == ImageCalloutKind.Diameter && features.Circles.Count > 0)
            {
                var candidate = features.Circles
                    .Select(c => new
                    {
                        Circle = c,
                        Distance = Distance(cx, cy, c.CenterX, c.CenterY)
                    })
                    .OrderBy(c => c.Distance)
                    .First();

                // Reject remote callouts: a drawing can contain multiple unrelated circles.
                if (candidate.Distance <= Math.Max(40.0, Math.Min(diagonal * 0.20, candidate.Circle.Radius * 8.0)))
                {
                    AddEstimate(2.0 * candidate.Circle.Radius / callout.Value.Value,
                        callout.Confidence / (1.0 + candidate.Distance / Math.Max(1.0, diagonal)));
                }
            }
            else if (callout.Kind == ImageCalloutKind.Radius)
            {
                var circleCandidate = features.Circles
                    .Select(c => new { Radius = c.Radius, Distance = Distance(cx, cy, c.CenterX, c.CenterY) })
                    .Where(c => c.Distance <= Math.Max(40.0, diagonal * 0.20))
                    .OrderBy(c => c.Distance)
                    .FirstOrDefault();

                var arcCandidate = features.Arcs
                    .Select(a => new { Radius = a.Radius, Distance = Distance(cx, cy, a.CenterX, a.CenterY) })
                    .Where(a => a.Distance <= Math.Max(40.0, diagonal * 0.20))
                    .OrderBy(a => a.Distance)
                    .FirstOrDefault();

                var best = circleCandidate is null ? arcCandidate
                    : arcCandidate is null || circleCandidate.Distance <= arcCandidate.Distance ? circleCandidate : arcCandidate;
                if (best is not null)
                {
                    AddEstimate(best.Radius / callout.Value.Value,
                        callout.Confidence / (1.0 + best.Distance / Math.Max(1.0, diagonal)));
                }
            }
            else if (callout.Kind == ImageCalloutKind.Linear)
            {
                var candidate = features.Segments
                    .Where(s => s.Length >= 12.0)
                    .Select(s => new
                    {
                        Segment = s,
                        Distance = Distance(cx, cy, (s.X1 + s.X2) / 2.0, (s.Y1 + s.Y2) / 2.0)
                    })
                    .OrderBy(s => s.Distance)
                    .FirstOrDefault();

                // Linear labels are often close to dimension lines. The association is a
                // heuristic; keep the resulting scale only if multiple observations agree.
                if (candidate is not null && candidate.Distance <= Math.Max(35.0, diagonal * 0.08))
                {
                    AddEstimate(candidate.Segment.Length / callout.Value.Value,
                        callout.Confidence / (1.0 + candidate.Distance / Math.Max(1.0, diagonal)));
                }
            }
        }

        // A single guessed pairing is too fragile for manufacturing. Require two estimates
        // to be reasonably consistent; otherwise leave the result explicitly uncalibrated.
        if (estimates.Count < 2)
        {
            return null;
        }

        var ordered = estimates.OrderBy(e => e.Scale).ToList();
        var median = ordered.Count % 2 == 1
            ? ordered[ordered.Count / 2].Scale
            : (ordered[ordered.Count / 2 - 1].Scale + ordered[ordered.Count / 2].Scale) / 2.0;
        var inliers = ordered.Where(e => Math.Abs(e.Scale - median) / median <= 0.15).ToList();

        if (inliers.Count < 2)
        {
            warnings.Add("Detected dimension-to-feature scale estimates disagree; scale was not auto-calibrated.");
            return null;
        }

        var totalWeight = inliers.Sum(e => e.Weight);
        if (totalWeight <= 0)
        {
            return null;
        }

        return inliers.Sum(e => e.Scale * e.Weight) / totalWeight;

        void AddEstimate(double scale, double weight)
        {
            if (double.IsFinite(scale) && scale > 0 && double.IsFinite(weight) && weight > 0)
            {
                estimates.Add((scale, weight));
            }
        }
    }

    private static void AddTextAndDimensions(
        ExtractedGeometry output,
        IReadOnlyList<RecognizedWord> words,
        ImageAnnotationResult annotations,
        double scaleX,
        double scaleY,
        Func<double, double, GeometryPoint> toPoint,
        double pixelsPerUnit)
    {
        foreach (var word in words)
        {
            var centerX = (word.X + word.Width / 2.0) * scaleX;
            var centerY = (word.Y + word.Height / 2.0) * scaleY;
            var tokens = ImageAnnotationParser.BuildTokens(word.Text);
            output.Texts.Add(new GeometryText
            {
                Text = word.Text,
                Position = toPoint(centerX, centerY),
                Height = Round(word.Height * scaleY / pixelsPerUnit),
                EntityType = "IMAGE_OCR",
                Tokens = tokens.Tokens,
                Numbers = tokens.Numbers,
                Layer = "image/text"
            });
        }

        foreach (var callout in annotations.Callouts.Where(c => c.Value.HasValue))
        {
            var centerX = callout.CenterX * scaleX;
            var centerY = callout.CenterY * scaleY;
            output.Dimensions.Add(new GeometryDimension
            {
                Text = callout.Text,
                Value = callout.Value,
                Position = toPoint(centerX, centerY),
                Layer = "image/dimension"
            });
        }
    }

    private static GeometryBounds? ComputeBounds(ExtractedGeometry geometry)
    {
        var xs = new List<double>();
        var ys = new List<double>();

        foreach (var line in geometry.Lines)
        {
            xs.Add(line.Start.X); xs.Add(line.End.X);
            ys.Add(line.Start.Y); ys.Add(line.End.Y);
        }

        foreach (var polyline in geometry.Polylines)
        {
            foreach (var vertex in polyline.Vertices)
            {
                xs.Add(vertex.X); ys.Add(vertex.Y);
            }
        }

        foreach (var circle in geometry.Circles)
        {
            xs.Add(circle.Center.X - circle.Radius); xs.Add(circle.Center.X + circle.Radius);
            ys.Add(circle.Center.Y - circle.Radius); ys.Add(circle.Center.Y + circle.Radius);
        }

        foreach (var arc in geometry.Arcs)
        {
            // Conservative bounds; this is sufficient for downstream profile normalization.
            xs.Add(arc.Center.X - arc.Radius); xs.Add(arc.Center.X + arc.Radius);
            ys.Add(arc.Center.Y - arc.Radius); ys.Add(arc.Center.Y + arc.Radius);
        }

        if (xs.Count == 0 || ys.Count == 0)
        {
            return null;
        }

        return new GeometryBounds(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static double NormalizeDegrees(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }
}
