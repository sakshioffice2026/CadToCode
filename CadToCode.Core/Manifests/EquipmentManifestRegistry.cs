using System.Text.Json;
using System.Text.RegularExpressions;

namespace CadToCode.Core.Manifests;

/// <summary>Outcome class of resolving free text to an equipment manifest.</summary>
public enum ManifestResolutionStatus
{
    /// <summary>Nothing in the text names a known equipment type.</summary>
    NoMatch = 0,

    /// <summary>Exactly one equipment type matches and no confusable type also matches.</summary>
    Resolved = 1,

    /// <summary>
    /// More than one equipment type matches equally well, or a declared confusable type also matches.
    /// The gate must ask the user instead of choosing.
    /// </summary>
    Ambiguous = 2
}

/// <summary>Result of resolving free text to an equipment manifest.</summary>
public sealed record ManifestResolution
{
    private static readonly IReadOnlyList<EquipmentManifest> NoCandidates = Array.Empty<EquipmentManifest>();

    public ManifestResolutionStatus Status { get; init; }

    /// <summary>The chosen manifest. Set only when <see cref="Status"/> is <see cref="ManifestResolutionStatus.Resolved"/>.</summary>
    public EquipmentManifest? Manifest { get; init; }

    /// <summary>
    /// Manifests the text may refer to. One item when resolved, several when ambiguous, empty on no match.
    /// </summary>
    public IReadOnlyList<EquipmentManifest> Candidates { get; init; } = NoCandidates;

    /// <summary>Stable machine reason, e.g. "TIE" or "CONFUSABLE". Empty when resolved or no match.</summary>
    public string Reason { get; init; } = string.Empty;

    public bool IsResolved => Status == ManifestResolutionStatus.Resolved && Manifest is not null;

    public bool IsAmbiguous => Status == ManifestResolutionStatus.Ambiguous;

    public static ManifestResolution None() => new() { Status = ManifestResolutionStatus.NoMatch };

    public static ManifestResolution Single(EquipmentManifest manifest) => new()
    {
        Status = ManifestResolutionStatus.Resolved,
        Manifest = manifest,
        Candidates = new[] { manifest }
    };

    public static ManifestResolution Ambiguity(IReadOnlyList<EquipmentManifest> candidates, string reason) => new()
    {
        Status = ManifestResolutionStatus.Ambiguous,
        Candidates = candidates,
        Reason = reason
    };
}

/// <summary>Read access to the loaded, validated equipment manifests.</summary>
public interface IEquipmentManifestRegistry
{
    /// <summary>Valid manifests only. Rejected manifests never appear here.</summary>
    IReadOnlyList<EquipmentManifest> All { get; }

    /// <summary>Every problem found during the last load (parse errors, validation, collisions).</summary>
    IReadOnlyList<ManifestIssue> LoadIssues { get; }

    bool TryGet(string id, out EquipmentManifest manifest);

    /// <summary>
    /// Finds the manifest named in free text by id, display name or alias.
    /// Returns null when nothing matches, when two manifests match equally well,
    /// or when a declared confusable manifest also matches.
    /// </summary>
    EquipmentManifest? Resolve(string? text);

    /// <summary>
    /// Same matching as <see cref="Resolve"/> but reports why nothing was chosen,
    /// so the gate can ask a structured clarifying question.
    /// </summary>
    ManifestResolution ResolveDetailed(string? text)
    {
        var manifest = Resolve(text);

        return manifest is null
            ? ManifestResolution.None()
            : ManifestResolution.Single(manifest);
    }

    /// <summary>
    /// Manifests easily mistaken for the given one, in either direction of
    /// <see cref="EquipmentManifest.ConfusableWith"/>.
    /// </summary>
    IReadOnlyList<EquipmentManifest> GetConfusables(string id)
        => Array.Empty<EquipmentManifest>();

    /// <summary>Reloads all manifest files from disk.</summary>
    void Reload();
}

