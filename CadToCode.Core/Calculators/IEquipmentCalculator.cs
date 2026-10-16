using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Immutable set of calculator inputs. All values are SI (see <see cref="ParameterDescriptor.SiUnit"/>).
/// Keys are case-insensitive and match <see cref="ParameterDescriptor.Key"/>.
/// </summary>
public sealed class EquipmentInputs : IReadOnlyDictionary<string, double>
{
    private readonly IReadOnlyDictionary<string, double> _values;

    public static EquipmentInputs Empty { get; } = new(new Dictionary<string, double>());

    public EquipmentInputs(IEnumerable<KeyValuePair<string, double>> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                throw new ArgumentException("Input key cannot be empty.", nameof(values));

            map[kv.Key.Trim()] = kv.Value;
        }

        _values = new ReadOnlyDictionary<string, double>(map);
    }

    public double this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<double> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => !string.IsNullOrWhiteSpace(key) && _values.ContainsKey(key);
    public bool TryGetValue(string key, out double value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            value = default;
            return false;
        }

        return _values.TryGetValue(key, out value);
    }

    /// <summary>Returns the value for a key, or <paramref name="fallback"/> when absent.</summary>
    public double GetOrDefault(string key, double fallback)
        => TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Returns a copy with one value added or replaced.</summary>
    public EquipmentInputs With(string key, double value)
    {
        var copy = _values.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        copy[key] = value;
        return new EquipmentInputs(copy);
    }

    /// <summary>Returns a copy without the given key. Unchanged copy when the key is absent.</summary>
    public EquipmentInputs Without(string key)
    {
        var copy = _values.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(key))
        {
            copy.Remove(key.Trim());
        }

        return new EquipmentInputs(copy);
    }

    /// <summary>Case-insensitive read-only view of the values, for mappers and audit snapshots.</summary>
    public IReadOnlyDictionary<string, double> AsDictionary() => _values;

    public IEnumerator<KeyValuePair<string, double>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A single input validation finding.
/// </summary>
/// <param name="Code">Stable machine code (e.g. "INPUT_MISSING", "INPUT_OUT_OF_RANGE").</param>
/// <param name="Message">Human-readable message suitable for a clarifying prompt.</param>
/// <param name="Key">Input key the issue refers to.</param>
/// <param name="IsBlocking">True when the calculation cannot proceed.</param>
public sealed record InputIssue(string Code, string Message, string? Key = null, bool IsBlocking = true);

/// <summary>
/// Result of input validation.
/// </summary>
public sealed class InputValidationResult
{
    public IReadOnlyList<InputIssue> Issues { get; }

    /// <summary>Required keys that were not supplied. Drives the clarifying-question generator.</summary>
    public IReadOnlyList<string> MissingRequiredKeys { get; }

    /// <summary>True when no blocking issues exist.</summary>
    public bool IsValid => Issues.All(i => !i.IsBlocking);

    public IEnumerable<InputIssue> BlockingIssues => Issues.Where(i => i.IsBlocking);

    public InputValidationResult(IEnumerable<InputIssue>? issues = null, IEnumerable<string>? missingRequiredKeys = null)
    {
        Issues = new ReadOnlyCollection<InputIssue>((issues ?? Enumerable.Empty<InputIssue>()).ToList());
        MissingRequiredKeys = new ReadOnlyCollection<string>((missingRequiredKeys ?? Enumerable.Empty<string>()).ToList());
    }

    public static InputValidationResult Valid { get; } = new();

    public static InputValidationResult Invalid(IEnumerable<InputIssue> issues, IEnumerable<string>? missingRequiredKeys = null)
        => new(issues, missingRequiredKeys);
}

/// <summary>
/// Deterministic physics calculator for one equipment type.
/// Implementations must not depend on any LLM, web, database or UI component.
/// All inputs and outputs are SI.
/// </summary>
public interface IEquipmentCalculator
{
    /// <summary>Equipment type handled by this calculator.</summary>
    EquipmentType EquipmentType { get; }

    /// <summary>
    /// Id that equipment manifests use in <c>calculatorId</c>. Defaults to the <see cref="EquipmentType"/> name
    /// (for example "AxialFan", "Cyclone"), which matches the existing manifests. Override it for calculators
    /// whose manifest id differs.
    /// </summary>
    string CalculatorId => EquipmentType.ToString();

    /// <summary>Semantic version of the calculation logic, recorded in every result for audit.</summary>
    string Version { get; }

    /// <summary>
    /// Validates inputs: required keys present, values finite, positive where required, and within
    /// the physically meaningful range. Never throws for bad user data.
    /// </summary>
    InputValidationResult Validate(EquipmentInputs inputs);

    /// <summary>
    /// Runs validation, then calculation, then range/sanity guards.
    /// Returns a failed result (never throws) when validation fails.
    /// Out-of-envelope designs produce <see cref="EngineeringWarning"/> entries and are never silently clamped.
    /// </summary>
    CalculationResult Calculate(EquipmentInputs inputs);
}

/// <summary>
/// Resolves calculators by equipment type or by the calculator id declared in an equipment manifest.
/// </summary>
public interface IEquipmentCalculatorRegistry
{
    /// <summary>All registered calculators.</summary>
    IReadOnlyCollection<IEquipmentCalculator> All { get; }

    /// <summary>Returns the calculator for the type, or false when none is registered.</summary>
    bool TryGet(EquipmentType type, out IEquipmentCalculator calculator);

    /// <summary>Returns the calculator for the type.</summary>
    /// <exception cref="NotSupportedException">Thrown when no calculator is registered. Callers must not substitute a generic pipe.</exception>
    IEquipmentCalculator Get(EquipmentType type);

    /// <summary>Ids of all registered calculators. Feeds <c>ConsistencyInputs.CalculatorIds</c>.</summary>
    IReadOnlyCollection<string> Ids
        => All.Select(c => c.CalculatorId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Returns the calculator whose <see cref="IEquipmentCalculator.CalculatorId"/> matches the id
    /// (case-insensitive), or false when none does.
    /// </summary>
    bool TryGetById(string? calculatorId, out IEquipmentCalculator calculator)
    {
        if (!string.IsNullOrWhiteSpace(calculatorId))
        {
            var id = calculatorId.Trim();
            var found = All.FirstOrDefault(c => string.Equals(c.CalculatorId, id, StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                calculator = found;
                return true;
            }
        }

        calculator = null!;
        return false;
    }

    /// <summary>Returns the calculator for a manifest calculator id.</summary>
    /// <exception cref="NotSupportedException">Thrown when no calculator has that id. Callers must not substitute a generic pipe.</exception>
    IEquipmentCalculator GetById(string? calculatorId)
        => TryGetById(calculatorId, out var calculator)
            ? calculator
            : throw new NotSupportedException(
                $"No calculator is registered with id '{calculatorId}'. Ask the user to clarify the equipment; do not substitute a generic pipe.");
}