using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Rules;

/// <summary>
/// A value band: when the selector value falls in [From, To), the band value applies.
/// Example: hub-to-tip ratio by static pressure (Pa).
/// </summary>
/// <param name="From">Inclusive lower bound of the selector.</param>
/// <param name="To">Exclusive upper bound of the selector. Use <see cref="double.PositiveInfinity"/> for open-ended.</param>
/// <param name="Value">Value applied inside the band.</param>
public sealed record DesignRuleBand(double From, double To, double Value)
{
    public bool Contains(double selector) => selector >= From && selector < To;
}

/// <summary>
/// A single engineering design rule or constant loaded from the knowledge base.
/// A rule carries exactly one of: a scalar <see cref="Value"/>, a <see cref="Min"/>/<see cref="Max"/> range, or <see cref="Bands"/>.
/// </summary>
public sealed class DesignRule
{
    /// <summary>Stable rule identifier (e.g. "FAN-RULE-003").</summary>
    public string Id { get; }

    /// <summary>Lookup key within the equipment rule set (e.g. "max_tip_speed_ms").</summary>
    public string Key { get; }

    public string Description { get; }

    /// <summary>SI unit of the value(s).</summary>
    public string SiUnit { get; }

    /// <summary>Citation (standard, handbook, section) for audit and display.</summary>
    public string SourceReference { get; }

    /// <summary>Scalar value, when the rule is a constant.</summary>
    public double? Value { get; }

    /// <summary>Lower recommended limit, when the rule is a range.</summary>
    public double? Min { get; }

    /// <summary>Upper recommended limit, when the rule is a range.</summary>
    public double? Max { get; }

    /// <summary>Selector-driven bands, when the rule is band-based.</summary>
    public IReadOnlyList<DesignRuleBand> Bands { get; }

    /// <summary>Key of the input/derived value used to pick a band (e.g. "static_pressure_pa").</summary>
    public string? BandSelectorKey { get; }

    public DesignRule(
        string id,
        string key,
        string description,
        string siUnit,
        string sourceReference,
        double? value = null,
        double? min = null,
        double? max = null,
        IEnumerable<DesignRuleBand>? bands = null,
        string? bandSelectorKey = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Rule id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Rule key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(sourceReference)) throw new ArgumentException("Source reference is required for every rule.", nameof(sourceReference));

        var bandList = (bands ?? Enumerable.Empty<DesignRuleBand>()).OrderBy(b => b.From).ToList();

        bool hasScalar = value.HasValue;
        bool hasRange = min.HasValue || max.HasValue;
        bool hasBands = bandList.Count > 0;

        int kinds = (hasScalar ? 1 : 0) + (hasRange ? 1 : 0) + (hasBands ? 1 : 0);
        if (kinds != 1)
            throw new ArgumentException($"Rule '{id}' must define exactly one of: scalar value, min/max range, or bands.");

        if (min.HasValue && max.HasValue && min.Value > max.Value)
            throw new ArgumentException($"Rule '{id}' has min greater than max.");

        if (hasBands)
        {
            if (string.IsNullOrWhiteSpace(bandSelectorKey))
                throw new ArgumentException($"Rule '{id}' defines bands and requires a band selector key.", nameof(bandSelectorKey));

            for (int i = 0; i < bandList.Count; i++)
            {
                if (bandList[i].From >= bandList[i].To)
                    throw new ArgumentException($"Rule '{id}' has an empty or inverted band.");

                if (i > 0 && bandList[i].From < bandList[i - 1].To)
                    throw new ArgumentException($"Rule '{id}' has overlapping bands.");
            }
        }

