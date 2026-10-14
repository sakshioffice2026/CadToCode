using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class GeminiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly GeminiEmbeddingOptions _options;
    private readonly ILogger<GeminiEmbeddingService> _logger;

    public GeminiEmbeddingService(
        HttpClient http,
        IOptions<GeminiEmbeddingOptions> options,
        ILogger<GeminiEmbeddingService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiKey)
        && !_options.ApiKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);

    public int Dimensions => _options.OutputDimensionality;

    public async Task<float[]> EmbedAsync(
        string text,
        EmbeddingTaskType taskType,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var url = BuildUrl("embedContent");
        var body = BuildRequest(text, taskType);

        using var document = await SendWithRetryAsync(url, body, cancellationToken);

        if (!document.RootElement.TryGetProperty("embedding", out var embedding)
            || !embedding.TryGetProperty("values", out var values))
        {
            throw new InvalidOperationException("Gemini embedContent response did not contain embedding values.");
        }

        return ReadVector(values);
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        EmbeddingTaskType taskType,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var results = new List<float[]>(texts.Count);
        var batchSize = Math.Clamp(_options.MaxBatchSize, 1, 100);

        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            var slice = texts.Skip(offset).Take(batchSize).ToList();
            var url = BuildUrl("batchEmbedContents");
            var body = new Dictionary<string, object?>
            {
                ["requests"] = slice.Select(t => BuildRequest(t, taskType)).ToList()
            };

            using var document = await SendWithRetryAsync(url, body, cancellationToken);

            if (!document.RootElement.TryGetProperty("embeddings", out var embeddings)
                || embeddings.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Gemini batchEmbedContents response did not contain embeddings.");
            }

            var count = 0;
            foreach (var item in embeddings.EnumerateArray())
            {
                if (!item.TryGetProperty("values", out var values))
                {
                    throw new InvalidOperationException("Gemini batch embedding item had no values.");
                }

                results.Add(ReadVector(values));
                count++;
            }

            if (count != slice.Count)
            {
                throw new InvalidOperationException(
                    "Gemini returned " + count + " embeddings for " + slice.Count + " inputs.");
            }
        }

        return results;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Gemini embedding is not configured. Set GeminiEmbeddingOptions:ApiKey (Google AI Studio key).");
        }
    }

    private string BuildUrl(string method)
    {
        return _options.BaseUrl.TrimEnd('/') + "/models/" + _options.Model + ":" + method;
    }

    private Dictionary<string, object?> BuildRequest(string text, EmbeddingTaskType taskType)
    {
        var clean = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        if (clean.Length > _options.MaxInputChars)
        {
            clean = clean[.._options.MaxInputChars];
        }

        return new Dictionary<string, object?>
        {
            ["model"] = "models/" + _options.Model,
            ["content"] = new Dictionary<string, object?>
            {
                ["parts"] = new[] { new Dictionary<string, object?> { ["text"] = clean } }
            },
            ["taskType"] = taskType == EmbeddingTaskType.RetrievalQuery
                ? "RETRIEVAL_QUERY"
                : "RETRIEVAL_DOCUMENT",
            ["outputDimensionality"] = _options.OutputDimensionality
        };
    }

    private async Task<JsonDocument> SendWithRetryAsync(
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.MaxRetries + 1);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("x-goog-api-key", _options.ApiKey);

            HttpResponseMessage? response = null;

            try
            {
                response = await _http.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    response.Dispose();
                    return document;
                }

                var status = response.StatusCode;
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                var retryable = status == HttpStatusCode.TooManyRequests || (int)status >= 500;

                if (!retryable || attempt >= attempts)
                {
                    throw new HttpRequestException(
                        "Gemini embedding request failed with " + (int)status + ": " + Truncate(detail, 300));
                }

                _logger.LogWarning(
                    "Gemini embedding attempt {Attempt}/{Max} failed with {Status}. Retrying.",
                    attempt, attempts, (int)status);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < attempts)
            {
                _logger.LogWarning("Gemini embedding attempt {Attempt}/{Max} timed out. Retrying.", attempt, attempts);
            }
            finally
            {
                response?.Dispose();
            }

            var delay = _options.RetryDelayMilliseconds * (int)Math.Pow(2, attempt - 1);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static float[] ReadVector(JsonElement values)
    {
        var list = new List<float>(values.GetArrayLength());

        foreach (var v in values.EnumerateArray())
        {
            list.Add((float)v.GetDouble());
        }

        var vector = list.ToArray();
        Normalize(vector);
        return vector;
    }

    // Truncated (non-3072) dimensions are not unit length, so normalize before storing.
    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12)
        {
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }

    private static string Truncate(string value, int max)
    {
        return value.Length > max ? value[..max] : value;
    }
}