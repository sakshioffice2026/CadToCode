using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one profile plate sheet. The outline is the real geometry taken from the GA drawing. All sizes are in mm.</summary>
public sealed class ProfilePlateRequest
{
    public string Name { get; init; } = "PLATE";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double ThicknessMm { get; init; }

    public IReadOnlyList<ProfileEntity> Profile { get; init; } = Array.Empty<ProfileEntity>();

    /// <summary>Added to the outline width and height to get the raw material size (assumed).</summary>
    public double RawMaterialAllowanceMm { get; init; } = 5.0;

    
    /// <summary>Finish weight in kg for one piece. When null the FINISH WT cell shows "-".</summary>
    public double? FinishWeightKg { get; init; }

    /// <summary>Outer diameter of a round outline. When the outline measures this size the raw blank is a disc, not a rectangle.</summary>
    public double? OuterDiameterMm { get; init; }

    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class ProfilePlateResult
{
    public ProfilePlateResult(DxfFile dxf, double rawWeightKg, string scale, double widthMm, double heightMm)
    {
        Dxf = dxf;
        RawWeightKg = rawWeightKg;
        Scale = scale;
        WidthMm = widthMm;
        HeightMm = heightMm;
    }

    public DxfFile Dxf { get; }

    public double RawWeightKg { get; }

    public string Scale { get; }

    public double WidthMm { get; }

    public double HeightMm { get; }
}

/// <summary>
/// Builds a complete sheet for a shaped plate from its real outline (lines, arcs, circles): frame, true geometry,
/// overall dimensions, hole notes, part callout and BOM row. The finish weight of an arbitrary outline is not
/// calculated and is shown as "-"; the raw material weight is calculated from the raw plate size.
/// </summary>
public sealed class ProfilePartDrawingBuilder
{
    private const string OutlineLayer = "OUTLINE";
    private const string CenterLayer = "CENTER";
    private const string DimLayer = "DIM";
    private const string TextLayer = "TEXT";
    private const string TableLayer = "TABLE";

    private const double ViewMargin = 36.0;
    private const double BomRowHeight = 8.0;
    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0, 100.0 };

    public ProfilePlateResult Build(ManufacturingRules rules, ProfilePlateRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Profile.Count == 0)
        {
            throw new ArgumentException("The outline has no geometry.");
        }

        if (request.ThicknessMm <= 0.0)
        {
            throw new ArgumentException("Thickness must be greater than zero.");
        }

        var extents = Extents(request.Profile);
        var widthMm = extents.MaxX - extents.MinX;
        var heightMm = extents.MaxY - extents.MinY;

        if (widthMm <= 0.0 || heightMm <= 0.0)
        {
            throw new ArgumentException("The outline has no size.");
        }

        var dxf = new DxfFile();
        foreach (var name in new[] { OutlineLayer, CenterLayer, DimLayer, TextLayer, TableLayer })
        {
            EnsureLayer(dxf, name);
        }

        var rawWidth = widthMm + request.RawMaterialAllowanceMm;
        var rawHeight = heightMm + request.RawMaterialAllowanceMm;
        var rawWeight = rules.PlateWeightKg(rawWidth * rawHeight, request.ThicknessMm);

        double? rawDiameter = null;

        if (request.OuterDiameterMm is > 0.0)
        {
            var od = request.OuterDiameterMm.Value;
            var roundTolerance = Math.Max(1.0, od * 0.01);

            if (Math.Abs(widthMm - od) <= roundTolerance && Math.Abs(heightMm - od) <= roundTolerance)
            {
                rawDiameter = od + request.RawMaterialAllowanceMm;
                rawWeight = rules.DiscWeightKg(rawDiameter.Value, request.ThicknessMm);
            }
        }

        var frameRequest = request.Frame;
        var frameBuilder = new PartSheetFrameBuilder();
        var probe = frameBuilder.Build(new DxfFile(), rules.Sheet, rules.Tolerance, frameRequest);
        var divisor = ChooseScale(widthMm, heightMm, probe);
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

        var originX = area.DrawingMinX + ViewMargin + 20.0;
        var originY = area.DrawingMinY + ViewMargin + 20.0;

        DrawProfile(dxf, request.Profile, extents, divisor, originX, originY);

        var x1 = originX + widthMm / divisor;
        var y1 = originY + heightMm / divisor;

        HorizontalDimension(dxf, originX, x1, originY - 12.0, originY, Format(Math.Round(widthMm, 1)));
        VerticalDimension(dxf, originY, y1, originX - 12.0, originX, Format(Math.Round(heightMm, 1)));

        var noteY = originY - 26.0;
        foreach (var note in FormatHoleNotes(request.Profile, Math.Max(widthMm, heightMm)))
        {
            Text(dxf, originX, noteY, 3.5, note, TextLayer);
            noteY -= 6.0;
        }

        var calloutY = y1;
        foreach (var line in FormatCallout(request, rawWeight))
        {
            Text(dxf, x1 + 20.0, calloutY, 3.5, line, TextLayer);
            calloutY -= 6.0;
        }

        DrawBom(dxf, rules.Sheet, request, area, rawWidth, rawHeight, rawWeight, rawDiameter);

