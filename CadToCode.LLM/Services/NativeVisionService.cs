using System.Text;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Prompts;
using CadToCode.LLM.Vision;
using CadToCode.Models.Options;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class NativeVisionService : IVisionInspectionService, IDisposable
{
    private const int MinTokens = 64;
    private const int MinTimeoutSeconds = 30;
    private const int CharsPerTokenCap = 8;
    private const int GateWaitSeconds = 20;

    private readonly VisionOptions _options;
    private readonly ILogger<NativeVisionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer? _idleTimer;
    private LLamaWeights? _weights;
    private MtmdWeights? _clip;
    private ModelParams? _modelParams;
    private string? _loadFailure;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private volatile bool _disposed;

    public NativeVisionService(
        IOptions<VisionOptions> options,
        ILogger<NativeVisionService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.IdleUnloadMinutes > 0)
        {
            _idleTimer = new Timer(_ => TryUnloadIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    public bool IsAvailable =>
        _options.Enabled
        && _loadFailure == null
        && File.Exists(ResolvePath(_options.ModelPath))
        && File.Exists(ResolvePath(_options.MmprojPath));

    public string ModelName => Path.GetFileNameWithoutExtension(_options.ModelPath) ?? string.Empty;

    public async Task<VisionInspectionResult?> InspectAsync(
        byte[] jpegBytes,
        string? userHint = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || !IsAvailable)
        {
            _logger.LogInformation("Vision inspection skipped: the vision model is not available.");
            return null;
        }

        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(MinTimeoutSeconds, _options.InferenceTimeoutSeconds)));

        var acquired = false;

        try
        {
            acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(GateWaitSeconds), timeout.Token).ConfigureAwait(false);

            if (!acquired)
            {
                _logger.LogWarning("Vision inspection skipped: another image is still being analysed.");
                return null;
            }

            await Task.Run(EnsureLoaded, timeout.Token).ConfigureAwait(false);

            var raw = await GenerateAsync(jpegBytes, userHint, timeout.Token).ConfigureAwait(false);

            var result = VisionResultParser.Parse(raw, _options, out var error);

            if (result is null)
            {
                _logger.LogWarning("Vision output rejected: {Error}", error);
                return null;
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Vision inspection timed out after {Seconds} seconds.", _options.InferenceTimeoutSeconds);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision inspection failed.");
            return null;
        }
        finally
        {
            if (acquired)
            {
                _lastUsedUtc = DateTime.UtcNow;
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();

        var acquired = _gate.Wait(TimeSpan.FromSeconds(5));

        try
        {
            Unload();
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_weights is not null && _clip is not null && _modelParams is not null)
        {
            return;
        }

        try
        {
            var modelPath = ResolvePath(_options.ModelPath);
            var mmprojPath = ResolvePath(_options.MmprojPath);
            var threads = _options.Threads > 0 ? _options.Threads : Math.Max(1, Environment.ProcessorCount);

            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = _options.ContextSize,
                GpuLayerCount = Math.Max(0, _options.GpuLayerCount),
                Threads = threads,
                BatchThreads = threads
            };

            _logger.LogInformation("Loading vision model from {Path}.", modelPath);
            _weights = LLamaWeights.LoadFromFile(_modelParams);

            var clipParams = MtmdContextParams.Default();
            clipParams.UseGpu = _options.GpuLayerCount > 0;
            clipParams.NThreads = threads;

            _clip = MtmdWeights.LoadFromFile(mmprojPath, _weights, clipParams);

            if (!_clip.SupportsVision)
            {
                throw new InvalidOperationException("The projector file does not support vision input.");
            }

            _logger.LogInformation("Vision model loaded.");
        }
        catch (Exception ex)
        {
            _loadFailure = "The vision model could not be loaded: " + ex.Message;
            _logger.LogError(ex, "Vision model load failed.");
            Unload();
            throw new InvalidOperationException(_loadFailure, ex);
        }
    }

    private async Task<string> GenerateAsync(byte[] jpegBytes, string? userHint, CancellationToken cancellationToken)
    {
        var weights = _weights ?? throw new InvalidOperationException("The vision model is not loaded.");
        var clip = _clip ?? throw new InvalidOperationException("The vision projector is not loaded.");
        var modelParams = _modelParams ?? throw new InvalidOperationException("The vision model is not loaded.");

        var prompt = BuildPrompt(weights, userHint);

        using var context = weights.CreateContext(modelParams);
        var executor = new InteractiveExecutor(context, clip);

        try
        {
            executor.Embeds.Add(clip.LoadMedia(jpegBytes));

            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = (float)_options.Temperature,
                TopP = (float)_options.TopP,
                Grammar = new Grammar(VisionPromptTemplates.JsonGrammar, "root")
            };

            var maxTokens = Math.Max(MinTokens, _options.MaxTokens);

            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                SamplingPipeline = sampler
            };

            var builder = new StringBuilder();
            var charCap = maxTokens * CharsPerTokenCap;

            await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
            {
                builder.Append(piece);

                if (builder.Length > charCap)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return builder.ToString();
        }
        finally
        {
            foreach (var embed in executor.Embeds)
            {
                embed.Dispose();
            }

            executor.Embeds.Clear();
            clip.ClearMedia();
        }
    }

    private static string BuildPrompt(LLamaWeights weights, string? userHint)
    {
        var marker = NativeApi.MtmdDefaultMarker() ?? "<media>";
        var userText = marker + "\n" + VisionPromptTemplates.BuildUserPrompt(userHint);

        try
        {
            var withSystem = new LLamaTemplate(weights) { AddAssistant = true };
            withSystem.Add("system", VisionPromptTemplates.SystemPrompt);
            withSystem.Add("user", userText);
            return Encoding.UTF8.GetString(withSystem.Apply());
        }
        catch (Exception)
        {
            var merged = new LLamaTemplate(weights) { AddAssistant = true };
            merged.Add("user", marker + "\n" + VisionPromptTemplates.SystemPrompt + " " + VisionPromptTemplates.BuildUserPrompt(userHint));
            return Encoding.UTF8.GetString(merged.Apply());
        }
    }

    private void TryUnloadIfIdle()
    {
        if (_disposed || _weights is null)
        {
            return;
        }

        if (DateTime.UtcNow - _lastUsedUtc < TimeSpan.FromMinutes(Math.Max(1, _options.IdleUnloadMinutes)))
        {
            return;
        }

        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_weights is not null)
            {
                _logger.LogInformation("Unloading the idle vision model.");
                Unload();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Unload()
    {
        try
        {
            _clip?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vision projector dispose failed.");
        }

        try
        {
            _weights?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Vision model dispose failed.");
        }

        _clip = null;
        _weights = null;
        _modelParams = null;
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }
}