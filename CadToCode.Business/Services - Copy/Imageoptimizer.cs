using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CadToCode.Business.Services;

public sealed record OptimizedImage(
    byte[] Bytes,
    string MimeType,
    int Width,
    int Height,
    int OriginalWidth,
    int OriginalHeight);

public interface IImageOptimizer
{
    bool HasSupportedSignature(ReadOnlySpan<byte> data);

    OptimizedImage? Optimize(byte[] data);
}

public sealed class ImageOptimizer : IImageOptimizer
{
    private const long MaxSourcePixels = 50_000_000;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF };

    private readonly VisionOptions _options;
    private readonly ILogger<ImageOptimizer> _logger;

    public ImageOptimizer(IOptions<VisionOptions> options, ILogger<ImageOptimizer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool HasSupportedSignature(ReadOnlySpan<byte> data)
    {
        return data.StartsWith(PngSignature) || data.StartsWith(JpegSignature);
    }

    public OptimizedImage? Optimize(byte[] data)
    {
        if (data is null || data.Length == 0 || data.Length > _options.MaxImageBytes)
        {
            _logger.LogWarning("Image rejected: empty or larger than {Max} bytes.", _options.MaxImageBytes);
            return null;
        }

        if (!HasSupportedSignature(data))
        {
            _logger.LogWarning("Image rejected: the file signature is not PNG or JPEG.");
            return null;
        }

        try
        {
            var info = Image.Identify(data);

            if (info is null || info.Width <= 0 || info.Height <= 0 ||
                (long)info.Width * info.Height > MaxSourcePixels)
            {
                _logger.LogWarning("Image rejected: unreadable or too many pixels.");
                return null;
            }

            using var image = Image.Load<Rgba32>(data);

            var originalWidth = image.Width;
            var originalHeight = image.Height;
            var maxEdge = Math.Max(256, _options.MaxImageEdgePixels);

            image.Mutate(x =>
            {
                x.AutoOrient();
                x.BackgroundColor(Color.White);

                if (Math.Max(image.Width, image.Height) > maxEdge)
                {
                    x.Resize(new ResizeOptions
                    {
                        Size = new Size(maxEdge, maxEdge),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3
                    });
                }
            });

            var quality = Math.Clamp(_options.JpegQuality, 40, 95);

            using var stream = new MemoryStream();
            image.Save(stream, new JpegEncoder { Quality = quality });

            return new OptimizedImage(
                stream.ToArray(),
                "image/jpeg",
                image.Width,
                image.Height,
                originalWidth,
                originalHeight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Image optimization failed.");
            return null;
        }
    }
}