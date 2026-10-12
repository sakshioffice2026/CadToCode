using System.Text.Json;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public class GeometryNormalizer : IGeometryNormalizer
{
    private static readonly string[] IgnoredMarkers = { "center", "centre", "dim", "hidden", "phantom", "dash" };

    private readonly ExtractionOptions _options;

    public GeometryNormalizer(IOptions<ExtractionOptions> options)
    {
        _options = options.Value;
    }

    public List<ExtractedComponent> Normalize(ExtractedGeometry geometry)
    {
        var scale = GetScaleToMillimeter(geometry.DetectedUnit);

        var segments = geometry.Lines
            .Where(l => !IsIgnored(l.Layer, l.LineType))
            .Select(l => ToSegment(l, scale))
            .Where(s => s.Length > 0)
            .ToList();

        var vertical = segments.Where(s => AngleFromVertical(s) <= _options.ParallelLineToleranceDegrees).ToList();
        var slanted = segments
            .Where(s =>
            {
                var angle = AngleFromVertical(s);
                return angle > _options.ParallelLineToleranceDegrees && angle < 90.0 - _options.ParallelLineToleranceDegrees;
            })
            .ToList();

        var hatches = geometry.Hatches
            .Select(h => ToHatchCenter(h, scale))
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();

        var found = new List<(double CenterY, ExtractedComponent Component)>();

        var cylinder = FindCylinder(vertical);
        if (cylinder is not null)
        {
            var c = cylinder.Value;
            found.Add(((c.BottomY + c.TopY) / 2.0, new ExtractedComponent
            {
                ComponentType = ComponentType.Cylinder,
                Name = "Cylinder",
                Height = ToDecimal(c.Height),
                Diameter = ToDecimal(c.Diameter),
                HasHatch = HasHatchNear(hatches, c.CenterX - c.Diameter / 2.0, c.CenterX + c.Diameter / 2.0, c.BottomY, c.TopY),
                Confidence = 1.00m,
                RawDataJson = JsonSerializer.Serialize(new { c.Height, c.Diameter, c.CenterX, c.BottomY, c.TopY })
            }));
        }

        var cone = FindCone(slanted);
        if (cone is not null)
        {
            var c = cone.Value;
            var maxDiameter = Math.Max(c.TopDiameter, c.BottomDiameter);

            found.Add(((c.BottomY + c.TopY) / 2.0, new ExtractedComponent
            {
                ComponentType = ComponentType.Cone,
                Name = "Cone",
                Height = ToDecimal(c.Height),
                TopDiameter = ToDecimal(c.TopDiameter),
                BottomDiameter = ToDecimal(c.BottomDiameter),
                Angle = ToDecimal(c.Angle),
                HasHatch = HasHatchNear(hatches, c.CenterX - maxDiameter / 2.0, c.CenterX + maxDiameter / 2.0, c.BottomY, c.TopY),
                Confidence = 1.00m,
                RawDataJson = JsonSerializer.Serialize(new { c.Height, c.TopDiameter, c.BottomDiameter, c.Angle, c.CenterX, c.BottomY, c.TopY })
            }));
        }

        var ordered = found.OrderByDescending(f => f.CenterY).Select(f => f.Component).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SequenceIndex = i + 1;
        }

        return ordered;
    }

    private CylinderCandidate? FindCylinder(List<Segment> vertical)
    {
        CylinderCandidate? best = null;

        for (var i = 0; i < vertical.Count; i++)
        {
            for (var j = i + 1; j < vertical.Count; j++)
            {
                var a = vertical[i];
                var b = vertical[j];

                var ax = (a.TopX + a.BottomX) / 2.0;
                var bx = (b.TopX + b.BottomX) / 2.0;

                var left = ax <= bx ? a : b;
                var right = ax <= bx ? b : a;

                var leftX = (left.TopX + left.BottomX) / 2.0;
                var rightX = (right.TopX + right.BottomX) / 2.0;
                var diameter = rightX - leftX;

                if (diameter <= Tolerance(diameter))
                {
                    continue;
                }

                var topY = Math.Min(a.TopY, b.TopY);
                var bottomY = Math.Max(a.BottomY, b.BottomY);
                var overlap = topY - bottomY;

                if (overlap <= 0)
                {
                    continue;
                }

                var minLength = Math.Min(a.Length, b.Length);
                if (overlap < 0.8 * minLength)
                {
                    continue;
                }

                var candidate = new CylinderCandidate(overlap, diameter, (leftX + rightX) / 2.0, bottomY, topY);

                if (best is null
                    || candidate.Diameter > best.Value.Diameter + Tolerance(best.Value.Diameter)
                    || (Math.Abs(candidate.Diameter - best.Value.Diameter) <= Tolerance(best.Value.Diameter)
                        && candidate.Height > best.Value.Height))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private ConeCandidate? FindCone(List<Segment> slanted)
    {
        ConeCandidate? best = null;
        var bestScore = 0.0;

        for (var i = 0; i < slanted.Count; i++)
        {
            for (var j = i + 1; j < slanted.Count; j++)
            {
                var a = slanted[i];
                var b = slanted[j];

                var left = a.TopX + a.BottomX <= b.TopX + b.BottomX ? a : b;
                var right = ReferenceEquals(left, a) ? b : a;

                var height = Math.Min(left.TopY, right.TopY) - Math.Max(left.BottomY, right.BottomY);
                var spanHeight = Math.Max(left.TopY - left.BottomY, right.TopY - right.BottomY);

                if (spanHeight <= 0)
                {
                    continue;
                }

                var heightTolerance = Tolerance(spanHeight);

                if (Math.Abs(left.TopY - right.TopY) > heightTolerance
                    || Math.Abs(left.BottomY - right.BottomY) > heightTolerance)
                {
                    continue;
                }

                var topDiameter = right.TopX - left.TopX;
                var bottomDiameter = right.BottomX - left.BottomX;

                if (topDiameter < 0 || bottomDiameter < 0)
                {
                    continue;
                }

                var topCenter = (left.TopX + right.TopX) / 2.0;
                var bottomCenter = (left.BottomX + right.BottomX) / 2.0;
                var maxDiameter = Math.Max(topDiameter, bottomDiameter);

                if (maxDiameter <= 0 || Math.Abs(topCenter - bottomCenter) > Tolerance(maxDiameter))
                {
                    continue;
                }

                var topY = (left.TopY + right.TopY) / 2.0;
                var bottomY = (left.BottomY + right.BottomY) / 2.0;
                var coneHeight = topY - bottomY;

                if (coneHeight <= 0)
                {
                    continue;
                }

                var radialDifference = Math.Abs(topDiameter - bottomDiameter) / 2.0;
                var angle = Math.Atan2(radialDifference, coneHeight) * 180.0 / Math.PI;
                var score = maxDiameter * coneHeight;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = new ConeCandidate(coneHeight, topDiameter, bottomDiameter, angle, (topCenter + bottomCenter) / 2.0, bottomY, topY);
                }
            }
        }

        return best;
    }

    private static bool HasHatchNear(List<(double X, double Y)> hatchCenters, double minX, double maxX, double minY, double maxY)
    {
        var marginX = 0.15 * (maxX - minX);
        var marginY = 0.01 * (maxY - minY);

        return hatchCenters.Any(h =>
            h.X >= minX - marginX && h.X <= maxX + marginX &&
            h.Y >= minY - marginY && h.Y <= maxY + marginY);
    }

    private static (double X, double Y)? ToHatchCenter(GeometryHatch hatch, double scale)
    {
        var points = hatch.BoundaryPaths.SelectMany(p => p).ToList();
        if (points.Count == 0)
        {
            return null;
        }

        var x = (points.Min(p => p.X) + points.Max(p => p.X)) / 2.0 * scale;
        var y = (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2.0 * scale;

        return (x, y);
    }

    private static Segment ToSegment(GeometryLine line, double scale)
    {
        var startX = line.Start.X * scale;
        var startY = line.Start.Y * scale;
        var endX = line.End.X * scale;
        var endY = line.End.Y * scale;

        return startY >= endY
            ? new Segment(startX, startY, endX, endY)
            : new Segment(endX, endY, startX, startY);
    }

    private static double AngleFromVertical(Segment segment)
    {
        var dx = Math.Abs(segment.TopX - segment.BottomX);
        var dy = segment.TopY - segment.BottomY;

        return Math.Atan2(dx, dy) * 180.0 / Math.PI;
    }

    private double Tolerance(double reference)
    {
        return Math.Max(0.01, Math.Abs(reference) * _options.DimensionTolerancePercent / 100.0);
    }

    private static bool IsIgnored(string? layer, string? lineType)
    {
        return Contains(layer) || Contains(lineType);

        static bool Contains(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && IgnoredMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static double GetScaleToMillimeter(DrawingUnit unit)
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

    private readonly record struct Segment(double TopX, double TopY, double BottomX, double BottomY)
    {
        public double Length
        {
            get
            {
                var dx = TopX - BottomX;
                var dy = TopY - BottomY;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }
    }

    private readonly record struct CylinderCandidate(double Height, double Diameter, double CenterX, double BottomY, double TopY);

    private readonly record struct ConeCandidate(double Height, double TopDiameter, double BottomDiameter, double Angle, double CenterX, double BottomY, double TopY);
}