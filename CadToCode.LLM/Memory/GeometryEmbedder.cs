using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Memory;

public class GeometryEmbedder
{
    private const int FeatureCount = 64;
    private const int OrientationBins = 12;
    private const int GridSize = 4;
    private const double AxisToleranceDegrees = 3.0;

    private static readonly string[] Keywords =
    {
        "cyclone", "fan", "axial", "duct", "door",
        "window", "cone", "cylinder", "dia", "blade"
    };

    private readonly int _dimensions;

    public GeometryEmbedder(IOptions<LlmOptions> options)
    {
        var configured = options.Value.EmbeddingDimensions;
        _dimensions = configured > 0 ? configured : FeatureCount;
    }

    public int Dimensions => _dimensions;

    public float[] Embed(ExtractedGeometry geometry)
    {
        var features = BuildFeatures(geometry);
        var vector = new float[_dimensions];

        for (var i = 0; i < features.Count; i++)
        {
            var value = (float)(2.0 * Clamp01(features[i]) - 1.0);
            vector[i % _dimensions] += value;
        }

        Normalize(vector);
        return vector;
    }

    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        double normA = 0;
        double normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 0 || normB <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static List<double> BuildFeatures(ExtractedGeometry geometry)
    {
        var features = new List<double>(FeatureCount);

        var lines = geometry.Lines.Where(IsValidLine).ToList();
        var arcs = geometry.Arcs.Where(a => IsFinite(a.Center.X) && IsFinite(a.Center.Y) && IsFinite(a.Radius) && a.Radius > 0).ToList();
        var circles = geometry.Circles.Where(c => IsFinite(c.Center.X) && IsFinite(c.Center.Y) && IsFinite(c.Radius) && c.Radius > 0).ToList();

        var bounds = ComputeBounds(geometry, lines, arcs, circles);
        var width = Math.Max(bounds.MaxX - bounds.MinX, 0);
        var height = Math.Max(bounds.MaxY - bounds.MinY, 0);
        var diagonal = Math.Sqrt(width * width + height * height);
        if (diagonal <= 1e-9)
        {
            diagonal = 1.0;
        }

        var centerX = (bounds.MinX + bounds.MaxX) / 2.0;
        var centerY = (bounds.MinY + bounds.MaxY) / 2.0;

        // 1. Entity counts (6)
        features.Add(LogCount(lines.Count));
        features.Add(LogCount(arcs.Count));
        features.Add(LogCount(circles.Count));
        features.Add(LogCount(geometry.Hatches.Count));
        features.Add(LogCount(geometry.Dimensions.Count));
        features.Add(LogCount(geometry.Texts.Count));

        // 2. Bounding box shape (2)
        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);
        features.Add(longSide > 1e-9 ? shortSide / longSide : 0);
        features.Add(height > width ? 1.0 : 0.0);

        // 3. Line orientation histogram (12) and axis fractions (3)
        var histogram = new double[OrientationBins];
        double totalLength = 0;
        double horizontal = 0;
        double vertical = 0;
        double diagonalLength = 0;
        var lengths = new List<double>(lines.Count);

        foreach (var line in lines)
        {
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-12)
            {
                continue;
            }

            lengths.Add(length / diagonal);

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            angle %= 180.0;
            if (angle < 0)
            {
                angle += 180.0;
            }

            var bin = (int)(angle / (180.0 / OrientationBins)) % OrientationBins;
            histogram[bin] += length;
            totalLength += length;

