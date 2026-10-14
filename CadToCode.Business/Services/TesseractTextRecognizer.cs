using CadToCode.Business.Interfaces;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tesseract;

namespace CadToCode.Business.Services;

/// <summary>
/// Local OCR adapter for image drawings. OCR is deliberately best-effort: unavailable
/// tessdata or a recognition failure returns no words so the geometric extractor can
/// continue with line/curve analysis. Returned boxes are mapped back to source-image pixels.
/// </summary>
public sealed class TesseractTextRecognizer : IImageTextRecognizer, IDisposable
{
    private const string Language = "eng";
    private const int PreferredLongestSide = 2200;
    private const int MaximumLongestSide = 3000;

    private readonly ExtractionOptions _options;
    private readonly ILogger<TesseractTextRecognizer> _logger;
    private readonly object _gate = new();

    private TesseractEngine? _engine;
    private bool _initialized;
    private bool _disposed;

    public TesseractTextRecognizer(
        IOptions<ExtractionOptions> options,
        ILogger<TesseractTextRecognizer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                EnsureInitialized();
                return _engine is not null;
            }
        }
    }

    public Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return Task.Run(() => Recognize(imageBytes, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<RecognizedWord> Recognize(byte[] imageBytes, CancellationToken cancellationToken)
    {
        var words = new List<RecognizedWord>();
        if (imageBytes.Length == 0)
        {
            return words;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var threshold = Math.Clamp(_options.OcrConfidenceThreshold, 0.0, 1.0) * 100.0;

        lock (_gate)
        {
            EnsureInitialized();
            if (_engine is null)
            {
                return words;
            }

            try
            {
                using var source = Image.Load<Rgba32>(imageBytes);
                var sourceWidth = source.Width;
                var sourceHeight = source.Height;
                if (sourceWidth <= 0 || sourceHeight <= 0)
                {
                    return words;
                }

                // Small screenshots often have tiny dimension text. Upscale them for OCR;
                // unusually large images are bounded to control CPU and memory use.
                var longest = Math.Max(sourceWidth, sourceHeight);
                var targetLongest = longest < PreferredLongestSide
                    ? PreferredLongestSide
                    : Math.Min(longest, MaximumLongestSide);
                if (targetLongest != longest)
                {
                    var ratio = (double)targetLongest / longest;
                    var width = Math.Max(1, (int)Math.Round(sourceWidth * ratio));
                    var height = Math.Max(1, (int)Math.Round(sourceHeight * ratio));
                    source.Mutate(ctx => ctx.Resize(width, height, KnownResamplers.Lanczos3));
                }

                var processedWidth = source.Width;
                var processedHeight = source.Height;
                var scaleX = (double)processedWidth / sourceWidth;
                var scaleY = (double)processedHeight / sourceHeight;

                // Grayscale and contrast enhancement improve thin callouts on screenshots.
                source.Mutate(ctx => ctx.Grayscale().Contrast(1.35f));
                using var processedStream = new MemoryStream();
                source.SaveAsPng(processedStream);
                var processedBytes = processedStream.ToArray();

                using var pix = Pix.LoadFromMemory(processedBytes);
                using var page = _engine.Process(pix, PageSegMode.SparseText);
                using var iterator = page.GetIterator();
                iterator.Begin();

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = iterator.GetText(PageIteratorLevel.Word);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var confidence = iterator.GetConfidence(PageIteratorLevel.Word);
                    if (!float.IsFinite(confidence) || confidence < threshold)
                    {
                        continue;
                    }

                    if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
                    {
                        continue;
                    }

                    // The downstream feature extractor operates in source-image pixel space.
                    var x = (int)Math.Floor(rect.X1 / scaleX);
                    var y = (int)Math.Floor(rect.Y1 / scaleY);
                    var right = (int)Math.Ceiling(rect.X2 / scaleX);
                    var bottom = (int)Math.Ceiling(rect.Y2 / scaleY);
                    x = Math.Clamp(x, 0, sourceWidth - 1);
                    y = Math.Clamp(y, 0, sourceHeight - 1);
                    right = Math.Clamp(right, x + 1, sourceWidth);
                    bottom = Math.Clamp(bottom, y + 1, sourceHeight);

                    words.Add(new RecognizedWord(
                        text.Trim(),
                        confidence / 100.0,
                        x,
                        y,
                        right - x,
                        bottom - y));
                }
                while (iterator.Next(PageIteratorLevel.Word));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Text recognition failed for the uploaded image.");
                words.Clear();
            }
        }

        return words;
    }

    private void EnsureInitialized()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        try
        {
            var path = _options.TessdataPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "tessdata";
            }

            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, path);
            }

            var trainedData = Path.Combine(path, Language + ".traineddata");
            if (!File.Exists(trainedData))
            {
                _logger.LogInformation(
                    "OCR language data was not found at {Path}. Image text recognition is disabled.",
                    trainedData);
                return;
            }

            _engine = new TesseractEngine(path, Language, EngineMode.Default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The OCR engine could not be started. Image text recognition is disabled.");
            _engine = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
    }
}
