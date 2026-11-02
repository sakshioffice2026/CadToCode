using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one hub part sheet (back plate with a boss and a through bore). All sizes are in mm.</summary>
public sealed class HubPartRequest
{
    public string Name { get; init; } = "HUB";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    /// <summary>Outer diameter of the back plate.</summary>
    public double PlateDiameterMm { get; init; }

    public double PlateThicknessMm { get; init; }

    public double BossDiameterMm { get; init; }

    /// <summary>Boss length measured from the plate face.</summary>
    public double BossLengthMm { get; init; }

    /// <summary>Through bore diameter. Zero means a solid hub.</summary>
    public double BoreDiameterMm { get; init; }

    public int HoleCount { get; init; }

    public double HoleDiameterMm { get; init; }

    public double HolePcdMm { get; init; }

    /// <summary>Added to the plate diameter and to the total length to get the raw bar size (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 5.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class HubPartResult
{
    public HubPartResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale)
    {
        Dxf = dxf;
        FinishWeightKg = finishWeightKg;
        RawWeightKg = rawWeightKg;
        Scale = scale;
    }

    public DxfFile Dxf { get; }

    public double FinishWeightKg { get; }

    public double RawWeightKg { get; }

    public string Scale { get; }
}

/// <summary>
/// Builds a complete A3 sheet for a hub: frame, front view with true circles, full section A-A
/// with hatching, dimensions, hole note, part callout and BOM row.
/// </summary>
public sealed class HubPartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string HiddenLayer = "HIDDEN";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";
    private const string HatchLayer = "HATCH";

    private const double SideMargin = 36.0;
    private const double BottomMargin = 56.0;
    private const double TopMargin = 36.0;
    private const double ViewGap = 70.0;
    private const double CalloutSpace = 90.0;
    private const double BomRowHeight = 8.0;
    private const double HatchSpacing = 3.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public HubPartResult Build(ManufacturingRules rules, HubPartRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, HiddenLayer, DimLayer, TextLayer, TableLayer, HatchLayer })
        {
            EnsureLayer(dxf, name);
        }

        var finishWeight = CalculateFinishWeight(rules, request);
        var rawDiameter = request.PlateDiameterMm + request.RawMaterialAllowanceMm;
        var rawLength = request.PlateThicknessMm + request.BossLengthMm + request.RawMaterialAllowanceMm;
        var rawWeight = rules.DiscWeightKg(rawDiameter, rawLength);

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var scaleDivisor = ChooseScale(request, probe);
        var scaleText = scaleDivisor <= 1.0 ? "1:1" : "1:" + Format(scaleDivisor);

        var finalFrame = new SheetFrameRequest
        {
            JobNo = frameRequest.JobNo,
            DrawingNo = frameRequest.DrawingNo,
            Revision = frameRequest.Revision,
            Title = frameRequest.Title,
            ModelNo = frameRequest.ModelNo,
            Scale = scaleText,
            Designed = frameRequest.Designed,
            Drawn = frameRequest.Drawn,
            Checked = frameRequest.Checked,
            Approved = frameRequest.Approved,
            Date = frameRequest.Date,
            IncludeToleranceTable = frameRequest.IncludeToleranceTable
        };

        var area = frameBuilder.Build(dxf, rules.Sheet, rules.Tolerance, finalFrame);

        var plateRadius = request.PlateDiameterMm / 2.0 / scaleDivisor;
        var cx = area.DrawingMinX + SideMargin + plateRadius;
        var cy = area.DrawingMinY + BottomMargin + plateRadius;

        DrawFrontView(dxf, request, scaleDivisor, cx, cy);

        var plateThickness = request.PlateThicknessMm / scaleDivisor;
        var bossLength = request.BossLengthMm / scaleDivisor;
        var sectionX0 = cx + plateRadius + ViewGap;
        var sectionX1 = sectionX0 + plateThickness;
        var sectionX2 = sectionX1 + bossLength;

        DrawSectionView(dxf, request, scaleDivisor, sectionX0, sectionX1, sectionX2, cy);
        DrawDimensions(dxf, request, scaleDivisor, cx, cy, sectionX0, sectionX1, sectionX2);

        Text(dxf, cx + 2.0, cy + plateRadius + 9.0, 4.0, "A", TextLayer);
        Text(dxf, sectionX0, cy - plateRadius - 30.0, 4.0, "SECTION A-A", TextLayer);

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0 && request.HolePcdMm > 0.0)
        {
            var note = FormatHoleNote(rules.Sheet, request);
            Text(dxf, cx - plateRadius, cy - plateRadius - 42.0, 3.5, note, TextLayer);
        }

        var calloutLines = FormatCallout(rules.Sheet, request, finishWeight);
        var calloutY = cy + plateRadius;
        var calloutX = sectionX2 + 50.0;
        foreach (var line in calloutLines)
        {
            Text(dxf, calloutX, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawDiameter, rawLength, rawWeight, finishWeight);

        return new HubPartResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static void Validate(HubPartRequest request)
    {
        if (request.PlateDiameterMm <= 0.0 || request.PlateThicknessMm <= 0.0)
        {
            throw new ArgumentException("Plate diameter and thickness must be greater than zero.");
        }

        if (request.BossDiameterMm <= 0.0 || request.BossLengthMm <= 0.0)
        {
            throw new ArgumentException("Boss diameter and length must be greater than zero.");
        }

        if (request.BossDiameterMm >= request.PlateDiameterMm)
        {
            throw new ArgumentException("Boss diameter must be smaller than the plate diameter.");
        }

        if (request.BoreDiameterMm < 0.0 || request.BoreDiameterMm >= request.BossDiameterMm)
        {
            throw new ArgumentException("Bore diameter must be zero or smaller than the boss diameter.");
        }

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0 && request.HolePcdMm > 0.0)
        {
            var inner = request.HolePcdMm / 2.0 - request.HoleDiameterMm / 2.0;
            var outer = request.HolePcdMm / 2.0 + request.HoleDiameterMm / 2.0;
            if (inner < request.BossDiameterMm / 2.0 || outer > request.PlateDiameterMm / 2.0)
            {
                throw new ArgumentException("Holes must lie between the boss and the plate edge.");
            }
        }
    }

    private static double CalculateFinishWeight(ManufacturingRules rules, HubPartRequest request)
    {
        var plate = request.BoreDiameterMm > 0.0
            ? rules.RingWeightKg(request.PlateDiameterMm, request.BoreDiameterMm, request.PlateThicknessMm)
            : rules.DiscWeightKg(request.PlateDiameterMm, request.PlateThicknessMm);

        var boss = request.BoreDiameterMm > 0.0
            ? rules.RingWeightKg(request.BossDiameterMm, request.BoreDiameterMm, request.BossLengthMm)
            : rules.DiscWeightKg(request.BossDiameterMm, request.BossLengthMm);

        var weight = plate + boss;

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0)
        {
            var holeArea = request.HoleCount * Math.PI / 4.0 * request.HoleDiameterMm * request.HoleDiameterMm;
            weight -= rules.PlateWeightKg(holeArea, request.PlateThicknessMm);
        }

        return Math.Max(weight, 0.0);
    }

    private static double ChooseScale(HubPartRequest request, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - BottomMargin - TopMargin - 2.0 * BomRowHeight - 10.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * SideMargin;
        var totalLength = request.PlateThicknessMm + request.BossLengthMm;

        foreach (var divisor in StandardScales)
        {
            var heightOk = request.PlateDiameterMm / divisor <= availableHeight;
            var width = (request.PlateDiameterMm + totalLength) / divisor + ViewGap + CalloutSpace;
            if (heightOk && width <= availableWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void DrawFrontView(DxfFile dxf, HubPartRequest request, double divisor, double cx, double cy)
    {
        var plateRadius = request.PlateDiameterMm / 2.0 / divisor;
        var bossRadius = request.BossDiameterMm / 2.0 / divisor;

        Circle(dxf, cx, cy, plateRadius, OutlineLayer);
        Circle(dxf, cx, cy, bossRadius, OutlineLayer);

        if (request.BoreDiameterMm > 0.0)
        {
            Circle(dxf, cx, cy, request.BoreDiameterMm / 2.0 / divisor, OutlineLayer);
        }

        const double extend = 6.0;
        Line(dxf, cx - plateRadius - extend, cy, cx + plateRadius + extend, cy, CenterLayer);
        Line(dxf, cx, cy - plateRadius - extend, cx, cy + plateRadius + extend, CenterLayer);

        if (request.HoleCount <= 0 || request.HoleDiameterMm <= 0.0 || request.HolePcdMm <= 0.0)
        {
            return;
        }

        var pcdRadius = request.HolePcdMm / 2.0 / divisor;
        var holeRadius = request.HoleDiameterMm / 2.0 / divisor;
        Circle(dxf, cx, cy, pcdRadius, CenterLayer);

        for (var i = 0; i < request.HoleCount; i++)
        {
            var angle = Math.PI / 2.0 + 2.0 * Math.PI * i / request.HoleCount;
            var hx = cx + pcdRadius * Math.Cos(angle);
            var hy = cy + pcdRadius * Math.Sin(angle);
            Circle(dxf, hx, hy, holeRadius, OutlineLayer);

            var mark = Math.Min(holeRadius * 1.4, holeRadius + 3.0);
            Line(dxf, hx - mark, hy, hx + mark, hy, CenterLayer);
            Line(dxf, hx, hy - mark, hx, hy + mark, CenterLayer);
        }
    }

    private static void DrawSectionView(DxfFile dxf, HubPartRequest request, double divisor, double x0, double x1, double x2, double cy)
    {
        var plateRadius = request.PlateDiameterMm / 2.0 / divisor;
        var bossRadius = request.BossDiameterMm / 2.0 / divisor;
        var boreRadius = request.BoreDiameterMm / 2.0 / divisor;

        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var yBore = cy + sign * boreRadius;
            var yPlate = cy + sign * plateRadius;
            var yBoss = cy + sign * bossRadius;

            Line(dxf, x0, yBore, x0, yPlate, OutlineLayer);
            Line(dxf, x0, yPlate, x1, yPlate, OutlineLayer);
            Line(dxf, x1, yPlate, x1, yBoss, OutlineLayer);
            Line(dxf, x1, yBoss, x2, yBoss, OutlineLayer);
            Line(dxf, x2, yBoss, x2, yBore, OutlineLayer);
            Line(dxf, x2, yBore, x0, yBore, OutlineLayer);

            HatchRectangle(dxf, x0, Math.Min(yBore, yPlate), x1, Math.Max(yBore, yPlate));
            HatchRectangle(dxf, x1, Math.Min(yBore, yBoss), x2, Math.Max(yBore, yBoss));
        }

        Line(dxf, x0 - 8.0, cy, x2 + 8.0, cy, CenterLayer);
    }

    private static void HatchRectangle(DxfFile dxf, double x0, double y0, double x1, double y1)
    {
        if (x1 - x0 < 0.5 || y1 - y0 < 0.5)
        {
            return;
        }

        var step = HatchSpacing * Math.Sqrt(2.0);
        for (var c = y0 - x1 + step; c < y1 - x0; c += step)
        {
            var startX = Math.Max(x0, y0 - c);
            var endX = Math.Min(x1, y1 - c);
            if (endX - startX > 0.01)
            {
                Line(dxf, startX, startX + c, endX, endX + c, HatchLayer);
            }
        }
    }

    private static void DrawDimensions(DxfFile dxf, HubPartRequest request, double divisor, double cx, double cy, double x0, double x1, double x2)
    {
        var plateRadius = request.PlateDiameterMm / 2.0 / divisor;
        var bossRadius = request.BossDiameterMm / 2.0 / divisor;
        var boreRadius = request.BoreDiameterMm / 2.0 / divisor;

        HorizontalDimension(dxf, cx - plateRadius, cx + plateRadius, cy - plateRadius - 12.0, cy - plateRadius, "%%C" + Format(request.PlateDiameterMm));
        HorizontalDimension(dxf, cx - bossRadius, cx + bossRadius, cy - plateRadius - 22.0, cy - bossRadius, "%%C" + Format(request.BossDiameterMm));

        HorizontalDimension(dxf, x0, x1, cy - plateRadius - 12.0, cy - plateRadius, Format(request.PlateThicknessMm));
        HorizontalDimension(dxf, x1, x2, cy - plateRadius - 22.0, cy - bossRadius, Format(request.BossLengthMm));

        if (request.BoreDiameterMm > 0.0)
        {
            VerticalDimension(dxf, cy - boreRadius, cy + boreRadius, x2 + 12.0, x2, "%%C" + Format(request.BoreDiameterMm));
        }

        VerticalDimension(dxf, cy - bossRadius, cy + bossRadius, x2 + 26.0, x2, "%%C" + Format(request.BossDiameterMm));
    }

    private static void HorizontalDimension(DxfFile dxf, double xa, double xb, double yDim, double yFeature, string text)
    {
        Line(dxf, xa, yFeature - 1.5, xa, yDim - 2.0, DimLayer);
        Line(dxf, xb, yFeature - 1.5, xb, yDim - 2.0, DimLayer);
        Line(dxf, xa, yDim, xb, yDim, DimLayer);

        const double arrowLength = 3.0;
        const double arrowHalfWidth = 0.8;
        Line(dxf, xa, yDim, xa + arrowLength, yDim + arrowHalfWidth, DimLayer);
        Line(dxf, xa, yDim, xa + arrowLength, yDim - arrowHalfWidth, DimLayer);
        Line(dxf, xb, yDim, xb - arrowLength, yDim + arrowHalfWidth, DimLayer);
        Line(dxf, xb, yDim, xb - arrowLength, yDim - arrowHalfWidth, DimLayer);

        const double textHeight = 3.5;
        var textWidth = text.Length * textHeight * 0.6;
        Text(dxf, (xa + xb) / 2.0 - textWidth / 2.0, yDim + 1.5, textHeight, text, DimLayer);
    }

    private static void VerticalDimension(DxfFile dxf, double ya, double yb, double xDim, double xFeature, string text)
    {
        Line(dxf, xFeature + 1.5, ya, xDim + 2.0, ya, DimLayer);
        Line(dxf, xFeature + 1.5, yb, xDim + 2.0, yb, DimLayer);
        Line(dxf, xDim, ya, xDim, yb, DimLayer);

        const double arrowLength = 3.0;
        const double arrowHalfWidth = 0.8;
        Line(dxf, xDim, ya, xDim + arrowHalfWidth, ya + arrowLength, DimLayer);
        Line(dxf, xDim, ya, xDim - arrowHalfWidth, ya + arrowLength, DimLayer);
        Line(dxf, xDim, yb, xDim + arrowHalfWidth, yb - arrowLength, DimLayer);
        Line(dxf, xDim, yb, xDim - arrowHalfWidth, yb - arrowLength, DimLayer);

        const double textHeight = 3.5;
        Text(dxf, xDim + 2.5, (ya + yb) / 2.0 - textHeight / 2.0, textHeight, text, DimLayer);
    }

    private static void DrawBom(DxfFile dxf, SheetRules sheet, HubPartRequest request, SheetFrameResult area, double rawDiameter, double rawLength, double rawWeight, double finishWeight)
    {
        var headers = sheet.BomColumns.Count == 4
            ? sheet.BomColumns
            : new List<string> { "ITEM", "RM SIZE", "RM WT.", "FINISH WT." };

        var widths = new[] { 20.0, 55.0, 25.0, 30.0 };
        var tableWidth = widths.Sum();
        var x = area.DrawingMaxX - tableWidth;
        var yTop = area.DrawingMaxY;
        var yMid = yTop - BomRowHeight;
        var yBottom = yMid - BomRowHeight;

        var rmSize = "%%C" + Format(rawDiameter) + " X " + Format(rawLength) + " LG";
        var values = new[]
        {
            request.ItemNo.ToString(CultureInfo.InvariantCulture),
            rmSize,
            Format(Math.Round(rawWeight, 2)),
            Format(Math.Round(finishWeight, 2))
        };

        for (var i = 0; i < 4; i++)
        {
            var xRight = x + widths[i];
            Cell(dxf, x, yMid, xRight, yTop, headers[i], 3.0);
            Cell(dxf, x, yBottom, xRight, yMid, values[i], 3.0);
            x = xRight;
        }
    }

    private static void Cell(DxfFile dxf, double x0, double y0, double x1, double y1, string text, double textHeight)
    {
        Rectangle(dxf, x0, y0, x1, y1, TableLayer);
        if (!string.IsNullOrEmpty(text))
        {
            Text(dxf, x0 + 1.5, y0 + (y1 - y0 - textHeight) / 2.0, textHeight, text, TableLayer);
        }
    }

    private static string FormatHoleNote(SheetRules sheet, HubPartRequest request)
    {
        var format = string.IsNullOrWhiteSpace(sheet.HoleNoteFormat)
            ? "{N} HOLES %%C{DIA} ON PCD %%C{PCD}"
            : sheet.HoleNoteFormat;

        return Clean(format
            .Replace("{N}", request.HoleCount.ToString(CultureInfo.InvariantCulture))
            .Replace("{DIA}", Format(request.HoleDiameterMm))
            .Replace("{PCD}", Format(request.HolePcdMm)));
    }

    private static List<string> FormatCallout(SheetRules sheet, HubPartRequest request, double weightKg)
    {
        var format = string.IsNullOrWhiteSpace(sheet.PartCalloutFormat)
            ? "{NAME}\\n{MATERIAL} {THICKNESS} MM THK\\nQTY. {QTY}\\nWT- {WEIGHT} KG."
            : sheet.PartCalloutFormat;

        var text = format
            .Replace("{NAME}", request.Name.ToUpperInvariant())
            .Replace("{MATERIAL}", request.Material)
            .Replace("{THICKNESS}", Format(request.PlateThicknessMm))
            .Replace("{QTY}", request.Qty.ToString(CultureInfo.InvariantCulture))
            .Replace("{WEIGHT}", Format(Math.Round(weightKg, 2)))
            .Replace("\\n", "\n")
            .Replace("\r", string.Empty);

        return text.Split('\n').Select(Clean).Where(line => line.Length > 0).ToList();
    }

    private static void Circle(DxfFile dxf, double cx, double cy, double radius, string layer)
    {
        dxf.Entities.Add(new DxfCircle(new DxfPoint(cx, cy, 0), radius) { Layer = layer });
    }

    private static void Rectangle(DxfFile dxf, double x0, double y0, double x1, double y1, string layer)
    {
        Line(dxf, x0, y0, x1, y0, layer);
        Line(dxf, x1, y0, x1, y1, layer);
        Line(dxf, x1, y1, x0, y1, layer);
        Line(dxf, x0, y1, x0, y0, layer);
    }

    private static void Line(DxfFile dxf, double x0, double y0, double x1, double y1, string layer)
    {
        dxf.Entities.Add(new DxfLine(new DxfPoint(x0, y0, 0), new DxfPoint(x1, y1, 0)) { Layer = layer });
    }

    private static void Text(DxfFile dxf, double x, double y, double height, string value, string layer)
    {
        dxf.Entities.Add(new DxfText(new DxfPoint(x, y, 0), height, value) { Layer = layer });
    }

    private static void EnsureLayer(DxfFile dxf, string name)
    {
        if (!dxf.Layers.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            dxf.Layers.Add(new DxfLayer(name));
        }
    }

    private static string Format(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch >= 32 && ch < 127)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }
}