            if (angle <= AxisToleranceDegrees || angle >= 180.0 - AxisToleranceDegrees)
            {
                horizontal += length;
            }
            else if (Math.Abs(angle - 90.0) <= AxisToleranceDegrees)
            {
                vertical += length;
            }
            else if (Math.Abs(angle - 45.0) <= AxisToleranceDegrees || Math.Abs(angle - 135.0) <= AxisToleranceDegrees)
            {
                diagonalLength += length;
            }
        }

        for (var i = 0; i < OrientationBins; i++)
        {
            features.Add(totalLength > 0 ? Math.Min(1.0, histogram[i] / totalLength * 3.0) : 0);
        }

        features.Add(totalLength > 0 ? horizontal / totalLength : 0);
        features.Add(totalLength > 0 ? vertical / totalLength : 0);
        features.Add(totalLength > 0 ? diagonalLength / totalLength : 0);

        // 4. Line length statistics (4)
        if (lengths.Count > 0)
        {
            var mean = lengths.Average();
            var variance = lengths.Sum(l => (l - mean) * (l - mean)) / lengths.Count;
            features.Add(Math.Min(1.0, mean * 5.0));
            features.Add(Math.Min(1.0, Math.Sqrt(variance) * 5.0));
            features.Add(Math.Min(1.0, lengths.Max()));
            features.Add(lengths.Count(l => l < 0.02) / (double)lengths.Count);
        }
        else
        {
            features.Add(0);
            features.Add(0);
            features.Add(0);
            features.Add(0);
        }

        // 5. Circle statistics (4)
        var primitiveTotal = lines.Count + arcs.Count + circles.Count;
        features.Add(primitiveTotal > 0 ? circles.Count / (double)primitiveTotal : 0);

        if (circles.Count > 0)
        {
            features.Add(Math.Min(1.0, circles.Average(c => c.Radius) / diagonal * 4.0));
            features.Add(Math.Min(1.0, circles.Max(c => c.Radius) / diagonal * 2.0));

            var cell = Math.Max(diagonal * 0.01, 1e-9);
            var distinct = circles
                .Select(c => ((long)Math.Round(c.Center.X / cell), (long)Math.Round(c.Center.Y / cell)))
                .Distinct()
                .Count();
            features.Add(1.0 - distinct / (double)circles.Count);
        }
        else
        {
            features.Add(0);
            features.Add(0);
            features.Add(0);
        }

        // 6. Arc statistics (3)
        if (arcs.Count > 0)
        {
            var sweeps = arcs.Select(a => NormalizeSweep(a.StartAngle, a.EndAngle)).ToList();
            features.Add(sweeps.Average() / 360.0);
            features.Add(Math.Min(1.0, arcs.Average(a => a.Radius) / diagonal * 4.0));
            features.Add(Math.Min(1.0, sweeps.Sum() / 3600.0));
        }
        else
        {
            features.Add(0);
            features.Add(0);
            features.Add(0);
        }

        // 7. Hatch statistics (2)
        var hatchPoints = 0;
        double hatchArea = 0;
        foreach (var hatch in geometry.Hatches)
        {
            foreach (var path in hatch.BoundaryPaths)
            {
                hatchPoints += path.Count;
                hatchArea += PolygonArea(path);
            }
        }

        var boundsArea = width * height;
        features.Add(LogCount(hatchPoints));
        features.Add(boundsArea > 1e-9 ? Math.Min(1.0, hatchArea / boundsArea) : 0);

        // 8. Mirror symmetry (2)
        var midpoints = lines
            .Select(l => ((l.Start.X + l.End.X) / 2.0, (l.Start.Y + l.End.Y) / 2.0))
            .ToList();
        var symmetryCell = Math.Max(diagonal * 0.02, 1e-9);
        features.Add(MirrorScore(midpoints, centerX, centerY, symmetryCell, mirrorX: true));
        features.Add(MirrorScore(midpoints, centerX, centerY, symmetryCell, mirrorX: false));

        // 9. Spatial occupancy grid (16)
        var grid = new double[GridSize * GridSize];
        var gridPoints = midpoints
            .Concat(circles.Select(c => (c.Center.X, c.Center.Y)))
            .ToList();

        foreach (var (px, py) in gridPoints)
        {
            var gx = width > 1e-9 ? (int)((px - bounds.MinX) / width * GridSize) : 0;
            var gy = height > 1e-9 ? (int)((py - bounds.MinY) / height * GridSize) : 0;
            gx = Math.Clamp(gx, 0, GridSize - 1);
            gy = Math.Clamp(gy, 0, GridSize - 1);
            grid[gy * GridSize + gx] += 1;
        }

        for (var i = 0; i < grid.Length; i++)
        {
            features.Add(gridPoints.Count > 0 ? Math.Min(1.0, grid[i] / gridPoints.Count * 4.0) : 0);
        }

        // 10. Text keywords (10)
        var allText = string.Join(" ", geometry.Texts.Select(t => t.Text)
                .Concat(geometry.Dimensions.Select(d => d.Text ?? string.Empty)))
            .ToLowerInvariant();

        foreach (var keyword in Keywords)
        {
            features.Add(allText.Contains(keyword, StringComparison.Ordinal) ? 1.0 : 0.0);
        }

        return features;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ComputeBounds(
        ExtractedGeometry geometry,
        List<GeometryLine> lines,
        List<GeometryArc> arcs,
        List<GeometryCircle> circles)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var any = false;

        void Include(double x, double y)
        {
            if (!IsFinite(x) || !IsFinite(y))
            {
                return;
            }

            any = true;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var line in lines)
        {
            Include(line.Start.X, line.Start.Y);
            Include(line.End.X, line.End.Y);
        }

        foreach (var arc in arcs)
        {
            Include(arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius);
            Include(arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius);
        }

        foreach (var circle in circles)
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

        return any ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    private static double MirrorScore(
        List<(double X, double Y)> points,
        double centerX,
        double centerY,
        double cell,
        bool mirrorX)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var occupied = new HashSet<(long, long)>();
        foreach (var (x, y) in points)
        {
            occupied.Add(((long)Math.Round(x / cell), (long)Math.Round(y / cell)));
        }

        var matched = 0;
        foreach (var (x, y) in points)
        {
            var mx = mirrorX ? 2.0 * centerX - x : x;
            var my = mirrorX ? y : 2.0 * centerY - y;
            var cx = (long)Math.Round(mx / cell);
            var cy = (long)Math.Round(my / cell);

            var found = false;
            for (var dx = -1; dx <= 1 && !found; dx++)
            {
                for (var dy = -1; dy <= 1 && !found; dy++)
                {
                    if (occupied.Contains((cx + dx, cy + dy)))
                    {
                        found = true;
                    }
                }
            }

            if (found)
            {
                matched++;
            }
        }

        return matched / (double)points.Count;
    }

    private static double PolygonArea(List<GeometryPoint> path)
    {
        if (path.Count < 3)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 0; i < path.Count; i++)
        {
            var current = path[i];
            var next = path[(i + 1) % path.Count];
            sum += current.X * next.Y - next.X * current.Y;
        }

        var area = Math.Abs(sum) / 2.0;
        return IsFinite(area) ? area : 0;
    }

    private static double NormalizeSweep(double startAngle, double endAngle)
    {
        var sweep = (endAngle - startAngle) % 360.0;
        if (sweep < 0)
        {
            sweep += 360.0;
        }

        return sweep;
    }

    private static bool IsValidLine(GeometryLine line)
    {
        return IsFinite(line.Start.X) && IsFinite(line.Start.Y)
            && IsFinite(line.End.X) && IsFinite(line.End.Y);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double LogCount(int count)
    {
        return Clamp01(Math.Log(1.0 + Math.Max(count, 0)) / Math.Log(1.0 + 2000.0));
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return Math.Min(1.0, Math.Max(0.0, value));
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        if (sum <= 0)
        {
            return;
        }

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }
}