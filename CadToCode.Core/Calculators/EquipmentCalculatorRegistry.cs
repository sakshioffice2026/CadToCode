using System;
using System.Collections.Generic;
using System.Linq;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Calculators;

/// <summary>
/// Default <see cref="IEquipmentCalculatorRegistry"/>. Built from all calculators registered in dependency injection.
/// A missing calculator is never substituted with a generic fallback.
/// </summary>
public sealed class EquipmentCalculatorRegistry : IEquipmentCalculatorRegistry
{
    private readonly IReadOnlyDictionary<EquipmentType, IEquipmentCalculator> _calculators;

    public EquipmentCalculatorRegistry(IEnumerable<IEquipmentCalculator> calculators)
    {
        if (calculators is null) throw new ArgumentNullException(nameof(calculators));

        var map = new Dictionary<EquipmentType, IEquipmentCalculator>();

        foreach (var calculator in calculators)
        {
            if (calculator is null)
                throw new ArgumentException("Calculator collection contains a null entry.", nameof(calculators));

            if (calculator.EquipmentType == EquipmentType.Unknown)
                throw new ArgumentException(
                    $"Calculator '{calculator.GetType().Name}' declares EquipmentType.Unknown and cannot be registered.",
                    nameof(calculators));

            if (!map.TryAdd(calculator.EquipmentType, calculator))
                throw new ArgumentException(
                    $"More than one calculator is registered for {calculator.EquipmentType}: " +
                    $"'{map[calculator.EquipmentType].GetType().Name}' and '{calculator.GetType().Name}'.",
                    nameof(calculators));
        }

        _calculators = map;
    }

    public IReadOnlyCollection<IEquipmentCalculator> All => _calculators.Values.ToList().AsReadOnly();

    public bool TryGet(EquipmentType type, out IEquipmentCalculator calculator)
    {
        if (_calculators.TryGetValue(type, out var found))
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
}
