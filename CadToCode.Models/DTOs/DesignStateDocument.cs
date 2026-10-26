using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadToCode.Models.Dtos;

/// <summary>
/// Backend-only, versioned snapshot of a complete engineering design state.
/// Stored in Projects.DesignStateJson and mirrored in the Qdrant point payload.
/// Never returned to the client in raw form.
/// All numeric values are SI.
/// </summary>
public sealed class DesignStateDocument
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict
    };

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("projectId")]
    public int ProjectId { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedUtc")]
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("revision")]
    public int Revision { get; set; } = 1;

    [JsonPropertyName("source")]
    public DesignStateSource Source { get; set; } = new();

    [JsonPropertyName("classification")]
    public DesignStateClassification Classification { get; set; } = new();

    /// <summary>User and extracted inputs, keyed by parameter key. SI units.</summary>
    [JsonPropertyName("inputs")]
    public Dictionary<string, double> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calculated dimensions and metrics from the rulepack calculators.</summary>
    [JsonPropertyName("calculation")]
    public DesignStateCalculation Calculation { get; set; } = new();

    [JsonPropertyName("components")]
    public List<DesignStateComponent> Components { get; set; } = new();

    /// <summary>Part counts keyed by component or part name.</summary>
    [JsonPropertyName("partCounts")]
    public Dictionary<string, int> PartCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("workflowState")]
    public string? WorkflowState { get; set; }

    /// <summary>Optional generated script hash, so a stale script can be detected after a parametric edit.</summary>
    [JsonPropertyName("scriptHash")]
    public string? ScriptHash { get; set; }

    /// <summary>True when Inputs changed after the last calculation and a recalculation is required.</summary>
    [JsonPropertyName("isStale")]
    public bool IsStale { get; set; }

    /// <summary>Parametric edit history, oldest first.</summary>
    [JsonPropertyName("edits")]
    public List<DesignStateEdit> Edits { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Serializes the design into a semantic summary string used as the text that is embedded
    /// and stored as a searchable vector. Read-only: does not modify the design state.
    /// Example: "Equipment: Centrifugal Fan. Subtype: Backward Curved Airfoil. Duty: Airflow 5 m3/s, ...
    /// Geometry: Impeller OD 0.6033m, ... Performance: Tip Speed 45.8m/s, Shaft Power 8.33kW."
    /// </summary>
    public string ToEmbeddingText()
    {
        var sb = new StringBuilder();

        var equipment = Classification?.EquipmentType;
        if (!string.IsNullOrWhiteSpace(equipment) && !string.Equals(equipment, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("Equipment: ").Append(Humanize(equipment)).Append(". ");
        }

        var subtype = Classification?.TemplateTitle;
        if (!string.IsNullOrWhiteSpace(subtype))
        {
            sb.Append("Subtype: ").Append(subtype.Trim()).Append(". ");
        }

        var values = Calculation?.Values ?? new List<DesignStateValue>();
        var valueByKey = new Dictionary<string, DesignStateValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v.Key))
            {
                valueByKey[v.Key] = v;
            }
        }

        var duty = new List<string>();
        foreach (var input in Inputs ?? new Dictionary<string, double>())
        {
            valueByKey.TryGetValue(input.Key, out var match);
            var label = match is not null && !string.IsNullOrWhiteSpace(match.DisplayName)
                ? match.DisplayName.Trim()
                : Humanize(input.Key);
            duty.Add(FormatQuantity(label, input.Value, match?.SiUnit, spaceBeforeUnit: true));
        }

        if (duty.Count > 0)
        {
            sb.Append("Duty: ").Append(string.Join(", ", duty)).Append(". ");
        }

        var geometry = new List<string>();
        var performance = new List<string>();

        foreach (var v in values)
        {
            if (Inputs is not null && Inputs.ContainsKey(v.Key))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(v.DisplayName) ? Humanize(v.Key) : v.DisplayName.Trim();
            var unit = (v.SiUnit ?? string.Empty).Trim();

            if (IsLengthUnit(unit))
            {
                geometry.Add(FormatQuantity(label, v.Value, unit, spaceBeforeUnit: false));
            }
            else
            {
                performance.Add(FormatQuantity(label, v.Value, unit, spaceBeforeUnit: false));
            }
        }

        if (PartCounts is not null)
        {
            foreach (var part in PartCounts)
            {
                geometry.Add($"{Humanize(part.Key)} {part.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (geometry.Count > 0)
        {
            sb.Append("Geometry: ").Append(string.Join(", ", geometry)).Append(". ");
        }

        if (performance.Count > 0)
        {
            sb.Append("Performance: ").Append(string.Join(", ", performance)).Append(". ");
        }

        return sb.ToString().Trim();
    }

    private static bool IsLengthUnit(string unit) =>
        string.Equals(unit, "m", StringComparison.OrdinalIgnoreCase)
        || string.Equals(unit, "mm", StringComparison.OrdinalIgnoreCase);

    private static string FormatQuantity(string label, double value, string? unit, bool spaceBeforeUnit)
    {
        var u = (unit ?? string.Empty).Trim();

        if (string.Equals(u, "W", StringComparison.Ordinal) && Math.Abs(value) >= 1000d)
        {
            value /= 1000d;
            u = "kW";
        }

        var number = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        if (u.Length == 0)
        {
            return $"{label} {number}";
        }

        return spaceBeforeUnit ? $"{label} {number} {u}" : $"{label} {number}{u}";
    }

    private static string Humanize(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '_' || c == '-')
            {
                sb.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && (char.IsLower(key[i - 1]) || char.IsDigit(key[i - 1])))
            {
                sb.Append(' ');
            }

            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }

        return sb.ToString().Trim();
    }

    public static DesignStateDocument? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<DesignStateDocument>(json, JsonOptions);
            return document is { SchemaVersion: > 0 and <= CurrentSchemaVersion } ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class DesignStateSource
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("sourceFileType")]
    public string SourceFileType { get; set; } = string.Empty;

    /// <summary>SHA-256 of the source file. Restore is only valid while this matches.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "Millimeter";

    [JsonPropertyName("scaleMmPerPixel")]
    public decimal? ScaleMmPerPixel { get; set; }

    /// <summary>Which pipeline produced the state: Dxf, Vision, Ocr, Template, Manual.</summary>
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = string.Empty;
}

