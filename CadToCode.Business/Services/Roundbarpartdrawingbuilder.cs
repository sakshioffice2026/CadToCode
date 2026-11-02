using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one round bar part sheet (shaft, pin, rod). All sizes are in mm.</summary>
public sealed class RoundBarRequest
{
    public string Name { get; init; } = "BAR";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double DiameterMm { get; init; }

    public double LengthMm { get; init; }

    /// <summary>Added to the length to get the raw material length (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 5.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class RoundBarResult
{
    public RoundBarResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale)
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
/// Builds a complete A3 sheet for a round bar: frame, elevation, end view, dimensions,
/// part callout and BOM row.
/// </summary>
public sealed class RoundBarPartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double ViewMargin = 36.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public RoundBarResult Build(ManufacturingRules rules, RoundBarRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.DiameterMm <= 0.0 || request.LengthMm <= 0.0)
        {
            throw new ArgumentException("Diameter and length must be greater than zero.");
        }

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var finishWeight = rules.DiscWeightKg(request.DiameterMm, request.LengthMm);
        var rawLength = request.LengthMm + request.RawMaterialAllowanceMm;
        var rawWeight = rules.DiscWeightKg(request.DiameterMm, rawLength);

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var divisor = ChooseScale(request, probe);
        var scaleText = divisor <= 1.0 ? "1:1" : "1:" + Format(divisor);

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

        var length = request.LengthMm / divisor;
        var radius = request.DiameterMm / 2.0 / divisor;

        var x0 = area.DrawingMinX + ViewMargin;
        var x1 = x0 + length;
        var cy = area.DrawingMinY + ViewMargin + radius;

        Rectangle(dxf, x0, cy - radius, x1, cy + radius, OutlineLayer);
        Line(dxf, x0 - 6.0, cy, x1 + 6.0, cy, CenterLayer);

        var cx = x1 + 60.0 + radius;
        dxf.Entities.Add(new DxfCircle(new DxfPoint(cx, cy, 0), radius) { Layer = OutlineLayer });
        Line(dxf, cx - radius - 6.0, cy, cx + radius + 6.0, cy, CenterLayer);
        Line(dxf, cx, cy - radius - 6.0, cx, cy + radius + 6.0, CenterLayer);

        HorizontalDimension(dxf, x0, x1, cy - radius - 12.0, cy - radius, Format(request.LengthMm));
        VerticalDimension(dxf, cy - radius, cy + radius, x0 - 12.0, x0, "%%C" + Format(request.DiameterMm));
        HorizontalDimension(dxf, cx - radius, cx + radius, cy - radius - 12.0, cy - radius, "%%C" + Format(request.DiameterMm));

        var calloutY = cy + radius + 20.0;
        foreach (var line in FormatCallout(rules.Sheet, request, finishWeight))
        {
            Text(dxf, cx + radius + 20.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawLength, rawWeight, finishWeight);

        return new RoundBarResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static double ChooseScale(RoundBarRequest request, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - 2.0 * ViewMargin - 2.0 * BomRowHeight - 40.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * ViewMargin - 60.0 - 80.0;

        foreach (var divisor in StandardScales)
        {
            var fitsHeight = request.DiameterMm / divisor <= availableHeight;
            var fitsWidth = (request.LengthMm + request.DiameterMm) / divisor <= availableWidth;
            if (fitsHeight && fitsWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
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
        var sign = xDim >= xFeature ? 1.0 : -1.0;
        Line(dxf, xFeature + sign * 1.5, ya, xDim + sign * 2.0, ya, DimLayer);
        Line(dxf, xFeature + sign * 1.5, yb, xDim + sign * 2.0, yb, DimLayer);
        Line(dxf, xDim, ya, xDim, yb, DimLayer);

        const double arrowLength = 3.0;
        const double arrowHalfWidth = 0.8;
        Line(dxf, xDim, ya, xDim + arrowHalfWidth, ya + arrowLength, DimLayer);
        Line(dxf, xDim, ya, xDim - arrowHalfWidth, ya + arrowLength, DimLayer);
        Line(dxf, xDim, yb, xDim + arrowHalfWidth, yb - arrowLength, DimLayer);
        Line(dxf, xDim, yb, xDim - arrowHalfWidth, yb - arrowLength, DimLayer);

        const double textHeight = 3.5;
        var textWidth = text.Length * textHeight * 0.6;
        dxf.Entities.Add(new DxfText(new DxfPoint(xDim - 1.5, (ya + yb) / 2.0 - textWidth / 2.0, 0), textHeight, text)
        {
            Layer = DimLayer,
            Rotation = 90.0
        });
    }

    private static void DrawBom(DxfFile dxf, SheetRules sheet, RoundBarRequest request, SheetFrameResult area, double rawLength, double rawWeight, double finishWeight)
    {
        var headers = sheet.BomColumns.Count == 4
            ? sheet.BomColumns
            : new List<string> { "ITEM", "RM SIZE", "RM WT.", "FINISH WT." };

        var widths = new[] { 20.0, 55.0, 25.0, 30.0 };
        var x = area.DrawingMaxX - widths.Sum();
        var yTop = area.DrawingMaxY;
        var yMid = yTop - BomRowHeight;
        var yBottom = yMid - BomRowHeight;

        var values = new[]
        {
            request.ItemNo.ToString(CultureInfo.InvariantCulture),
            "%%C" + Format(request.DiameterMm) + " X " + Format(rawLength),
            Format(Math.Round(rawWeight, 2)),
            Format(Math.Round(finishWeight, 2))
        };

        for (var i = 0; i < 4; i++)
        {
            var xNext = x + widths[i];
            Cell(dxf, x, yMid, xNext, yTop, headers[i], 3.0);
            Cell(dxf, x, yBottom, xNext, yMid, values[i], 3.0);
            x = xNext;
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

    private static List<string> FormatCallout(SheetRules sheet, RoundBarRequest request, double weightKg)
    {
        var format = string.IsNullOrWhiteSpace(sheet.PartCalloutFormat)
            ? "{NAME}\\n{MATERIAL} {THICKNESS} MM THK\\nQTY. {QTY}\\nWT- {WEIGHT} KG."
            : sheet.PartCalloutFormat;

        var text = format
            .Replace("{NAME}", request.Name.ToUpperInvariant())
            .Replace("{MATERIAL}", request.Material)
            .Replace("{THICKNESS}", Format(request.DiameterMm))
            .Replace("{QTY}", request.Qty.ToString(CultureInfo.InvariantCulture))
            .Replace("{WEIGHT}", Format(Math.Round(weightKg, 2)))
            .Replace("\\n", "\n")
            .Replace("\r", string.Empty);

        return text.Split('\n').Select(Clean).Where(line => line.Length > 0).ToList();
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