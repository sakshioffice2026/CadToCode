using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CadToCode.Core.Domain;

/// <summary>
/// Severity of an engineering warning raised during validation or calculation.
/// </summary>
public enum WarningSeverity
{
    /// <summary>Informational note. Does not affect CAD generation.</summary>
    Info = 0,

    /// <summary>Design is outside the recommended envelope but physically computable. Requires user acknowledgement.</summary>
    Caution = 1,

    /// <summary>Design is outside the validated range. CAD generation must be blocked until resolved.</summary>
    Critical = 2
}

/// <summary>
/// An engineering warning attached to a calculation.
/// </summary>
/// <param name="Code">Stable machine code (e.g. "FAN_TIP_SPEED_HIGH").</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Severity">Severity level.</param>
/// <param name="RelatedKey">Optional parameter key the warning refers to.</param>
/// <param name="FormulaId">Optional formula ID that triggered the warning.</param>
public sealed record EngineeringWarning(
    string Code,
    string Message,
    WarningSeverity Severity,
    string? RelatedKey = null,
    string? FormulaId = null);

/// <summary>
/// A single deterministic calculated value. Always stored in SI units.
/// </summary>
/// <param name="Key">Stable parameter key matching <see cref="ParameterDescriptor.Key"/>.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Value">Numeric value in <paramref name="SiUnit"/>.</param>
/// <param name="SiUnit">SI unit symbol (e.g. "m", "Pa", "m/s", "deg", "-").</param>
/// <param name="FormulaId">Formula identifier from the engineering knowledge base (e.g. "FAN-001").</param>
/// <param name="SourceReference">Citation for the formula or rule (standard, handbook, section).</param>
/// <param name="Expression">Human-readable equation used for the review step.</param>
public sealed record CalculatedValue(
    string Key,
    string DisplayName,
    double Value,
    string SiUnit,
    string FormulaId,
    string SourceReference,
    string Expression = "")
{
    /// <summary>True when the value is a finite number.</summary>
    public bool IsFinite => double.IsFinite(Value);
}

/// <summary>
/// Typed output of an equipment calculator.
/// Immutable once built. Use <see cref="Builder"/> to construct.
/// </summary>
public sealed class CalculationResult
{
    private readonly Dictionary<string, CalculatedValue> _byKey;

    public EquipmentType EquipmentType { get; }

    /// <summary>Calculated values in calculation order.</summary>
    public IReadOnlyList<CalculatedValue> Values { get; }

    /// <summary>Engineering warnings raised during validation and calculation.</summary>
    public IReadOnlyList<EngineeringWarning> Warnings { get; }

    /// <summary>Blocking errors (invalid or missing inputs, calculation failure).</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Distinct formula IDs used, for audit trail.</summary>
    public IReadOnlyList<string> FormulaIdsUsed { get; }

    /// <summary>UTC timestamp of calculation.</summary>
    public DateTimeOffset CalculatedAtUtc { get; }

    /// <summary>Version of the calculator that produced this result.</summary>
    public string CalculatorVersion { get; }

    /// <summary>True when no errors exist and at least one value was calculated.</summary>
    public bool Succeeded => Errors.Count == 0 && Values.Count > 0;

    /// <summary>True when any warning has <see cref="WarningSeverity.Critical"/> severity.</summary>
    public bool HasCriticalWarnings => Warnings.Any(w => w.Severity == WarningSeverity.Critical);

    /// <summary>True when any warning has <see cref="WarningSeverity.Caution"/> severity.</summary>
    public bool HasCautionWarnings => Warnings.Any(w => w.Severity == WarningSeverity.Caution);

    /// <summary>
    /// True only when the result is safe to hand to CAD script generation:
    /// succeeded, all values finite, and no critical warnings.
    /// </summary>
    public bool IsCadReady => Succeeded && !HasCriticalWarnings && Values.All(v => v.IsFinite);

    private CalculationResult(
        EquipmentType equipmentType,
        IEnumerable<CalculatedValue> values,
        IEnumerable<EngineeringWarning> warnings,
        IEnumerable<string> errors,
        string calculatorVersion,
        DateTimeOffset calculatedAtUtc)
    {
        EquipmentType = equipmentType;
        var valueList = values.ToList();
        _byKey = new Dictionary<string, CalculatedValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in valueList)
            _byKey[v.Key] = v;

