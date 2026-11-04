using System.Globalization;
using CadToCode.Core.Rules;
using IxMilia.Dxf;

namespace CadToCode.Business.Services;

/// <summary>One finished manufacturing sheet.</summary>
public sealed record GeneratedPartSheet(int ItemNo, string Name, DxfFile Dxf);

/// <summary>
/// Builds a part sheet from BOM parameters only. No line is traced from a General Arrangement drawing.
/// Every missing parameter, assumed value and builder rejection is written to the <see cref="GenerationReport"/>.
/// </summary>
public sealed class ParametricPartRulesEngine
{
    private const string DefaultMaterialName = "MS";

    private readonly FlangePartDrawingBuilder _flange = new();
    private readonly RectangularPlatePartDrawingBuilder _plate = new();
    private readonly RoundBarPartDrawingBuilder _bar = new();
    private readonly RolledRingPartDrawingBuilder _ring = new();
    private readonly HubPartDrawingBuilder _hub = new();
    private readonly ConeFlatPatternDrawingBuilder _cone = new();
    private readonly ScrollFlatPatternDrawingBuilder _scroll = new();
    private readonly TransitionFlatPatternDrawingBuilder _transition = new();
    private readonly ProfilePartDrawingBuilder _profile = new();

    public GeneratedPartSheet? Generate(
        ManufacturingRules rules,
        PartBomItem item,
        SheetFrameRequest frame,
        string? defaultMaterial,
        double? defaultThicknessMm,
        GenerationReport report)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(report);

        var name = item.DisplayName;
        var material = ResolveMaterial(item, defaultMaterial, report, name);
        var thickness = item.ThicknessMm;

        var grade = material.Trim().ToUpperInvariant();
        if (grade != "MS" && !grade.StartsWith("IS 2062", StringComparison.Ordinal) && !grade.StartsWith("E250", StringComparison.Ordinal))
        {
            report.Warn(
                "WEIGHT_DENSITY_ASSUMED",
                name,
                "RMWT and FINISH WT use the rules density of " + rules.Weight.DensityKgPerM3.ToString("0.##", CultureInfo.InvariantCulture) + " kg/m3. Material '" + material + "' may differ.");
        }

        if (thickness is not > 0.0 && NeedsThickness(item.Kind) && defaultThicknessMm is > 0.0)
        {
            report.Warn("THICKNESS_DEFAULT_NOT_USED", name, "Thickness is not in the BOM or drawing text. The default " + Format(defaultThicknessMm.Value) + " mm was not applied.");
        }

