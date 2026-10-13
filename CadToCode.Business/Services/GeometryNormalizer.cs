using System.Text.Json;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public class GeometryNormalizer : IGeometryNormalizer
{
    private const double MinExplainedFraction = 0.85;

    private static readonly string[] IgnoredMarkers = { "center", "centre", "axis", "dim", "hidden", "phantom", "dash" };

    private static readonly string[] AxisMarkers = { "center", "centre", "axis" };

    private static readonly string[] RevolveSymbols = { "\u00D8", "\u2300", "%%c" };

    private static readonly HashSet<string> RevolveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dia", "diameter", "cyl", "cylinder", "cylindrical", "cone", "conical", "cyclone", "tank", "vessel", "shaft", "pipe", "tube", "hopper"
    };

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
        var cone = FindCone(slanted);

        if (HasRevolveEvidence(geometry, cylinder, cone) && IsExplainedByPrimitives(segments, cylinder, cone))
        {
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
        }

        if (found.Count == 0)
        {
            var profile = BuildProfileComponent(geometry, scale);
            if (profile is not null)
            {
                found.Add((0.0, profile));
            }
        }

        var ordered = found.OrderByDescending(f => f.CenterY).Select(f => f.Component).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SequenceIndex = i + 1;
        }

        return ordered;
    }

    private static ExtractedComponent? BuildProfileComponent(ExtractedGeometry geometry, double scale)
    {
        var profile = StructuralProfileExtractor.Extract(geometry, scale);
        if (profile is null || profile.BoundingWidth <= 0 || profile.BoundingHeight <= 0)
        {
            return null;
        }

        var hints = geometry.Texts.Select(t => (string?)t.Text).Concat(geometry.Layers.Select(l => (string?)l));
        var type = ProfileKeywordResolver.Resolve(hints) ?? ComponentType.CustomProfile;

        return new ExtractedComponent
        {
            ComponentType = type,
            Name = type == ComponentType.CustomProfile ? "Custom profile" : type + " profile",
            Width = ToDecimal(profile.BoundingWidth),
            Height = ToDecimal(profile.BoundingHeight),
            HasHatch = geometry.Hatches.Count > 0,
            Confidence = profile.IsClosed ? 0.90m : 0.60m,
            RawDataJson = profile.ToRawJson()
        };
    }

    private bool HasRevolveEvidence(ExtractedGeometry geometry, CylinderCandidate? cylinder, ConeCandidate? cone)
    {
        if (cylinder is null && cone is null)
        {
            return false;
        }

        if (cylinder is not null && cone is not null)
        {
            var reference = Math.Max(cylinder.Value.Diameter, Math.Max(cone.Value.TopDiameter, cone.Value.BottomDiameter));
            if (Math.Abs(cylinder.Value.CenterX - cone.Value.CenterX) <= Tolerance(reference))
            {
                return true;
            }
        }

        if (geometry.Lines.Any(l => IsAxis(l.Layer, l.LineType))
            || geometry.Polylines.Any(p => IsAxis(p.Layer, p.LineType)))
        {
            return true;
        }

        var sources = geometry.Texts.Select(t => t.Text)
            .Concat(geometry.Dimensions.Select(d => d.Text ?? string.Empty))
            .Concat(geometry.Layers)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        foreach (var source in sources)
        {
            if (RevolveSymbols.Any(symbol => source.Contains(symbol, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var tokens = System.Text.RegularExpressions.Regex.Split(source.ToLowerInvariant(), "[^a-z]+");
            if (tokens.Any(token => token.Length > 0 && RevolveWords.Contains(token)))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsExplainedByPrimitives(List<Segment> segments, CylinderCandidate? cylinder, ConeCandidate? cone)
    {
        if (cylinder is null && cone is null)
        {
            return false;
        }

        var total = segments.Sum(s => s.Length);
        if (total <= 0)
        {
            return false;
        }

        var explained = 0.0;

        foreach (var segment in segments)
        {
            if ((cylinder is not null && ExplainedByCylinder(segment, cylinder.Value))
                || (cone is not null && ExplainedByCone(segment, cone.Value)))
            {
                explained += segment.Length;
            }
        }

        return explained / total >= MinExplainedFraction;
    }

    private bool ExplainedByCylinder(Segment segment, CylinderCandidate c)
    {
        var tolerance = Tolerance(c.Diameter);
        var left = c.CenterX - c.Diameter / 2.0;
        var right = c.CenterX + c.Diameter / 2.0;

        if (AngleFromVertical(segment) <= _options.ParallelLineToleranceDegrees)
        {
            var x = (segment.TopX + segment.BottomX) / 2.0;

            return (Math.Abs(x - left) <= tolerance || Math.Abs(x - right) <= tolerance)
                && segment.BottomY >= c.BottomY - tolerance
                && segment.TopY <= c.TopY + tolerance;
        }

        if (IsHorizontal(segment))
        {
            var y = (segment.TopY + segment.BottomY) / 2.0;
            var minX = Math.Min(segment.TopX, segment.BottomX);
            var maxX = Math.Max(segment.TopX, segment.BottomX);

            return (Math.Abs(y - c.TopY) <= tolerance || Math.Abs(y - c.BottomY) <= tolerance)
                && minX >= left - tolerance
                && maxX <= right + tolerance;
        }

        return false;
    }

    private bool ExplainedByCone(Segment segment, ConeCandidate k)
    {
        var maxDiameter = Math.Max(k.TopDiameter, k.BottomDiameter);
        var tolerance = Tolerance(maxDiameter);
        var span = k.TopY - k.BottomY;

        if (span <= 0)
        {
            return false;
        }

        double SideX(double y, double side)
        {
            var t = (y - k.BottomY) / span;
            var half = k.BottomDiameter / 2.0 + (k.TopDiameter / 2.0 - k.BottomDiameter / 2.0) * t;
            return k.CenterX + side * half;
        }

        if (IsHorizontal(segment))
        {
            var y = (segment.TopY + segment.BottomY) / 2.0;
            var minX = Math.Min(segment.TopX, segment.BottomX);
            var maxX = Math.Max(segment.TopX, segment.BottomX);

            return (Math.Abs(y - k.TopY) <= tolerance || Math.Abs(y - k.BottomY) <= tolerance)
                && minX >= k.CenterX - maxDiameter / 2.0 - tolerance
                && maxX <= k.CenterX + maxDiameter / 2.0 + tolerance;
        }

        foreach (var side in new[] { -1.0, 1.0 })
        {
            if (Math.Abs(SideX(segment.TopY, side) - segment.TopX) <= tolerance
                && Math.Abs(SideX(segment.BottomY, side) - segment.BottomX) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsHorizontal(Segment segment)
    {
        var dx = Math.Abs(segment.TopX - segment.BottomX);
        var dy = Math.Abs(segment.TopY - segment.BottomY);
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        return angle <= _options.ParallelLineToleranceDegrees;
    }

    private static bool IsAxis(string? layer, string? lineType)
    {
        return Contains(layer) || Contains(lineType);

        static bool Contains(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && AxisMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
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