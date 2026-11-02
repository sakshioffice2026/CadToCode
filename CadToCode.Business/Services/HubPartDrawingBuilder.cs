using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one hub part sheet (flange with boss and bore). All sizes are in mm.</summary>
public sealed class HubPartRequest
{
    public string Name { get; init; } = "HUB";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double FlangeDiameterMm { get; init; }

    public double FlangeThicknessMm { get; init; }

    public double BossDiameterMm { get; init; }

    /// <summary>Boss length measured from the flange face, not including the flange thickness.</summary>
    public double BossLengthMm { get; init; }

    public double BoreDiameterMm { get; init; }

    public int HoleCount { get; init; }

    public double HoleDiameterMm { get; init; }

    public double HolePcdMm { get; init; }

    /// <summary>Keyway width. Zero means no keyway.</summary>
    public double KeywayWidthMm { get; init; }

    /// <summary>Keyway depth measured from the bore surface.</summary>
    public double KeywayDepthMm { get; init; }

    /// <summary>Added to the diameter and length to get the raw material size (assumed).</summary>
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
/// Builds a complete A3 sheet for a hub: frame, front view with true circles and arcs, hatched
/// section A-A, dimensions, hole and keyway notes, part callout and BOM row.
/// </summary>
public sealed class HubPartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string HatchLayer = "HATCH";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double ViewMargin = 42.0;
    private const double BomRowHeight = 8.0;
    private const double HatchSpacing = 3.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public HubPartResult Build(ManufacturingRules rules, HubPartRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.FlangeDiameterMm <= 0.0 || request.FlangeThicknessMm <= 0.0 ||
            request.BossDiameterMm <= 0.0 || request.BossLengthMm <= 0.0 || request.BoreDiameterMm <= 0.0)
        {
            throw new ArgumentException("Flange, boss and bore sizes must be greater than zero.");
        }

        if (request.BoreDiameterMm >= request.BossDiameterMm || request.BossDiameterMm > request.FlangeDiameterMm)
        {
            throw new ArgumentException("Bore must be smaller than the boss, and the boss must not exceed the flange.");
        }

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, HatchLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var totalLength = request.FlangeThicknessMm + request.BossLengthMm;
        var finishWeight = CalculateFinishWeight(rules, request, totalLength);
        var rawWeight = rules.DiscWeightKg(
            request.FlangeDiameterMm + request.RawMaterialAllowanceMm,
            totalLength + request.RawMaterialAllowanceMm);

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var divisor = ChooseScale(request, totalLength, probe);
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

        var flangeR = request.FlangeDiameterMm / 2.0 / divisor;
        var cx = area.DrawingMinX + ViewMargin + flangeR;
        var cy = area.DrawingMinY + ViewMargin + flangeR;

        DrawFrontView(dxf, request, divisor, cx, cy);

        var sectionX0 = cx + flangeR + 60.0;
        var sectionX1 = DrawSection(dxf, request, divisor, sectionX0, cy);
        DrawSectionMarks(dxf, cx, cy, flangeR);
        DrawDimensions(dxf, request, divisor, cx, cy, sectionX0, sectionX1);