        try
        {
            var dxf = item.Kind switch
            {
                BomPartKind.Flange => BuildFlange(rules, item, frame, material, thickness, report, name),
                BomPartKind.RectangularPlate => BuildPlate(rules, item, frame, material, thickness, report, name),
                BomPartKind.RoundBar => BuildBar(rules, item, frame, material, report, name),
                BomPartKind.RolledRing => BuildRing(rules, item, frame, material, thickness, report, name),
                BomPartKind.Hub => BuildHub(rules, item, frame, material, thickness, report, name),
                BomPartKind.ConeFlatPattern => BuildCone(rules, item, frame, material, thickness, report, name),
                BomPartKind.ScrollFlatPattern => BuildScroll(rules, item, frame, material, thickness, report, name),
                BomPartKind.TransitionFlatPattern => BuildTransition(rules, item, frame, material, thickness, report, name),
                BomPartKind.ProfilePlate => BuildProfile(rules, item, frame, material, thickness, report, name),
                _ => null
            };

            if (dxf is null)
            {

                if (item.Kind == BomPartKind.Unknown)
                {
                    report.Error("KIND_UNSUPPORTED", name, "The part type is not supported.");
                }

                return null;
            }

            report.MarkGenerated();
            return new GeneratedPartSheet(item.ItemNo, name, dxf);
        }
        catch (ArgumentException ex)
        {
            report.Error("BUILDER_REJECTED", name, ex.Message);
            return null;
        }
    }

    private DxfFile? BuildFlange(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        if (!Require(report, name, ("outer diameter", item.OuterDiameterMm), ("thickness", thickness)))
        {
            return null;
        }

        var holeCount = item.HoleCount ?? 0;
        CheckHoles(report, name, holeCount, item.HoleDiameterMm, item.HolePcdMm);

        return _flange.Build(rules, new FlangePartRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            OuterDiameterMm = item.OuterDiameterMm!.Value,
            InnerDiameterMm = item.InnerDiameterMm ?? 0.0,
            ThicknessMm = thickness!.Value,
            HoleCount = holeCount,
            HoleDiameterMm = item.HoleDiameterMm ?? 0.0,
            HolePcdMm = item.HolePcdMm ?? 0.0,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildPlate(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        if (!Require(report, name, ("length", item.LengthMm), ("width", item.WidthMm), ("thickness", thickness)))
        {
            return null;
        }

        if ((item.HoleCount ?? 0) > 0)
        {
            report.Warn("PLATE_HOLES_IGNORED", name, "The BOM lists holes but gives no hole positions for a rectangular plate. The sheet was made without holes.");
        }

        return _plate.Build(rules, new RectangularPlateRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            LengthMm = item.LengthMm!.Value,
            WidthMm = item.WidthMm!.Value,
            ThicknessMm = thickness!.Value,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildBar(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, GenerationReport report, string name)
    {
        if (!Require(report, name, ("diameter", item.OuterDiameterMm), ("length", item.LengthMm)))
        {
            return null;
        }

        return _bar.Build(rules, new RoundBarRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            DiameterMm = item.OuterDiameterMm!.Value,
            LengthMm = item.LengthMm!.Value,
            ChamferMm = item.ChamferMm ?? 0.0,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildRing(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        var inner = item.InnerDiameterMm;
        if (inner is not > 0.0 && item.OuterDiameterMm is > 0.0 && thickness is > 0.0)
        {
            inner = item.OuterDiameterMm.Value - 2.0 * thickness.Value;
            report.Warn("RING_ID_DERIVED", name, "Inside diameter is not in the BOM. " + Format(inner.Value) + " mm was derived from outer diameter and thickness.");
        }

        if (!Require(report, name, ("inside diameter", inner), ("bar width", item.WidthMm), ("bar thickness", thickness)))
        {
            return null;
        }

        return _ring.Build(rules, new RolledRingPartRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            InnerDiameterMm = inner!.Value,
            BarWidthMm = item.WidthMm!.Value,
            BarThicknessMm = thickness!.Value,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildHub(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        if (!Require(report, name, ("plate diameter", item.OuterDiameterMm), ("plate thickness", thickness), ("boss diameter", item.SmallDiameterMm), ("boss length", item.LengthMm)))
        {
            return null;
        }

        var holeCount = item.HoleCount ?? 0;
        CheckHoles(report, name, holeCount, item.HoleDiameterMm, item.HolePcdMm);

        return _hub.Build(rules, new HubPartRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            PlateDiameterMm = item.OuterDiameterMm!.Value,
            PlateThicknessMm = thickness!.Value,
            BossDiameterMm = item.SmallDiameterMm!.Value,
            BossLengthMm = item.LengthMm!.Value,
            BoreDiameterMm = item.InnerDiameterMm ?? 0.0,
            HoleCount = holeCount,
            HoleDiameterMm = item.HoleDiameterMm ?? 0.0,
            HolePcdMm = item.HolePcdMm ?? 0.0,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildCone(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        var bottom = item.LargeDiameterMm ?? item.OuterDiameterMm;
        var height = item.HeightMm ?? item.LengthMm;

        if (!Require(report, name, ("large diameter", bottom), ("height", height), ("thickness", thickness)))
        {
            return null;
        }

        if (item.SmallDiameterMm is null)
        {
            report.Warn("CONE_TOP_ASSUMED", name, "Small diameter is not in the BOM. 0 mm (a full cone) was used.");
        }

        return _cone.Build(rules, new ConeFlatPatternRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            BottomDiameterMm = bottom!.Value,
            TopDiameterMm = item.SmallDiameterMm ?? 0.0,
            HeightMm = height!.Value,
            ThicknessMm = thickness!.Value,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildScroll(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        if (!Require(report, name, ("small diameter (start)", item.SmallDiameterMm), ("large diameter (end)", item.LargeDiameterMm), ("width", item.WidthMm), ("thickness", thickness)))
        {
            return null;
        }

        report.Info("SCROLL_SWEEP_ASSUMED", name, "Sweep angle is not part of the BOM. 360 degrees was used.");

        return _scroll.Build(rules, new ScrollFlatPatternRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            StartRadiusMm = item.SmallDiameterMm!.Value / 2.0,
            EndRadiusMm = item.LargeDiameterMm!.Value / 2.0,
            SweepDegrees = 360.0,
            WidthMm = item.WidthMm!.Value,
            ThicknessMm = thickness!.Value,
            Frame = frame
        }).Dxf;
    }

    private DxfFile? BuildTransition(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        var height = item.HeightMm;

        if (!Require(report, name, ("rectangle length", item.LengthMm), ("rectangle width", item.WidthMm), ("round diameter", item.OuterDiameterMm), ("height", height), ("thickness", thickness)))
        {
            return null;
        }

        return _transition.Build(rules, new TransitionFlatPatternRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            RectLengthMm = item.LengthMm!.Value,
            RectWidthMm = item.WidthMm!.Value,
            RoundDiameterMm = item.OuterDiameterMm!.Value,
            HeightMm = height!.Value,
            ThicknessMm = thickness!.Value,
            Frame = frame
        }).Dxf;
    }
    private DxfFile? BuildProfile(ManufacturingRules rules, PartBomItem item, SheetFrameRequest frame, string material, double? thickness, GenerationReport report, string name)
    {
        if (item.Profile is not { Count: > 0 })
        {
            report.Error("PROFILE_MISSING", name, "The part has no outline geometry.");
            return null;
        }

        if (!Require(report, name, ("thickness", thickness)))
        {
            return null;
        }

        if (!item.FinishWeightKg.HasValue)
        {
            report.Info("PROFILE_WEIGHT_NOT_CALCULATED", name, "The finish weight of a shaped outline is not calculated. Only the raw material weight is shown.");
        }

        return _profile.Build(rules, new ProfilePlateRequest
        {
            Name = name,
            Material = material,
            ItemNo = item.ItemNo,
            Qty = item.Qty,
            ThicknessMm = thickness!.Value,
            Profile = item.Profile,
            FinishWeightKg = item.FinishWeightKg,
            Frame = frame
        }).Dxf;
    }
    private static string ResolveMaterial(PartBomItem item, string? defaultMaterial, GenerationReport report, string name)
    {
        if (!string.IsNullOrWhiteSpace(item.Material))
        {
            return item.Material.Trim();
        }

        var material = string.IsNullOrWhiteSpace(defaultMaterial) ? DefaultMaterialName : defaultMaterial.Trim();
        report.Warn("MATERIAL_DEFAULT", name, "Material is not in the BOM. '" + material + "' was used.");
        return material;
    }

    private static bool NeedsThickness(BomPartKind kind)
    {
        return kind is BomPartKind.Flange or BomPartKind.RectangularPlate or BomPartKind.RolledRing
                        or BomPartKind.Hub or BomPartKind.ConeFlatPattern or BomPartKind.ScrollFlatPattern or BomPartKind.TransitionFlatPattern
            or BomPartKind.ProfilePlate;
    }

    private static void CheckHoles(GenerationReport report, string name, int holeCount, double? holeDiameter, double? holePcd)
    {
        if (holeCount > 0 && (holeDiameter is not > 0.0 || holePcd is not > 0.0))
        {
            report.Warn("HOLE_DATA_INCOMPLETE", name, holeCount.ToString(CultureInfo.InvariantCulture) + " holes are listed but hole diameter or PCD is missing. The hole pattern may be left out.");
        }

        if (holeCount <= 0 && holePcd is > 0.0)
        {
            var missing = holeDiameter is > 0.0 ? "hole count" : "hole count and hole diameter";
            report.Warn("HOLE_COUNT_MISSING", name, "A bolt circle (PCD " + Format(holePcd.Value) + " mm) is present but " + missing + " are not in the BOM. The sheet was made without holes.");
        }
    }

    private static bool Require(GenerationReport report, string name, params (string Label, double? Value)[] values)
    {
        var missing = values.Where(v => v.Value is not > 0.0).Select(v => v.Label).ToList();
        if (missing.Count == 0)
        {
            return true;
        }

        report.Error("PARAMETER_MISSING", name, "Missing or zero BOM parameter: " + string.Join(", ", missing) + ".");
        return false;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}