/// <summary>
/// Loads equipment manifests from JSON files named <c>equipment-manifest.json</c> found anywhere
/// under the knowledge root. A file may hold one manifest object or an array of manifests.
/// Invalid manifests are rejected and reported through <see cref="LoadIssues"/>; they are never
/// loaded partially.
/// </summary>
public sealed class JsonEquipmentManifestRegistry : IEquipmentManifestRegistry
{
    public const string ManifestFileName = "equipment-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Regex NonAlphaNumeric = new(
        "[^a-z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private const int MinPhraseLength = 3;

    private readonly string _rootDirectory;
    private readonly object _sync = new();
    private Snapshot _snapshot = Snapshot.Empty;

    public JsonEquipmentManifestRegistry(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _rootDirectory = Path.GetFullPath(rootDirectory);
        Reload();
    }

    public IReadOnlyList<EquipmentManifest> All => Volatile.Read(ref _snapshot).Manifests;

    public IReadOnlyList<ManifestIssue> LoadIssues => Volatile.Read(ref _snapshot).Issues;

    public bool TryGet(string id, out EquipmentManifest manifest)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        if (!string.IsNullOrWhiteSpace(id) && snapshot.ById.TryGetValue(id.Trim(), out var found))
        {
            manifest = found;
            return true;
        }

