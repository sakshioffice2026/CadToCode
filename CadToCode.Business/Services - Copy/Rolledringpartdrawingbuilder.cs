using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one ring rolled from flat bar (bent through its thickness). All sizes are in mm.</summary>
public sealed class RolledRingPartRequest
{
    public string Name { get; init; } = "RING";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    /// <summary>Inside diameter of the rolled ring.</summary>
    public double InnerDiameterMm { get; init; }

    /// <summary>Flat bar width. This becomes the axial width of the ring.</summary>
    public double BarWidthMm { get; init; }

    /// <summary>Flat bar thickness. This becomes the radial depth of the ring.</summary>
    public double BarThicknessMm { get; init; }

    /// <summary>Added to the developed length to get the raw bar length (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 10.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class RolledRingPartResult
{
    public RolledRingPartResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale, double developedLengthMm)
    {
        Dxf = dxf;
        FinishWeightKg = finishWeightKg;
        RawWeightKg = rawWeightKg;
        Scale = scale;
        DevelopedLengthMm = developedLengthMm;
    }

    public DxfFile Dxf { get; }

    public double FinishWeightKg { get; }

    public double RawWeightKg { get; }

    public string Scale { get; }

    /// <summary>Length of flat bar before rolling, measured on the neutral axis.</summary>
    public double DevelopedLengthMm { get; }
}

