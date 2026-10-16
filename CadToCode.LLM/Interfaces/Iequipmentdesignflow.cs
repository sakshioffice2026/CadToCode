using CadToCode.LLM.Templates;

namespace CadToCode.LLM.Interfaces;

/// <summary>
/// One chat turn for equipment that is designed from a calculator (for example airflow, pressure and speed)
/// instead of from a dimension template. Values are always in SI units and keyed by the calculator and mapper keys.
/// </summary>
public sealed class EquipmentDesignRequest
{
    public string ManifestId { get; init; } = string.Empty;

    /// <summary>The user's message for this turn.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Values collected in earlier turns. The flow returns the merged set.</summary>
    public IReadOnlyDictionary<string, double> Values { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the user asked to generate the CAD scripts now.</summary>
    public bool GenerateRequested { get; init; }

    /// <summary>Part keys or titles to build. Empty means every part the manifest declares.</summary>
    public IReadOnlyList<string> PartKeys { get; init; } = Array.Empty<string>();
}

public sealed class EquipmentDesignTurn
{
    public const string StatusNeedInputs = "design-need-inputs";
    public const string StatusCalculated = "design-calculated";
    public const string StatusGenerated = "design-generated";
    public const string StatusBlocked = "design-blocked";

    /// <summary>Merged values after this turn.</summary>
    public IReadOnlyDictionary<string, double> Values { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public string Reply { get; init; } = string.Empty;

    public string Status { get; init; } = StatusNeedInputs;

    /// <summary>Input keys still needed before the next step can run.</summary>
    public IReadOnlyList<string> MissingKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>One script per manufacturable part. Set only when generation succeeded.</summary>
    public ComposedPartsResult? Parts { get; init; }

    public bool Generated => Parts is { Success: true };

    public bool Blocked => Status == StatusBlocked;
}

public interface IEquipmentDesignFlow
{
    /// <summary>True when this flow designs the equipment named by the manifest id.</summary>
    bool CanHandle(string? manifestId);

    /// <summary>
    /// Reads values from the message, asks for what is missing, runs the calculator and mapper,
    /// and builds one script per part once everything is confirmed. It never substitutes another equipment type.
    /// </summary>
    EquipmentDesignTurn Handle(EquipmentDesignRequest request);
}