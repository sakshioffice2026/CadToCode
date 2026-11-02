using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one round plate or flange part sheet. All sizes are in mm.</summary>
public sealed class FlangePartRequest
{
    public string Name { get; init; } = "FLANGE";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double OuterDiameterMm { get; init; }

    /// <summary>Centre bore diameter. Zero means a solid disc.</summary>
    public double InnerDiameterMm { get; init; }

    public double ThicknessMm { get; init; }

    public int HoleCount { get; init; }

    public double HoleDiameterMm { get; init; }

    public double HolePcdMm { get; init; }

    /// <summary>Added to the outer diameter to get the raw material size (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 5.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class FlangePartResult
{
    public FlangePartResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale)
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
/// Builds a complete A3 sheet for a round plate or flange: frame, front view with true circles,
/// side view, dimensions, hole note, part callout and BOM row.
/// </summary>
public sealed class FlangePartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string HiddenLayer = "HIDDEN";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double ViewMargin = 36.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public FlangePartResult Build(ManufacturingRules rules, FlangePartRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.OuterDiameterMm <= 0.0 || request.ThicknessMm <= 0.0)
        {
            throw new ArgumentException("Outer diameter and thickness must be greater than zero.");
        }

        if (request.InnerDiameterMm < 0.0 || request.InnerDiameterMm >= request.OuterDiameterMm)
        {
            throw new ArgumentException("Inner diameter must be zero or smaller than the outer diameter.");
        }

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, HiddenLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var finishWeight = CalculateFinishWeight(rules, request);
        var rawDiameter = request.OuterDiameterMm + request.RawMaterialAllowanceMm;
        var rawWeight = rules.DiscWeightKg(rawDiameter, request.ThicknessMm);

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var scaleDivisor = ChooseScale(request.OuterDiameterMm, probe);
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

        var radius = request.OuterDiameterMm / 2.0 / scaleDivisor;
        var thickness = request.ThicknessMm / scaleDivisor;
        var cx = area.DrawingMinX + ViewMargin + radius;
        var cy = area.DrawingMinY + ViewMargin + radius;

        DrawFrontView(dxf, request, scaleDivisor, cx, cy);

        var sideX0 = cx + radius + 70.0;
        var sideX1 = sideX0 + thickness;
        DrawSideView(dxf, request, scaleDivisor, sideX0, sideX1, cy);

