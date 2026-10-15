using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class DuckDuckGoOptions
{
    public const string SectionName = "DuckDuckGoOptions";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://api.duckduckgo.com";

    public int MaxResults { get; set; } = 5;

    public int MaxSnippetChars { get; set; } = 600;

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Key-less advisory web search using the DuckDuckGo Instant Answer API.
/// Coverage is limited to abstracts and related topics; results never feed
/// deterministic calculations.
/// </summary>
public sealed class DuckDuckGoWebSearchService : IWebSearchService
{
    private readonly HttpClient _http;
    private readonly DuckDuckGoOptions _options;
    private readonly ILogger<DuckDuckGoWebSearchService> _logger;

    public DuckDuckGoWebSearchService(
        HttpClient http,
        IOptions<DuckDuckGoOptions> options,
        ILogger<DuckDuckGoWebSearchService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
    }

    public bool IsConfigured => _options.Enabled;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsConfigured)
            return Array.Empty<WebSearchResult>();

        var limit = Math.Clamp(maxResults <= 0 ? _options.MaxResults : maxResults, 1, 20);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/?q={Uri.EscapeDataString(query.Trim())}" +
                  "&format=json&no_html=1&skip_disambig=1&no_redirect=1";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DuckDuckGo web search failed with HTTP {Status}.", (int)response.StatusCode);
                return Array.Empty<WebSearchResult>();
            }

            var payload = await response.Content
                .ReadFromJsonAsync<DuckDuckGoResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
                return Array.Empty<WebSearchResult>();

            var results = new List<WebSearchResult>();

            if (!string.IsNullOrWhiteSpace(payload.AbstractText) && !string.IsNullOrWhiteSpace(payload.AbstractUrl))
            {
                results.Add(new WebSearchResult(
                    Title: string.IsNullOrWhiteSpace(payload.Heading) ? payload.AbstractUrl! : payload.Heading!,
                    Url: payload.AbstractUrl!,
                    Snippet: Truncate(payload.AbstractText, _options.MaxSnippetChars)));
            }

            foreach (var topic in Flatten(payload.RelatedTopics))
            {
                if (results.Count >= limit)
                    break;

                if (string.IsNullOrWhiteSpace(topic.FirstUrl) || string.IsNullOrWhiteSpace(topic.Text))
                    continue;

                results.Add(new WebSearchResult(
                    Title: ExtractTitle(topic.Text!),
                    Url: topic.FirstUrl!,
                    Snippet: Truncate(topic.Text, _options.MaxSnippetChars)));
            }

            return results.Take(limit).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("DuckDuckGo web search timed out.");
            return Array.Empty<WebSearchResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DuckDuckGo web search request failed.");
            return Array.Empty<WebSearchResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DuckDuckGo web search returned an unreadable response.");
            return Array.Empty<WebSearchResult>();
        }
    }

    private static IEnumerable<DuckDuckGoTopic> Flatten(List<DuckDuckGoTopic>? topics)
    {
        if (topics is null)
            yield break;

        foreach (var topic in topics)
        {
            if (topic.Topics is { Count: > 0 })
            {
                foreach (var nested in Flatten(topic.Topics))
                    yield return nested;
            }
            else
            {
                yield return topic;
            }
        }
    }

    private static string ExtractTitle(string text)
    {
        var separator = text.IndexOf(" - ", StringComparison.Ordinal);
        var title = separator > 0 ? text[..separator] : text;
        return title.Length <= 120 ? title : title[..120];
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class DuckDuckGoResponse
    {
        [JsonPropertyName("Heading")]
        public string? Heading { get; set; }

        [JsonPropertyName("AbstractText")]
        public string? AbstractText { get; set; }

        [JsonPropertyName("AbstractURL")]
        public string? AbstractUrl { get; set; }

        [JsonPropertyName("RelatedTopics")]
        public List<DuckDuckGoTopic>? RelatedTopics { get; set; }
    }

    private sealed class DuckDuckGoTopic
    {
        [JsonPropertyName("Text")]
        public string? Text { get; set; }

        [JsonPropertyName("FirstURL")]
        public string? FirstUrl { get; set; }

        [JsonPropertyName("Topics")]
        public List<DuckDuckGoTopic>? Topics { get; set; }
    }
}
