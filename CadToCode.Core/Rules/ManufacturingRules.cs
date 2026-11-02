using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadToCode.Core.Rules;

/// <summary>
/// Typed view of manufacturing-rules.json. Values are either "from sample drawing X" or
/// "assumed"; the Sources dictionaries record which.
/// </summary>
public sealed class ManufacturingRules
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("units")]
    public string Units { get; init; } = "mm";

    [JsonPropertyName("weight")]
    public WeightRules Weight { get; init; } = new();

    [JsonPropertyName("sheet")]
    public SheetRules Sheet { get; init; } = new();

    [JsonPropertyName("tolerance")]
    public ToleranceRules Tolerance { get; init; } = new();

    [JsonPropertyName("casingThicknessBySizeClass")]
    public CasingClassRules CasingThicknessBySizeClass { get; init; } = new();

    [JsonPropertyName("referenceSets")]
    public Dictionary<string, ReferenceSet> ReferenceSets { get; init; } = new();

    [JsonPropertyName("unverified")]
    public List<string> Unverified { get; init; } = new();

    public static ManufacturingRules FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The manufacturing rules JSON is empty.");
        }

        return JsonSerializer.Deserialize<ManufacturingRules>(json, Options)
            ?? throw new InvalidOperationException("The manufacturing rules JSON could not be read.");
    }

    public static ManufacturingRules FromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The manufacturing rules file was not found.", path);
        }

        return FromJson(File.ReadAllText(path));
    }

    /// <summary>Weight in kg of a flat plate: area (mm2) x thickness (mm) x density.</summary>
    public double PlateWeightKg(double areaMm2, double thicknessMm)
    {
        return VolumeToKg(areaMm2 * thicknessMm);
    }

    /// <summary>Weight in kg of a solid disc or round bar: diameter and thickness (or length) in mm.</summary>
    public double DiscWeightKg(double diameterMm, double thicknessMm)
    {
        var area = Math.PI / 4.0 * diameterMm * diameterMm;
        return VolumeToKg(area * thicknessMm);
    }

    /// <summary>Weight in kg of a ring: outer and inner diameter and thickness in mm.</summary>
    public double RingWeightKg(double outerDiameterMm, double innerDiameterMm, double thicknessMm)
    {
        var area = Math.PI / 4.0 * (outerDiameterMm * outerDiameterMm - innerDiameterMm * innerDiameterMm);
        return VolumeToKg(area * thicknessMm);
    }

    /// <summary>True when the calculated weight is within the configured percentage of the reference weight.</summary>
    public bool IsWithinReference(double calculatedKg, double referenceKg)
    {
        if (referenceKg <= 0.0)
        {
            return false;
        }

        var difference = Math.Abs(calculatedKg - referenceKg) / referenceKg * 100.0;
        return difference <= Weight.CompareToReferenceTolerancePercent;
    }

    private double VolumeToKg(double volumeMm3)
    {
        return volumeMm3 * Weight.DensityKgPerM3 / 1e9;
    }
}

public sealed class WeightRules
{
    [JsonPropertyName("formula")]
    public string Formula { get; init; } = string.Empty;

    [JsonPropertyName("densityKgPerM3")]
    public double DensityKgPerM3 { get; init; } = 7850.0;

    [JsonPropertyName("label")]
    public string Label { get; init; } = "calculated";

    [JsonPropertyName("compareToReferenceTolerancePercent")]
    public double CompareToReferenceTolerancePercent { get; init; } = 10.0;

    [JsonPropertyName("sources")]
    public Dictionary<string, string> Sources { get; init; } = new();
}

public sealed class SheetRules
{
    [JsonPropertyName("size")]
    public string Size { get; init; } = "A3";

    [JsonPropertyName("borderZones")]
    public BorderZones BorderZones { get; init; } = new();

    [JsonPropertyName("company")]
    public string Company { get; init; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("titleBlockFields")]
    public List<string> TitleBlockFields { get; init; } = new();