        return new ProfilePlateResult(dxf, rawWeight, scaleText, widthMm, heightMm);
    }

    private static void DrawProfile(DxfFile dxf, IReadOnlyList<ProfileEntity> profile, (double MinX, double MinY, double MaxX, double MaxY) extents, double divisor, double originX, double originY)
    {
        double X(double value) => originX + (value - extents.MinX) / divisor;
        double Y(double value) => originY + (value - extents.MinY) / divisor;

        foreach (var entity in profile)
        {
            switch (entity.Kind)
            {
                case ProfileEntityKind.Line:
                    Line(dxf, X(entity.X1), Y(entity.Y1), X(entity.X2), Y(entity.Y2), OutlineLayer);
                    break;

                case ProfileEntityKind.Arc:
                    dxf.Entities.Add(new DxfArc(
                        new DxfPoint(X(entity.Cx), Y(entity.Cy), 0),
                        entity.Radius / divisor,
                        entity.StartAngle,
                        entity.EndAngle)
                    { Layer = OutlineLayer });
                    break;

                case ProfileEntityKind.Circle:
                    var cx = X(entity.Cx);
                    var cy = Y(entity.Cy);
                    var radius = entity.Radius / divisor;
                    dxf.Entities.Add(new DxfCircle(new DxfPoint(cx, cy, 0), radius) { Layer = OutlineLayer });

                    var mark = Math.Min(radius * 1.4, radius + 3.0);
                    Line(dxf, cx - mark, cy, cx + mark, cy, CenterLayer);
                    Line(dxf, cx, cy - mark, cx, cy + mark, CenterLayer);
                    break;
            }
        }
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Extents(IReadOnlyList<ProfileEntity> profile)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var entity in profile)
        {
            switch (entity.Kind)
            {
                case ProfileEntityKind.Line:
                    Include(entity.X1, entity.Y1);
                    Include(entity.X2, entity.Y2);
                    break;

                case ProfileEntityKind.Circle:
                    Include(entity.Cx - entity.Radius, entity.Cy - entity.Radius);
                    Include(entity.Cx + entity.Radius, entity.Cy + entity.Radius);
                    break;

                case ProfileEntityKind.Arc:
                    var sweep = (entity.EndAngle - entity.StartAngle) % 360.0;
                    if (sweep <= 0.0)
                    {
                        sweep += 360.0;
                    }

                    var angles = new List<double> { entity.StartAngle, entity.EndAngle };
                    foreach (var cardinal in new[] { 0.0, 90.0, 180.0, 270.0 })
                    {
                        var offset = (cardinal - entity.StartAngle) % 360.0;
                        if (offset < 0.0)
                        {
                            offset += 360.0;
                        }

                        if (offset <= sweep)
                        {
                            angles.Add(cardinal);
                        }
                    }

                    foreach (var angle in angles)
                    {
                        var radians = angle * Math.PI / 180.0;
                        Include(entity.Cx + entity.Radius * Math.Cos(radians), entity.Cy + entity.Radius * Math.Sin(radians));
                    }

                    break;
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private static double ChooseScale(double widthMm, double heightMm, SheetFrameResult area)
    {
        var availableHeight = area.DrawingMaxY - area.DrawingMinY - 2.0 * ViewMargin - 2.0 * BomRowHeight - 50.0;
        var availableWidth = area.DrawingMaxX - area.DrawingMinX - 2.0 * ViewMargin - 190.0;

        foreach (var divisor in StandardScales)
        {
            if (heightMm / divisor <= availableHeight && widthMm / divisor <= availableWidth)
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    private static List<string> FormatHoleNotes(IReadOnlyList<ProfileEntity> profile, double largestSizeMm)
    {
        var notes = new List<string>();

        foreach (var group in profile
            .Where(e => e.Kind == ProfileEntityKind.Circle && e.Radius > 0.0)
            .GroupBy(e => Math.Round(e.Radius * 2.0, 1))
            .OrderBy(g => g.Key))
        {
            notes.Add(group.Key <= largestSizeMm * 0.25
                ? group.Count().ToString(CultureInfo.InvariantCulture) + " HOLES %%C" + Format(group.Key)
                : "OPENING %%C" + Format(group.Key));
        }

        return notes;
    }

    private static List<string> FormatCallout(ProfilePlateRequest request, double rawWeightKg)
    {
        return new List<string>
        {
            Clean(request.Name.ToUpperInvariant()),
            Clean(request.Material + " " + Format(request.ThicknessMm) + " MM THK"),
            "QTY. " + request.Qty.ToString(CultureInfo.InvariantCulture),
            "RM WT- " + Format(Math.Round(rawWeightKg, 2)) + " KG."
        };
    }

    private static void DrawBom(DxfFile dxf, SheetRules sheet, ProfilePlateRequest request, SheetFrameResult area, double rawWidth, double rawHeight, double rawWeight, double? rawDiameter = null)
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
            rawDiameter.HasValue
                ? "%%C" + Format(Math.Round(rawDiameter.Value, 0)) + " X " + Format(request.ThicknessMm)
                : Format(Math.Round(rawWidth, 0)) + " X " + Format(Math.Round(rawHeight, 0)) + " X " + Format(request.ThicknessMm),
            Format(Math.Round(rawWeight, 2)),
            request.FinishWeightKg.HasValue ? Format(Math.Round(request.FinishWeightKg.Value, 2)) : "-"
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
        Line(dxf, xFeature - 1.5, ya, xDim - 2.0, ya, DimLayer);
        Line(dxf, xFeature - 1.5, yb, xDim - 2.0, yb, DimLayer);
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