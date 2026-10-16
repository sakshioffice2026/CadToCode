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

    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.tavily.com";
    public string SearchDepth { get; set; } = "basic";
    public int MaxResults { get; set; } = 5;
    public int MaxSnippetChars { get; set; } = 600;
    public int TimeoutSeconds { get; set; } = 20;
}

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
        _options = options?.Value
            ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _http.Timeout = TimeSpan.FromSeconds(
            Math.Clamp(_options.TimeoutSeconds, 5, 120));
    }

    public string ProviderName => "Tavily";

    public bool IsAvailable =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !_options.ApiKey.StartsWith(
            "YOUR_", StringComparison.OrdinalIgnoreCase);

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
                "Tavily is not configured.");
        }

        var limit = Math.Clamp(
            Math.Min(request.MaxResults, Math.Max(1, _options.MaxResults)),
            1,
            WebSearchRequest.MaxAllowedResults);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/search")
        {
            Content = JsonContent.Create(new TavilyRequest
            {
                Query = request.Query,
                SearchDepth = string.IsNullOrWhiteSpace(_options.SearchDepth)
                    ? "basic"
                    : _options.SearchDepth,
                MaxResults = limit,
                IncludeAnswer = false,
                IncludeRawContent = false
            })
        };

        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        try
        {
            using var response = await _http.SendAsync(
                message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tavily search failed with HTTP {Status}.",
                    (int)response.StatusCode);

                return WebSearchResult.Failure(
                    request.Query,
                    ProviderName,
                    $"Tavily returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<TavilyResponse>(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var snippets = new List<WebSearchSnippet>();

            foreach (var item in payload?.Results ?? new List<TavilyItem>())
            {
                if (!Uri.TryCreate(
                        item.Url,
                        UriKind.Absolute,
                        out var sourceUri) ||
                    (sourceUri.Scheme != Uri.UriSchemeHttp &&
                     sourceUri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                var domain = sourceUri.Host.ToLowerInvariant();

                if (request.BlockedDomains.Any(
                        rule => DomainMatches(domain, rule)))
                {
                    continue;
                }

                if (request.AllowedDomains.Count > 0 &&
                    !request.AllowedDomains.Any(
                        rule => DomainMatches(domain, rule)))
                {
                    continue;
                }

                snippets.Add(new WebSearchSnippet(
                    Title: string.IsNullOrWhiteSpace(item.Title)
                        ? sourceUri.Host
                        : item.Title.Trim(),
                    Url: sourceUri.AbsoluteUri,
                    Domain: domain,
                    Snippet: Truncate(
                        item.Content,
                        Math.Min(
                            request.MaxSnippetCharacters,
                            Math.Max(1, _options.MaxSnippetChars)))));

                if (snippets.Count >= limit)
                {
                    break;
                }
            }

            return WebSearchResult.Success(
                request.Query, ProviderName, snippets);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Tavily search timed out.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "Tavily request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Tavily request failed.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "Tavily network request failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Tavily returned an unreadable response.");

            return WebSearchResult.Failure(
                request.Query,
                ProviderName,
                "Tavily returned an unreadable response.");
        }
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

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
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
        public List<TavilyItem>? Results { get; set; }
    }

    private sealed class TavilyItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}