        var noteY = cy - flangeR - 26.0;
        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0 && request.HolePcdMm > 0.0)
        {
            Text(dxf, cx - flangeR, noteY, 3.5, FormatHoleNote(rules.Sheet, request), TextLayer);
            noteY -= 7.0;
        }

        if (request.KeywayWidthMm > 0.0 && request.KeywayDepthMm > 0.0)
        {
            Text(dxf, cx - flangeR, noteY, 3.5,
                "KEYWAY " + Format(request.KeywayWidthMm) + " X " + Format(request.KeywayDepthMm) + " DEEP", TextLayer);
        }

        var calloutY = cy + flangeR;
        foreach (var line in FormatCallout(rules.Sheet, request, finishWeight))
        {
            Text(dxf, sectionX1 + 50.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, totalLength, rawWeight, finishWeight);

        return new HubPartResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static double CalculateFinishWeight(ManufacturingRules rules, HubPartRequest request, double totalLength)
    {
        var weight = rules.RingWeightKg(request.FlangeDiameterMm, request.BoreDiameterMm, request.FlangeThicknessMm)
            + rules.RingWeightKg(request.BossDiameterMm, request.BoreDiameterMm, request.BossLengthMm);

        if (request.HoleCount > 0 && request.HoleDiameterMm > 0.0)
        {
            var holeArea = request.HoleCount * Math.PI / 4.0 * request.HoleDiameterMm * request.HoleDiameterMm;
            weight -= rules.PlateWeightKg(holeArea, request.FlangeThicknessMm);
        }

        if (request.KeywayWidthMm > 0.0 && request.KeywayDepthMm > 0.0)
        {
            weight -= rules.PlateWeightKg(request.KeywayWidthMm * request.KeywayDepthMm, totalLength);
        }

        return Math.Max(weight, 0.0);
    }

    private static double ChooseScale(HubPartRequest request, double totalLength, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - 2.0 * ViewMargin - 2.0 * BomRowHeight - 10.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * ViewMargin - 60.0 - 50.0 - 60.0;

        foreach (var divisor in StandardScales)
        {
            var fitsHeight = request.FlangeDiameterMm / divisor <= availableHeight;
            var fitsWidth = (request.FlangeDiameterMm + totalLength) / divisor <= availableWidth;
            if (fitsHeight && fitsWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static void DrawFrontView(DxfFile dxf, HubPartRequest request, double divisor, double cx, double cy)
    {
        var flangeR = request.FlangeDiameterMm / 2.0 / divisor;
        var bossR = request.BossDiameterMm / 2.0 / divisor;
        var boreR = request.BoreDiameterMm / 2.0 / divisor;

        Circle(dxf, cx, cy, flangeR, OutlineLayer);
        Circle(dxf, cx, cy, bossR, OutlineLayer);

        if (request.KeywayWidthMm > 0.0 && request.KeywayDepthMm > 0.0 && request.KeywayWidthMm < request.BoreDiameterMm)
        {
            var halfWidth = request.KeywayWidthMm / 2.0 / divisor;
            var depth = request.KeywayDepthMm / divisor;
            var halfAngle = Math.Asin(halfWidth / boreR) * 180.0 / Math.PI;
            var rootX = Math.Sqrt(boreR * boreR - halfWidth * halfWidth);

            dxf.Entities.Add(new DxfArc(new DxfPoint(cx, cy, 0), boreR, 90.0 + halfAngle, 90.0 - halfAngle + 360.0)
            {
                Layer = OutlineLayer
            });

            Line(dxf, cx - halfWidth, cy + rootX, cx - halfWidth, cy + boreR + depth, OutlineLayer);
            Line(dxf, cx + halfWidth, cy + rootX, cx + halfWidth, cy + boreR + depth, OutlineLayer);
            Line(dxf, cx - halfWidth, cy + boreR + depth, cx + halfWidth, cy + boreR + depth, OutlineLayer);
        }
        else
        {
            Circle(dxf, cx, cy, boreR, OutlineLayer);
        }

        const double extend = 6.0;
        Line(dxf, cx - flangeR - extend, cy, cx + flangeR + extend, cy, CenterLayer);
        Line(dxf, cx, cy - flangeR - extend, cx, cy + flangeR + extend, CenterLayer);

        if (request.HoleCount <= 0 || request.HoleDiameterMm <= 0.0 || request.HolePcdMm <= 0.0)
        {
            return;
        }

        var pcdR = request.HolePcdMm / 2.0 / divisor;
        var holeR = request.HoleDiameterMm / 2.0 / divisor;
        Circle(dxf, cx, cy, pcdR, CenterLayer);

        for (var i = 0; i < request.HoleCount; i++)
        {
            var angle = Math.PI / 2.0 + 2.0 * Math.PI * i / request.HoleCount;
            var hx = cx + pcdR * Math.Cos(angle);
            var hy = cy + pcdR * Math.Sin(angle);
            Circle(dxf, hx, hy, holeR, OutlineLayer);

            var mark = Math.Min(holeR * 1.4, holeR + 3.0);
            Line(dxf, hx - mark, hy, hx + mark, hy, CenterLayer);
            Line(dxf, hx, hy - mark, hx, hy + mark, CenterLayer);
        }
    }

    private static double DrawSection(DxfFile dxf, HubPartRequest request, double divisor, double x0, double cy)
    {
        var flangeR = request.FlangeDiameterMm / 2.0 / divisor;
        var bossR = request.BossDiameterMm / 2.0 / divisor;
        var boreR = request.BoreDiameterMm / 2.0 / divisor;
        var flangeT = request.FlangeThicknessMm / divisor;
        var bossL = request.BossLengthMm / divisor;

        var xFlange = x0 + flangeT;
        var xEnd = xFlange + bossL;

        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var yBore = cy + sign * boreR;
            var yBoss = cy + sign * bossR;
            var yFlange = cy + sign * flangeR;

            Line(dxf, x0, yBore, x0, yFlange, OutlineLayer);
            Line(dxf, x0, yFlange, xFlange, yFlange, OutlineLayer);
            Line(dxf, xFlange, yFlange, xFlange, yBoss, OutlineLayer);
            Line(dxf, xFlange, yBoss, xEnd, yBoss, OutlineLayer);
            Line(dxf, xEnd, yBoss, xEnd, yBore, OutlineLayer);
            Line(dxf, x0, yBore, xEnd, yBore, OutlineLayer);

            HatchRectangle(dxf, x0, Math.Min(yBore, yFlange), xFlange, Math.Max(yBore, yFlange));
            HatchRectangle(dxf, xFlange, Math.Min(yBore, yBoss), xEnd, Math.Max(yBore, yBoss));
        }

        Line(dxf, x0 - 6.0, cy, xEnd + 6.0, cy, CenterLayer);
        return xEnd;
    }

    private static void DrawSectionMarks(DxfFile dxf, double cx, double cy, double flangeR)
    {
        Line(dxf, cx - flangeR - 10.0, cy, cx - flangeR - 6.0, cy, CenterLayer);
        Line(dxf, cx + flangeR + 6.0, cy, cx + flangeR + 10.0, cy, CenterLayer);
        Text(dxf, cx - flangeR - 16.0, cy - 1.75, 3.5, "A", TextLayer);
        Text(dxf, cx + flangeR + 12.0, cy - 1.75, 3.5, "A", TextLayer);
    }

    private static void HatchRectangle(DxfFile dxf, double x0, double y0, double x1, double y1)
    {
        var cMin = y0 - x1;
        var cMax = y1 - x0;
        for (var c = cMin + HatchSpacing; c < cMax; c += HatchSpacing)
        {
            var startX = Math.Max(x0, y0 - c);
            var endX = Math.Min(x1, y1 - c);
            if (endX <= startX)
            {
                continue;
            }

            Line(dxf, startX, startX + c, endX, endX + c, HatchLayer);
        }
    }

    private static void DrawDimensions(DxfFile dxf, HubPartRequest request, double divisor, double cx, double cy, double x0, double xEnd)
    {
        var flangeR = request.FlangeDiameterMm / 2.0 / divisor;
        var bossR = request.BossDiameterMm / 2.0 / divisor;
        var boreR = request.BoreDiameterMm / 2.0 / divisor;
        var xFlange = x0 + request.FlangeThicknessMm / divisor;

        HorizontalDimension(dxf, cx - flangeR, cx + flangeR, cy - flangeR - 12.0, cy - flangeR, "%%C" + Format(request.FlangeDiameterMm));

        var baseY = cy - flangeR;
        HorizontalDimension(dxf, x0, xFlange, baseY - 12.0, baseY, Format(request.FlangeThicknessMm));
        HorizontalDimension(dxf, xFlange, xEnd, baseY - 12.0, baseY, Format(request.BossLengthMm));
        HorizontalDimension(dxf, x0, xEnd, baseY - 24.0, baseY, Format(request.FlangeThicknessMm + request.BossLengthMm));

        VerticalDimension(dxf, cy - boreR, cy + boreR, xEnd + 14.0, xEnd, "%%C" + Format(request.BoreDiameterMm));
        VerticalDimension(dxf, cy - bossR, cy + bossR, xEnd + 26.0, xEnd, "%%C" + Format(request.BossDiameterMm));
        VerticalDimension(dxf, cy - flangeR, cy + flangeR, xEnd + 38.0, xFlange, "%%C" + Format(request.FlangeDiameterMm));

        Text(dxf, x0, baseY - 36.0, 3.5, "SECTION A-A", TextLayer);
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

    private static void DrawBom(DxfFile dxf, SheetRules sheet, HubPartRequest request, SheetFrameResult area, double totalLength, double rawWeight, double finishWeight)
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
            "%%C" + Format(request.FlangeDiameterMm + request.RawMaterialAllowanceMm) + " X " + Format(totalLength + request.RawMaterialAllowanceMm),
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
            .Replace("{THICKNESS}", Format(request.FlangeThicknessMm))
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