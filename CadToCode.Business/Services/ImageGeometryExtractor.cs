using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CadToCode.Business.Services;

public class ImageGeometryExtractor : IGeometryExtractor
{
    private const int MaxWorkingDimension = 1200;
    private const long MaxPixels = 40_000_000;
    private const int ThetaSteps = 360;
    private const int MaxLines = 80;
    private const int MaxThinningIterations = 60;
    private const double InlierDistance = 1.5;
    private const double GapTolerance = 5.0;
    private const double MinInkFraction = 0.0002;

    public bool CanHandle(SourceFileType fileType) => fileType == SourceFileType.Image;

    public Task<ExtractedGeometry> ExtractAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Extract(fileStream, cancellationToken), cancellationToken);
    }

    private static ExtractedGeometry Extract(Stream fileStream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        fileStream.CopyTo(memory);

        memory.Position = 0;
        ImageInfo info;
        try
        {
            info = Image.Identify(memory);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException || ex is InvalidImageContentException || ex is NotSupportedException)
        {
            throw new InvalidOperationException("The image could not be read.");
        }

        if ((long)info.Width * info.Height > MaxPixels)
        {
            throw new InvalidOperationException("The image resolution is too large.");
        }

        memory.Position = 0;
        using var image = Image.Load<Rgba32>(memory);

        var originalWidth = image.Width;
        var width = image.Width;
        var height = image.Height;

        var longest = Math.Max(width, height);
        if (longest > MaxWorkingDimension)
        {
            var ratio = (double)MaxWorkingDimension / longest;
            width = Math.Max(1, (int)Math.Round(width * ratio));
            height = Math.Max(1, (int)Math.Round(height * ratio));
            image.Mutate(context => context.Resize(width, height));
        }

        var factor = (double)originalWidth / width;

        var gray = ToGray(image, width, height);
        var mask = Binarize(gray, width, height);

        var inkCount = mask.Count(v => v);
        if (inkCount < MinInkFraction * width * height)
        {
            throw new InvalidOperationException("No drawing lines were found in the image.");
        }

        ClearBorder(mask, width, height);
        Thin(mask, width, height, cancellationToken);

        var segments = DetectSegments(mask, width, height, cancellationToken);

        var result = new ExtractedGeometry
        {
            DetectedUnit = DrawingUnit.Millimeter
        };

        foreach (var segment in segments)
        {
            result.Lines.Add(new GeometryLine
            {
                Start = new GeometryPoint(segment.X1 * factor, (height - segment.Y1) * factor),
                End = new GeometryPoint(segment.X2 * factor, (height - segment.Y2) * factor),
                Layer = "image"
            });
        }

        return result;
    }

    private static byte[] ToGray(Image<Rgba32> image, int width, int height)
    {
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

    private readonly record struct Segment(double X1, double Y1, double X2, double Y2);
}