public sealed class DesignStateClassification
{
    /// <summary>Equipment code, for example AxialFan, CentrifugalFan, Cyclone, WetScrubber.</summary>
    [JsonPropertyName("equipmentType")]
    public string EquipmentType { get; set; } = "Unknown";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("templateTitle")]
    public string? TemplateTitle { get; set; }

    [JsonPropertyName("templateScore")]
    public double? TemplateScore { get; set; }

    [JsonPropertyName("rulepackId")]
    public string? RulepackId { get; set; }

    [JsonPropertyName("calculatorId")]
    public string? CalculatorId { get; set; }
}

public sealed class DesignStateCalculation
{
    [JsonPropertyName("calculatorVersion")]
    public string CalculatorVersion { get; set; } = string.Empty;

    [JsonPropertyName("knowledgeVersion")]
    public string KnowledgeVersion { get; set; } = string.Empty;

    [JsonPropertyName("calculatedUtc")]
    public DateTimeOffset? CalculatedUtc { get; set; }

    [JsonPropertyName("isCadReady")]
    public bool IsCadReady { get; set; }

    [JsonPropertyName("values")]
    public List<DesignStateValue> Values { get; set; } = new();

    [JsonPropertyName("formulaIdsUsed")]
    public List<string> FormulaIdsUsed { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<DesignStateWarning> Warnings { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
}

public sealed class DesignStateValue
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("siUnit")]
    public string SiUnit { get; set; } = string.Empty;

    [JsonPropertyName("formulaId")]
    public string? FormulaId { get; set; }
}

public sealed class DesignStateWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Info, Caution or Critical.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Info";

    [JsonPropertyName("relatedKey")]
    public string? RelatedKey { get; set; }
}

public sealed class DesignStateComponent
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("sequenceIndex")]
    public int SequenceIndex { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("componentType")]
    public string ComponentType { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("parentName")]
    public string? ParentName { get; set; }

    /// <summary>Dimensions keyed by parameter key, SI units.</summary>
    [JsonPropertyName("dimensions")]
    public Dictionary<string, double> Dimensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Calculated or input keys this component's dimensions depend on, for targeted recalculation.</summary>
    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = new();
}

public sealed class DesignStateEdit
{
    [JsonPropertyName("revision")]
    public int Revision { get; set; }

    [JsonPropertyName("editedUtc")]
    public DateTimeOffset EditedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("oldValue")]
    public double? OldValue { get; set; }

    [JsonPropertyName("newValue")]
    public double NewValue { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}