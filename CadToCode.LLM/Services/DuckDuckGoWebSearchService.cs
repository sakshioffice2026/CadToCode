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
    public string BaseUrl { get; set; } =
        "https://api.duckduckgo.com/";
    public int MaxResults { get; set; } = 5;
    public int MaxSnippetChars { get; set; } = 600;
    public int TimeoutSeconds { get; set; } = 15;
}

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
        _options = options?.Value
            ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _http.Timeout = TimeSpan.FromSeconds(
            Math.Clamp(_options.TimeoutSeconds, 5, 120));
    }

    public string ProviderName => "DuckDuckGo";

    public bool IsAvailable =>
        _options.Enabled &&
        Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps ||
         uri.Scheme == Uri.UriSchemeHttp);

    public async Task<WebSearchResult> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAvailable)
        {
            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "DuckDuckGo search is not configured.");
        }

        var endpoint = BuildEndpoint(request.Query);

        try
        {
            using var response = await _http.GetAsync(
                endpoint, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "DuckDuckGo search failed with HTTP {Status}.",
                    (int)response.StatusCode);

                return WebSearchResult.Failure(
                    request.Query,
                    ProviderName,
                    $"DuckDuckGo returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<DuckDuckGoResponse>(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var snippets = new List<WebSearchSnippet>();
            var limit = Math.Clamp(
                Math.Min(request.MaxResults, Math.Max(1, _options.MaxResults)),
                1,
                WebSearchRequest.MaxAllowedResults);

            if (payload is not null)
            {
                AddResult(
                    payload.AbstractText,
                    payload.AbstractURL,
                    payload.Heading,
                    request,
                    snippets,
                    limit);

                AddTopics(
                    payload.RelatedTopics,
                    request,
                    snippets,
                    limit);
            }

            return snippets.Count == 0
                ? WebSearchResult.Empty(request.Query, ProviderName)
                : WebSearchResult.Success(
                    request.Query, ProviderName, snippets);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("DuckDuckGo search timed out.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "DuckDuckGo request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DuckDuckGo search request failed.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "DuckDuckGo network request failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "DuckDuckGo returned an unreadable response.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "DuckDuckGo returned an unreadable response.");
        }
    }

    private string BuildEndpoint(string query)
    {
        var separator = _options.BaseUrl.Contains('?') ? "&" : "?";

        return $"{_options.BaseUrl}{separator}" +
               $"q={Uri.EscapeDataString(query)}&format=json" +
               "&no_html=1&skip_disambig=1";
    }

    private void AddTopics(
        IEnumerable<DuckDuckGoTopic>? topics,
        WebSearchRequest request,
        List<WebSearchSnippet> snippets,
        int limit)
    {
        if (topics is null)
        {
            return;
        }

        foreach (var topic in topics)
        {
            if (snippets.Count >= limit)
            {
                return;
            }

            if (topic.Topics is { Count: > 0 })
            {
                AddTopics(topic.Topics, request, snippets, limit);
                continue;
            }

            AddResult(
                topic.Text,
                topic.FirstURL,
                topic.Text,
                request,
                snippets,
                limit);
        }
    }

    private void AddResult(
        string? text,
        string? url,
        string? title,
        WebSearchRequest request,
        List<WebSearchSnippet> snippets,
        int limit)
    {
        if (snippets.Count >= limit ||
            string.IsNullOrWhiteSpace(text) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp &&
             sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        var domain = sourceUri.Host.ToLowerInvariant();

        if (request.BlockedDomains.Any(
                rule => DomainMatches(domain, rule)))
        {
            return;
        }

        if (request.AllowedDomains.Count > 0 &&
            !request.AllowedDomains.Any(
                rule => DomainMatches(domain, rule)))
        {
            return;
        }

        if (snippets.Any(
                item => string.Equals(
                    item.Url, sourceUri.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        snippets.Add(new WebSearchSnippet(
            Title: string.IsNullOrWhiteSpace(title)
                ? sourceUri.Host
                : title.Trim(),
            Url: sourceUri.AbsoluteUri,
            Domain: domain,
            Snippet: Truncate(
                text,
                Math.Min(
                    request.MaxSnippetCharacters,
                    Math.Max(1, _options.MaxSnippetChars)))));
    }

    private static bool DomainMatches(string domain, string rule)
    {
        var normalized = rule.Trim().TrimStart('.');

        return normalized.Length > 0 &&
               (domain.Equals(
                    normalized, StringComparison.OrdinalIgnoreCase) ||
                domain.EndsWith(
                    "." + normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }

    private sealed class DuckDuckGoResponse
    {
        [JsonPropertyName("Heading")]
        public string? Heading { get; set; }

        [JsonPropertyName("AbstractText")]
        public string? AbstractText { get; set; }

        [JsonPropertyName("AbstractURL")]
        public string? AbstractURL { get; set; }

        [JsonPropertyName("RelatedTopics")]
        public List<DuckDuckGoTopic>? RelatedTopics { get; set; }
    }

    private sealed class DuckDuckGoTopic
    {
        [JsonPropertyName("Text")]
        public string? Text { get; set; }

        [JsonPropertyName("FirstURL")]
        public string? FirstURL { get; set; }

        [JsonPropertyName("Topics")]
        public List<DuckDuckGoTopic>? Topics { get; set; }
    }
}