    [JsonPropertyName("defaultValues")]
    public Dictionary<string, string> DefaultValues { get; init; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; init; } = new();

    [JsonPropertyName("alternateNotes")]
    public Dictionary<string, string> AlternateNotes { get; init; } = new();

    [JsonPropertyName("drawingNumberPattern")]
    public string DrawingNumberPattern { get; init; } = string.Empty;

    [JsonPropertyName("drawingNumberExample")]
    public string DrawingNumberExample { get; init; } = string.Empty;

    [JsonPropertyName("bomColumns")]
    public List<string> BomColumns { get; init; } = new();

    [JsonPropertyName("partCalloutFormat")]
    public string PartCalloutFormat { get; init; } = string.Empty;

    [JsonPropertyName("holeNoteFormat")]
    public string HoleNoteFormat { get; init; } = string.Empty;

    [JsonPropertyName("drillAndTapNoteFormat")]
    public string DrillAndTapNoteFormat { get; init; } = string.Empty;

    [JsonPropertyName("finishSymbols")]
    public List<string> FinishSymbols { get; init; } = new();

    [JsonPropertyName("weldSymbols")]
    public List<string> WeldSymbols { get; init; } = new();

    [JsonPropertyName("views")]
    public List<string> Views { get; init; } = new();

    [JsonPropertyName("sources")]
    public Dictionary<string, string> Sources { get; init; } = new();
}

public sealed class BorderZones
{
    [JsonPropertyName("columns")]
    public int Columns { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("columnLabels")]
    public List<string> ColumnLabels { get; init; } = new();

    [JsonPropertyName("rowLabels")]
    public List<string> RowLabels { get; init; } = new();
}

public sealed class ToleranceRules
{
    [JsonPropertyName("standard")]
    public string Standard { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("rangesAboveUpTo")]
    public List<List<double>> RangesAboveUpTo { get; init; } = new();

    [JsonPropertyName("classes")]
    public Dictionary<string, List<double?>> Classes { get; init; } = new();

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed class CasingClassRules
{
    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    [JsonPropertyName("classes")]
    public List<CasingClass> Classes { get; init; } = new();
}

public sealed class CasingClass
{
    [JsonPropertyName("class")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("casingThicknessMm")]
    public double CasingThicknessMm { get; init; }

    [JsonPropertyName("fanHp")]
    public double? FanHp { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed class ReferenceSet
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("material")]
    public string? Material { get; init; }

    [JsonPropertyName("totalImpellerWeightKg")]
    public double? TotalImpellerWeightKg { get; init; }

    [JsonPropertyName("totalImpellerInertiaKgM2")]
    public double? TotalImpellerInertiaKgM2 { get; init; }

    [JsonPropertyName("outsideDiameterMm")]
    public double? OutsideDiameterMm { get; init; }

    [JsonPropertyName("parts")]
    public List<ReferencePart> Parts { get; init; } = new();

    [JsonPropertyName("dimensionsSeen")]
    public List<string> DimensionsSeen { get; init; } = new();

    [JsonPropertyName("holes")]
    public List<string> Holes { get; init; } = new();
}

public sealed class ReferencePart
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("material")]
    public string? Material { get; init; }

    [JsonPropertyName("thicknessMm")]
    public double? ThicknessMm { get; init; }

    [JsonPropertyName("barDiameterMm")]
    public double? BarDiameterMm { get; init; }

    [JsonPropertyName("sizeText")]
    public string? SizeText { get; init; }

    [JsonPropertyName("qty")]
    public int? Qty { get; init; }

    [JsonPropertyName("qtyText")]
    public string? QtyText { get; init; }

    [JsonPropertyName("referenceWeightKg")]
    public double? ReferenceWeightKg { get; init; }

    [JsonPropertyName("rmSize")]
    public string? RmSize { get; init; }

    [JsonPropertyName("rmWeightKg")]
    public double? RmWeightKg { get; init; }

    [JsonPropertyName("finishWeightKg")]
    public double? FinishWeightKg { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}