        Id = id;
        Key = key;
        Description = description ?? string.Empty;
        SiUnit = siUnit ?? string.Empty;
        SourceReference = sourceReference;
        Value = value;
        Min = min;
        Max = max;
        Bands = new ReadOnlyCollection<DesignRuleBand>(bandList);
        BandSelectorKey = bandSelectorKey;
    }

    /// <summary>
    /// Resolves the applicable value. For scalars returns the value; for bands returns the band value for the selector.
    /// </summary>
    /// <exception cref="InvalidOperationException">Rule is a range, or the selector is outside all bands.</exception>
    public double Resolve(double? selector = null)
    {
        if (Value.HasValue)
            return Value.Value;

        if (Bands.Count > 0)
        {
            if (!selector.HasValue)
                throw new InvalidOperationException($"Rule '{Id}' requires selector '{BandSelectorKey}'.");

            var band = Bands.FirstOrDefault(b => b.Contains(selector.Value));
            if (band is null)
                throw new InvalidOperationException($"Selector {selector.Value} is outside all bands of rule '{Id}'.");

            return band.Value;
        }

        throw new InvalidOperationException($"Rule '{Id}' is a range. Use {nameof(IsWithinRange)} instead.");
    }

    /// <summary>True when the value is inside the rule's recommended min/max range (open sides are unbounded).</summary>
    public bool IsWithinRange(double value)
    {
        if (Min.HasValue && value < Min.Value) return false;
        if (Max.HasValue && value > Max.Value) return false;
        return true;
    }
}

/// <summary>
/// Raised when a required design rule is missing. Calculators must not substitute hidden defaults.
/// </summary>
public sealed class DesignRuleNotFoundException : Exception
{
    public EquipmentType EquipmentType { get; }
    public string Key { get; }

    public DesignRuleNotFoundException(EquipmentType equipmentType, string key)
        : base($"Design rule '{key}' was not found for {equipmentType}. Add it to the engineering knowledge base.")
    {
        EquipmentType = equipmentType;
        Key = key;
    }
}

/// <summary>
/// Versioned collection of design rules for one equipment type.
/// </summary>
public sealed class DesignRuleSet
{
    private readonly IReadOnlyDictionary<string, DesignRule> _rules;

    public EquipmentType EquipmentType { get; }

    /// <summary>Knowledge base version for this rule set, recorded for audit.</summary>
    public string Version { get; }

    public IReadOnlyCollection<DesignRule> Rules => _rules.Values.ToList().AsReadOnly();

    public DesignRuleSet(EquipmentType equipmentType, string version, IEnumerable<DesignRule> rules)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version is required.", nameof(version));
        if (rules is null) throw new ArgumentNullException(nameof(rules));

        var map = new Dictionary<string, DesignRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!map.TryAdd(rule.Key, rule))
                throw new ArgumentException($"Duplicate design rule key '{rule.Key}' in {equipmentType} rule set.", nameof(rules));
        }

        EquipmentType = equipmentType;
        Version = version;
        _rules = new ReadOnlyDictionary<string, DesignRule>(map);
    }

    public bool Contains(string key) => !string.IsNullOrWhiteSpace(key) && _rules.ContainsKey(key);

    public bool TryGet(string key, out DesignRule rule)
    {
        if (!string.IsNullOrWhiteSpace(key) && _rules.TryGetValue(key, out var found))
        {
            rule = found;
            return true;
        }

        rule = null!;
        return false;
    }

    /// <summary>Gets a rule or throws <see cref="DesignRuleNotFoundException"/>.</summary>
    public DesignRule Get(string key)
        => TryGet(key, out var rule) ? rule : throw new DesignRuleNotFoundException(EquipmentType, key);

    /// <summary>Gets a scalar or band-resolved value or throws when the rule is missing.</summary>
    public double GetValue(string key, double? selector = null) => Get(key).Resolve(selector);
}

/// <summary>
/// Supplies versioned engineering design rules (limits, ratios, constants) to calculators.
/// Implementations load from the local knowledge base (JSON) and must not depend on LLM, web or UI layers.
/// </summary>
public interface IDesignRulesProvider
{
    /// <summary>Version of the loaded knowledge base.</summary>
    string KnowledgeVersion { get; }

    /// <summary>Returns the rule set for the equipment type, or false when none is loaded.</summary>
    bool TryGetRuleSet(EquipmentType type, out DesignRuleSet ruleSet);

    /// <summary>Returns the rule set for the equipment type.</summary>
    /// <exception cref="NotSupportedException">Thrown when no rule set exists for the type.</exception>
    DesignRuleSet GetRuleSet(EquipmentType type);

    /// <summary>Reloads rules from the knowledge base source.</summary>
    void Reload();
}
