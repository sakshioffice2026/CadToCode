using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one rolled cone (frustum) part sheet. Diameters are measured on the sheet surface. All sizes are in mm.</summary>
public sealed class ConeFlatPatternRequest
{
    public string Name { get; init; } = "CONE";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double BottomDiameterMm { get; init; }

    /// <summary>Top diameter. Zero means a full cone.</summary>
    public double TopDiameterMm { get; init; }

    public double HeightMm { get; init; }

    public double ThicknessMm { get; init; }

    /// <summary>Added to the bounding width and height of the pattern to get the raw sheet size (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 10.0;

    /// <summary>Title block values. The scale is filled in by the builder.</summary>
    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class ConeFlatPatternResult
{
    public ConeFlatPatternResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, double outerRadiusMm, double innerRadiusMm, double angleDegrees, string scale)
    {
        Dxf = dxf;
        FinishWeightKg = finishWeightKg;
        RawWeightKg = rawWeightKg;
        OuterRadiusMm = outerRadiusMm;
        InnerRadiusMm = innerRadiusMm;
        AngleDegrees = angleDegrees;
        Scale = scale;
    }

    public DxfFile Dxf { get; }

    public double FinishWeightKg { get; }

    public double RawWeightKg { get; }

    public double OuterRadiusMm { get; }

    public double InnerRadiusMm { get; }

    public double AngleDegrees { get; }

    public string Scale { get; }
}

