using System;
using System.Collections.Generic;
using System.Linq;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Default <see cref="IEquipmentCalculatorRegistry"/>. Built from all calculators registered in dependency injection.
/// Calculators can be found by equipment type or by the calculator id an equipment manifest declares.
/// A missing calculator is never substituted with a generic fallback.
/// </summary>
public sealed class EquipmentCalculatorRegistry : IEquipmentCalculatorRegistry
{
    private readonly IReadOnlyDictionary<EquipmentType, IEquipmentCalculator> _byType;
    private readonly IReadOnlyDictionary<string, IEquipmentCalculator> _byId;
    private readonly IReadOnlyCollection<IEquipmentCalculator> _all;
    private readonly IReadOnlyCollection<string> _ids;

    public EquipmentCalculatorRegistry(IEnumerable<IEquipmentCalculator> calculators)
    {
        if (calculators is null) throw new ArgumentNullException(nameof(calculators));

        var byType = new Dictionary<EquipmentType, IEquipmentCalculator>();
        var byId = new Dictionary<string, IEquipmentCalculator>(StringComparer.OrdinalIgnoreCase);

        foreach (var calculator in calculators)
        {
            if (calculator is null)
                throw new ArgumentException("Calculator collection contains a null entry.", nameof(calculators));

            if (calculator.EquipmentType == EquipmentType.Unknown)
                throw new ArgumentException(
                    $"Calculator '{calculator.GetType().Name}' declares EquipmentType.Unknown and cannot be registered.",
                    nameof(calculators));

            var id = calculator.CalculatorId;

            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    $"Calculator '{calculator.GetType().Name}' has an empty CalculatorId and cannot be registered.",
                    nameof(calculators));

            if (!byType.TryAdd(calculator.EquipmentType, calculator))
                throw new ArgumentException(
                    $"More than one calculator is registered for {calculator.EquipmentType}: " +
                    $"'{byType[calculator.EquipmentType].GetType().Name}' and '{calculator.GetType().Name}'.",
                    nameof(calculators));

            if (!byId.TryAdd(id.Trim(), calculator))
                throw new ArgumentException(
                    $"More than one calculator uses the id '{id.Trim()}': " +
                    $"'{byId[id.Trim()].GetType().Name}' and '{calculator.GetType().Name}'.",
                    nameof(calculators));
        }

        _byType = byType;
        _byId = byId;
        _all = byType.Values.ToList().AsReadOnly();
        _ids = byId.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public IReadOnlyCollection<IEquipmentCalculator> All => _all;

    public IReadOnlyCollection<string> Ids => _ids;

    public bool TryGet(EquipmentType type, out IEquipmentCalculator calculator)
    {
        if (_byType.TryGetValue(type, out var found))
        {
            calculator = found;
            return true;
        }

        calculator = null!;
        return false;
    }

    public IEquipmentCalculator Get(EquipmentType type)
        => TryGet(type, out var calculator)
            ? calculator
            : throw new NotSupportedException(
                $"No calculator is registered for {type}. Ask the user to clarify the equipment or use the equipment schema; do not substitute a generic pipe.");

    public bool TryGetById(string? calculatorId, out IEquipmentCalculator calculator)
    {
        if (!string.IsNullOrWhiteSpace(calculatorId) && _byId.TryGetValue(calculatorId.Trim(), out var found))
        {
            calculator = found;
            return true;
        }

        calculator = null!;
        return false;
    }

    public IEquipmentCalculator GetById(string? calculatorId)
        => TryGetById(calculatorId, out var calculator)
            ? calculator
            : throw new NotSupportedException(
                $"No calculator is registered with id '{calculatorId}'. Ask the user to clarify the equipment; do not substitute a generic pipe.");
}