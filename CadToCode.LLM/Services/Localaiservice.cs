using System.Diagnostics;
using System.Text;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Prompts;
using CadToCode.LLM.Validation;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class LocalAiService : ILocalAiService, IDisposable
{
    private const int MinTokens = 16;
    private const int CharsPerTokenCap = 8;
    private const int GateWaitSeconds = 20;

    private readonly LocalAiOptions _options;
    private readonly IChatMemoryStore _memory;
    private readonly ILogger<LocalAiService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string? _loadFailure;
    private bool _disposed;

    public LocalAiService(
        IOptions<LocalAiOptions> options,
        IChatMemoryStore memory,
        ILogger<LocalAiService> logger)
    {
        _options = options.Value;
        _memory = memory;
        _logger = logger;
    }

    public bool IsAvailable =>
        _options.Enabled
        && _loadFailure == null
        && File.Exists(ResolvePath(_options.ModelPath));

    public string ModelName => Path.GetFileNameWithoutExtension(_options.ModelPath) ?? string.Empty;

    public CadAssistStatusDto GetStatus()
    {
        var status = new CadAssistStatusDto
        {
            Enabled = _options.Enabled,
            Available = IsAvailable,
            Model = ModelName
        };

        if (!_options.Enabled)
        {
            status.Message = "The local assistant is turned off in the application settings.";
        }
        else if (_loadFailure != null)
        {
            status.Message = _loadFailure;
        }
        else if (!File.Exists(ResolvePath(_options.ModelPath)))
        {
            status.Message = "The model file was not found at " + _options.ModelPath + ".";
        }

        return status;
    }

    public string EnsureSession(int projectId, string? sessionId)
    {
        return _memory.EnsureSession(projectId, sessionId);
    }

    public IReadOnlyList<CadAssistMessageDto> GetHistory(int projectId, string sessionId)
    {
        return _memory.GetMessages(projectId, sessionId);
    }

    public void ResetSession(int projectId, string sessionId)
    {
        _memory.Reset(projectId, sessionId);
    }

    public async Task<LocalAiGenerationResult> ChatAsync(
        LocalAiGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        if (_disposed)
        {
            return Fail(clock, "The assistant is shutting down.");
        }

        if (!_options.Enabled)
        {
            return Fail(clock, "The local assistant is turned off in the application settings.");
        }

        var modelPath = ResolvePath(_options.ModelPath);

        if (!File.Exists(modelPath))
        {
            return Fail(clock, "The model file was not found at " + _options.ModelPath + ".");
        }

        if (_loadFailure != null)
        {
            return Fail(clock, _loadFailure);
        }

        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length > _options.MaxPromptChars)
        {
            prompt = prompt[.._options.MaxPromptChars];
        }

        var attachments = (request.Attachments ?? new List<CadAssistAttachmentDto>())
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.FileName))
            .Take(Math.Max(0, _options.MaxAttachments))
            .ToList();

        if (prompt.Length == 0 && attachments.Count == 0)
        {
            return Fail(clock, "Type a message or attach a file.");
        }

        var sessionId = _memory.EnsureSession(request.ProjectId, request.SessionId);

        var userText = CadAssistPromptTemplates.BuildUserText(
            prompt,
            request.DrawingType,
            request.FilterInstructions,
            attachments,
            _options.MaxAttachmentTextChars);

        var history = _memory.GetMessages(request.ProjectId, sessionId);

        var fullPrompt = CadAssistPromptTemplates.Build(
            _options,
            request.GeometryContext,
            request.CurrentCode,
            history,
            userText);

        using (var gateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            gateTimeout.CancelAfter(TimeSpan.FromSeconds(GateWaitSeconds));

            try
            {
                await _gate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(clock, "The model is busy with another request. Try again in a moment.");
            }
        }

        string raw;

        try
        {
            await Task.Run(() => EnsureModelLoaded(modelPath), cancellationToken).ConfigureAwait(false);

            using var inferenceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inferenceTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.InferenceTimeoutSeconds)));

            raw = await GenerateAsync(fullPrompt, inferenceTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Fail(clock, "The model took too long to answer. Try a shorter request.");
        }
        catch (InvalidOperationException) when (_loadFailure != null)
        {
            return Fail(clock, _loadFailure);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local model inference failed for project {ProjectId}.", request.ProjectId);
            return Fail(clock, "The local model could not produce an answer.");
        }
        finally
        {
            _gate.Release();
        }

        var markers = CadAssistPromptTemplates.GetStopMarkers(_options);
        var parsed = CadAssistReplyParser.Parse(raw, markers);

        if (parsed.Text.Length == 0 && parsed.CodeBlocks.Count == 0)
        {
            return Fail(clock, "The model returned an empty answer. Try rephrasing the request.");
        }

        clock.Stop();

        var reply = parsed.Text.Length > 0 ? parsed.Text : "Here is the STEP script.";

        var userMessage = new CadAssistMessageDto
        {
            Role = CadAssistRole.User,
            Content = prompt.Length > 0 ? prompt : "Attached: " + string.Join(", ", attachments.Select(a => a.FileName)),
            Attachments = attachments.Select(a => a.FileName).ToList(),
            ModelContent = userText
        };

        var assistantMessage = new CadAssistMessageDto
        {
            Role = CadAssistRole.Assistant,
            Content = reply,
            CodeBlocks = parsed.CodeBlocks,
            Actions = BuildActions(parsed.CodeBlocks),
            Model = ModelName,
            ElapsedMs = clock.ElapsedMilliseconds,
            ModelContent = BuildModelContent(reply, parsed.CodeBlocks)
        };

        _memory.Append(request.ProjectId, sessionId, userMessage);
        _memory.Append(request.ProjectId, sessionId, assistantMessage);

        return new LocalAiGenerationResult
        {
            Success = true,
            Reply = reply,
            CodeBlocks = parsed.CodeBlocks,
            Message = assistantMessage,
            Model = ModelName,
            ElapsedMs = clock.ElapsedMilliseconds
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _weights?.Dispose();
        _weights = null;
        _executor = null;
        _gate.Dispose();
    }

    private void EnsureModelLoaded(string modelPath)
    {
        if (_executor != null)
        {
            return;
        }

        try
        {
            var threads = _options.Threads > 0 ? _options.Threads : Math.Max(1, Environment.ProcessorCount);

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = _options.ContextSize,
                GpuLayerCount = Math.Max(0, _options.GpuLayerCount),
                Threads = threads,
                BatchThreads = threads
            };

            _logger.LogInformation("Loading local assistant model from {Path}.", modelPath);
            _weights = LLamaWeights.LoadFromFile(modelParams);
            _executor = new StatelessExecutor(_weights, modelParams);
            _logger.LogInformation("Local assistant model loaded.");
        }
        catch (Exception ex)
        {
            _loadFailure = "The local model could not be loaded: " + ex.Message;
            _logger.LogError(ex, "Local assistant model load failed.");
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            throw new InvalidOperationException(_loadFailure, ex);
        }
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        var executor = _executor ?? throw new InvalidOperationException("The local model is not loaded.");

        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = (float)_options.Temperature,
            TopP = (float)_options.TopP
        };

        var maxTokens = Math.Max(MinTokens, _options.MaxTokens);

        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = CadAssistPromptTemplates.GetStopMarkers(_options),
            SamplingPipeline = sampler
        };

        var builder = new StringBuilder();
        var charCap = maxTokens * CharsPerTokenCap;

        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken))
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

    private static List<CadAssistActionDto> BuildActions(List<CadAssistCodeBlockDto> blocks)
    {
        var actions = new List<CadAssistActionDto>();

        foreach (var block in blocks)
        {
            if (block.Language == "csharp")
            {
                actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Run, Label = "Run", TargetCodeBlockId = block.Id });
                actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Apply, Label = "Apply to editor", TargetCodeBlockId = block.Id });
                actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.DownloadStep, Label = "Download STEP", TargetCodeBlockId = block.Id });
            }

            actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Copy, Label = "Copy", TargetCodeBlockId = block.Id });
        }

        actions.Add(new CadAssistActionDto { Kind = CadAssistActionKind.Regenerate, Label = "Regenerate" });

        return actions;
    }

    private static string BuildModelContent(string reply, List<CadAssistCodeBlockDto> blocks)
    {
        var sb = new StringBuilder(reply);

        foreach (var block in blocks)
        {
            sb.Append("\n```").Append(block.Language).Append('\n').Append(block.Code).Append("\n```");
        }

        return sb.ToString();
    }

    private static LocalAiGenerationResult Fail(Stopwatch clock, string error)
    {
        return new LocalAiGenerationResult
        {
            Success = false,
            Error = error,
            ElapsedMs = clock.ElapsedMilliseconds
        };
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }
}