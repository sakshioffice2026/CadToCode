using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Rules;

/// <summary>
/// Loads versioned design rules from JSON files in the local engineering knowledge base.
/// Expected layout: {root}/{folder}/design-rules.json (for example EngineeringKnowledge/fans/design-rules.json).
///
/// File schema:
/// {
///   "equipmentType": "AxialFan",
///   "version": "1.0.0",
///   "rules": [
///     { "id": "...", "key": "...", "description": "...", "siUnit": "...", "sourceReference": "...", "value": 0.0 },
///     { "id": "...", "key": "...", ..., "min": 0.0, "max": 0.0 },
///     { "id": "...", "key": "...", ..., "bandSelectorKey": "static_pressure_pa",
///       "bands": [ { "from": 0, "to": 500, "value": 0.4 }, { "from": 500, "value": 0.5 } ] }
///   ]
/// }
/// An omitted or null "to" in a band means open-ended (+infinity).
/// </summary>
public sealed class JsonDesignRulesProvider : IDesignRulesProvider
{
    public const string RulesFileName = "design-rules.json";

    private readonly string _rootDirectory;
    private readonly object _sync = new();
    private Snapshot _snapshot = Snapshot.Empty;

    public JsonDesignRulesProvider(string knowledgeRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(knowledgeRootDirectory))
            throw new ArgumentException("Knowledge root directory is required.", nameof(knowledgeRootDirectory));

        _rootDirectory = Path.GetFullPath(knowledgeRootDirectory);

        if (!Directory.Exists(_rootDirectory))
            throw new DirectoryNotFoundException($"Engineering knowledge directory not found: {_rootDirectory}");

        Reload();
    }

    public string KnowledgeVersion => Volatile.Read(ref _snapshot).Version;

    public bool TryGetRuleSet(EquipmentType type, out DesignRuleSet ruleSet)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.RuleSets.TryGetValue(type, out var found))
        {
            ruleSet = found;
            return true;
        }

        ruleSet = null!;
        return false;
    }

    public DesignRuleSet GetRuleSet(EquipmentType type)
        => TryGetRuleSet(type, out var ruleSet)
            ? ruleSet
            : throw new NotSupportedException(
                $"No design rule set is loaded for {type}. Add '{RulesFileName}' under the matching EngineeringKnowledge folder.");

    public void Reload()
    {
        lock (_sync)
        {
            var files = Directory.EnumerateFiles(_rootDirectory, RulesFileName, SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            var ruleSets = new Dictionary<EquipmentType, DesignRuleSet>();

            foreach (var file in files)
            {
                var ruleSet = ParseFile(file);

                if (!ruleSets.TryAdd(ruleSet.EquipmentType, ruleSet))
                    throw new InvalidDataException(
                        $"Duplicate rule set for {ruleSet.EquipmentType} found in '{file}'.");
            }

            var version = ruleSets.Count == 0
                ? "empty"
                : string.Join(";", ruleSets.Values
                    .OrderBy(r => r.EquipmentType)
                    .Select(r => $"{r.EquipmentType}:{r.Version}"));

            // Swap only after everything parsed successfully; a failed reload keeps the previous snapshot.
            Volatile.Write(ref _snapshot, new Snapshot(ruleSets, version));
        }
    }

    private static DesignRuleSet ParseFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Root element must be an object.");

            var typeText = RequireString(root, "equipmentType");
            if (!Enum.TryParse<EquipmentType>(typeText, ignoreCase: true, out var type) || type == EquipmentType.Unknown)
                throw new InvalidDataException($"Unknown equipmentType '{typeText}'.");

            var version = RequireString(root, "version");

            if (!root.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("'rules' array is required.");

            var rules = new List<DesignRule>();
            int index = 0;
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement, index));
                index++;
            }

            return new DesignRuleSet(type, version, rules);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid design rules file '{path}': {ex.Message}", ex);
        }
    }

    private static DesignRule ParseRule(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Rule #{index} must be an object.");

        var id = RequireString(element, "id");
        var key = RequireString(element, "key");
        var sourceReference = RequireString(element, "sourceReference");
        var description = OptionalString(element, "description") ?? string.Empty;
        var siUnit = OptionalString(element, "siUnit") ?? string.Empty;

        double? value = OptionalNumber(element, "value");
        double? min = OptionalNumber(element, "min");
        double? max = OptionalNumber(element, "max");

        List<DesignRuleBand>? bands = null;
        string? selectorKey = OptionalString(element, "bandSelectorKey");

        if (element.TryGetProperty("bands", out var bandsElement) && bandsElement.ValueKind == JsonValueKind.Array)
        {
            bands = new List<DesignRuleBand>();
            foreach (var b in bandsElement.EnumerateArray())
            {
                var from = OptionalNumber(b, "from") ?? throw new InvalidDataException($"Rule '{id}' band is missing 'from'.");
                var to = OptionalNumber(b, "to") ?? double.PositiveInfinity;
                var bandValue = OptionalNumber(b, "value") ?? throw new InvalidDataException($"Rule '{id}' band is missing 'value'.");
                bands.Add(new DesignRuleBand(from, to, bandValue));
            }
        }

        return new DesignRule(id, key, description, siUnit, sourceReference, value, min, max, bands, selectorKey);
    }

    private static string RequireString(JsonElement element, string name)
    {
        var text = OptionalString(element, name);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"'{name}' is required.");

        return text;
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? OptionalNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;

        if (p.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException($"'{name}' must be a number.");

        var number = p.GetDouble();
        if (!double.IsFinite(number))
            throw new InvalidDataException($"'{name}' must be a finite number.");

        return number;
    }

    private sealed class Snapshot
    {
        public static Snapshot Empty { get; } = new(new Dictionary<EquipmentType, DesignRuleSet>(), "empty");

        public IReadOnlyDictionary<EquipmentType, DesignRuleSet> RuleSets { get; }
        public string Version { get; }

        public Snapshot(IReadOnlyDictionary<EquipmentType, DesignRuleSet> ruleSets, string version)
        {
            RuleSets = ruleSets;
            Version = version;
        }
    }
}
