using System.Globalization;
using System.Text;
using System.Text.Json;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Prompts;
using CadToCode.LLM.Validation;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class LLamaGenerationService : ILLMGenerationService, IDisposable
{
    private const int MaxResponseChars = 4000;
    private const decimal MaxDimension = 10_000_000m;
    private const int AnalysisMaxResponseChars = 6000;
    private const int CodeMaxResponseChars = 12000;
    private const int CodeMaxTokens = 1024;
    private const int CodeTimeoutMultiplier = 3;

    private static readonly string[] StopMarkers = { "<|end|>", "<|endoftext|>", "<|user|>" };

    private static readonly int[] TextLimits = { 40, 20, 10, 0 };

    private const string SystemPrompt =
        "You classify mechanical and architectural CAD drawings. "
        + "You reply with one JSON object and nothing else. No markdown, no commentary.";

    private readonly LlmOptions _options;
    private readonly ILogger<LLamaGenerationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string? _loadFailure;
    private bool _disposed;

    public LLamaGenerationService(IOptions<LlmOptions> options, ILogger<LLamaGenerationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable =>
        _options.Enabled
        && _loadFailure == null
        && File.Exists(ResolvePath(_options.ModelPath));

    public async Task<ComponentClassificationResult> ClassifyAsync(
        ComponentClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return Fail("The AI service has been shut down.");
        }

        if (!_options.Enabled)
        {
            return Fail("The local AI model is disabled in the configuration.");
        }

        var modelPath = ResolvePath(_options.ModelPath);
        if (!File.Exists(modelPath))
        {
            return Fail("The AI model file was not found at " + modelPath + ".");
        }

        if (_loadFailure != null)
        {
            return Fail(_loadFailure);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.InferenceTimeoutSeconds)));

        try
        {
            await _gate.WaitAsync(timeout.Token);

            try
            {
                await Task.Run(() => EnsureModelLoaded(modelPath), CancellationToken.None);

                var attempts = 1 + Math.Max(0, _options.MaxRetries);
                ComponentClassificationResult last = Fail("The AI model returned no usable answer.");

                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    timeout.Token.ThrowIfCancellationRequested();

                    var prompt = BuildPrompt(request);
                    var raw = await GenerateAsync(prompt, timeout.Token);
                    var (result, retryable) = ParseAndValidate(raw);
                    last = result;

                    if (result.Success || !retryable)
                    {
                        return result;
                    }

                    _logger.LogWarning("AI answer rejected on attempt {Attempt}: {Error}", attempt, result.Error);
                }

                return last;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AI classification timed out after {Seconds} seconds.", _options.InferenceTimeoutSeconds);
            return Fail("The AI model took too long to answer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI classification failed.");
            return Fail("The AI model failed: " + ex.Message);
        }
    }

    public Task<AssemblyAnalysisResult> AnalyzeAssemblyAsync(
        AssemblyAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var maxTokens = Math.Max(768, _options.MaxTokens);

        return RunAsync(
            "assembly analysis",
            maxChars => AssemblyPromptTemplates.BuildAnalysisPrompt(request, maxChars),
            maxTokens,
            AnalysisMaxResponseChars,
            Math.Max(5, _options.InferenceTimeoutSeconds),
            raw => AssemblyOutputValidator.ParseAnalysis(raw, request.Tree, (decimal)_options.MinConfidence),
            error => new AssemblyAnalysisResult { Success = false, Error = error },
            result => result.Success,
            cancellationToken);
    }

    public Task<CodeSynthesisResult> SynthesizeCodeAsync(
        CodeSynthesisRequest request,
        CancellationToken cancellationToken = default)
    {
        var maxTokens = Math.Min(CodeMaxTokens, (int)Math.Max(256u, _options.ContextSize / 2));

        return RunAsync(
            "code synthesis",
            maxChars => AssemblyPromptTemplates.BuildCodePrompt(request, maxChars),
            maxTokens,
            CodeMaxResponseChars,
            Math.Max(5, _options.InferenceTimeoutSeconds) * CodeTimeoutMultiplier,
            raw => AssemblyOutputValidator.ValidateCode(raw, request),
            error => new CodeSynthesisResult { Success = false, Error = error },
            result => result.Success,
            cancellationToken);
    }

    private async Task<T> RunAsync<T>(
        string operation,
        Func<int, string> buildPrompt,
        int maxTokens,
        int maxResponseChars,
        int timeoutSeconds,
        Func<string, (T Result, bool Retryable)> validate,
        Func<string, T> fail,
        Func<T, bool> isSuccess,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return fail("The AI service has been shut down.");
        }

        if (!_options.Enabled)
        {
            return fail("The local AI model is disabled in the configuration.");
        }

        var modelPath = ResolvePath(_options.ModelPath);
        if (!File.Exists(modelPath))
        {
            return fail("The AI model file was not found at " + modelPath + ".");
        }

        if (_loadFailure != null)
        {
            return fail(_loadFailure);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await _gate.WaitAsync(timeout.Token);

            try
            {
                await Task.Run(() => EnsureModelLoaded(modelPath), CancellationToken.None);

                var promptChars = (int)Math.Max(1500, ((long)_options.ContextSize - maxTokens - 64) * 3);
                var attempts = 1 + Math.Max(0, _options.MaxRetries);
                var last = fail("The AI model returned no usable answer.");

                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    timeout.Token.ThrowIfCancellationRequested();

                    var prompt = buildPrompt(promptChars);
                    var raw = await GenerateAsync(prompt, timeout.Token, maxTokens, maxResponseChars);
                    var (result, retryable) = validate(raw);
                    last = result;

                    if (isSuccess(result) || !retryable)
                    {
                        return result;
                    }

                    _logger.LogWarning("AI {Operation} answer rejected on attempt {Attempt}.", operation, attempt);
                }

                return last;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AI {Operation} timed out after {Seconds} seconds.", operation, timeoutSeconds);
            return fail("The AI model took too long to answer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI {Operation} failed.", operation);
            return fail("The AI model failed: " + ex.Message);
        }
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
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads
            };

            _logger.LogInformation("Loading AI model from {Path}.", modelPath);
            _weights = LLamaWeights.LoadFromFile(modelParams);
            _executor = new StatelessExecutor(_weights, modelParams);
            _logger.LogInformation("AI model loaded.");
        }
        catch (Exception ex)
        {
            _loadFailure = "The AI model could not be loaded: " + ex.Message;
            _logger.LogError(ex, "AI model load failed.");
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            throw new InvalidOperationException(_loadFailure, ex);
        }
    }

    private async Task<string> GenerateAsync(
        string prompt,
        CancellationToken cancellationToken,
        int? maxTokens = null,
        int? maxChars = null)
    {
        var executor = _executor ?? throw new InvalidOperationException("The AI model is not loaded.");

        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = (float)_options.Temperature,
            TopP = (float)_options.TopP
        };

        var inference = new InferenceParams
        {
            MaxTokens = Math.Max(16, maxTokens ?? _options.MaxTokens),
            AntiPrompts = StopMarkers,
            SamplingPipeline = sampler
        };

        var builder = new StringBuilder();

        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken))
        {
            builder.Append(piece);
            if (builder.Length > (maxChars ?? MaxResponseChars))
            {
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private string BuildPrompt(ComponentClassificationRequest request)
    {
        var maxChars = (int)Math.Max(1500, ((long)_options.ContextSize - _options.MaxTokens - 64) * 3);
        var prompt = string.Empty;

        foreach (var textLimit in TextLimits)
        {
            prompt = BuildPromptCore(request, textLimit);
            if (prompt.Length <= maxChars)
            {
                return prompt;
            }
        }

        return prompt.Length > maxChars ? prompt[..maxChars] : prompt;
    }

    private static string BuildPromptCore(ComponentClassificationRequest request, int textLimit)
    {
        var types = string.Join(", ", Enum.GetNames<ComponentType>());
        var user = new StringBuilder();

        user.AppendLine("Classify the main component in this CAD drawing.");
        user.AppendLine();
        user.AppendLine("Allowed componentType values: " + types + ".");
        user.AppendLine();
        user.AppendLine("Drawing summary:");
        user.AppendLine(Truncate(request.DrawingSummary, 600));

        var texts = request.Texts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Truncate(t.Trim(), 80))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(textLimit)
            .ToList();

        if (texts.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("Texts found in the drawing:");
            foreach (var text in texts)
            {
                user.AppendLine("- " + text);
            }
        }

        var layers = request.Layers
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Truncate(l.Trim(), 30))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(textLimit, 15))
            .ToList();

        if (layers.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("Layers: " + string.Join(", ", layers));
        }

        if (request.SimilarPatterns.Count > 0)
        {
            user.AppendLine();
            user.AppendLine("Similar known patterns:");
            foreach (var pattern in request.SimilarPatterns.Take(3))
            {
                user.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "- {0} (similarity {1:0.00}): {2}",
                    pattern.ComponentType,
                    pattern.Similarity,
                    Truncate(pattern.Description, 200)));
            }
        }

        var candidate = request.DeterministicCandidate;
        if (candidate != null)
        {
            user.AppendLine();
            user.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "Rule-based guess: {0}, height {1}, width {2}, diameter {3}, top diameter {4}, bottom diameter {5}, angle {6}.",
                candidate.ComponentType,
                Num(candidate.Height),
                Num(candidate.Width),
                Num(candidate.Diameter),
                Num(candidate.TopDiameter),
                Num(candidate.BottomDiameter),
                Num(candidate.Angle)));
        }

        user.AppendLine();
        user.AppendLine("Reply with exactly this JSON shape. Use null for unknown dimensions.");
        user.AppendLine("{\"componentType\":\"<allowed value>\",\"name\":\"<short name>\",\"height\":null,\"width\":null,"
            + "\"diameter\":null,\"topDiameter\":null,\"bottomDiameter\":null,\"angle\":null,"
            + "\"confidence\":0.0,\"reasoning\":\"<one short sentence>\"}");

        return "<|system|>\n" + SystemPrompt + "<|end|>\n"
            + "<|user|>\n" + user.ToString().TrimEnd() + "<|end|>\n"
            + "<|assistant|>\n";
    }

    private (ComponentClassificationResult Result, bool Retryable) ParseAndValidate(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json == null)
        {
            return (Fail("The AI answer did not contain a JSON object.", raw), true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (Fail("The AI answer was not valid JSON.", raw), true);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (Fail("The AI answer was not a JSON object.", raw), true);
            }

            if (!TryGetProperty(root, "componentType", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !TryParseComponentType(typeElement.GetString(), out var componentType))
            {
                return (Fail("The AI answer has a missing or unknown componentType.", raw), true);
            }

            if (!TryGetProperty(root, "confidence", out var confidenceElement)
                || !TryReadDecimal(confidenceElement, out var confidence)
                || confidence is null
                || confidence < 0m
                || confidence > 1m)
            {
                return (Fail("The AI answer has a missing or invalid confidence.", raw), true);
            }

            var dimensions = new Dictionary<string, decimal?>();
            foreach (var key in new[] { "height", "width", "diameter", "topDiameter", "bottomDiameter", "angle" })
            {
                decimal? value = null;
                if (TryGetProperty(root, key, out var element))
                {
                    if (!TryReadDecimal(element, out value))
                    {
                        return (Fail("The AI answer has an invalid " + key + " value.", raw), true);
                    }
                }

                var limit = key == "angle" ? 360m : MaxDimension;
                if (value.HasValue && (value.Value <= 0m || value.Value > limit))
                {
                    return (Fail("The AI answer has an out-of-range " + key + " value.", raw), true);
                }

                dimensions[key] = value;
            }

            var name = string.Empty;
            if (TryGetProperty(root, "name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = (nameElement.GetString() ?? string.Empty).Trim();
            }

            if (name.Length == 0)
            {
                name = componentType.ToString();
            }

            var reasoning = string.Empty;
            if (TryGetProperty(root, "reasoning", out var reasoningElement) && reasoningElement.ValueKind == JsonValueKind.String)
            {
                reasoning = (reasoningElement.GetString() ?? string.Empty).Trim();
            }

            var result = new ComponentClassificationResult
            {
                Success = true,
                ComponentType = componentType,
                Name = Truncate(name, 100),
                Height = dimensions["height"],
                Width = dimensions["width"],
                Diameter = dimensions["diameter"],
                TopDiameter = dimensions["topDiameter"],
                BottomDiameter = dimensions["bottomDiameter"],
                Angle = dimensions["angle"],
                Confidence = confidence.Value,
                Reasoning = reasoning.Length > 0 ? Truncate(reasoning, 300) : null,
                RawResponse = raw
            };

            if ((double)confidence.Value < _options.MinConfidence)
            {
                result.Success = false;
                result.Error = "The AI answer confidence was below the required minimum.";
                return (result, false);
            }

            return (result, false);
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return raw.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal? value)
    {
        value = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;

            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var number))
                {
                    value = number;
                    return true;
                }

                return false;

            case JsonValueKind.String:
                if (decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryParseComponentType(string? text, out ComponentType type)
    {
        type = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = new string(text.Where(char.IsLetter).ToArray());

        foreach (var name in Enum.GetNames<ComponentType>())
        {
            if (string.Equals(name, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                type = Enum.Parse<ComponentType>(name);
                return true;
            }
        }

        return false;
    }

    private static ComponentClassificationResult Fail(string error, string? raw = null)
    {
        return new ComponentClassificationResult
        {
            Success = false,
            Error = error,
            RawResponse = raw
        };
    }

    private static string Num(decimal? value)
    {
        return value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "unknown";
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }
}