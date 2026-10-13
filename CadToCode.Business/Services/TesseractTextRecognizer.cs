using CadToCode.Business.Interfaces;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tesseract;

namespace CadToCode.Business.Services;

public sealed class TesseractTextRecognizer : IImageTextRecognizer, IDisposable
{
    private const string Language = "eng";

    private readonly ExtractionOptions _options;
    private readonly ILogger<TesseractTextRecognizer> _logger;
    private readonly object _gate = new();

    private TesseractEngine? _engine;
    private bool _initialized;
    private bool _disposed;

    public TesseractTextRecognizer(IOptions<ExtractionOptions> options, ILogger<TesseractTextRecognizer> logger)
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

    public Task<IReadOnlyList<RecognizedWord>> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Recognize(imageBytes, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<RecognizedWord> Recognize(byte[] imageBytes, CancellationToken cancellationToken)
    {
        var words = new List<RecognizedWord>();

        if (imageBytes is null || imageBytes.Length == 0)
        {
            return words;
        }

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
                cancellationToken.ThrowIfCancellationRequested();

                using var pix = Pix.LoadFromMemory(imageBytes);
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
                    if (confidence < threshold)
                    {
                        continue;
                    }

                    if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
                    {
                        continue;
                    }

                    words.Add(new RecognizedWord(
                        text.Trim(),
                        confidence / 100.0,
                        rect.X1,
                        rect.Y1,
                        Math.Max(1, rect.Width),
                        Math.Max(1, rect.Height)));
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

            var trained = Path.Combine(path, Language + ".traineddata");
            if (!File.Exists(trained))
            {
                _logger.LogInformation("OCR language data was not found at {Path}. Image text recognition is disabled.", trained);
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