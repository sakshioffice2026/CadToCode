using System.Globalization;
using SkiaSharp;

namespace CadToCode.Business.Services;

public enum PdfPaperSize
{
    A4Landscape = 0,
    A3Landscape = 1
}

public sealed record PdfSheetMetadata(
    string PartName,
    string? ProjectName,
    string? DrawingNumber,
    string? Material,
    DateTime? Date = null,
    string? Layer = null);

public interface IPdfDrawingSheetService
{
    byte[] Render(DxfParseResult geometry, PdfSheetMetadata metadata, PdfPaperSize paperSize = PdfPaperSize.A3Landscape);

    void RenderToFile(DxfParseResult geometry, PdfSheetMetadata metadata, string filePath, PdfPaperSize paperSize = PdfPaperSize.A3Landscape);
}

public sealed class PdfDrawingSheetService : IPdfDrawingSheetService
{
    private const float Mm = 72f / 25.4f;

    private static readonly double[] StandardScales =
    {
        50, 20, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001
    };

    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;

    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) ?? SKTypeface.Default;

    public byte[] Render(DxfParseResult geometry, PdfSheetMetadata metadata, PdfPaperSize paperSize = PdfPaperSize.A3Landscape)
    {
        if (geometry is null)
        {
            throw new ArgumentNullException(nameof(geometry));
        }

        if (metadata is null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        var pageWidth = (paperSize == PdfPaperSize.A4Landscape ? 297f : 420f) * Mm;
        var pageHeight = (paperSize == PdfPaperSize.A4Landscape ? 210f : 297f) * Mm;

        using var stream = new MemoryStream();

        using (var document = SKDocument.CreatePdf(stream))
        {
            using (var canvas = document.BeginPage(pageWidth, pageHeight))
            {
                DrawSheet(canvas, pageWidth, pageHeight, geometry, metadata, paperSize);
            }

            document.EndPage();
            document.Close();
        }

        return stream.ToArray();
    }

    public void RenderToFile(DxfParseResult geometry, PdfSheetMetadata metadata, string filePath, PdfPaperSize paperSize = PdfPaperSize.A3Landscape)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        var folder = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(filePath, Render(geometry, metadata, paperSize));
    }

    private static void DrawSheet(
        SKCanvas canvas,
        float pageWidth,
        float pageHeight,
        DxfParseResult geometry,
        PdfSheetMetadata metadata,
        PdfPaperSize paperSize)
    {
        canvas.Clear(SKColors.White);

        var frame = new SKRect(10f * Mm, 10f * Mm, pageWidth - 10f * Mm, pageHeight - 10f * Mm);

        using (var border = Stroke(1.5f, SKColors.Black))
        {
            canvas.DrawRect(frame, border);
        }

        var blockHeight = 40f * Mm;
        var blockWidth = Math.Min(180f * Mm, frame.Width * 0.6f);
        var blockLeft = frame.Right - blockWidth;
        var blockTop = frame.Bottom - blockHeight;

        var drawingArea = new SKRect(
            frame.Left + 5f * Mm,
            frame.Top + 5f * Mm,
            frame.Right - 5f * Mm,
            blockTop - 5f * Mm);

        var scaleLabel = DrawGeometry(canvas, drawingArea, geometry);

        DrawTitleBlock(canvas, new SKRect(blockLeft, blockTop, frame.Right, frame.Bottom), metadata, scaleLabel);
        DrawDetailsBox(canvas, new SKRect(frame.Left, blockTop, blockLeft, frame.Bottom), geometry, metadata, paperSize);
    }

    private static string DrawGeometry(SKCanvas canvas, SKRect area, DxfParseResult geometry)
    {
        if (geometry.IsEmpty)
        {
            using var note = Text(12f, false);
            const string message = "No drawable geometry (lines, circles or arcs) was found.";
            canvas.DrawText(message, area.MidX - note.MeasureText(message) / 2f, area.MidY, note);
            return "N/A";
        }

        var unit = UnitToMm(geometry.Units);
        var geometryWidth = geometry.Width * unit;
        var geometryHeight = geometry.Height * unit;

        var reserve = 22f * Mm;
        var availableWidth = Math.Max(10f, area.Width - reserve);
        var availableHeight = Math.Max(10f, area.Height - reserve);

        var fit = Math.Min(
            availableWidth / Math.Max(geometryWidth, 1e-6),
            availableHeight / Math.Max(geometryHeight, 1e-6)) / Mm;

        var factor = StandardScales[StandardScales.Length - 1];

        foreach (var candidate in StandardScales)
        {
            if (candidate <= fit)
            {
                factor = candidate;
                break;
            }
        }

        var scale = (float)(factor * Mm);
        var drawnWidth = (float)(geometryWidth * scale);
        var drawnHeight = (float)(geometryHeight * scale);
        var originX = area.Left + (availableWidth - drawnWidth) / 2f;
        var originY = area.Top + (availableHeight - drawnHeight) / 2f;
        var minX = geometry.MinX;
        var minY = geometry.MinY;

        float X(double x) => originX + (float)((x - minX) * unit * scale);
        float Y(double y) => originY + drawnHeight - (float)((y - minY) * unit * scale);

        using (var line = Stroke(1.0f, SKColors.Black))
        {
            foreach (var l in geometry.Lines ?? Array.Empty<DxfLineData>())
            {
                canvas.DrawLine(X(l.X1), Y(l.Y1), X(l.X2), Y(l.Y2), line);
            }

            foreach (var c in geometry.Circles ?? Array.Empty<DxfCircleData>())
            {
                canvas.DrawCircle(X(c.CenterX), Y(c.CenterY), (float)(c.Radius * unit * scale), line);
            }

            foreach (var a in geometry.Arcs ?? Array.Empty<DxfArcData>())
            {
                var radius = (float)(a.Radius * unit * scale);
                var oval = new SKRect(
                    X(a.CenterX) - radius,
                    Y(a.CenterY) - radius,
                    X(a.CenterX) + radius,
                    Y(a.CenterY) + radius);

                using var path = new SKPath();
                path.AddArc(oval, (float)(-(a.StartAngleDegrees + a.SweepDegrees)), (float)a.SweepDegrees);
                canvas.DrawPath(path, line);
            }
        }

        using (var dimLine = Stroke(0.5f, new SKColor(90, 90, 90)))
        using (var dimText = Text(9f, false))
        {
            var dimY = originY + drawnHeight + 8f * Mm;
            var dimX = originX + drawnWidth + 8f * Mm;

            canvas.DrawLine(originX, originY + drawnHeight + 1f * Mm, originX, dimY + 1.5f * Mm, dimLine);
            canvas.DrawLine(originX + drawnWidth, originY + drawnHeight + 1f * Mm, originX + drawnWidth, dimY + 1.5f * Mm, dimLine);
            DrawDimension(canvas, originX, dimY, originX + drawnWidth, dimY, Format(geometryWidth), false, dimLine, dimText);

            canvas.DrawLine(originX + drawnWidth + 1f * Mm, originY, dimX + 1.5f * Mm, originY, dimLine);
            canvas.DrawLine(originX + drawnWidth + 1f * Mm, originY + drawnHeight, dimX + 1.5f * Mm, originY + drawnHeight, dimLine);
            DrawDimension(canvas, dimX, originY, dimX, originY + drawnHeight, Format(geometryHeight), true, dimLine, dimText);
        }

        return factor >= 1.0
            ? Format(factor) + ":1"
            : "1:" + Format(1.0 / factor);
    }

    private static void DrawDimension(
        SKCanvas canvas,
        float x1,
        float y1,
        float x2,
        float y2,
        string text,
        bool vertical,
        SKPaint line,
        SKPaint textPaint)
    {
        var tick = 1.5f * Mm;

        canvas.DrawLine(x1, y1, x2, y2, line);

        if (vertical)
        {
            canvas.DrawLine(x1 - tick, y1 + tick, x1 + tick, y1 - tick, line);
            canvas.DrawLine(x2 - tick, y2 + tick, x2 + tick, y2 - tick, line);

            canvas.Save();
            canvas.Translate(x1 - 1.5f * Mm, (y1 + y2) / 2f);
            canvas.RotateDegrees(-90f);
            canvas.DrawText(text, -textPaint.MeasureText(text) / 2f, 0f, textPaint);
            canvas.Restore();
        }
        else
        {
            canvas.DrawLine(x1 - tick, y1 + tick, x1 + tick, y1 - tick, line);
            canvas.DrawLine(x2 - tick, y2 + tick, x2 + tick, y2 - tick, line);

            canvas.DrawText(text, (x1 + x2) / 2f - textPaint.MeasureText(text) / 2f, y1 - 1.5f * Mm, textPaint);
        }
    }

    private static void DrawTitleBlock(SKCanvas canvas, SKRect block, PdfSheetMetadata metadata, string scaleLabel)
    {
        using var line = Stroke(1.0f, SKColors.Black);
        using var label = Text(6f, false);
        using var value = Text(11f, true);

        var rowHeight = block.Height / 2f;
        var column1 = block.Width * 0.5f;
        var column2 = block.Width * 0.25f;

        var date = (metadata.Date ?? DateTime.Today).ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);

        DrawCell(canvas, new SKRect(block.Left, block.Top, block.Left + column1, block.Top + rowHeight),
            "PART NAME", metadata.PartName, line, label, value);
        DrawCell(canvas, new SKRect(block.Left + column1, block.Top, block.Left + column1 + column2, block.Top + rowHeight),
            "DRAWING NO.", metadata.DrawingNumber, line, label, value);
        DrawCell(canvas, new SKRect(block.Left + column1 + column2, block.Top, block.Right, block.Top + rowHeight),
            "SCALE", scaleLabel, line, label, value);

        DrawCell(canvas, new SKRect(block.Left, block.Top + rowHeight, block.Left + column1, block.Bottom),
            "PROJECT NAME", metadata.ProjectName, line, label, value);
        DrawCell(canvas, new SKRect(block.Left + column1, block.Top + rowHeight, block.Left + column1 + column2, block.Bottom),
            "MATERIAL", metadata.Material, line, label, value);
        DrawCell(canvas, new SKRect(block.Left + column1 + column2, block.Top + rowHeight, block.Right, block.Bottom),
            "DATE", date, line, label, value);
    }

    private static void DrawDetailsBox(
        SKCanvas canvas,
        SKRect box,
        DxfParseResult geometry,
        PdfSheetMetadata metadata,
        PdfPaperSize paperSize)
    {
        using var line = Stroke(1.0f, SKColors.Black);
        using var heading = Text(7f, true);
        using var body = Text(9f, false);

        canvas.DrawRect(box, line);

        var x = box.Left + 3f * Mm;
        var y = box.Top + 5f * Mm;

        canvas.DrawText("COMPONENT DETAILS", x, y, heading);
        y += 6f * Mm;

        var unit = UnitToMm(geometry.Units);
        var lineCount = geometry.Lines?.Count ?? 0;
        var circleCount = geometry.Circles?.Count ?? 0;
        var arcCount = geometry.Arcs?.Count ?? 0;

        var rows = new[]
        {
            "Overall size: " + Format(geometry.Width * unit) + " x " + Format(geometry.Height * unit) + " mm",
            "Entities: " + lineCount + " lines, " + circleCount + " circles, " + arcCount + " arcs",
            "Layer: " + (string.IsNullOrWhiteSpace(metadata.Layer) ? "-" : metadata.Layer),
            "Sheet: " + (paperSize == PdfPaperSize.A4Landscape ? "A4" : "A3") + " landscape, all dimensions in mm"
        };

        foreach (var row in rows)
        {
            if (y > box.Bottom - 2f * Mm)
            {
                break;
            }

            canvas.DrawText(Fit(row, body, box.Width - 6f * Mm), x, y, body);
            y += 5.5f * Mm;
        }
    }

    private static void DrawCell(
        SKCanvas canvas,
        SKRect rect,
        string caption,
        string? text,
        SKPaint line,
        SKPaint captionPaint,
        SKPaint valuePaint)
    {
        canvas.DrawRect(rect, line);
        canvas.DrawText(caption, rect.Left + 2f * Mm, rect.Top + 3.5f * Mm, captionPaint);
        canvas.DrawText(Fit(text, valuePaint, rect.Width - 4f * Mm), rect.Left + 2f * Mm, rect.Bottom - 3f * Mm, valuePaint);
    }

    private static string Fit(string? text, SKPaint paint, float maxWidth)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "-" : text.Trim();

        if (paint.MeasureText(value) <= maxWidth)
        {
            return value;
        }

        while (value.Length > 1 && paint.MeasureText(value + "...") > maxWidth)
        {
            value = value.Substring(0, value.Length - 1);
        }

        return value + "...";
    }

    private static string Format(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static double UnitToMm(string? units)
    {
        switch ((units ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "inches":
                return 25.4;
            case "feet":
                return 304.8;
            case "centimeters":
                return 10.0;
            case "decimeters":
                return 100.0;
            case "meters":
                return 1000.0;
            default:
                return 1.0;
        }
    }

    private static SKPaint Stroke(float width, SKColor color)
    {
        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            Color = color,
            StrokeCap = SKStrokeCap.Round
        };
    }

    private static SKPaint Text(float size, bool bold)
    {
        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColors.Black,
            TextSize = size,
            Typeface = bold ? Bold : Regular
        };
    }
}
