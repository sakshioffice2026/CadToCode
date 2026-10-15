using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Services;

/// <summary>
/// Why a web search is being performed. Providers may use this to bias query construction and ranking.
/// </summary>
public enum WebSearchPurpose
{
    /// <summary>Look up a published standard, code or regulation reference.</summary>
    StandardsLookup = 0,

    /// <summary>Look up a vendor datasheet or catalogue specification.</summary>
    VendorSpecification = 1,

    /// <summary>Look up equipment not covered by the local knowledge base.</summary>
    UnindexedEquipment = 2,

    /// <summary>General engineering background used to explain a design decision.</summary>
    GeneralReference = 3
}

/// <summary>
/// A web search request. Immutable and validated on construction.
/// </summary>
public sealed class WebSearchRequest
{
    public const int DefaultMaxResults = 5;
    public const int MaxAllowedResults = 10;
    public const int DefaultMaxSnippetCharacters = 500;

    public string Query { get; }
    public WebSearchPurpose Purpose { get; }

    /// <summary>Equipment context for query shaping. <see cref="EquipmentType.Unknown"/> when not applicable.</summary>
    public EquipmentType EquipmentType { get; }

    public int MaxResults { get; }

    /// <summary>Maximum snippet length returned per result. Providers must truncate to this length.</summary>
    public int MaxSnippetCharacters { get; }

    /// <summary>Optional domain allow-list (for example "asme.org", "amca.org"). Empty means no restriction.</summary>
    public IReadOnlyList<string> AllowedDomains { get; }

    /// <summary>Optional domain block-list. Takes precedence over <see cref="AllowedDomains"/>.</summary>
    public IReadOnlyList<string> BlockedDomains { get; }

    /// <summary>Optional recency window in days. Null means no recency filter.</summary>
    public int? MaxAgeDays { get; }

    public WebSearchRequest(
        string query,
        WebSearchPurpose purpose = WebSearchPurpose.GeneralReference,
        EquipmentType equipmentType = EquipmentType.Unknown,
        int maxResults = DefaultMaxResults,
        int maxSnippetCharacters = DefaultMaxSnippetCharacters,
        IEnumerable<string>? allowedDomains = null,
        IEnumerable<string>? blockedDomains = null,
        int? maxAgeDays = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(query));

        if (maxResults < 1 || maxResults > MaxAllowedResults)
            throw new ArgumentOutOfRangeException(nameof(maxResults), $"Must be between 1 and {MaxAllowedResults}.");

        if (maxSnippetCharacters < 50)
            throw new ArgumentOutOfRangeException(nameof(maxSnippetCharacters), "Must be at least 50.");

        if (maxAgeDays is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAgeDays), "Must be greater than 0 when specified.");

        Query = query.Trim();
        Purpose = purpose;
        EquipmentType = equipmentType;
        MaxResults = maxResults;
        MaxSnippetCharacters = maxSnippetCharacters;
        AllowedDomains = NormalizeDomains(allowedDomains);
        BlockedDomains = NormalizeDomains(blockedDomains);
        MaxAgeDays = maxAgeDays;
    }

    private static IReadOnlyList<string> NormalizeDomains(IEnumerable<string>? domains)
        => new ReadOnlyCollection<string>(
            (domains ?? Enumerable.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().ToLowerInvariant())
                .Distinct()
                .ToList());
}

/// <summary>
/// A single cleaned search hit.
/// </summary>
/// <param name="Title">Page title.</param>
/// <param name="Url">Absolute source URL.</param>
/// <param name="Domain">Host name of the source (for example "amca.org").</param>
/// <param name="Snippet">Short cleaned text excerpt, plain text, truncated to the request limit.</param>
/// <param name="PublishedUtc">Publication date when the provider supplies one.</param>
/// <param name="Relevance">Provider relevance score normalized to 0-1, or null when unavailable.</param>
public sealed record WebSearchSnippet(
    string Title,
    string Url,
    string Domain,
    string Snippet,
    DateTimeOffset? PublishedUtc = null,
    double? Relevance = null);

/// <summary>
/// Result of a web search. Never throws for provider or network failures; failures are reported through
/// <see cref="Succeeded"/> and <see cref="Error"/>.
/// </summary>
public sealed class WebSearchResult
{
    /// <summary>
    /// Fixed notice that must accompany any web-derived context shown to a user or injected into a prompt.
    /// Web content is advisory only: it can inform explanations, but every physical parameter must come from
    /// a deterministic calculator and design rules.
    /// </summary>
    public const string AdvisoryNotice =
        "Web results are advisory context only. Physical parameters must be validated by the deterministic calculators before CAD generation.";

    public string Query { get; }
    public string Provider { get; }
    public IReadOnlyList<WebSearchSnippet> Snippets { get; }
    public bool Succeeded { get; }
    public string? Error { get; }
    public DateTimeOffset RetrievedAtUtc { get; }

    /// <summary>Always true. Web results are never an authoritative source of calculation inputs.</summary>
    public bool IsAdvisoryOnly => true;

    private WebSearchResult(
        string query,
        string provider,
        IEnumerable<WebSearchSnippet> snippets,
        bool succeeded,
        string? error)
    {
        Query = query;
        Provider = provider;
        Snippets = new ReadOnlyCollection<WebSearchSnippet>(snippets.ToList());
        Succeeded = succeeded;
        Error = error;
        RetrievedAtUtc = DateTimeOffset.UtcNow;
    }

    public static WebSearchResult Success(string query, string provider, IEnumerable<WebSearchSnippet> snippets)
        => new(query, provider, snippets ?? throw new ArgumentNullException(nameof(snippets)), true, null);

    public static WebSearchResult Failure(string query, string provider, string error)
        => new(query, provider, Enumerable.Empty<WebSearchSnippet>(), false,
            string.IsNullOrWhiteSpace(error) ? "Web search failed." : error);

    /// <summary>Returns a successful result with no hits.</summary>
    public static WebSearchResult Empty(string query, string provider)
        => new(query, provider, Enumerable.Empty<WebSearchSnippet>(), true, null);
}

/// <summary>
/// Executes external web searches for standards, vendor specifications and unindexed equipment.
/// Implementations (Tavily, DuckDuckGo, Google) live outside Core and must not be referenced by Core.
/// Results provide explanatory context only and never replace calculator output.
/// </summary>
public interface IWebSearchService
{
    /// <summary>Provider display name (for example "Tavily").</summary>
    string ProviderName { get; }

    /// <summary>
    /// True when the provider is configured and usable (API key present, network enabled).
    /// Callers must degrade gracefully to local knowledge when false.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs a web search and returns cleaned snippets.
    /// Must honor <see cref="WebSearchRequest.MaxResults"/>, <see cref="WebSearchRequest.MaxSnippetCharacters"/>,
    /// and the domain allow/block lists. Must not throw for provider or network errors; return
    /// <see cref="WebSearchResult.Failure"/> instead. May throw <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    Task<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default);
}
