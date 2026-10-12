using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Memory;

public class GeometryEmbedder
{
    public const int StructuralCount = 61;
    public const int MinDimensions = 96;

    private const int OrientationBins = 12;
    private const int GridSize = 4;
    private const double AxisToleranceDegrees = 3.0;
    private const double StructuralWeight = 0.9;
    private const int MaxParallelLines = 4000;
    private const double ParallelClusterTolerance = 1.5;
    private const double ParallelClusterSpan = 3.0;

    private static readonly double TokenWeight = Math.Sqrt(1.0 - StructuralWeight * StructuralWeight);

    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "dia", "mm", "cm", "scale", "typ", "sheet", "drawing", "dwg", "rev", "title",
        "all", "dim", "note", "notes", "size", "qty", "nts", "ref", "unit", "units",
        "by", "of", "the", "and", "for", "to", "in", "on", "at", "or", "as", "is"
    };

    private readonly int _dimensions;

    public GeometryEmbedder(IOptions<LlmOptions> options)
    {
        var configured = options.Value.EmbeddingDimensions;
        _dimensions = configured >= MinDimensions ? configured : MinDimensions;
    }

    public int Dimensions => _dimensions;

    public float[] Embed(ExtractedGeometry geometry)
    {
        var structural = BuildStructuralFeatures(geometry);
        var vector = new float[_dimensions];

        double structuralNorm = 0;
        for (var i = 0; i < StructuralCount; i++)
        {
            var value = 2.0 * Clamp01(i < structural.Count ? structural[i] : 0) - 1.0;
            vector[i] = (float)value;
            structuralNorm += value * value;
        }

        if (structuralNorm > 0)
        {
            var scale = (float)(StructuralWeight / Math.Sqrt(structuralNorm));
            for (var i = 0; i < StructuralCount; i++)
            {
                vector[i] *= scale;
            }
        }

        var bucketCount = _dimensions - StructuralCount;
        var buckets = new double[bucketCount];
        var tokens = CollectTokens(geometry);

        foreach (var token in tokens)
        {
            var hash = Fnv1a(token);
            var bucket = (int)(hash % (uint)bucketCount);
            var sign = ((hash >> 31) & 1u) == 0 ? 1.0 : -1.0;
            buckets[bucket] += sign;
        }

        double tokenNorm = 0;
        for (var i = 0; i < bucketCount; i++)
        {
            tokenNorm += buckets[i] * buckets[i];
        }

        if (tokenNorm > 0)
        {
            var scale = TokenWeight / Math.Sqrt(tokenNorm);
            for (var i = 0; i < bucketCount; i++)
            {
                vector[StructuralCount + i] = (float)(buckets[i] * scale);
            }
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

    private static List<double> BuildStructuralFeatures(ExtractedGeometry geometry)
    {
        var features = new List<double>(StructuralCount);

        var lines = geometry.Lines.Where(IsValidLine).ToList();
        var arcs = geometry.Arcs
            .Where(a => IsFinite(a.Center.X) && IsFinite(a.Center.Y) && IsFinite(a.Radius) && a.Radius > 0)
            .ToList();
        var circles = geometry.Circles
            .Where(c => IsFinite(c.Center.X) && IsFinite(c.Center.Y) && IsFinite(c.Radius) && c.Radius > 0)
            .ToList();

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

        // 1. Entity counts (7)
        features.Add(LogCount(lines.Count));
        features.Add(LogCount(arcs.Count));
        features.Add(LogCount(circles.Count));
        features.Add(LogCount(geometry.Hatches.Count));
        features.Add(LogCount(geometry.Dimensions.Count));
        features.Add(LogCount(geometry.Texts.Count));
        features.Add(LogCount(geometry.Polylines.Count(p => p.IsClosed)));

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

        // 9. Rotational symmetry (2)
        var rotationPoints = midpoints
            .Concat(circles.Select(c => (c.Center.X, c.Center.Y)))
            .Concat(arcs.Select(a => (a.Center.X, a.Center.Y)))
            .ToList();
        features.Add(RotationScore(rotationPoints, centerX, centerY, symmetryCell, Math.PI));
        features.Add(RotationScore(rotationPoints, centerX, centerY, symmetryCell, Math.PI / 2.0));

        // 10. Parallel line groups (4)
        var parallel = AnalyzeParallelGroups(lines, diagonal, totalLength);
        features.Add(parallel.LengthFraction);
        features.Add(LogCount(parallel.MaxTracks));
        features.Add(LogCount(parallel.GroupCount));
        features.Add(parallel.EvenFraction);

        // 11. Spatial occupancy grid (16)
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

        return features;
    }

    private static (double LengthFraction, int MaxTracks, int GroupCount, double EvenFraction) AnalyzeParallelGroups(
        List<GeometryLine> lines,
        double diagonal,
        double totalLength)
    {
        var minLength = diagonal * 0.02;
        var trackTolerance = Math.Max(diagonal * 0.004, 1e-9);

        var candidates = new List<LineInfo>();
        foreach (var line in lines)
        {
            var dx = line.End.X - line.Start.X;
            var dy = line.End.Y - line.Start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < minLength)
            {
                continue;
            }

            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            angle %= 180.0;
            if (angle < 0)
            {
                angle += 180.0;
            }

            candidates.Add(new LineInfo(
                angle,
                length,
                (line.Start.X + line.End.X) / 2.0,
                (line.Start.Y + line.End.Y) / 2.0));
        }

        if (candidates.Count < 2)
        {
            return (0, 0, 0, 0);
        }

        var sorted = candidates
            .OrderByDescending(c => c.Length)
            .Take(MaxParallelLines)
            .OrderBy(c => c.Angle)
            .ToList();

        var clusters = new List<List<LineInfo>>();
        var current = new List<LineInfo>();
        var clusterStart = 0.0;
        var previous = 0.0;

        foreach (var item in sorted)
        {
            if (current.Count == 0)
            {
                clusterStart = item.Angle;
            }
            else if (item.Angle - previous > ParallelClusterTolerance || item.Angle - clusterStart > ParallelClusterSpan)
            {
                clusters.Add(current);
                current = new List<LineInfo>();
                clusterStart = item.Angle;
            }

            current.Add(item);
            previous = item.Angle;
        }

        if (current.Count > 0)
        {
            clusters.Add(current);
        }

        if (clusters.Count > 1)
        {
            var first = clusters[0];
            var last = clusters[^1];
            if (first[0].Angle + 180.0 - last[^1].Angle <= ParallelClusterTolerance)
            {
                first.AddRange(last);
                clusters.RemoveAt(clusters.Count - 1);
            }
        }

        var parallelLength = 0.0;
        var maxTracks = 0;
        var groupCount = 0;
        var evenCandidates = 0;
        var evenCount = 0;

        foreach (var cluster in clusters)
        {
            if (cluster.Count < 2)
            {
                continue;
            }

            double sumSin = 0;
            double sumCos = 0;
            foreach (var item in cluster)
            {
                var doubled = item.Angle * 2.0 * Math.PI / 180.0;
                sumSin += Math.Sin(doubled);
                sumCos += Math.Cos(doubled);
            }

            var theta = Math.Atan2(sumSin, sumCos) / 2.0;
            var sin = Math.Sin(theta);
            var cos = Math.Cos(theta);

            var offsets = cluster
                .Select(item => (Offset: -item.MidX * sin + item.MidY * cos, item.Length))
                .OrderBy(o => o.Offset)
                .ToList();

            var tracks = new List<double>();
            var trackSum = offsets[0].Offset;
            var trackCount = 1;

            for (var i = 1; i < offsets.Count; i++)
            {
                if (offsets[i].Offset - offsets[i - 1].Offset > trackTolerance)
                {
                    tracks.Add(trackSum / trackCount);
                    trackSum = 0;
                    trackCount = 0;
                }

                trackSum += offsets[i].Offset;
                trackCount++;
            }

            tracks.Add(trackSum / trackCount);

            if (tracks.Count < 2)
            {
                continue;
            }

            groupCount++;
            maxTracks = Math.Max(maxTracks, tracks.Count);
            parallelLength += cluster.Sum(item => item.Length);

            if (tracks.Count >= 3)
            {
                evenCandidates++;

                var gaps = new List<double>();
                for (var i = 1; i < tracks.Count; i++)
                {
                    gaps.Add(tracks[i] - tracks[i - 1]);
                }

                var meanGap = gaps.Average();
                if (meanGap > 1e-9)
                {
                    var variance = gaps.Sum(g => (g - meanGap) * (g - meanGap)) / gaps.Count;
                    if (Math.Sqrt(variance) / meanGap < 0.1)
                    {
                        evenCount++;
                    }
                }
            }
        }

        var lengthFraction = totalLength > 0 ? Math.Min(1.0, parallelLength / totalLength) : 0;
        var evenFraction = evenCandidates > 0 ? evenCount / (double)evenCandidates : 0;

        return (lengthFraction, maxTracks, groupCount, evenFraction);
    }

    private static List<string> CollectTokens(ExtractedGeometry geometry)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var text in geometry.Texts)
        {
            if (text.Tokens.Count > 0)
            {
                foreach (var token in text.Tokens)
                {
                    AddToken(set, token);
                }
            }
            else
            {
                foreach (var token in Tokenize(text.Text))
                {
                    AddToken(set, token);
                }
            }
        }

        foreach (var dimension in geometry.Dimensions)
        {
            foreach (var token in Tokenize(dimension.Text))
            {
                AddToken(set, token);
            }
        }

        return set.ToList();
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in WordToken.Matches(text))
        {
            yield return match.Value;
        }
    }

    private static void AddToken(HashSet<string> set, string token)
    {
        var normalized = token.Trim().ToLowerInvariant();
        if (normalized.Length < 2 || StopTokens.Contains(normalized))
        {
            return;
        }

        set.Add(normalized);
    }

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
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
        return TransformMatchScore(points, cell, point => mirrorX
            ? (2.0 * centerX - point.X, point.Y)
            : (point.X, 2.0 * centerY - point.Y));
    }

    private static double RotationScore(
        List<(double X, double Y)> points,
        double centerX,
        double centerY,
        double cell,
        double angleRadians)
    {
        var cos = Math.Cos(angleRadians);
        var sin = Math.Sin(angleRadians);

        return TransformMatchScore(points, cell, point =>
        {
            var dx = point.X - centerX;
            var dy = point.Y - centerY;
            return (centerX + dx * cos - dy * sin, centerY + dx * sin + dy * cos);
        });
    }

    private static double TransformMatchScore(
        List<(double X, double Y)> points,
        double cell,
        Func<(double X, double Y), (double X, double Y)> transform)
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
        foreach (var point in points)
        {
            var (tx, ty) = transform(point);
            var cx = (long)Math.Round(tx / cell);
            var cy = (long)Math.Round(ty / cell);

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

    private readonly record struct LineInfo(double Angle, double Length, double MidX, double MidY);
}