        Values = new ReadOnlyCollection<CalculatedValue>(valueList);
        Warnings = new ReadOnlyCollection<EngineeringWarning>(warnings.ToList());
        Errors = new ReadOnlyCollection<string>(errors.ToList());
        FormulaIdsUsed = new ReadOnlyCollection<string>(
            valueList.Select(v => v.FormulaId)
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList());
        CalculatorVersion = calculatorVersion;
        CalculatedAtUtc = calculatedAtUtc;
    }

    /// <summary>Gets a calculated value by key, or null when absent.</summary>
    public CalculatedValue? Find(string key)
        => !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out var v) ? v : null;

    /// <summary>Tries to get a calculated value by key.</summary>
    public bool TryGet(string key, out CalculatedValue value)
    {
        if (!string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = null!;
        return false;
    }

    /// <summary>Gets the numeric value for a key in its SI unit.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when the key was not calculated.</exception>
    public double GetValue(string key)
        => Find(key)?.Value ?? throw new KeyNotFoundException($"Calculated value '{key}' not found for {EquipmentType}.");

    /// <summary>Returns a failed result containing only errors.</summary>
    public static CalculationResult Failure(
        EquipmentType equipmentType,
        IEnumerable<string> errors,
        string calculatorVersion = "0.0.0",
        IEnumerable<EngineeringWarning>? warnings = null)
        => new(
            equipmentType,
            Enumerable.Empty<CalculatedValue>(),
            warnings ?? Enumerable.Empty<EngineeringWarning>(),
            errors ?? throw new ArgumentNullException(nameof(errors)),
            calculatorVersion,
            DateTimeOffset.UtcNow);

    /// <summary>Starts building a result.</summary>
    public static Builder Create(EquipmentType equipmentType, string calculatorVersion)
        => new(equipmentType, calculatorVersion);

    /// <summary>Mutable builder used by calculators.</summary>
    public sealed class Builder
    {
        private readonly EquipmentType _equipmentType;
        private readonly string _calculatorVersion;
        private readonly List<CalculatedValue> _values = new();
        private readonly List<EngineeringWarning> _warnings = new();
        private readonly List<string> _errors = new();

        internal Builder(EquipmentType equipmentType, string calculatorVersion)
        {
            if (string.IsNullOrWhiteSpace(calculatorVersion))
                throw new ArgumentException("Calculator version is required.", nameof(calculatorVersion));

            _equipmentType = equipmentType;
            _calculatorVersion = calculatorVersion;
        }

        public Builder AddValue(CalculatedValue value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrWhiteSpace(value.Key)) throw new ArgumentException("Value key is required.", nameof(value));

            if (!value.IsFinite)
            {
                _errors.Add($"Calculated value '{value.Key}' is not a finite number.");
                return this;
            }

            if (_values.Any(v => string.Equals(v.Key, value.Key, StringComparison.OrdinalIgnoreCase)))
            {
                _errors.Add($"Calculated value '{value.Key}' was produced more than once.");
                return this;
            }

            _values.Add(value);
            return this;
        }

        public Builder AddValue(
            string key,
            string displayName,
            double value,
            string siUnit,
            string formulaId,
            string sourceReference,
            string expression = "")
            => AddValue(new CalculatedValue(key, displayName, value, siUnit, formulaId, sourceReference, expression));

        public Builder AddWarning(EngineeringWarning warning)
        {
            if (warning is null) throw new ArgumentNullException(nameof(warning));
            _warnings.Add(warning);
            return this;
        }

        public Builder AddWarning(
            string code,
            string message,
            WarningSeverity severity,
            string? relatedKey = null,
            string? formulaId = null)
            => AddWarning(new EngineeringWarning(code, message, severity, relatedKey, formulaId));

        public Builder AddError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("Error text is required.", nameof(error));
            _errors.Add(error);
            return this;
        }

        public CalculationResult Build()
            => new(_equipmentType, _values, _warnings, _errors, _calculatorVersion, DateTimeOffset.UtcNow);
    }
}
