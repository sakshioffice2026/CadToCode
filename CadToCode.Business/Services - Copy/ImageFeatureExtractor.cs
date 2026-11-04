using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CadToCode.Business.Services;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height);
public readonly record struct PixelPoint(double X, double Y);
public readonly record struct PixelSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}
public readonly record struct PixelCircle(double CenterX, double CenterY, double Radius);
public readonly record struct PixelArc(double CenterX, double CenterY, double Radius, double StartDegrees, double EndDegrees);

public sealed class PixelLoop
{
    public List<PixelPoint> Points { get; } = new();
    public double Area { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
}

public sealed class ImageFeatures
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double InkWidth { get; set; } = 1.0;
    public List<PixelSegment> Segments { get; } = new();
    public List<PixelCircle> Circles { get; } = new();
    public List<PixelArc> Arcs { get; } = new();
    public List<PixelLoop> Loops { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Local raster feature extraction for engineering drawings. This class deliberately emits
/// pixel-space features only; conversion to drawing units belongs to ImageGeometryExtractor.
/// </summary>
public static class ImageFeatureExtractor
{
    private const int MaxLines = 80;
    private const int MaxThinningIterations = 60;
    private const int MaxCurveComponents = 300;
    private const int MaxLoops = 24;
    private const int MaxLoopPoints = 96;
    private const double MinInkFraction = 0.0002;
    private const double MinLoopAreaFraction = 0.0008;
    private const double LineInlierDistance = 1.65;
    private const double LineGapTolerance = 5.0;

    public static byte[] ToGray(Image<Rgba32> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = image.Width;
        var gray = new byte[checked(width * image.Height)];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var alpha = p.A / 255.0;
                    var luminance = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    gray[y * width + x] = (byte)Math.Clamp((int)Math.Round(luminance * alpha + 255 * (1 - alpha)), 0, 255);
                }
            }
        });
        return gray;
    }

    public static ImageFeatures Analyze(
        byte[] gray,
        int width,
        int height,
        IReadOnlyList<PixelRegion> textRegions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (width <= 0 || height <= 0 || gray.Length != checked(width * height))
            throw new ArgumentException("The grayscale buffer dimensions are invalid.", nameof(gray));

        cancellationToken.ThrowIfCancellationRequested();
        var mask = BinarizeOtsu(gray);
        ClearBorder(mask, width, height);
        EraseRegions(mask, width, height, textRegions, padding: 2);

        var inkCount = CountSet(mask);
        if (inkCount < MinInkFraction * width * height)
            throw new InvalidOperationException("No drawing lines were found in the image.");

        var skeleton = (bool[])mask.Clone();
        Thin(skeleton, width, height, cancellationToken);
        var skeletonCount = CountSet(skeleton);
        if (skeletonCount == 0)
            throw new InvalidOperationException("No drawing lines were found in the image.");

        var features = new ImageFeatures
        {
            Width = width,
            Height = height,
            InkWidth = Math.Max(1.0, inkCount / (double)skeletonCount)
        };

        // Curves and closed outlines are estimated from connected skeleton components before
        // line pixels are classified. This avoids line removal breaking a circle into fragments.
        var components = FindComponents(skeleton, width, height, cancellationToken, MaxCurveComponents);
        DetectCurvesAndLoops(components, width, height, features, cancellationToken);
        DetectLineSegments(skeleton, width, height, features, cancellationToken);

        if (features.Segments.Count == 0 && features.Circles.Count == 0 && features.Arcs.Count == 0 && features.Loops.Count == 0)
            features.Notes.Add("Thresholding found ink, but no stable line, curve, or closed-outline candidates survived geometric filtering.");

        return features;
    }

    private static bool[] BinarizeOtsu(byte[] gray)
    {
        var histogram = new long[256];
        foreach (var value in gray) histogram[value]++;
        var total = (long)gray.Length;
        double sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];

        long backgroundWeight = 0;
        double backgroundSum = 0;
        double bestVariance = -1;
        var threshold = 127;
        for (var i = 0; i < 256; i++)
        {
            backgroundWeight += histogram[i];
            if (backgroundWeight == 0) continue;
            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0) break;
            backgroundSum += i * (double)histogram[i];
            var meanBackground = backgroundSum / backgroundWeight;
            var meanForeground = (sumAll - backgroundSum) / foregroundWeight;
            var delta = meanBackground - meanForeground;
            var variance = (double)backgroundWeight * foregroundWeight * delta * delta;
            if (variance > bestVariance) { bestVariance = variance; threshold = i; }
        }

        var mask = new bool[gray.Length];
        var darkCount = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            mask[i] = gray[i] <= threshold;
            if (mask[i]) darkCount++;
        }
        // Engineering drawings are normally dark strokes on a light background. For an
        // inverted screenshot, reverse the mask rather than treating the background as ink.
        if (darkCount > gray.Length / 2)
            for (var i = 0; i < mask.Length; i++) mask[i] = !mask[i];
        return mask;
    }

    private static void ClearBorder(bool[] mask, int width, int height)
    {
        for (var x = 0; x < width; x++)
        {
            mask[x] = false;
            mask[(height - 1) * width + x] = false;
        }
        for (var y = 0; y < height; y++)
        {
            mask[y * width] = false;
            mask[y * width + width - 1] = false;
        }
    }

    private static void EraseRegions(bool[] mask, int width, int height, IReadOnlyList<PixelRegion>? regions, int padding)
    {
        if (regions is null) return;
        foreach (var region in regions)
        {
            var left = Math.Clamp(region.X - padding, 0, width - 1);
            var top = Math.Clamp(region.Y - padding, 0, height - 1);
            var right = Math.Clamp(region.X + region.Width + padding, 0, width - 1);
            var bottom = Math.Clamp(region.Y + region.Height + padding, 0, height - 1);
            for (var y = top; y <= bottom; y++)
                Array.Clear(mask, y * width + left, right - left + 1);
        }
    }

    private static void Thin(bool[] mask, int width, int height, CancellationToken token)
    {
        var remove = new List<int>();
        for (var iteration = 0; iteration < MaxThinningIterations; iteration++)
        {
            var changed = false;
            for (var phase = 0; phase < 2; phase++)
            {
                token.ThrowIfCancellationRequested();
                remove.Clear();
                for (var y = 1; y < height - 1; y++)
                for (var x = 1; x < width - 1; x++)
                {
                    var i = y * width + x;
                    if (!mask[i]) continue;
                    var p2 = mask[i - width];
                    var p3 = mask[i - width + 1];
                    var p4 = mask[i + 1];
                    var p5 = mask[i + width + 1];
                    var p6 = mask[i + width];
                    var p7 = mask[i + width - 1];
                    var p8 = mask[i - 1];
                    var p9 = mask[i - width - 1];
                    var neighbours = Bit(p2) + Bit(p3) + Bit(p4) + Bit(p5) + Bit(p6) + Bit(p7) + Bit(p8) + Bit(p9);
                    if (neighbours is < 2 or > 6) continue;
                    var transitions = (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0)
                        + (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                    if (transitions != 1) continue;
                    if (phase == 0 && ((p2 && p4 && p6) || (p4 && p6 && p8))) continue;
                    if (phase == 1 && ((p2 && p4 && p8) || (p2 && p6 && p8))) continue;
                    remove.Add(i);
                }
                foreach (var i in remove) mask[i] = false;
                changed |= remove.Count > 0;
            }
            if (!changed) break;
        }
    }

    private static int Bit(bool value) => value ? 1 : 0;
    private static int CountSet(bool[] values) { var count = 0; foreach (var v in values) if (v) count++; return count; }

    private sealed class Component
    {
        public List<PixelPoint> Points { get; } = new();
        public int MinX { get; set; } = int.MaxValue;
        public int MinY { get; set; } = int.MaxValue;
        public int MaxX { get; set; } = int.MinValue;
        public int MaxY { get; set; } = int.MinValue;
        public void Add(int x, int y)
        {
            Points.Add(new PixelPoint(x, y));
            MinX = Math.Min(MinX, x); MinY = Math.Min(MinY, y);
            MaxX = Math.Max(MaxX, x); MaxY = Math.Max(MaxY, y);
        }
    }

    private static List<Component> FindComponents(bool[] mask, int width, int height, CancellationToken token, int maxComponents)
    {
        var visited = new bool[mask.Length];
        var queue = new Queue<int>();
        var result = new List<Component>();
        // A few very large connected drawings can dominate curve fitting. Retain them for line
        // detection, but avoid storing millions of points in a component object.
        const int maxStoredPixelsPerComponent = 150_000;
        for (var start = 0; start < mask.Length && result.Count < maxComponents; start++)
        {
            if (!mask[start] || visited[start]) continue;
            token.ThrowIfCancellationRequested();
            var component = new Component();
            queue.Enqueue(start); visited[start] = true;
            var oversized = false;
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % width; var y = index / width;
                if (!oversized)
                {
                    component.Add(x, y);
                    if (component.Points.Count > maxStoredPixelsPerComponent)
                    {
                        component.Points.Clear();
                        oversized = true;
                    }
                }
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var ni = ny * width + nx;
                    if (!mask[ni] || visited[ni]) continue;
                    visited[ni] = true;
                    queue.Enqueue(ni);
                }
            }
            if (!oversized && component.Points.Count >= 20) result.Add(component);
        }
        return result;
    }

    private static void DetectCurvesAndLoops(List<Component> components, int width, int height, ImageFeatures features, CancellationToken token)
    {
        var minLoopArea = width * (double)height * MinLoopAreaFraction;
        foreach (var component in components)
        {
            token.ThrowIfCancellationRequested();
            var points = component.Points;
            if (points.Count < 24) continue;
            var boxWidth = component.MaxX - component.MinX + 1;
            var boxHeight = component.MaxY - component.MinY + 1;
            if (boxWidth < 8 || boxHeight < 8) continue;

            var circle = FitCircle(points);
            var isCurve = false;
            double coverage = 0;
            if (circle is { } fitted && fitted.Radius >= 5.0)
            {
                var residual = points.Average(p => Math.Abs(Math.Sqrt(Sq(p.X - fitted.X) + Sq(p.Y - fitted.Y)) - fitted.Radius));
                var relativeResidual = residual / Math.Max(1.0, fitted.Radius);
                coverage = AngularCoverage(points, fitted.X, fitted.Y);
                if (relativeResidual <= 0.12 && coverage >= 245.0)
                {
                    AddDistinctCircle(features, new PixelCircle(fitted.X, fitted.Y, fitted.Radius));
                    isCurve = true;
                }
                else if (relativeResidual <= 0.10 && coverage >= 35.0 && coverage <= 340.0)
                {
                    var angles = points.Select(p => NormalizeDegrees(Math.Atan2(-(p.Y - fitted.Y), p.X - fitted.X) * 180.0 / Math.PI)).ToList();
                    var start = angles.OrderBy(a => a).First();
                    var end = angles.OrderBy(a => a).Last();
                    // Use the complement of the largest angular gap to preserve a partial arc's endpoints.
                    (start, end) = ArcEndpoints(angles);
                    features.Arcs.Add(new PixelArc(fitted.X, fitted.Y, fitted.Radius, start, end));
                    isCurve = true;
                }
            }

            // A non-circular component can still be a closed polygonal outline. The perimeter
            // proxy rejects filled text blobs and compact symbols while retaining large profiles.
            var bboxArea = (double)boxWidth * boxHeight;
            var perimeterProxy = 2.0 * (boxWidth + boxHeight);
            var ratio = points.Count / Math.Max(1.0, perimeterProxy);
            if (!isCurve && bboxArea >= minLoopArea && ratio >= 0.45 && ratio <= 2.8 && points.Count >= 30 && features.Loops.Count < MaxLoops)
            {
                var loop = BuildApproximateLoop(points, component);
                if (loop.Points.Count >= 3 && loop.Area >= minLoopArea)
                {
                    // A closed curve is already represented as a circle; do not duplicate it as a loop.
                    features.Loops.Add(loop);
                }
            }
        }
    }

    private static void AddDistinctCircle(ImageFeatures features, PixelCircle candidate)
    {
        if (features.Circles.Any(c => Math.Abs(c.CenterX - candidate.CenterX) < 3 && Math.Abs(c.CenterY - candidate.CenterY) < 3 && Math.Abs(c.Radius - candidate.Radius) < 3)) return;
        if (features.Circles.Count < 60) features.Circles.Add(candidate);
    }

    private static (double X, double Y, double Radius)? FitCircle(List<PixelPoint> pts)
    {
        // Kasa least-squares circle fit: solve x^2+y^2 + A*x + B*y + C = 0.
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sz = 0, sxz = 0, syz = 0;
        foreach (var p in pts)
        {
            var z = p.X * p.X + p.Y * p.Y;
            sx += p.X; sy += p.Y; sxx += p.X * p.X; syy += p.Y * p.Y; sxy += p.X * p.Y;
            sz += z; sxz += p.X * z; syz += p.Y * z;
        }
        var n = (double)pts.Count;
        var matrix = new double[,]
        {
            { sxx, sxy, sx, -sxz },
            { sxy, syy, sy, -syz },
            { sx, sy, n, -sz }
        };
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 3; row++) if (Math.Abs(matrix[row, col]) > Math.Abs(matrix[pivot, col])) pivot = row;
            if (Math.Abs(matrix[pivot, col]) < 1e-9) return null;
            if (pivot != col) for (var j = col; j < 4; j++) (matrix[col, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[col, j]);
            var divisor = matrix[col, col];
            for (var j = col; j < 4; j++) matrix[col, j] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == col) continue;
                var factor = matrix[row, col];
                for (var j = col; j < 4; j++) matrix[row, j] -= factor * matrix[col, j];
            }
        }
        var a = matrix[0, 3]; var b = matrix[1, 3]; var c = matrix[2, 3];
        var cx = -a / 2.0; var cy = -b / 2.0; var r2 = cx * cx + cy * cy - c;
        if (r2 <= 0 || double.IsNaN(r2) || double.IsInfinity(r2)) return null;
        return (cx, cy, Math.Sqrt(r2));
    }

    private static double AngularCoverage(List<PixelPoint> points, double cx, double cy)
    {
        var angles = points.Select(p => NormalizeDegrees(Math.Atan2(p.Y - cy, p.X - cx) * 180.0 / Math.PI)).Distinct().OrderBy(a => a).ToArray();
        if (angles.Length < 2) return 0;
        var largestGap = 360.0 - angles[^1] + angles[0];
        for (var i = 1; i < angles.Length; i++) largestGap = Math.Max(largestGap, angles[i] - angles[i - 1]);
        return 360.0 - largestGap;
    }

    private static (double Start, double End) ArcEndpoints(List<double> angles)
    {
        var sorted = angles.Distinct().OrderBy(a => a).ToArray();
        if (sorted.Length < 2) return (sorted.FirstOrDefault(), sorted.FirstOrDefault());
        var gapIndex = 0; var largestGap = -1.0;
        for (var i = 0; i < sorted.Length; i++)
        {
            var next = i == sorted.Length - 1 ? sorted[0] + 360.0 : sorted[i + 1];
            var gap = next - sorted[i];
            if (gap > largestGap) { largestGap = gap; gapIndex = i; }
        }
        var start = sorted[(gapIndex + 1) % sorted.Length];
        var end = sorted[gapIndex];
        return (start, end);
    }

    private static PixelLoop BuildApproximateLoop(List<PixelPoint> points, Component component)
    {
        var cx = points.Average(p => p.X); var cy = points.Average(p => p.Y);
        // Sort boundary samples angularly around their centroid; this is an intentionally
        // approximate contour for recognition, not a substitute for traced vector boundaries.
        var ordered = points.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToList();
        var step = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)MaxLoopPoints));
        var loop = new PixelLoop { MinX = component.MinX, MinY = component.MinY, MaxX = component.MaxX, MaxY = component.MaxY };
        for (var i = 0; i < ordered.Count; i += step) loop.Points.Add(ordered[i]);
        if (loop.Points.Count > 2 && loop.Points[0] != loop.Points[^1]) loop.Points.Add(loop.Points[0]);
        var twiceArea = 0.0;
        for (var i = 0; i < loop.Points.Count - 1; i++)
            twiceArea += loop.Points[i].X * loop.Points[i + 1].Y - loop.Points[i + 1].X * loop.Points[i].Y;
        loop.Area = Math.Abs(twiceArea) / 2.0;
        return loop;
    }

    private static void DetectLineSegments(bool[] skeleton, int width, int height, ImageFeatures features, CancellationToken token)
    {
        var allPoints = new List<PixelPoint>();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) if (skeleton[y * width + x]) allPoints.Add(new PixelPoint(x, y));
        if (allPoints.Count < 10) return;

        const int thetaCount = 180;
        var maxRho = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height));
        var rhoCount = 2 * maxRho + 1;
        var accumulator = new int[thetaCount * rhoCount];
        var cos = new double[thetaCount]; var sin = new double[thetaCount];
        for (var t = 0; t < thetaCount; t++)
        {
            var angle = Math.PI * t / thetaCount;
            cos[t] = Math.Cos(angle); sin[t] = Math.Sin(angle);
        }

        var stride = Math.Max(1, (int)Math.Ceiling(allPoints.Count / 7000.0));
        for (var i = 0; i < allPoints.Count; i += stride)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var p = allPoints[i];
            for (var t = 0; t < thetaCount; t++)
            {
                var rho = (int)Math.Round(p.X * cos[t] + p.Y * sin[t]) + maxRho;
                accumulator[t * rhoCount + rho]++;
            }
        }

        var minVotes = Math.Max(10, (int)Math.Round(Math.Max(width, height) * 0.035 / stride));
        var minLength = Math.Max(15.0, Math.Max(width, height) * 0.045);
        for (var line = 0; line < MaxLines; line++)
        {
            token.ThrowIfCancellationRequested();
            var peakIndex = 0; var peakVotes = 0;
            for (var i = 0; i < accumulator.Length; i++)
                if (accumulator[i] > peakVotes) { peakVotes = accumulator[i]; peakIndex = i; }
            if (peakVotes < minVotes) break;

            var theta = peakIndex / rhoCount;
            var rho = peakIndex % rhoCount - maxRho;
            var inliers = new List<(double T, PixelPoint Point)>();
            foreach (var p in allPoints)
            {
                if (Math.Abs(p.X * cos[theta] + p.Y * sin[theta] - rho) <= LineInlierDistance)
                    inliers.Add((-p.X * sin[theta] + p.Y * cos[theta], p));
            }
            inliers.Sort((a, b) => a.T.CompareTo(b.T));

            var start = 0;
            for (var i = 1; i <= inliers.Count; i++)
            {
                var endRun = i == inliers.Count || inliers[i].T - inliers[i - 1].T > LineGapTolerance;
                if (!endRun) continue;
                if (i > start)
                {
                    var firstT = inliers[start].T; var lastT = inliers[i - 1].T;
                    if (lastT - firstT >= minLength)
                    {
                        var a = new PixelPoint(rho * cos[theta] - firstT * sin[theta], rho * sin[theta] + firstT * cos[theta]);
                        var b = new PixelPoint(rho * cos[theta] - lastT * sin[theta], rho * sin[theta] + lastT * cos[theta]);
                        var candidate = new PixelSegment(a.X, a.Y, b.X, b.Y);
                        if (!features.Segments.Any(s => NearDuplicate(s, candidate, 4.0))) features.Segments.Add(candidate);
                    }
                }
                start = i;
            }

            // Non-maximum suppression around this (theta,rho) peak.
            for (var dt = -2; dt <= 2; dt++)
            for (var dr = -3; dr <= 3; dr++)
            {
                var t = theta + dt; var r = rho + dr + maxRho;
                if (t >= 0 && t < thetaCount && r >= 0 && r < rhoCount) accumulator[t * rhoCount + r] = 0;
            }
            if (features.Segments.Count >= MaxLines) break;
        }
    }

    private static bool NearDuplicate(PixelSegment a, PixelSegment b, double tolerance)
    {
        static double D(double x1, double y1, double x2, double y2) => Math.Sqrt(Sq(x1 - x2) + Sq(y1 - y2));
        return (D(a.X1, a.Y1, b.X1, b.Y1) <= tolerance && D(a.X2, a.Y2, b.X2, b.Y2) <= tolerance)
            || (D(a.X1, a.Y1, b.X2, b.Y2) <= tolerance && D(a.X2, a.Y2, b.X1, b.Y1) <= tolerance);
    }

    private static double NormalizeDegrees(double angle)
    {
        angle %= 360.0;
        return angle < 0 ? angle + 360.0 : angle;
    }

    private static double Sq(double x) => x * x;
}
