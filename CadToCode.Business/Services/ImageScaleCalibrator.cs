using CadToCode.Business.Services;

namespace CadToCode.Business.Services;

/// <summary>
/// Estimates the number of source-image pixels per model unit from OCR dimensions
/// and detected geometry. This is deliberately conservative: one guessed match is
/// not enough to establish a manufacturing scale.
///
/// Read-only proposal for CadToCode; this file has not been added to the repository.
/// </summary>
public static class ImageScaleCalibrator
{
    private const double MinimumAssociationConfidence = 0.40;
    private const double AgreementTolerance = 0.15;

    /// <summary>
    /// Returns pixels per drawing unit, or null when there is insufficient reliable
    /// evidence. scaleX/scaleY transform OCR coordinates from original-image pixels
    /// into the resized working-image coordinate system used by ImageFeatureExtractor.
    /// </summary>
    public static double? EstimatePixelsPerUnit(
        ImageFeatures features,
        ImageAnnotationResult annotations,
        double scaleX,
        double scaleY,
        List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(warnings);

        if (!double.IsFinite(scaleX) || scaleX <= 0 ||
            !double.IsFinite(scaleY) || scaleY <= 0)
        {
            warnings.Add("Image resize factors are invalid; drawing scale could not be calibrated.");
            return null;
        }

        var estimates = new List<(double PixelsPerUnit, double Weight)>();
        var diagonal = Math.Sqrt((double)features.Width * features.Width + (double)features.Height * features.Height);
        if (!double.IsFinite(diagonal) || diagonal <= 0)
        {
            warnings.Add("Image bounds are invalid; drawing scale could not be calibrated.");
            return null;
        }

        foreach (var callout in annotations.Callouts)
        {
            if (callout.Value is not > 0 ||
                !double.IsFinite(callout.Value.Value) ||
                callout.Confidence < MinimumAssociationConfidence)
            {
                continue;
            }

            var centerX = callout.CenterX * scaleX;
            var centerY = callout.CenterY * scaleY;

            switch (callout.Kind)
            {
                case ImageCalloutKind.Diameter when features.Circles.Count > 0:
                {
                    var candidate = features.Circles
                        .Select(circle => new
                        {
                            Circle = circle,
                            Distance = Distance(centerX, centerY, circle.CenterX, circle.CenterY)
                        })
                        .OrderBy(item => item.Distance)
                        .First();

                    var maxDistance = Math.Max(40.0, Math.Min(diagonal * 0.20, candidate.Circle.Radius * 8.0));
                    if (candidate.Distance <= maxDistance)
                    {
                        Add(2.0 * candidate.Circle.Radius / callout.Value.Value,
                            callout.Confidence / (1.0 + candidate.Distance / diagonal));
                    }
                    break;
                }

                case ImageCalloutKind.Radius:
                {
                    var circle = features.Circles
                        .Select(item => new
                        {
                            Radius = item.Radius,
                            Distance = Distance(centerX, centerY, item.CenterX, item.CenterY)
                        })
                        .Where(item => item.Distance <= Math.Max(40.0, diagonal * 0.20))
                        .OrderBy(item => item.Distance)
                        .FirstOrDefault();

                    var arc = features.Arcs
                        .Select(item => new
                        {
                            Radius = item.Radius,
                            Distance = Distance(centerX, centerY, item.CenterX, item.CenterY)
                        })
                        .Where(item => item.Distance <= Math.Max(40.0, diagonal * 0.20))
                        .OrderBy(item => item.Distance)
                        .FirstOrDefault();

                    var bestRadius = circle is null ? arc
                        : arc is null || circle.Distance <= arc.Distance ? circle : arc;

                    if (bestRadius is not null)
                    {
                        Add(bestRadius.Radius / callout.Value.Value,
                            callout.Confidence / (1.0 + bestRadius.Distance / diagonal));
                    }
                    break;
                }

                case ImageCalloutKind.Linear:
                {
                    // A linear label may sit beside a dimension line rather than the
                    // measured edge. This proximity association is only a candidate;
                    // independent agreeing observations are required below.
                    var candidate = features.Segments
                        .Where(segment => segment.Length >= 12.0)
                        .Select(segment => new
                        {
                            Segment = segment,
                            Distance = Distance(centerX, centerY,
                                (segment.X1 + segment.X2) / 2.0,
                                (segment.Y1 + segment.Y2) / 2.0)
                        })
                        .OrderBy(item => item.Distance)
                        .FirstOrDefault();

                    if (candidate is not null && candidate.Distance <= Math.Max(35.0, diagonal * 0.08))
                    {
                        Add(candidate.Segment.Length / callout.Value.Value,
                            callout.Confidence / (1.0 + candidate.Distance / diagonal));
                    }
                    break;
                }
            }
        }

        if (estimates.Count < 2)
        {
            return null;
        }

        var sorted = estimates.OrderBy(item => item.PixelsPerUnit).ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1
            ? sorted[middle].PixelsPerUnit
            : (sorted[middle - 1].PixelsPerUnit + sorted[middle].PixelsPerUnit) / 2.0;

        if (!double.IsFinite(median) || median <= 0)
        {
            return null;
        }

        var inliers = sorted
            .Where(item => Math.Abs(item.PixelsPerUnit - median) / median <= AgreementTolerance)
            .ToList();

        if (inliers.Count < 2)
        {
            warnings.Add("Detected dimension-to-feature scale estimates disagree; scale was not auto-calibrated.");
            return null;
        }

        var totalWeight = inliers.Sum(item => item.Weight);
        if (!double.IsFinite(totalWeight) || totalWeight <= 0)
        {
            return null;
        }

        var weightedScale = inliers.Sum(item => item.PixelsPerUnit * item.Weight) / totalWeight;
        if (!double.IsFinite(weightedScale) || weightedScale <= 0)
        {
            return null;
        }

        return weightedScale;

        void Add(double pixelsPerUnit, double weight)
        {
            if (double.IsFinite(pixelsPerUnit) && pixelsPerUnit > 0 &&
                double.IsFinite(weight) && weight > 0)
            {
                estimates.Add((pixelsPerUnit, weight));
            }
        }
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
