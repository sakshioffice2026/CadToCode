using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Shared drawing helpers for the part sheet builders.</summary>
public static class SheetDrawingKit
{
    public const string OutlineLayer = "OUTLINE";
    public const string CenterLayer = "CENTER";
    public const string DimLayer = "DIM";
    public const string TextLayer = "TEXT";
    public const string TableLayer = "TABLE";
    public const string FoldLayer = "FOLD";

    private static readonly double[] StandardScales = { 1.0, 2.0, 2.5, 4.0, 5.0, 10.0, 20.0, 25.0, 50.0 };

    public static void EnsureLayers(DxfFile dxf, params string[] names)
    {
        foreach (var name in names)
        {
            if (!dxf.Layers.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                dxf.Layers.Add(new DxfLayer(name));
            }
        }
    }

    /// <summary>Returns the first standard scale divisor for which the layout fits.</summary>
    public static double ChooseScale(Func<double, bool> fits)
    {
        foreach (var divisor in StandardScales)
        {
            if (fits(divisor))
            {
                return divisor;
            }
        }

        return StandardScales[^1];
    }

    public static string ScaleText(double divisor)
    {
        return divisor <= 1.0 ? "1:1" : "1:" + Format(divisor);
    }

    public static SheetFrameRequest WithScale(SheetFrameRequest source, string scale)
    {
        return new SheetFrameRequest
        {
            JobNo = source.JobNo,
            DrawingNo = source.DrawingNo,
            Revision = source.Revision,
            Title = source.Title,
            ModelNo = source.ModelNo,
            Scale = scale,
            Designed = source.Designed,
            Drawn = source.Drawn,
            Checked = source.Checked,
            Approved = source.Approved,
            Date = source.Date,
            IncludeToleranceTable = source.IncludeToleranceTable
        };
    }

    public static SheetFrameResult Frame(DxfFile dxf, ManufacturingRules rules, SheetFrameRequest frame)
    {
        return new PartSheetFrameBuilder().Build(dxf, rules.Sheet, rules.Tolerance, frame);
    }

    public static void Line(DxfFile dxf, double x0, double y0, double x1, double y1, string layer)
    {
        dxf.Entities.Add(new DxfLine(new DxfPoint(x0, y0, 0), new DxfPoint(x1, y1, 0)) { Layer = layer });
    }

    public static void Rectangle(DxfFile dxf, double x0, double y0, double x1, double y1, string layer)
    {
        Line(dxf, x0, y0, x1, y0, layer);
        Line(dxf, x1, y0, x1, y1, layer);
        Line(dxf, x1, y1, x0, y1, layer);
        Line(dxf, x0, y1, x0, y0, layer);
    }

    public static void Circle(DxfFile dxf, double cx, double cy, double radius, string layer)
    {
        dxf.Entities.Add(new DxfCircle(new DxfPoint(cx, cy, 0), radius) { Layer = layer });
    }

    public static void Text(DxfFile dxf, double x, double y, double height, string value, string layer)
    {
        dxf.Entities.Add(new DxfText(new DxfPoint(x, y, 0), height, value) { Layer = layer });
    }

    public static string Format(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string Clean(string? value)
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

    public static string Fit(string? value, double width, double textHeight)
    {
        var text = Clean(value);
        var max = Math.Max(1, (int)(width / (textHeight * 0.65)));
        return text.Length <= max ? text : text[..max];
    }

    public static List<string> Callout(SheetRules sheet, string name, string material, double thicknessMm, int qty, double weightKg)
    {
        var format = string.IsNullOrWhiteSpace(sheet.PartCalloutFormat)
            ? "{NAME}\\n{MATERIAL} {THICKNESS} MM THK\\nQTY. {QTY}\\nWT- {WEIGHT} KG."
            : sheet.PartCalloutFormat;

        var text = format
            .Replace("{NAME}", name.ToUpperInvariant())
            .Replace("{MATERIAL}", material)
            .Replace("{THICKNESS}", Format(thicknessMm))
            .Replace("{QTY}", qty.ToString(CultureInfo.InvariantCulture))
            .Replace("{WEIGHT}", Format(Math.Round(weightKg, 2)))
            .Replace("\\n", "\n")
            .Replace("\r", string.Empty);

        return text.Split('\n').Select(Clean).Where(line => line.Length > 0).ToList();
    }

    public static void DrawCallout(DxfFile dxf, List<string> lines, double x, double yTop)
    {
        var y = yTop;
        foreach (var line in lines)
        {
            Text(dxf, x, y, 3.5, line, TextLayer);
            y -= 6.0;
        }
    }

    public static void Bom(DxfFile dxf, SheetRules sheet, SheetFrameResult area, int itemNo, string rmSize, double rawWeightKg, double finishWeightKg)
    {
        var headers = sheet.BomColumns.Count == 4
            ? sheet.BomColumns
            : new List<string> { "ITEM", "RM SIZE", "RM WT.", "FINISH WT." };

        var widths = new[] { 20.0, 55.0, 25.0, 30.0 };
        var x = area.DrawingMaxX - widths.Sum();
        const double rowHeight = 8.0;
        var yTop = area.DrawingMaxY;
        var yMid = yTop - rowHeight;
        var yBottom = yMid - rowHeight;

        var values = new[]
        {
            itemNo.ToString(CultureInfo.InvariantCulture),
            rmSize,
            Format(Math.Round(rawWeightKg, 2)),
            Format(Math.Round(finishWeightKg, 2))
        };

        for (var i = 0; i < 4; i++)
        {
            var next = x + widths[i];
            Cell(dxf, x, yMid, next, yTop, headers[i], 3.0);
            Cell(dxf, x, yBottom, next, yMid, values[i], 3.0);
            x = next;
        }
    }

    public static void Cell(DxfFile dxf, double x0, double y0, double x1, double y1, string text, double textHeight)
    {
        Rectangle(dxf, x0, y0, x1, y1, TableLayer);
        if (!string.IsNullOrEmpty(text))
        {
            Text(dxf, x0 + 1.5, y0 + (y1 - y0 - textHeight) / 2.0, textHeight, Fit(text, x1 - x0 - 3.0, textHeight), TableLayer);
        }
    }
}
