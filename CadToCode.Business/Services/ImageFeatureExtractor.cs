using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CadToCode.Business.Services;

public readonly record struct PixelRegion(int X, int Y, int Width, int Height);

public readonly record struct PixelPoint(double X, double Y);

public readonly record struct PixelSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length
    {
        get
        {
            var dx = X2 - X1;
            var dy = Y2 - Y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
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

public static class ImageFeatureExtractor
{
    private const int ThetaSteps = 360;
    private const int MaxLines = 80;
    private const int MaxThinningIterations = 60;
    private const double InlierDistance = 1.5;
    private const double GapTolerance = 5.0;
    private const double MinInkFraction = 0.0002;

    private const int AngleBins = 36;
    private const int MinCurvePixels = 24;
    private const double MinCurveRadius = 5.0;
    private const double CurveTolerance = 1.6;
    private const int MaxCurves = 60;
    private const int MaxRansacComponents = 12;
    private const int RansacPasses = 4;
    private const int RansacIterations = 1200;
    private const int RansacSampleCap = 2500;

    private const int GapCloseRadius = 2;
    private const double MinLoopAreaFraction = 0.0008;
    private const int MaxLoopCandidates = 40;
    private const int MaxLoops = 24;
    private const double FrameFraction = 0.92;

    public static byte[] ToGray(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        var gray = new byte[width * height];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    var alpha = pixel.A / 255.0;
                    var luminance = 0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B;
                    var blended = luminance * alpha + 255.0 * (1.0 - alpha);
                    gray[y * width + x] = (byte)Math.Clamp((int)Math.Round(blended), 0, 255);
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
        var mask = Binarize(gray, width, height);
        ClearBorder(mask, width, height);
        EraseRegions(mask, width, height, textRegions, 2);

        var inkCount = CountSet(mask);
        if (inkCount < MinInkFraction * width * height)
        {
            throw new InvalidOperationException("No drawing lines were found in the image.");
        }

        var skeleton = (bool[])mask.Clone();
        Thin(skeleton, width, height, cancellationToken);

        var skeletonCount = CountSet(skeleton);
        if (skeletonCount == 0)
        {
            throw new InvalidOperationException("No drawing lines were found in the image.");
        }

        var features = new ImageFeatures
        {
            Width = width,
            Height = height,
            InkWidth = Math.Max(1.0, inkCount / (double)skeletonCount)
        };

        var barrier = (bool[])skeleton.Clone();

        DetectCurves(skeleton, width, height, features, cancellationToken);

        foreach (var segment in DetectSegments(skeleton, width, height, cancellationToken))
        {
            features.Segments.Add(new PixelSegment(segment.X1, segment.Y1, segment.X2, segment.Y2));
        }

        DetectLoops(barrier, width, height, features, cancellationToken);

        return features;
    }

    private static int CountSet(bool[] mask)
    {
        var count = 0;
        foreach (var value in mask)
        {
            if (value)
            {
                count++;
            }
        }

        return count;
    }

    private static void EraseRegions(bool[] mask, int width, int height, IReadOnlyList<PixelRegion> regions, int padding)
    {
        if (regions is null)
        {
            return;
        }

        foreach (var region in regions)
        {
            var left = Math.Max(0, region.X - padding);
            var top = Math.Max(0, region.Y - padding);
            var right = Math.Min(width - 1, region.X + region.Width + padding);
            var bottom = Math.Min(height - 1, region.Y + region.Height + padding);

            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    mask[y * width + x] = false;
                }
            }
        }
    }

    private static bool[] Binarize(byte[] gray, int width, int height)
    {
        var histogram = new int[256];
        foreach (var value in gray)
        {
            histogram[value]++;
        }

        var total = gray.Length;
        double sumAll = 0;
        for (var i = 0; i < 256; i++)
        {
            sumAll += i * (double)histogram[i];
        }

        double sumBackground = 0;
        long weightBackground = 0;
        double bestVariance = -1;
        var threshold = 127;

        for (var i = 0; i < 256; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0)
            {
                continue;
            }

            var weightForeground = total - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += i * (double)histogram[i];
            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sumAll - sumBackground) / weightForeground;
            var between = (double)weightBackground * weightForeground * Math.Pow(meanBackground - meanForeground, 2);

            if (between > bestVariance)
            {
                bestVariance = between;
                threshold = i;
            }
        }

        var mask = new bool[width * height];
        var dark = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            if (gray[i] <= threshold)
            {
                mask[i] = true;
                dark++;
            }
        }

        if (dark > total / 2)
        {
            for (var i = 0; i < mask.Length; i++)
            {
                mask[i] = !mask[i];
            }
        }

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

    private static void Thin(bool[] mask, int width, int height, CancellationToken cancellationToken)
    {
        var toClear = new List<int>();
        var changed = true;
        var iteration = 0;

        while (changed && iteration < MaxThinningIterations)
        {
            changed = false;
            iteration++;

            for (var step = 0; step < 2; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                toClear.Clear();

                for (var y = 1; y < height - 1; y++)
                {
                    for (var x = 1; x < width - 1; x++)
                    {
                        var i = y * width + x;
                        if (!mask[i])
                        {
                            continue;
                        }

                        var p2 = mask[i - width];
                        var p3 = mask[i - width + 1];
                        var p4 = mask[i + 1];
                        var p5 = mask[i + width + 1];
                        var p6 = mask[i + width];
                        var p7 = mask[i + width - 1];
                        var p8 = mask[i - 1];
                        var p9 = mask[i - width - 1];

                        var neighbours = Bit(p2) + Bit(p3) + Bit(p4) + Bit(p5) + Bit(p6) + Bit(p7) + Bit(p8) + Bit(p9);
                        if (neighbours < 2 || neighbours > 6)
                        {
                            continue;
                        }

                        var transitions =
                            (!p2 && p3 ? 1 : 0) + (!p3 && p4 ? 1 : 0) + (!p4 && p5 ? 1 : 0) + (!p5 && p6 ? 1 : 0) +
                            (!p6 && p7 ? 1 : 0) + (!p7 && p8 ? 1 : 0) + (!p8 && p9 ? 1 : 0) + (!p9 && p2 ? 1 : 0);
                        if (transitions != 1)
                        {
                            continue;
                        }

                        if (step == 0)
                        {
                            if ((p2 && p4 && p6) || (p4 && p6 && p8))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            if ((p2 && p4 && p8) || (p2 && p6 && p8))
                            {
                                continue;
                            }
                        }

                        toClear.Add(i);
                    }
                }

                foreach (var index in toClear)
                {
                    mask[index] = false;
                }

                if (toClear.Count > 0)
                {
                    changed = true;
                }
            }
        }
    }

    private static int Bit(bool value) => value ? 1 : 0;

    private static List<int[]> LabelComponents(bool[] mask, int width, int height)
    {
        var result = new List<int[]>();
        var visited = new bool[mask.Length];
        var queue = new int[mask.Length];

        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || visited[i])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = i;
            visited[i] = true;

            while (head < tail)
            {
                var p = queue[head++];
                var px = p % width;
                var py = p / width;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = py + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        var nx = px + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        var n = ny * width + nx;
                        if (mask[n] && !visited[n])
                        {
                            visited[n] = true;
                            queue[tail++] = n;
                        }
                    }
                }
            }

            var pixels = new int[tail];
            Array.Copy(queue, pixels, tail);
            result.Add(pixels);
        }

        return result;
    }

    private static void DetectCurves(
        bool[] skeleton,
        int width,
        int height,
        ImageFeatures features,
        CancellationToken cancellationToken)
    {
        var components = LabelComponents(skeleton, width, height)
            .Where(c => c.Length >= MinCurvePixels)
            .OrderByDescending(c => c.Length)
            .ToList();

        var random = new Random(7919);
        var maxRadius = 0.5 * Math.Max(width, height);
        var ransacBudget = MaxRansacComponents;

        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (features.Circles.Count + features.Arcs.Count >= MaxCurves)
            {
                break;
            }

            if (TryWholeComponent(component, width, features, maxRadius))
            {
                foreach (var pixel in component)
                {
                    skeleton[pixel] = false;
                }

                continue;
            }

            if (component.Length < 3 * MinCurvePixels || ransacBudget <= 0)
            {
                continue;
            }

            ransacBudget--;
            RansacComponent(component, width, maxRadius, random, features, skeleton, cancellationToken);
        }
    }

    private static bool TryWholeComponent(int[] pixels, int width, ImageFeatures features, double maxRadius)
    {
        if (!FitCircle(pixels, width, out var cx, out var cy, out var r))
        {
            return false;
        }

        if (r < MinCurveRadius || r > maxRadius)
        {
            return false;
        }

        var inliers = CollectInliers(pixels, width, cx, cy, r, Math.Max(CurveTolerance, 0.02 * r));
        if (inliers.Count < 0.92 * pixels.Length)
        {
            return false;
        }

        return AcceptCurve(inliers, width, cx, cy, r, features);
    }

    private static void RansacComponent(
        int[] pixels,
        int width,
        double maxRadius,
        Random random,
        ImageFeatures features,
        bool[] skeleton,
        CancellationToken cancellationToken)
    {
        var remaining = new List<int>(pixels);

        for (var pass = 0; pass < RansacPasses; pass++)
        {
            if (remaining.Count < MinCurvePixels || features.Circles.Count + features.Arcs.Count >= MaxCurves)
            {
                return;
            }

            var step = Math.Max(1, remaining.Count / RansacSampleCap);
            var bestCount = 0;
            double bestX = 0;
            double bestY = 0;
            double bestR = 0;

            for (var iteration = 0; iteration < RansacIterations; iteration++)
            {
                if ((iteration & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var a = remaining[random.Next(remaining.Count)];
                var b = remaining[random.Next(remaining.Count)];
                var c = remaining[random.Next(remaining.Count)];

                if (a == b || b == c || a == c)
                {
                    continue;
                }

                if (!CircleFromPoints(a % width, a / width, b % width, b / width, c % width, c / width, out var cx, out var cy, out var r))
                {
                    continue;
                }

                if (r < MinCurveRadius || r > maxRadius)
                {
                    continue;
                }

                var count = 0;
                for (var k = 0; k < remaining.Count; k += step)
                {
                    var p = remaining[k];
                    var dx = p % width - cx;
                    var dy = p / width - cy;

                    if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r) <= CurveTolerance)
                    {
                        count++;
                    }
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    bestX = cx;
                    bestY = cy;
                    bestR = r;
                }
            }

            if (bestCount * step < MinCurvePixels)
            {
                return;
            }

            var seed = CollectInliers(remaining, width, bestX, bestY, bestR, CurveTolerance);
            if (seed.Count < MinCurvePixels)
            {
                return;
            }

            if (!FitCircle(seed, width, out var fx, out var fy, out var fr))
            {
                return;
            }

            if (fr < MinCurveRadius || fr > maxRadius)
            {
                return;
            }

            var inliers = CollectInliers(remaining, width, fx, fy, fr, Math.Max(CurveTolerance, 0.02 * fr));

            if (!AcceptCurve(inliers, width, fx, fy, fr, features))
            {
                return;
            }

            var removed = new HashSet<int>(inliers);
            foreach (var pixel in inliers)
            {
                skeleton[pixel] = false;
            }

            remaining = remaining.Where(p => !removed.Contains(p)).ToList();
        }
    }

    private static bool AcceptCurve(
        IReadOnlyList<int> inliers,
        int width,
        double cx,
        double cy,
        double r,
        ImageFeatures features)
    {
        var binSize = 360.0 / AngleBins;
        var bins = new int[AngleBins];

        foreach (var pixel in inliers)
        {
            var angle = AngleOf(pixel, width, cx, cy);
            bins[Math.Min(AngleBins - 1, (int)(angle / binSize))]++;
        }

        var occupied = new bool[AngleBins];
        var occupiedCount = 0;
        for (var i = 0; i < AngleBins; i++)
        {
            occupied[i] = bins[i] >= 2;
            if (occupied[i])
            {
                occupiedCount++;
            }
        }

        if (occupiedCount >= 31)
        {
            if (inliers.Count < 0.55 * 2.0 * Math.PI * r)
            {
                return false;
            }

            features.Circles.Add(new PixelCircle(cx, cy, r));
            return true;
        }

        var bestStart = -1;
        var bestLength = 0;

        for (var start = 0; start < AngleBins; start++)
        {
            if (!occupied[start] || occupied[(start + AngleBins - 1) % AngleBins])
            {
                continue;
            }

            var length = 0;
            while (length < AngleBins && occupied[(start + length) % AngleBins])
            {
                length++;
            }

            if (length > bestLength)
            {
                bestLength = length;
                bestStart = start;
            }
        }

        if (bestStart < 0 || bestLength < 6)
        {
            return false;
        }

        var runStart = bestStart * binSize;
        var runSpan = bestLength * binSize;
        var minRelative = double.MaxValue;
        var maxRelative = double.MinValue;
        var inRun = 0;

        foreach (var pixel in inliers)
        {
            var relative = AngleOf(pixel, width, cx, cy) - runStart;
            if (relative < 0)
            {
                relative += 360.0;
            }

            if (relative > runSpan)
            {
                continue;
            }

            inRun++;
            minRelative = Math.Min(minRelative, relative);
            maxRelative = Math.Max(maxRelative, relative);
        }

        if (inRun == 0)
        {
            return false;
        }

        var span = maxRelative - minRelative;
        if (inRun < 0.55 * r * span * Math.PI / 180.0)
        {
            return false;
        }

        var sagitta = r * (1.0 - Math.Cos(span * Math.PI / 360.0));
        if (sagitta < 3.0)
        {
            return false;
        }

        features.Arcs.Add(new PixelArc(
            cx,
            cy,
            r,
            NormalizeDegrees(runStart + minRelative),
            NormalizeDegrees(runStart + maxRelative)));

        return true;
    }

    private static double AngleOf(int pixel, int width, double cx, double cy)
    {
        var angle = Math.Atan2(pixel / width - cy, pixel % width - cx) * 180.0 / Math.PI;
        return angle < 0 ? angle + 360.0 : angle;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var value = degrees % 360.0;
        return value < 0 ? value + 360.0 : value;
    }

    private static List<int> CollectInliers(
        IReadOnlyList<int> pixels,
        int width,
        double cx,
        double cy,
        double r,
        double tolerance)
    {
        var result = new List<int>();

        for (var i = 0; i < pixels.Count; i++)
        {
            var p = pixels[i];
            var dx = p % width - cx;
            var dy = p / width - cy;

            if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r) <= tolerance)
            {
                result.Add(p);
            }
        }

        return result;
    }

    private static bool FitCircle(IReadOnlyList<int> pixels, int width, out double cx, out double cy, out double r)
    {
        cx = 0;
        cy = 0;
        r = 0;

        var n = pixels.Count;
        if (n < 3)
        {
            return false;
        }

        double meanX = 0;
        double meanY = 0;
        for (var i = 0; i < n; i++)
        {
            meanX += pixels[i] % width;
            meanY += pixels[i] / width;
        }

        meanX /= n;
        meanY /= n;

        double suu = 0, suv = 0, svv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;

        for (var i = 0; i < n; i++)
        {
            var u = pixels[i] % width - meanX;
            var v = pixels[i] / width - meanY;

            suu += u * u;
            suv += u * v;
            svv += v * v;
            suuu += u * u * u;
            svvv += v * v * v;
            suvv += u * v * v;
            svuu += v * u * u;
        }

        var determinant = suu * svv - suv * suv;
        if (Math.Abs(determinant) < 1e-9)
        {
            return false;
        }

        var b1 = (suuu + suvv) / 2.0;
        var b2 = (svvv + svuu) / 2.0;

        var uc = (b1 * svv - b2 * suv) / determinant;
        var vc = (suu * b2 - suv * b1) / determinant;

        var radiusSquared = uc * uc + vc * vc + (suu + svv) / n;
        if (radiusSquared <= 0 || double.IsNaN(radiusSquared) || double.IsInfinity(radiusSquared))
        {
            return false;
        }

        cx = meanX + uc;
        cy = meanY + vc;
        r = Math.Sqrt(radiusSquared);

        return !double.IsNaN(cx) && !double.IsNaN(cy);
    }

    private static bool CircleFromPoints(
        double x1,
        double y1,
        double x2,
        double y2,
        double x3,
        double y3,
        out double cx,
        out double cy,
        out double r)
    {
        cx = 0;
        cy = 0;
        r = 0;

        var d = 2.0 * (x1 * (y2 - y3) + x2 * (y3 - y1) + x3 * (y1 - y2));
        if (Math.Abs(d) < 1e-6)
        {
            return false;
        }

        var s1 = x1 * x1 + y1 * y1;
        var s2 = x2 * x2 + y2 * y2;
        var s3 = x3 * x3 + y3 * y3;

        cx = (s1 * (y2 - y3) + s2 * (y3 - y1) + s3 * (y1 - y2)) / d;
        cy = (s1 * (x3 - x2) + s2 * (x1 - x3) + s3 * (x2 - x1)) / d;

        var dx = x1 - cx;
        var dy = y1 - cy;
        r = Math.Sqrt(dx * dx + dy * dy);

        return !double.IsNaN(r) && !double.IsInfinity(r);
    }

    private readonly record struct Region(
        int Label,
        int Area,
        bool TouchesBorder,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY,
        int StartX,
        int StartY);

    private static void DetectLoops(
        bool[] barrier,
        int width,
        int height,
        ImageFeatures features,
        CancellationToken cancellationToken)
    {
        var closed = Dilate(barrier, width, height, GapCloseRadius);
        var labels = new int[width * height];
        var queue = new int[width * height];
        var regions = new List<Region>();
        var nextLabel = 0;

        for (var i = 0; i < closed.Length; i++)
        {
            if (closed[i] || labels[i] != 0)
            {
                continue;
            }

            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            nextLabel++;

            var head = 0;
            var tail = 0;
            queue[tail++] = i;
            labels[i] = nextLabel;

            var area = 0;
            var touches = false;
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;

            while (head < tail)
            {
                var p = queue[head++];
                var x = p % width;
                var y = p / width;

                area++;

                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    touches = true;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;

                int n;

                if (x > 0)
                {
                    n = p - 1;
                    if (!closed[n] && labels[n] == 0)
                    {
                        labels[n] = nextLabel;
                        queue[tail++] = n;
                    }
                }

                if (x < width - 1)
                {
                    n = p + 1;
                    if (!closed[n] && labels[n] == 0)
                    {
                        labels[n] = nextLabel;
                        queue[tail++] = n;
                    }
                }

                if (y > 0)
                {
                    n = p - width;
                    if (!closed[n] && labels[n] == 0)
                    {
                        labels[n] = nextLabel;
                        queue[tail++] = n;
                    }
                }

                if (y < height - 1)
                {
                    n = p + width;
                    if (!closed[n] && labels[n] == 0)
                    {
                        labels[n] = nextLabel;
                        queue[tail++] = n;
                    }
                }
            }

            regions.Add(new Region(nextLabel, area, touches, minX, minY, maxX, maxY, i % width, i / width));
        }

        var minArea = Math.Max(30, (int)(MinLoopAreaFraction * width * height));

        var candidates = regions
            .Where(r => !r.TouchesBorder
                && r.Area >= minArea
                && r.MaxX - r.MinX >= 8
                && r.MaxY - r.MinY >= 8)
            .OrderByDescending(r => r.Area)
            .Take(MaxLoopCandidates)
            .ToList();

        var epsilon = Math.Max(1.5, 0.004 * Math.Max(width, height));
        var offset = GapCloseRadius + 1.0;
        var loops = new List<PixelLoop>();

        foreach (var region in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var traced = TraceBoundary(labels, region, width, height);
            if (traced.Count < 8)
            {
                continue;
            }

            var simplified = SimplifyClosed(traced, epsilon);
            if (simplified.Count < 3)
            {
                continue;
            }

            var expanded = OffsetPolygon(simplified, offset);

            var loop = new PixelLoop();
            foreach (var point in expanded)
            {
                loop.Points.Add(new PixelPoint(
                    Math.Clamp(point.X, 0, width),
                    Math.Clamp(point.Y, 0, height)));
            }

            loop.Area = Math.Abs(SignedArea(loop.Points));
            loop.MinX = loop.Points.Min(p => p.X);
            loop.MinY = loop.Points.Min(p => p.Y);
            loop.MaxX = loop.Points.Max(p => p.X);
            loop.MaxY = loop.Points.Max(p => p.Y);

            if (loop.Area <= 0 || IsCircleDuplicate(loop, features.Circles))
            {
                continue;
            }

            loops.Add(loop);
        }

        loops = loops.OrderByDescending(l => l.Area).ToList();

        if (loops.Count >= 2 && LooksLikeSheetBorder(loops[0], loops.Skip(1).ToList(), width, height))
        {
            loops.RemoveAt(0);
            features.Notes.Add("A sheet border was ignored.");
        }

        foreach (var loop in loops.Take(MaxLoops))
        {
            features.Loops.Add(loop);
        }
    }

    private static bool LooksLikeSheetBorder(PixelLoop top, List<PixelLoop> others, int width, int height)
    {
        if (top.Points.Count != 4)
        {
            return false;
        }

        if (top.MaxX - top.MinX < FrameFraction * width || top.MaxY - top.MinY < FrameFraction * height)
        {
            return false;
        }

        var left = top.MinX / width;
        var upper = top.MinY / height;
        var right = (width - top.MaxX) / width;
        var lower = (height - top.MaxY) / height;

        bool InRange(double margin) => margin >= 0.01 && margin <= 0.10;

        if (!InRange(left) || !InRange(upper) || !InRange(right) || !InRange(lower))
        {
            return false;
        }

        return others.Any(o => o.Area >= 0.03 * width * height);
    }

    private static bool IsCircleDuplicate(PixelLoop loop, List<PixelCircle> circles)
    {
        var centerX = (loop.MinX + loop.MaxX) / 2.0;
        var centerY = (loop.MinY + loop.MaxY) / 2.0;

        foreach (var circle in circles)
        {
            var reach = 0.25 * circle.Radius + 3.0;
            var circleArea = Math.PI * circle.Radius * circle.Radius;

            if (Math.Abs(centerX - circle.CenterX) <= reach
                && Math.Abs(centerY - circle.CenterY) <= reach
                && loop.Area >= 0.6 * circleArea
                && loop.Area <= 1.5 * circleArea)
            {
                return true;
            }
        }

        return false;
    }

    private static bool[] Dilate(bool[] source, int width, int height, int radius)
    {
        var horizontal = new bool[source.Length];
        var result = new bool[source.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!source[y * width + x])
                {
                    continue;
                }

                var from = Math.Max(0, x - radius);
                var to = Math.Min(width - 1, x + radius);
                for (var k = from; k <= to; k++)
                {
                    horizontal[y * width + k] = true;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!horizontal[y * width + x])
                {
                    continue;
                }

                var from = Math.Max(0, y - radius);
                var to = Math.Min(height - 1, y + radius);
                for (var k = from; k <= to; k++)
                {
                    result[k * width + x] = true;
                }
            }
        }

        return result;
    }

    private static readonly int[] DirectionX = { -1, -1, 0, 1, 1, 1, 0, -1 };
    private static readonly int[] DirectionY = { 0, -1, -1, -1, 0, 1, 1, 1 };

    private static List<PixelPoint> TraceBoundary(int[] labels, Region region, int width, int height)
    {
        var points = new List<PixelPoint>();

        var startX = region.StartX;
        var startY = region.StartY;
        var currentX = startX;
        var currentY = startY;
        var backtrack = 0;
        var maxSteps = 4 * (region.Area + 4);
        var steps = 0;

        points.Add(new PixelPoint(currentX, currentY));

        do
        {
            var found = false;

            for (var k = 1; k <= 8; k++)
            {
                var direction = (backtrack + k) % 8;
                var nx = currentX + DirectionX[direction];
                var ny = currentY + DirectionY[direction];

                if (nx < 0 || ny < 0 || nx >= width || ny >= height || labels[ny * width + nx] != region.Label)
                {
                    continue;
                }

                var previous = (direction + 7) % 8;
                var bx = currentX + DirectionX[previous];
                var by = currentY + DirectionY[previous];
                backtrack = DirectionIndex(bx - nx, by - ny);

                currentX = nx;
                currentY = ny;
                found = true;
                break;
            }

            if (!found)
            {
                break;
            }

            points.Add(new PixelPoint(currentX, currentY));
            steps++;
        }
        while (!(currentX == startX && currentY == startY) && steps < maxSteps);

        if (points.Count > 1 && points[^1].X == startX && points[^1].Y == startY)
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    private static int DirectionIndex(int dx, int dy)
    {
        for (var i = 0; i < 8; i++)
        {
            if (DirectionX[i] == dx && DirectionY[i] == dy)
            {
                return i;
            }
        }

        return 0;
    }

    private static List<PixelPoint> SimplifyClosed(List<PixelPoint> points, double epsilon)
    {
        if (points.Count < 4)
        {
            return points;
        }

        var far = 0;
        var farDistance = -1.0;

        for (var i = 1; i < points.Count; i++)
        {
            var distance = Distance(points[0], points[i]);
            if (distance > farDistance)
            {
                farDistance = distance;
                far = i;
            }
        }

        if (far == 0)
        {
            return points;
        }

        var first = points.GetRange(0, far + 1);
        var second = points.GetRange(far, points.Count - far);
        second.Add(points[0]);

        var a = Rdp(first, epsilon);
        var b = Rdp(second, epsilon);

        var result = new List<PixelPoint>(a);
        for (var i = 1; i < b.Count - 1; i++)
        {
            result.Add(b[i]);
        }

        return result;
    }

    private static List<PixelPoint> Rdp(List<PixelPoint> points, double epsilon)
    {
        if (points.Count < 3)
        {
            return points;
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            var maxDistance = -1.0;
            var index = -1;

            for (var i = start + 1; i < end; i++)
            {
                var distance = PerpendicularDistance(points[i], points[start], points[end]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    index = i;
                }
            }

            if (index >= 0 && maxDistance > epsilon)
            {
                keep[index] = true;
                stack.Push((start, index));
                stack.Push((index, end));
            }
        }

        var result = new List<PixelPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    private static double PerpendicularDistance(PixelPoint point, PixelPoint a, PixelPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < 1e-9)
        {
            return Distance(point, a);
        }

        return Math.Abs(dy * point.X - dx * point.Y + b.X * a.Y - b.Y * a.X) / length;
    }

    private static double Distance(PixelPoint a, PixelPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SignedArea(List<PixelPoint> points)
    {
        var sum = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum / 2.0;
    }

    private static List<PixelPoint> OffsetPolygon(List<PixelPoint> points, double distance)
    {
        var count = points.Count;
        var orientation = SignedArea(points) >= 0 ? 1.0 : -1.0;
        var normals = new (double X, double Y)[count];

        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % count];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var length = Math.Sqrt(ex * ex + ey * ey);

            if (length < 1e-9)
            {
                normals[i] = (0, 0);
                continue;
            }

            ex /= length;
            ey /= length;

            normals[i] = orientation > 0 ? (ey, -ex) : (-ey, ex);
        }

        var result = new List<PixelPoint>(count);

        for (var i = 0; i < count; i++)
        {
            var previous = normals[(i + count - 1) % count];
            var next = normals[i];

            var nx = previous.X + next.X;
            var ny = previous.Y + next.Y;
            var length = Math.Sqrt(nx * nx + ny * ny);

            if (length < 1e-9)
            {
                result.Add(points[i]);
                continue;
            }

            nx /= length;
            ny /= length;

            var cosine = nx * next.X + ny * next.Y;
            var scale = Math.Min(2.5, 1.0 / Math.Max(0.4, cosine));

            result.Add(new PixelPoint(points[i].X + nx * distance * scale, points[i].Y + ny * distance * scale));
        }

        return result;
    }

    private readonly record struct Segment(double X1, double Y1, double X2, double Y2);

    private static List<Segment> DetectSegments(bool[] mask, int width, int height, CancellationToken cancellationToken)
    {
        var xs = new List<double>();
        var ys = new List<double>();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x])
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        var count = xs.Count;
        var segments = new List<Segment>();
        if (count == 0)
        {
            return segments;
        }

        var minLength = Math.Max(15.0, 0.06 * Math.Max(width, height));

        var cos = new double[ThetaSteps];
        var sin = new double[ThetaSteps];
        for (var t = 0; t < ThetaSteps; t++)
        {
            var angle = Math.PI * t / ThetaSteps;
            cos[t] = Math.Cos(angle);
            sin[t] = Math.Sin(angle);
        }

        var maxRho = (int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height));
        var rhoCount = 2 * maxRho + 1;
        var accumulator = new int[ThetaSteps * rhoCount];
        var alive = new bool[count];

        void Vote(int index, int delta)
        {
            var px = xs[index];
            var py = ys[index];
            for (var t = 0; t < ThetaSteps; t++)
            {
                var rho = (int)Math.Round(px * cos[t] + py * sin[t]) + maxRho;
                accumulator[t * rhoCount + rho] += delta;
            }
        }

        for (var i = 0; i < count; i++)
        {
            alive[i] = true;
            Vote(i, 1);
        }

        for (var iteration = 0; iteration < MaxLines; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var peakIndex = 0;
            var peakVotes = 0;
            for (var i = 0; i < accumulator.Length; i++)
            {
                if (accumulator[i] > peakVotes)
                {
                    peakVotes = accumulator[i];
                    peakIndex = i;
                }
            }

            if (peakVotes < minLength)
            {
                break;
            }

            var thetaIndex = peakIndex / rhoCount;
            var rhoValue = (double)(peakIndex % rhoCount - maxRho);
            var cosT = cos[thetaIndex];
            var sinT = sin[thetaIndex];

            var inliers = new List<(double T, int Index)>();
            for (var i = 0; i < count; i++)
            {
                if (!alive[i])
                {
                    continue;
                }

                var distance = Math.Abs(xs[i] * cosT + ys[i] * sinT - rhoValue);
                if (distance <= InlierDistance)
                {
                    inliers.Add((-xs[i] * sinT + ys[i] * cosT, i));
                }
            }

            inliers.Sort((a, b) => a.T.CompareTo(b.T));

            var runStart = 0;
            for (var i = 1; i <= inliers.Count; i++)
            {
                var endOfRun = i == inliers.Count || inliers[i].T - inliers[i - 1].T > GapTolerance;
                if (!endOfRun)
                {
                    continue;
                }

                var startT = inliers[runStart].T;
                var endT = inliers[i - 1].T;

                if (endT - startT >= minLength)
                {
                    segments.Add(new Segment(
                        rhoValue * cosT - startT * sinT,
                        rhoValue * sinT + startT * cosT,
                        rhoValue * cosT - endT * sinT,
                        rhoValue * sinT + endT * cosT));

                    for (var k = runStart; k < i; k++)
                    {
                        var pixel = inliers[k].Index;
                        alive[pixel] = false;
                        Vote(pixel, -1);
                    }
                }

                runStart = i;
            }

            accumulator[peakIndex] = 0;
        }

        return segments;
    }
}