/// <summary>
/// Builds a complete A3 sheet for a rolled ring: frame, flat bar before rolling, front and side
/// views after rolling, dimensions, part callout and BOM row.
/// </summary>
public sealed class RolledRingPartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string HiddenLayer = "HIDDEN";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double SideMargin = 36.0;
    private const double BottomMargin = 50.0;
    private const double TopMargin = 36.0;
    private const double RowGap = 64.0;
    private const double ViewGap = 60.0;
    private const double CalloutSpace = 80.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public RolledRingPartResult Build(ManufacturingRules rules, RolledRingPartRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        var innerDiameter = request.InnerDiameterMm;
        var outerDiameter = innerDiameter + 2.0 * request.BarThicknessMm;
        var developedLength = Math.PI * (innerDiameter + request.BarThicknessMm);

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, HiddenLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var finishWeight = rules.PlateWeightKg(developedLength * request.BarWidthMm, request.BarThicknessMm);
        var rawLength = developedLength + request.RawMaterialAllowanceMm;
        var rawWeight = rules.PlateWeightKg(rawLength * request.BarWidthMm, request.BarThicknessMm);

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var scaleDivisor = ChooseScale(request, outerDiameter, developedLength, probe);
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

        var barLength = developedLength / scaleDivisor;
        var barWidth = request.BarWidthMm / scaleDivisor;
        var outerRadius = outerDiameter / 2.0 / scaleDivisor;
        var innerRadius = innerDiameter / 2.0 / scaleDivisor;

        var barX0 = area.DrawingMinX + SideMargin;
        var barY0 = area.DrawingMinY + BottomMargin;
        var barX1 = barX0 + barLength;
        var barY1 = barY0 + barWidth;

        Rectangle(dxf, barX0, barY0, barX1, barY1, OutlineLayer);
        Text(dxf, barX0, barY1 + 10.0, 4.0, "FLAT BAR (BEFORE ROLLING)", TextLayer);

        HorizontalDimension(dxf, barX0, barX1, barY0 - 12.0, barY0, Format(Math.Round(developedLength, 1)));
        VerticalDimension(dxf, barY0, barY1, barX1 + 12.0, barX1, Format(request.BarWidthMm));
        Text(dxf, barX0, barY0 - 28.0, 3.5, "DEVELOPED LENGTH = PI X (ID + THK) AT NEUTRAL AXIS", DimLayer);

        var cx = barX0 + outerRadius;
        var cy = barY1 + RowGap + outerRadius;

        DrawFrontView(dxf, cx, cy, outerRadius, innerRadius);
        HorizontalDimension(dxf, cx - outerRadius, cx + outerRadius, cy - outerRadius - 12.0, cy - outerRadius, "%%C" + Format(outerDiameter));
        HorizontalDimension(dxf, cx - innerRadius, cx + innerRadius, cy - outerRadius - 22.0, cy - innerRadius, "%%C" + Format(innerDiameter));

        var sideX0 = cx + outerRadius + ViewGap;
        var sideX1 = sideX0 + barWidth;
        DrawSideView(dxf, sideX0, sideX1, cy, outerRadius, innerRadius);
        HorizontalDimension(dxf, sideX0, sideX1, cy - outerRadius - 12.0, cy - outerRadius, Format(request.BarWidthMm));

        Text(dxf, cx - outerRadius, cy + outerRadius + 10.0, 4.0, "AFTER ROLLING", TextLayer);

        var calloutLines = FormatCallout(request, finishWeight);
        var calloutY = cy + outerRadius;
        foreach (var line in calloutLines)
        {
            Text(dxf, sideX1 + 20.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawLength, rawWeight, finishWeight);

        return new RolledRingPartResult(dxf, finishWeight, rawWeight, scaleText, developedLength);
    }

    private static void Validate(RolledRingPartRequest request)
    {
        if (request.InnerDiameterMm <= 0.0 || request.BarWidthMm <= 0.0 || request.BarThicknessMm <= 0.0)
        {
            throw new ArgumentException("Inside diameter, bar width and bar thickness must be greater than zero.");
        }

        if (request.BarThicknessMm * 2.0 >= request.InnerDiameterMm)
        {
            throw new ArgumentException("Bar thickness is too large for this inside diameter.");
        }
    }

    private static double ChooseScale(RolledRingPartRequest request, double outerDiameter, double developedLength, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - BottomMargin - TopMargin - 2.0 * BomRowHeight - 10.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * SideMargin;

        foreach (var divisor in StandardScales)
        {
            var height = (request.BarWidthMm + outerDiameter) / divisor + RowGap + 20.0;
            var topRowWidth = (outerDiameter + request.BarWidthMm) / divisor + ViewGap + CalloutSpace;
            var bottomRowWidth = developedLength / divisor + 30.0;
            if (height <= availableHeight && topRowWidth <= availableWidth && bottomRowWidth <= availableWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void DrawFrontView(DxfFile dxf, double cx, double cy, double outerRadius, double innerRadius)
    {
        Circle(dxf, cx, cy, outerRadius, OutlineLayer);
        Circle(dxf, cx, cy, innerRadius, OutlineLayer);

        const double extend = 6.0;
        Line(dxf, cx - outerRadius - extend, cy, cx + outerRadius + extend, cy, CenterLayer);
        Line(dxf, cx, cy - outerRadius - extend, cx, cy + outerRadius + extend, CenterLayer);
    }

    private static void DrawSideView(DxfFile dxf, double x0, double x1, double cy, double outerRadius, double innerRadius)
    {
        Rectangle(dxf, x0, cy - outerRadius, x1, cy + outerRadius, OutlineLayer);
        Line(dxf, x0, cy + innerRadius, x1, cy + innerRadius, HiddenLayer);
        Line(dxf, x0, cy - innerRadius, x1, cy - innerRadius, HiddenLayer);
        Line(dxf, x0 - 6.0, cy, x1 + 6.0, cy, CenterLayer);
    }

    private static void HorizontalDimension(DxfFile dxf, double xa, double xb, double yDim, double yFeature, string text)
    {
        var direction = yDim >= yFeature ? 1.0 : -1.0;
        Line(dxf, xa, yFeature + direction * 1.5, xa, yDim + direction * 2.0, DimLayer);
        Line(dxf, xb, yFeature + direction * 1.5, xb, yDim + direction * 2.0, DimLayer);
        Line(dxf, xa, yDim, xb, yDim, DimLayer);

        const double arrowLength = 3.0;
        const double arrowHalfWidth = 0.8;
        var arrow = Math.Min(arrowLength, Math.Abs(xb - xa) / 3.0);
        Line(dxf, xa, yDim, xa + arrow, yDim + arrowHalfWidth, DimLayer);
        Line(dxf, xa, yDim, xa + arrow, yDim - arrowHalfWidth, DimLayer);
        Line(dxf, xb, yDim, xb - arrow, yDim + arrowHalfWidth, DimLayer);
        Line(dxf, xb, yDim, xb - arrow, yDim - arrowHalfWidth, DimLayer);

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
        var arrow = Math.Min(arrowLength, Math.Abs(yb - ya) / 3.0);
        Line(dxf, xDim, ya, xDim + arrowHalfWidth, ya + arrow, DimLayer);
        Line(dxf, xDim, ya, xDim - arrowHalfWidth, ya + arrow, DimLayer);
        Line(dxf, xDim, yb, xDim + arrowHalfWidth, yb - arrow, DimLayer);
        Line(dxf, xDim, yb, xDim - arrowHalfWidth, yb - arrow, DimLayer);

        const double textHeight = 3.5;
        Text(dxf, xDim + 2.5, (ya + yb) / 2.0 - textHeight / 2.0, textHeight, text, DimLayer);
    }

    private static void DrawBom(DxfFile dxf, SheetRules sheet, RolledRingPartRequest request, SheetFrameResult area, double rawLength, double rawWeight, double finishWeight)
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

        var rmSize = Format(Math.Ceiling(rawLength)) + " X " + Format(request.BarWidthMm) + " X " + Format(request.BarThicknessMm);
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

    private static List<string> FormatCallout(RolledRingPartRequest request, double weightKg)
    {
        return new List<string>
        {
            Clean(request.Name.ToUpperInvariant()),
            Clean(request.Material + " " + Format(request.BarWidthMm) + " X " + Format(request.BarThicknessMm) + " FLAT"),
            "QTY. " + request.Qty.ToString(CultureInfo.InvariantCulture),
            "WT- " + Format(Math.Round(weightKg, 2)) + " KG.",
            "ROLL TO %%C" + Format(request.InnerDiameterMm) + " ID AND WELD"
        };
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