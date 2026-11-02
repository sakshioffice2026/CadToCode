using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one round bar part sheet. All sizes are in mm.</summary>
public sealed class RoundBarRequest
{
    public string Name { get; init; } = "ROUND BAR";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double DiameterMm { get; init; }

    public double LengthMm { get; init; }

    /// <summary>45 degree chamfer size at both ends. Zero means square ends.</summary>
    public double ChamferMm { get; init; }

    /// <summary>Diameter of one cross hole drilled through the bar. Zero means no cross hole.</summary>
    public double CrossHoleDiameterMm { get; init; }

    /// <summary>Distance from the left end to the cross hole centre.</summary>
    public double CrossHolePositionMm { get; init; }

    /// <summary>Added to diameter and length to get the raw bar size (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 5.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class RoundBarPartResult
{
    public RoundBarPartResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale)
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
/// Builds a complete A3 sheet for a round bar: frame, side view with centre line, end view,
/// dimensions, optional chamfers and cross hole, part callout and BOM row.
/// </summary>
public sealed class RoundBarPartDrawingBuilder
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
    private const double AboveDimSpace = 30.0;
    private const double ViewGap = 60.0;
    private const double CalloutSpace = 80.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public RoundBarPartResult Build(ManufacturingRules rules, RoundBarRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, HiddenLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var finishWeight = CalculateFinishWeight(rules, request);
        var rawDiameter = request.DiameterMm + request.RawMaterialAllowanceMm;
        var rawLength = request.LengthMm + request.RawMaterialAllowanceMm;
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

        var radius = request.DiameterMm / 2.0 / scaleDivisor;
        var length = request.LengthMm / scaleDivisor;

        var x0 = area.DrawingMinX + SideMargin;
        var x1 = x0 + length;
        var cy = area.DrawingMinY + BottomMargin + radius;

        DrawSideView(dxf, request, scaleDivisor, x0, x1, cy);

        var endCx = x1 + ViewGap + radius;
        DrawEndView(dxf, request, scaleDivisor, endCx, cy);

        DrawDimensions(dxf, request, scaleDivisor, x0, x1, cy, endCx);

        var calloutLines = FormatCallout(request, finishWeight);
        var calloutY = cy + radius;
        foreach (var line in calloutLines)
        {
            Text(dxf, endCx + radius + 20.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawDiameter, rawLength, rawWeight, finishWeight);

        return new RoundBarPartResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static void Validate(RoundBarRequest request)
    {
        if (request.DiameterMm <= 0.0 || request.LengthMm <= 0.0)
        {
            throw new ArgumentException("Diameter and length must be greater than zero.");
        }

        if (request.ChamferMm < 0.0 || request.ChamferMm * 2.0 >= request.DiameterMm || request.ChamferMm * 2.0 >= request.LengthMm)
        {
            throw new ArgumentException("Chamfer is too large for the bar.");
        }

        if (request.CrossHoleDiameterMm > 0.0)
        {
            var radius = request.CrossHoleDiameterMm / 2.0;
            if (request.CrossHoleDiameterMm >= request.DiameterMm
                || request.CrossHolePositionMm < radius
                || request.CrossHolePositionMm > request.LengthMm - radius)
            {
                throw new ArgumentException("Cross hole does not fit in the bar.");
            }
        }
    }

    private static double CalculateFinishWeight(ManufacturingRules rules, RoundBarRequest request)
    {
        var weight = rules.DiscWeightKg(request.DiameterMm, request.LengthMm);

        if (request.ChamferMm > 0.0)
        {
            var c = request.ChamferMm;
            var r = request.DiameterMm / 2.0;
            var removedVolumePerEnd = 2.0 * Math.PI * (r - c / 3.0) * c * c / 2.0;
            weight -= 2.0 * rules.PlateWeightKg(removedVolumePerEnd, 1.0);
        }

        if (request.CrossHoleDiameterMm > 0.0)
        {
            var holeArea = Math.PI / 4.0 * request.CrossHoleDiameterMm * request.CrossHoleDiameterMm;
            weight -= rules.PlateWeightKg(holeArea, request.DiameterMm);
        }

        return Math.Max(weight, 0.0);
    }

    private static double ChooseScale(RoundBarRequest request, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - BottomMargin - TopMargin - AboveDimSpace - 2.0 * BomRowHeight - 10.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * SideMargin;

        foreach (var divisor in StandardScales)
        {
            var heightOk = request.DiameterMm / divisor <= availableHeight;
            var width = (request.LengthMm + request.DiameterMm) / divisor + ViewGap + CalloutSpace;
            if (heightOk && width <= availableWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void DrawSideView(DxfFile dxf, RoundBarRequest request, double divisor, double x0, double x1, double cy)
    {
        var radius = request.DiameterMm / 2.0 / divisor;
        var c = request.ChamferMm / divisor;

        Line(dxf, x0, cy - radius + c, x0, cy + radius - c, OutlineLayer);
        Line(dxf, x1, cy - radius + c, x1, cy + radius - c, OutlineLayer);
        Line(dxf, x0 + c, cy + radius, x1 - c, cy + radius, OutlineLayer);
        Line(dxf, x0 + c, cy - radius, x1 - c, cy - radius, OutlineLayer);

        if (c > 0.0)
        {
            Line(dxf, x0, cy + radius - c, x0 + c, cy + radius, OutlineLayer);
            Line(dxf, x0, cy - radius + c, x0 + c, cy - radius, OutlineLayer);
            Line(dxf, x1, cy + radius - c, x1 - c, cy + radius, OutlineLayer);
            Line(dxf, x1, cy - radius + c, x1 - c, cy - radius, OutlineLayer);
            Line(dxf, x0 + c, cy - radius, x0 + c, cy + radius, OutlineLayer);
            Line(dxf, x1 - c, cy - radius, x1 - c, cy + radius, OutlineLayer);
        }

        Line(dxf, x0 - 8.0, cy, x1 + 8.0, cy, CenterLayer);

        if (request.CrossHoleDiameterMm > 0.0)
        {
            var hx = x0 + request.CrossHolePositionMm / divisor;
            var hr = request.CrossHoleDiameterMm / 2.0 / divisor;
            Line(dxf, hx - hr, cy - radius, hx - hr, cy + radius, HiddenLayer);
            Line(dxf, hx + hr, cy - radius, hx + hr, cy + radius, HiddenLayer);
            Line(dxf, hx, cy - radius - 6.0, hx, cy + radius + 6.0, CenterLayer);
        }
    }

    private static void DrawEndView(DxfFile dxf, RoundBarRequest request, double divisor, double cx, double cy)
    {
        var radius = request.DiameterMm / 2.0 / divisor;
        Circle(dxf, cx, cy, radius, OutlineLayer);

        if (request.ChamferMm > 0.0)
        {
            Circle(dxf, cx, cy, radius - request.ChamferMm / divisor, OutlineLayer);
        }

        const double extend = 6.0;
        Line(dxf, cx - radius - extend, cy, cx + radius + extend, cy, CenterLayer);
        Line(dxf, cx, cy - radius - extend, cx, cy + radius + extend, CenterLayer);
    }

    private static void DrawDimensions(DxfFile dxf, RoundBarRequest request, double divisor, double x0, double x1, double cy, double endCx)
    {
        var radius = request.DiameterMm / 2.0 / divisor;

        HorizontalDimension(dxf, x0, x1, cy - radius - 12.0, cy - radius, Format(request.LengthMm));
        HorizontalDimension(dxf, endCx - radius, endCx + radius, cy - radius - 12.0, cy - radius, "%%C" + Format(request.DiameterMm));

        if (request.CrossHoleDiameterMm > 0.0)
        {
            var hx = x0 + request.CrossHolePositionMm / divisor;
            HorizontalDimension(dxf, x0, hx, cy + radius + 12.0, cy + radius, Format(request.CrossHolePositionMm));
            Text(dxf, hx + 2.0, cy + radius + 20.0, 3.5, "%%C" + Format(request.CrossHoleDiameterMm) + " THRU", TextLayer);
        }

        if (request.ChamferMm > 0.0)
        {
            Text(dxf, x0, cy - radius - 28.0, 3.5, Format(request.ChamferMm) + " X 45%%D CHAMFER BOTH ENDS", TextLayer);
        }
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

    private static void DrawBom(DxfFile dxf, SheetRules sheet, RoundBarRequest request, SheetFrameResult area, double rawDiameter, double rawLength, double rawWeight, double finishWeight)
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

    private static List<string> FormatCallout(RoundBarRequest request, double weightKg)
    {
        return new List<string>
        {
            Clean(request.Name.ToUpperInvariant()),
            Clean(request.Material + " %%C" + Format(request.DiameterMm) + " X " + Format(request.LengthMm) + " LG"),
            "QTY. " + request.Qty.ToString(CultureInfo.InvariantCulture),
            "WT- " + Format(Math.Round(weightKg, 2)) + " KG."
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