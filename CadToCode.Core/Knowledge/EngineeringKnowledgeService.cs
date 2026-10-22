using System.Globalization;
using System.Text;
using System.Text.Json;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Knowledge;

public sealed class EngineeringKnowledgeOptions
{
    /// <summary>Root folder that contains one sub-folder per equipment family (e.g. "fans").</summary>
    public string RootPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "EngineeringKnowledge");

    public int MaxChunkChars { get; set; } = KnowledgeChunker.DefaultMaxChars;

    public int OverlapChars { get; set; } = KnowledgeChunker.DefaultOverlapChars;
}

/// <summary>
/// In-memory, deterministic keyword (TF-IDF) retrieval over local knowledge files.
/// Supported inputs: .json, .txt, .md. No network access, no LLM calls.
/// </summary>
public sealed class EngineeringKnowledgeService : IEngineeringKnowledgeService
{
    private static readonly IReadOnlyDictionary<string, EquipmentType> FolderMap =
        new Dictionary<string, EquipmentType>(StringComparer.OrdinalIgnoreCase)
        {
            ["fans"] = EquipmentType.AxialFan,
            ["centrifugal-fans"] = EquipmentType.CentrifugalFan,
            ["cyclones"] = EquipmentType.Cyclone,
            ["scrubbers"] = EquipmentType.WetScrubber,
            ["pressurevessels"] = EquipmentType.PressureVessel,
            ["piping"] = EquipmentType.PipingLayout
        };

    private static readonly string[] SupportedExtensions = { ".json", ".txt", ".md" };

    private readonly EngineeringKnowledgeOptions _options;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Index? _index;

    public EngineeringKnowledgeService(EngineeringKnowledgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        string query,
        EquipmentType? equipmentType = null,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return Array.Empty<KnowledgeSearchResult>();

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var queryTerms = Tokenize(query).Distinct().ToArray();
        if (queryTerms.Length == 0)
            return Array.Empty<KnowledgeSearchResult>();

        var results = new List<KnowledgeSearchResult>();

        foreach (var entry in index.Entries)
        {
            if (equipmentType.HasValue && entry.Chunk.EquipmentType != equipmentType.Value)
                continue;

            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!entry.TermFrequency.TryGetValue(term, out var tf))
                    continue;

                var idf = index.Idf.TryGetValue(term, out var v) ? v : 0d;
                score += (1d + Math.Log(tf)) * idf;

                if (entry.TitleTerms.Contains(term))
                    score += idf * 0.5;
            }

            if (score > 0)
                results.Add(new KnowledgeSearchResult(entry.Chunk, score / Math.Sqrt(entry.Length)));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Chunk.Id, StringComparer.Ordinal)
            .Take(topK)
            .ToList();
    }

    public async Task<KnowledgeChunk?> GetChunkAsync(
        string chunkId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chunkId))
            return null;

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        return index.ById.TryGetValue(chunkId, out var chunk) ? chunk : null;
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> ListChunksAsync(
        EquipmentType? equipmentType = null,
        CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);

        return index.Entries
            .Select(e => e.Chunk)
            .Where(c => !equipmentType.HasValue || c.EquipmentType == equipmentType.Value)
            .ToList();
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _index = await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Index> GetIndexAsync(CancellationToken cancellationToken)
    {
        var current = _index;
        if (current is not null)
            return current;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _index ??= await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
            return _index;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Index> BuildIndexAsync(CancellationToken cancellationToken)
    {
        var chunks = new List<KnowledgeChunk>();

        if (Directory.Exists(_options.RootPath))
        {
            foreach (var folder in Directory.EnumerateDirectories(_options.RootPath).OrderBy(p => p, StringComparer.Ordinal))
            {
                var folderName = Path.GetFileName(folder);
                if (!FolderMap.TryGetValue(folderName, out var equipmentType))
                    continue;

                var files = Directory
                    .EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.Ordinal);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var raw = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    var text = string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)
                        ? JsonToText(raw)
                        : raw;

                    var source = Path.GetRelativePath(_options.RootPath, file).Replace('\\', '/');

                    chunks.AddRange(KnowledgeChunker.Chunk(
                        equipmentType,
                        source,
                        text,
                        _options.MaxChunkChars,
                        _options.OverlapChars));
                }
            }
        }

        return Index.Build(chunks);
    }

    private static string JsonToText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var sb = new StringBuilder();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    sb.Append("# ").Append(prop.Name).Append("\n\n");
                    var lines = new StringBuilder();
                    Flatten(prop.Value, prop.Name, lines);
                    sb.Append(lines.ToString().TrimEnd()).Append("\n\n");
                }
            }
            else
            {
                Flatten(doc.RootElement, "root", sb);
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void Flatten(JsonElement element, string path, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    Flatten(prop.Value, $"{path}.{prop.Name}", sb);
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, $"{path}[{i++}]", sb);
                break;

            case JsonValueKind.String:
                sb.Append(path).Append(": ").Append(element.GetString()).Append('\n');
                break;

            case JsonValueKind.Number:
                sb.Append(path).Append(": ").Append(element.GetRawText()).Append('\n');
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.Append(path).Append(": ").Append(element.GetBoolean().ToString(CultureInfo.InvariantCulture).ToLowerInvariant()).Append('\n');
                break;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0)
            {
                if (sb.Length > 1)
                    yield return sb.ToString();
                sb.Clear();
            }
        }

        if (sb.Length > 1)
            yield return sb.ToString();
    }

    private sealed class Entry
    {
        public required KnowledgeChunk Chunk { get; init; }
        public required Dictionary<string, int> TermFrequency { get; init; }
        public required HashSet<string> TitleTerms { get; init; }
        public required int Length { get; init; }
    }

    private sealed class Index
    {
        public required IReadOnlyList<Entry> Entries { get; init; }
        public required IReadOnlyDictionary<string, double> Idf { get; init; }
        public required IReadOnlyDictionary<string, KnowledgeChunk> ById { get; init; }

        public static Index Build(IReadOnlyList<KnowledgeChunk> chunks)
        {
            var entries = new List<Entry>(chunks.Count);
            var documentFrequency = new Dictionary<string, int>();

            foreach (var chunk in chunks)
            {
                var tf = new Dictionary<string, int>();
                var length = 0;

                foreach (var term in Tokenize(chunk.Content))
                {
                    tf[term] = tf.TryGetValue(term, out var n) ? n + 1 : 1;
                    length++;
                }

                var titleTerms = new HashSet<string>(Tokenize(chunk.Title).Concat(chunk.Tags));

                foreach (var term in tf.Keys)
                    documentFrequency[term] = documentFrequency.TryGetValue(term, out var d) ? d + 1 : 1;

                entries.Add(new Entry
                {
                    Chunk = chunk,
                    TermFrequency = tf,
                    TitleTerms = titleTerms,
                    Length = Math.Max(length, 1)
                });
            }

            var total = Math.Max(chunks.Count, 1);
            var idf = documentFrequency.ToDictionary(
                kv => kv.Key,
                kv => Math.Log(1d + (double)total / kv.Value));

            var byId = new Dictionary<string, KnowledgeChunk>(StringComparer.Ordinal);
            foreach (var chunk in chunks)
                byId[chunk.Id] = chunk;

            return new Index { Entries = entries, Idf = idf, ById = byId };
        }
    }
}