        DrawDimensions(dxf, request, scaleDivisor, cx, cy, sideX0, sideX1);

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0 && request.HolePcdMm > 0.0)
        {
            var note = FormatHoleNote(rules.Sheet, request);
            Text(dxf, cx - radius, cy - radius - 26.0, 3.5, note, TextLayer);
        }

        var calloutLines = FormatCallout(rules.Sheet, request, finishWeight);
        var calloutY = cy + radius;
        foreach (var line in calloutLines)
        {
            Text(dxf, sideX1 + 20.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawDiameter, rawWeight, finishWeight);

        return new FlangePartResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static double CalculateFinishWeight(ManufacturingRules rules, FlangePartRequest request)
    {
        var weight = request.InnerDiameterMm > 0.0
            ? rules.RingWeightKg(request.OuterDiameterMm, request.InnerDiameterMm, request.ThicknessMm)
            : rules.DiscWeightKg(request.OuterDiameterMm, request.ThicknessMm);

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0)
        {
            var holeArea = request.HoleCount * Math.PI / 4.0 * request.HoleDiameterMm * request.HoleDiameterMm;
            weight -= rules.PlateWeightKg(holeArea, request.ThicknessMm);
        }

        return Math.Max(weight, 0.0);
    }

    private static double ChooseScale(double outerDiameter, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - 2.0 * ViewMargin - 2.0 * BomRowHeight - 10.0;
        foreach (var divisor in StandardScales)
        {
            if (outerDiameter / divisor <= availableHeight)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void DrawFrontView(DxfFile dxf, FlangePartRequest request, double divisor, double cx, double cy)
    {
        var radius = request.OuterDiameterMm / 2.0 / divisor;
        Circle(dxf, cx, cy, radius, OutlineLayer);

        if (request.InnerDiameterMm > 0.0)
        {
            Circle(dxf, cx, cy, request.InnerDiameterMm / 2.0 / divisor, OutlineLayer);
        }

        var extend = 6.0;
        Line(dxf, cx - radius - extend, cy, cx + radius + extend, cy, CenterLayer);
        Line(dxf, cx, cy - radius - extend, cx, cy + radius + extend, CenterLayer);

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

    private static void DrawSideView(DxfFile dxf, FlangePartRequest request, double divisor, double x0, double x1, double cy)
    {
        var radius = request.OuterDiameterMm / 2.0 / divisor;
        Rectangle(dxf, x0, cy - radius, x1, cy + radius, OutlineLayer);
        Line(dxf, x0 - 6.0, cy, x1 + 6.0, cy, CenterLayer);

        if (request.InnerDiameterMm > 0.0)
        {
            var boreRadius = request.InnerDiameterMm / 2.0 / divisor;
            Line(dxf, x0, cy + boreRadius, x1, cy + boreRadius, HiddenLayer);
            Line(dxf, x0, cy - boreRadius, x1, cy - boreRadius, HiddenLayer);
        }
    }

    private static void DrawDimensions(DxfFile dxf, FlangePartRequest request, double divisor, double cx, double cy, double sideX0, double sideX1)
    {
        var radius = request.OuterDiameterMm / 2.0 / divisor;
        var dimY = cy - radius - 12.0;

        HorizontalDimension(dxf, cx - radius, cx + radius, dimY, cy - radius, "%%C" + Format(request.OuterDiameterMm));

        if (request.InnerDiameterMm > 0.0)
        {
            var boreRadius = request.InnerDiameterMm / 2.0 / divisor;
            HorizontalDimension(dxf, cx - boreRadius, cx + boreRadius, cy + 0.0 + boreRadius + 8.0, cy + boreRadius, "%%C" + Format(request.InnerDiameterMm));
        }

        HorizontalDimension(dxf, sideX0, sideX1, cy - radius - 12.0, cy - radius, Format(request.ThicknessMm));
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

    private static void DrawBom(DxfFile dxf, SheetRules sheet, FlangePartRequest request, SheetFrameResult area, double rawDiameter, double rawWeight, double finishWeight)
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

        var rmSize = "%%C" + Format(rawDiameter) + " X " + Format(request.ThicknessMm);
        var values = new[]
        {
            request.ItemNo.ToString(CultureInfo.InvariantCulture),
            rmSize,
            Format(Math.Round(rawWeight, 2)),
            Format(Math.Round(finishWeight, 2))
        };

        for (var i = 0; i < 4; i++)
        {
            var x1 = x + widths[i];
            Cell(dxf, x, yMid, x1, yTop, headers[i], 3.0);
            Cell(dxf, x, yBottom, x1, yMid, values[i], 3.0);
            x = x1;
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

    private static string FormatHoleNote(SheetRules sheet, FlangePartRequest request)
    {
        var format = string.IsNullOrWhiteSpace(sheet.HoleNoteFormat)
            ? "{N} HOLES %%C{DIA} ON PCD %%C{PCD}"
            : sheet.HoleNoteFormat;

        return Clean(format
            .Replace("{N}", request.HoleCount.ToString(CultureInfo.InvariantCulture))
            .Replace("{DIA}", Format(request.HoleDiameterMm))
            .Replace("{PCD}", Format(request.HolePcdMm)));
    }

    private static List<string> FormatCallout(SheetRules sheet, FlangePartRequest request, double weightKg)
    {
        var format = string.IsNullOrWhiteSpace(sheet.PartCalloutFormat)
            ? "{NAME}\\n{MATERIAL} {THICKNESS} MM THK\\nQTY. {QTY}\\nWT- {WEIGHT} KG."
            : sheet.PartCalloutFormat;

        var text = format
            .Replace("{NAME}", request.Name.ToUpperInvariant())
            .Replace("{MATERIAL}", request.Material)
            .Replace("{THICKNESS}", Format(request.ThicknessMm))
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