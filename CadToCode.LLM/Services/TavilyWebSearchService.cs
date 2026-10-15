using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Services;

public sealed class TavilyOptions
{
    public const string SectionName = "TavilyOptions";

    public bool Enabled { get; set; } = false;

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.tavily.com";

    public string SearchDepth { get; set; } = "basic";

    public int MaxResults { get; set; } = 5;

    public int MaxSnippetChars { get; set; } = 600;

    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Advisory-only web search backed by the Tavily API.
/// Results are reference material; they never feed deterministic calculations.
/// </summary>
public sealed class TavilyWebSearchService : IWebSearchService
{
    private readonly HttpClient _http;
    private readonly TavilyOptions _options;
    private readonly ILogger<TavilyWebSearchService> _logger;

    public TavilyWebSearchService(
        HttpClient http,
        IOptions<TavilyOptions> options,
        ILogger<TavilyWebSearchService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
    }

    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiKey)
        && !_options.ApiKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WebSearchResult>();

        if (!IsConfigured)
        {
            _logger.LogDebug("Tavily web search skipped: not configured.");
            return Array.Empty<WebSearchResult>();
        }

        var limit = Math.Clamp(maxResults <= 0 ? _options.MaxResults : maxResults, 1, 20);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/search";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new TavilyRequest
            {
                Query = query.Trim(),
                SearchDepth = _options.SearchDepth,
                MaxResults = limit,
                IncludeAnswer = false,
                IncludeRawContent = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tavily web search failed with HTTP {Status}.", (int)response.StatusCode);
                return Array.Empty<WebSearchResult>();
            }

            var payload = await response.Content
                .ReadFromJsonAsync<TavilyResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (payload?.Results is null)
                return Array.Empty<WebSearchResult>();

            return payload.Results
                .Where(r => !string.IsNullOrWhiteSpace(r.Url))
                .Take(limit)
                .Select(r => new WebSearchResult(
                    Title: r.Title ?? r.Url!,
                    Url: r.Url!,
                    Snippet: Truncate(r.Content, _options.MaxSnippetChars)))
                .ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Tavily web search timed out.");
            return Array.Empty<WebSearchResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Tavily web search request failed.");
            return Array.Empty<WebSearchResult>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Tavily web search returned an unreadable response.");
            return Array.Empty<WebSearchResult>();
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class TavilyRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("search_depth")]
        public string SearchDepth { get; set; } = "basic";

        [JsonPropertyName("max_results")]
        public int MaxResults { get; set; } = 5;

        [JsonPropertyName("include_answer")]
        public bool IncludeAnswer { get; set; }

        [JsonPropertyName("include_raw_content")]
        public bool IncludeRawContent { get; set; }
    }

    private sealed class TavilyResponse
    {
        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private sealed class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