/// <summary>
/// Builds a complete A3 sheet for a rolled cone: BEFORE BENDING view with the developed sector,
/// AFTER BENDING elevation, dimensions, developed radii and angle notes, part callout and BOM row.
/// </summary>
public sealed class ConeFlatPatternDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double ViewMargin = 56.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public ConeFlatPatternResult Build(ManufacturingRules rules, ConeFlatPatternRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.BottomDiameterMm <= 0.0 || request.HeightMm <= 0.0 || request.ThicknessMm <= 0.0 || request.TopDiameterMm < 0.0)
        {
            throw new ArgumentException("Bottom diameter, height and thickness must be greater than zero, and top diameter cannot be negative.");
        }

        if (request.TopDiameterMm >= request.BottomDiameterMm)
        {
            throw new ArgumentException("Top diameter must be smaller than the bottom diameter.");
        }

        var diameterDifference = request.BottomDiameterMm - request.TopDiameterMm;
        var slant = Math.Sqrt(request.HeightMm * request.HeightMm + diameterDifference * diameterDifference / 4.0);
        var outerRadius = slant * request.BottomDiameterMm / diameterDifference;
        var innerRadius = outerRadius - slant;
        var angleDegrees = 180.0 * request.BottomDiameterMm / outerRadius;

        var startAngle = 90.0 - angleDegrees / 2.0;
        var endAngle = 90.0 + angleDegrees / 2.0;
        var bounds = MeasureSector(outerRadius, innerRadius, startAngle, endAngle);

        var sectorArea = 0.5 * (angleDegrees * Math.PI / 180.0) * (outerRadius * outerRadius - innerRadius * innerRadius);
        var finishWeight = rules.PlateWeightKg(sectorArea, request.ThicknessMm);
        var rawWeight = rules.PlateWeightKg(
            (bounds.Width + request.RawMaterialAllowanceMm) * (bounds.Height + request.RawMaterialAllowanceMm),
            request.ThicknessMm);

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var divisor = ChooseScale(request, bounds, probe);
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

        var x0 = area.DrawingMinX + ViewMargin;
        var y0 = area.DrawingMinY + ViewMargin;
        var apexX = x0 - bounds.MinX / divisor;
        var apexY = y0 - bounds.MinY / divisor;
        var outerScaled = outerRadius / divisor;
        var innerScaled = innerRadius / divisor;

        DrawSector(dxf, apexX, apexY, outerScaled, innerScaled, startAngle, endAngle);

        var patternX1 = x0 + bounds.Width / divisor;
        var patternY1 = y0 + bounds.Height / divisor;
        HorizontalDimension(dxf, x0, patternX1, y0 - 12.0, y0, Format(Math.Round(bounds.Width, 1)));
        VerticalDimension(dxf, y0, patternY1, x0 - 12.0, x0, Format(Math.Round(bounds.Height, 1)));

        Text(dxf, x0, y0 - 36.0, 3.5, "BEFORE BENDING", TextLayer);
        Text(dxf, x0, y0 - 43.0, 3.0, "R" + Format(Math.Round(outerRadius, 1)) + " AND R" + Format(Math.Round(innerRadius, 1)) + " DEVELOPED", TextLayer);
        Text(dxf, x0, y0 - 50.0, 3.0, "DEVELOPED ANGLE " + Format(Math.Round(angleDegrees, 2)) + " DEG", TextLayer);

        var bottomHalf = request.BottomDiameterMm / 2.0 / divisor;
        var topHalf = request.TopDiameterMm / 2.0 / divisor;
        var height = request.HeightMm / divisor;
        var ex = patternX1 + 70.0 + bottomHalf;
        DrawElevation(dxf, request, divisor, ex, y0, bottomHalf, topHalf, height);

        var calloutY = y0 + height;
        foreach (var line in FormatCallout(rules.Sheet, request, finishWeight))
        {
            Text(dxf, ex + bottomHalf + 28.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, bounds, rawWeight, finishWeight);

        return new ConeFlatPatternResult(dxf, finishWeight, rawWeight, outerRadius, innerRadius, angleDegrees, scaleText);
    }

    private static void DrawSector(DxfFile dxf, double apexX, double apexY, double outerRadius, double innerRadius, double startAngle, double endAngle)
    {
        var centre = new DxfPoint(apexX, apexY, 0);
        dxf.Entities.Add(new DxfArc(centre, outerRadius, startAngle, endAngle) { Layer = OutlineLayer });

        if (innerRadius > 0.0)
        {
            dxf.Entities.Add(new DxfArc(centre, innerRadius, startAngle, endAngle) { Layer = OutlineLayer });
        }

        foreach (var angle in new[] { startAngle, endAngle })
        {
            var radians = angle * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            Line(dxf, apexX + innerRadius * cos, apexY + innerRadius * sin, apexX + outerRadius * cos, apexY + outerRadius * sin, OutlineLayer);
        }

        Line(dxf, apexX, apexY, apexX, apexY + outerRadius + 6.0, CenterLayer);
    }

    private static void DrawElevation(DxfFile dxf, ConeFlatPatternRequest request, double divisor, double ex, double y0, double bottomHalf, double topHalf, double height)
    {
        var yTop = y0 + height;
        Line(dxf, ex - bottomHalf, y0, ex + bottomHalf, y0, OutlineLayer);
        Line(dxf, ex - bottomHalf, y0, ex - topHalf, yTop, OutlineLayer);
        Line(dxf, ex + bottomHalf, y0, ex + topHalf, yTop, OutlineLayer);

        if (topHalf > 0.0)
        {
            Line(dxf, ex - topHalf, yTop, ex + topHalf, yTop, OutlineLayer);
        }

        Line(dxf, ex, y0 - 6.0, ex, yTop + 6.0, CenterLayer);

        HorizontalDimension(dxf, ex - bottomHalf, ex + bottomHalf, y0 - 12.0, y0, "%%C" + Format(request.BottomDiameterMm));
        if (topHalf > 0.0)
        {
            HorizontalDimension(dxf, ex - topHalf, ex + topHalf, yTop + 12.0, yTop, "%%C" + Format(request.TopDiameterMm));
        }

        VerticalDimension(dxf, y0, yTop, ex + bottomHalf + 14.0, ex + bottomHalf, Format(request.HeightMm));

        Text(dxf, ex - bottomHalf, y0 - 36.0, 3.5, "AFTER BENDING", TextLayer);
    }

    private static SectorBounds MeasureSector(double outerRadius, double innerRadius, double startAngle, double endAngle)
    {
        var minX = 0.0;
        var maxX = 0.0;
        var minY = 0.0;
        var maxY = 0.0;
        var first = true;

        void Include(double x, double y)
        {
            if (first)
            {
                minX = maxX = x;
                minY = maxY = y;
                first = false;
                return;
            }

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        var steps = Math.Max(2, (int)Math.Ceiling(endAngle - startAngle));
        for (var i = 0; i <= steps; i++)
        {
            var radians = (startAngle + (endAngle - startAngle) * i / steps) * Math.PI / 180.0;
            Include(outerRadius * Math.Cos(radians), outerRadius * Math.Sin(radians));
            Include(innerRadius * Math.Cos(radians), innerRadius * Math.Sin(radians));
        }

        return new SectorBounds(minX, minY, maxX - minX, maxY - minY);
    }

    private static double ChooseScale(ConeFlatPatternRequest request, SectorBounds bounds, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - 2.0 * ViewMargin - 2.0 * BomRowHeight - 10.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * ViewMargin - 70.0 - 60.0;

        foreach (var divisor in StandardScales)
        {
            var fitsHeight = Math.Max(bounds.Height, request.HeightMm) / divisor <= availableHeight;
            var fitsWidth = (bounds.Width + request.BottomDiameterMm) / divisor <= availableWidth;
            if (fitsHeight && fitsWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void HorizontalDimension(DxfFile dxf, double xa, double xb, double yDim, double yFeature, string text)
    {
        var sign = yDim >= yFeature ? 1.0 : -1.0;
        Line(dxf, xa, yFeature + sign * 1.5, xa, yDim + sign * 2.0, DimLayer);
        Line(dxf, xb, yFeature + sign * 1.5, xb, yDim + sign * 2.0, DimLayer);
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

    private static void DrawBom(DxfFile dxf, SheetRules sheet, ConeFlatPatternRequest request, SheetFrameResult area, SectorBounds bounds, double rawWeight, double finishWeight)
    {
        var headers = sheet.BomColumns.Count == 4
            ? sheet.BomColumns
            : new List<string> { "ITEM", "RM SIZE", "RM WT.", "FINISH WT." };

        var widths = new[] { 20.0, 55.0, 25.0, 30.0 };
        var x = area.DrawingMaxX - widths.Sum();
        var yTop = area.DrawingMaxY;
        var yMid = yTop - BomRowHeight;
        var yBottom = yMid - BomRowHeight;

        var rawWidth = Math.Ceiling(bounds.Width + request.RawMaterialAllowanceMm);
        var rawHeight = Math.Ceiling(bounds.Height + request.RawMaterialAllowanceMm);

        var values = new[]
        {
            request.ItemNo.ToString(CultureInfo.InvariantCulture),
            Format(rawWidth) + " X " + Format(rawHeight) + " X " + Format(request.ThicknessMm),
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

    private static List<string> FormatCallout(SheetRules sheet, ConeFlatPatternRequest request, double weightKg)
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

    private readonly record struct SectorBounds(double MinX, double MinY, double Width, double Height);
}