        manifest = null!;
        return false;
    }

    public EquipmentManifest? Resolve(string? text)
    {
        var resolution = ResolveDetailed(text);

        return resolution.IsResolved ? resolution.Manifest : null;
    }

    public ManifestResolution ResolveDetailed(string? text)
    {
        var normalized = Normalize(text);

        if (normalized.Length == 0)
        {
            return ManifestResolution.None();
        }

        var padded = " " + normalized + " ";
        var snapshot = Volatile.Read(ref _snapshot);

        var matches = new List<(EquipmentManifest Manifest, int Score)>();

        foreach (var manifest in snapshot.Manifests)
        {
            var score = Score(manifest, padded);

            if (score > 0)
            {
                matches.Add((manifest, score));
            }
        }

        if (matches.Count == 0)
        {
            return ManifestResolution.None();
        }

        matches = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var top = matches[0];

        var tied = matches
            .Where(m => m.Score == top.Score)
            .Select(m => m.Manifest)
            .ToList();

        if (tied.Count > 1)
        {
            return ManifestResolution.Ambiguity(tied, "TIE");
        }

        var confusable = matches
            .Skip(1)
            .Where(m => AreConfusable(top.Manifest, m.Manifest))
            .Select(m => m.Manifest)
            .ToList();

        if (confusable.Count > 0)
        {
            var candidates = new List<EquipmentManifest> { top.Manifest };
            candidates.AddRange(confusable);

            return ManifestResolution.Ambiguity(candidates, "CONFUSABLE");
        }

        return ManifestResolution.Single(top.Manifest);
    }

    public IReadOnlyList<EquipmentManifest> GetConfusables(string id)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        if (string.IsNullOrWhiteSpace(id) || !snapshot.ById.TryGetValue(id.Trim(), out var source))
        {
            return Array.Empty<EquipmentManifest>();
        }

        return snapshot.Manifests
            .Where(m => !string.Equals(m.Id, source.Id, StringComparison.OrdinalIgnoreCase)
                        && AreConfusable(source, m))
            .ToList();
    }

    public void Reload()
    {
        lock (_sync)
        {
            Volatile.Write(ref _snapshot, Load());
        }
    }

    private static bool AreConfusable(EquipmentManifest a, EquipmentManifest b)
        => Lists(a, b.Id) || Lists(b, a.Id);

    private static bool Lists(EquipmentManifest owner, string otherId)
        => owner.ConfusableWith.Any(c =>
            !string.IsNullOrWhiteSpace(c)
            && string.Equals(c.Trim(), otherId, StringComparison.OrdinalIgnoreCase));

    private Snapshot Load()
    {
        var issues = new List<ManifestIssue>();

        if (!Directory.Exists(_rootDirectory))
        {
            issues.Add(new ManifestIssue(
                "MANIFEST_ROOT_MISSING",
                "Manifest directory not found: " + _rootDirectory,
                _rootDirectory));

            return new Snapshot(Array.Empty<EquipmentManifest>(), issues);
        }

        var files = Directory
            .EnumerateFiles(_rootDirectory, ManifestFileName, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var valid = new List<EquipmentManifest>();

        foreach (var file in files)
        {
            foreach (var manifest in ReadFile(file, issues))
            {
                var own = EquipmentManifestValidator.Validate(manifest);

                if (own.Count > 0)
                {
                    issues.AddRange(own);
                    continue;
                }

                valid.Add(manifest);
            }
        }

        var setIssues = EquipmentManifestValidator.ValidateSet(valid);

        var collisions = setIssues
            .Where(i => i.Code is "MANIFEST_DUPLICATE_ID" or "MANIFEST_ALIAS_CONFLICT")
            .ToList();

        var reportOnly = setIssues
            .Where(i => i.Code == "MANIFEST_CONFUSABLE_UNKNOWN")
            .ToList();

        issues.AddRange(collisions);
        issues.AddRange(reportOnly);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in collisions)
        {
            var slash = issue.Path.IndexOf('/');
            excluded.Add(slash >= 0 ? issue.Path[..slash] : issue.Path);
        }

        var accepted = valid
            .Where(m => !excluded.Contains(m.Id))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Snapshot(accepted, issues);
    }

    private static IEnumerable<EquipmentManifest> ReadFile(string file, List<ManifestIssue> issues)
    {
        var manifests = new List<EquipmentManifest>();

        try
        {
            var text = File.ReadAllText(file);

            using var document = JsonDocument.Parse(text, DocumentOptions);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    AddParsed(element.GetRawText(), file, manifests, issues);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddParsed(root.GetRawText(), file, manifests, issues);
            }
            else
            {
                issues.Add(new ManifestIssue(
                    "MANIFEST_PARSE_ERROR",
                    "File must contain a manifest object or an array of manifests.",
                    file));
            }
        }
        catch (JsonException ex)
        {
            issues.Add(new ManifestIssue("MANIFEST_PARSE_ERROR", "Invalid JSON: " + ex.Message, file));
        }
        catch (IOException ex)
        {
            issues.Add(new ManifestIssue("MANIFEST_READ_ERROR", "Could not read file: " + ex.Message, file));
        }
        catch (UnauthorizedAccessException ex)
        {
            issues.Add(new ManifestIssue("MANIFEST_READ_ERROR", "Could not read file: " + ex.Message, file));
        }

        return manifests;
    }

    private static void AddParsed(
        string json,
        string file,
        List<EquipmentManifest> manifests,
        List<ManifestIssue> issues)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<EquipmentManifest>(json, JsonOptions);

            if (manifest is null)
            {
                issues.Add(new ManifestIssue("MANIFEST_PARSE_ERROR", "Manifest entry is empty.", file));
                return;
            }

            manifests.Add(manifest);
        }
        catch (JsonException ex)
        {
            issues.Add(new ManifestIssue("MANIFEST_PARSE_ERROR", "Invalid manifest: " + ex.Message, file));
        }
    }

    private static int Score(EquipmentManifest manifest, string paddedText)
    {
        var best = 0;

        best = Math.Max(best, ScorePhrase(manifest.Id, paddedText));
        best = Math.Max(best, ScorePhrase(manifest.DisplayName, paddedText));

        foreach (var alias in manifest.Aliases)
        {
            best = Math.Max(best, ScorePhrase(alias, paddedText));
        }

        return best;
    }

    private static int ScorePhrase(string? phrase, string paddedText)
    {
        var normalized = Normalize(phrase);

        if (normalized.Length < MinPhraseLength)
        {
            return 0;
        }

        return paddedText.Contains(" " + normalized + " ", StringComparison.Ordinal)
            ? normalized.Length
            : 0;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return NonAlphaNumeric.Replace(value.ToLowerInvariant(), " ").Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(Array.Empty<EquipmentManifest>(), Array.Empty<ManifestIssue>());

        public Snapshot(IReadOnlyList<EquipmentManifest> manifests, IReadOnlyList<ManifestIssue> issues)
        {
            Manifests = manifests;
            Issues = issues;
            ById = manifests.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<EquipmentManifest> Manifests { get; }

        public IReadOnlyList<ManifestIssue> Issues { get; }

        public IReadOnlyDictionary<string, EquipmentManifest> ById { get; }
    }
}