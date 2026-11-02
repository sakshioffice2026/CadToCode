using System.Globalization;
using System.Text;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>Values printed in the title block of a part sheet.</summary>
public sealed class SheetFrameRequest
{
    public string JobNo { get; init; } = string.Empty;

    public string DrawingNo { get; init; } = string.Empty;

    public string Revision { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string ModelNo { get; init; } = string.Empty;

    public string Scale { get; init; } = string.Empty;

    public string Designed { get; init; } = string.Empty;

    public string Drawn { get; init; } = string.Empty;

    public string Checked { get; init; } = string.Empty;

    public string Approved { get; init; } = string.Empty;

    public string Date { get; init; } = string.Empty;

    public bool IncludeToleranceTable { get; init; } = true;
}

/// <summary>Free area inside the sheet frame where views and callouts can be placed.</summary>
public sealed record SheetFrameResult(double DrawingMinX, double DrawingMinY, double DrawingMaxX, double DrawingMaxY);

/// <summary>
/// Draws the sheet frame of a part drawing: border with zone marks, top strip with notes and scale,
/// title block, revision table and the IS 2102 tolerance table. Layout and wording follow the sample
/// drawings (see manufacturing-rules.json). Only the A3 size is supported.
/// </summary>
public sealed class PartSheetFrameBuilder
{
    private const double SheetWidth = 420.0;
    private const double SheetHeight = 297.0;
    private const double OuterMargin = 5.0;
    private const double InnerMargin = 12.0;

    private const string BorderLayer = "BORDER";
    private const string TitleLayer = "TITLE";
    private const string TableLayer = "TABLE";

    public SheetFrameResult Build(DxfFile dxf, SheetRules rules, ToleranceRules tolerance, SheetFrameRequest request)
    {
        ArgumentNullException.ThrowIfNull(dxf);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(rules.Size, "A3", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only the A3 sheet size is supported.");
        }

        EnsureLayer(dxf, BorderLayer);
        EnsureLayer(dxf, TitleLayer);
        EnsureLayer(dxf, TableLayer);

        var innerX0 = InnerMargin;
        var innerY0 = InnerMargin;
        var innerX1 = SheetWidth - InnerMargin;
        var innerY1 = SheetHeight - InnerMargin;

        Rectangle(dxf, OuterMargin, OuterMargin, SheetWidth - OuterMargin, SheetHeight - OuterMargin, BorderLayer);
        Rectangle(dxf, innerX0, innerY0, innerX1, innerY1, BorderLayer);

        DrawZones(dxf, rules.BorderZones, innerX0, innerY0, innerX1, innerY1);

        var topStripBottom = innerY1 - 15.0;
        DrawTopStrip(dxf, rules, request, innerX0, topStripBottom, innerX1, innerY1);

        var blockWidth = 190.0;
        var blockX0 = innerX1 - blockWidth;
        var blockTop = DrawTitleBlock(dxf, rules, request, blockX0, innerY0, innerX1);
        var revisionTop = DrawRevisionTable(dxf, blockX0, blockTop, innerX1);

        if (request.IncludeToleranceTable && tolerance.Classes.Count > 0)
        {
            DrawToleranceTable(dxf, tolerance, innerX0, innerY0);
        }

        var areaMinY = Math.Max(revisionTop, innerY0 + 36.0) + 5.0;
        return new SheetFrameResult(innerX0 + 5.0, areaMinY, innerX1 - 5.0, topStripBottom - 5.0);
    }

    private static void DrawZones(DxfFile dxf, BorderZones zones, double x0, double y0, double x1, double y1)
    {
        var columnLabels = zones.ColumnLabels.Count > 0 ? zones.ColumnLabels : new List<string> { "6", "5", "4", "3", "2", "1" };
        var rowLabels = zones.RowLabels.Count > 0 ? zones.RowLabels : new List<string> { "F", "E", "D", "C", "B", "A" };

        var columnWidth = (x1 - x0) / columnLabels.Count;
        var rowHeight = (y1 - y0) / rowLabels.Count;
        const double textHeight = 3.0;

        for (var i = 0; i < columnLabels.Count; i++)
        {
            var centre = x0 + columnWidth * (i + 0.5);
            var label = Clean(columnLabels[i]);
            var textX = centre - textHeight * 0.35 * label.Length;

            Text(dxf, textX, OuterMargin + (InnerMargin - OuterMargin - textHeight) / 2.0, textHeight, label, BorderLayer);
            Text(dxf, textX, y1 + (InnerMargin - OuterMargin - textHeight) / 2.0, textHeight, label, BorderLayer);

            if (i > 0)
            {
                var x = x0 + columnWidth * i;
                Line(dxf, x, OuterMargin, x, InnerMargin, BorderLayer);
                Line(dxf, x, y1, x, SheetHeight - OuterMargin, BorderLayer);
            }
        }

        for (var i = 0; i < rowLabels.Count; i++)
        {
            var centre = y1 - rowHeight * (i + 0.5);
            var label = Clean(rowLabels[i]);
            var textY = centre - textHeight / 2.0;

            Text(dxf, OuterMargin + (InnerMargin - OuterMargin - textHeight * 0.7) / 2.0, textY, textHeight, label, BorderLayer);
            Text(dxf, x1 + (InnerMargin - OuterMargin - textHeight * 0.7) / 2.0, textY, textHeight, label, BorderLayer);

            if (i > 0)
            {
                var y = y1 - rowHeight * i;
                Line(dxf, OuterMargin, y, InnerMargin, y, BorderLayer);
                Line(dxf, x1, y, SheetWidth - OuterMargin, y, BorderLayer);
            }
        }
    }

    private static void DrawTopStrip(DxfFile dxf, SheetRules rules, SheetFrameRequest request, double x0, double y0, double x1, double y1)
    {
        Line(dxf, x0, y0, x1, y0, TitleLayer);

        var notesEnd = x0 + 128.0;
        var scaleStart = x1 - 108.0;
        var doNotScale = rules.Notes.FirstOrDefault(n => n.Contains("DO NOT SCALE", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var askIfInDoubt = rules.Notes.FirstOrDefault(n => n.Contains("ASK", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        var leftNotes = rules.Notes
            .Where(n => !string.Equals(n, doNotScale, StringComparison.Ordinal) && !string.Equals(n, askIfInDoubt, StringComparison.Ordinal))
            .Take(2)
            .ToList();

        var middleX = notesEnd + (scaleStart - notesEnd) / 2.0;

        Line(dxf, notesEnd, y0, notesEnd, y1, TitleLayer);
        Line(dxf, middleX, y0, middleX, y1, TitleLayer);
        Line(dxf, scaleStart, y0, scaleStart, y1, TitleLayer);

        for (var i = 0; i < leftNotes.Count; i++)
        {
            Text(dxf, x0 + 1.5, y1 - 5.5 - i * 5.5, 2.5, Clean(leftNotes[i]), TitleLayer);
        }

        Text(dxf, notesEnd + 3.0, y0 + 5.5, 4.0, Clean(doNotScale), TitleLayer);
        Text(dxf, middleX + 3.0, y0 + 5.5, 3.5, Clean(askIfInDoubt), TitleLayer);

        var scale = FirstNonBlank(request.Scale, rules.DefaultValues.GetValueOrDefault("scale"));
        Text(dxf, scaleStart + 3.0, y0 + 5.5, 4.0, "SCALE:- " + Clean(scale), TitleLayer);
    }

    /// <returns>The Y coordinate of the top edge of the title block.</returns>
    private static double DrawTitleBlock(DxfFile dxf, SheetRules rules, SheetFrameRequest request, double x0, double y0, double x1)
    {
        var revision = FirstNonBlank(request.Revision, rules.DefaultValues.GetValueOrDefault("revision"));
        var designed = FirstNonBlank(request.Designed, rules.DefaultValues.GetValueOrDefault("designed"));
        var drawn = FirstNonBlank(request.Drawn, rules.DefaultValues.GetValueOrDefault("drawn"));
        var checkedBy = FirstNonBlank(request.Checked, rules.DefaultValues.GetValueOrDefault("checked"));
        var approved = FirstNonBlank(request.Approved, rules.DefaultValues.GetValueOrDefault("approved"));
        var date = Clean(request.Date);

        var split = x0 + 112.0;

        // Row 1: company | drawing number
        var y = y0;
        Cell(dxf, x0, y, split, y + 12.0, Clean(rules.Company), 4.0, TitleLayer);
        Cell(dxf, split, y, x1, y + 12.0, "Drg No.  " + Clean(request.DrawingNo), 3.0, TitleLayer);

        // Row 2: address | revision
        y += 12.0;
        Cell(dxf, x0, y, split, y + 10.0, Clean(rules.Address), 3.0, TitleLayer);
        Cell(dxf, split, y, x1, y + 10.0, "Rev. No.  " + Clean(revision), 3.0, TitleLayer);

        // Row 3: model | job
        y += 10.0;
        Cell(dxf, x0, y, split, y + 10.0, "Model No:- " + Clean(request.ModelNo), 3.0, TitleLayer);
        Cell(dxf, split, y, x1, y + 10.0, "JOB NO :- " + Clean(request.JobNo), 3.0, TitleLayer);

        // Row 4: title
        y += 10.0;
        Cell(dxf, x0, y, x1, y + 10.0, "TITLE:- " + Clean(request.Title), 3.5, TitleLayer);

        // Row 5: designed, drawn, checked, approved
        y += 10.0;
        var columnWidth = (x1 - x0) / 4.0;
        var people = new[]
        {
            "DESIGNED :- " + Clean(designed),
            "DRAWN :- " + Clean(drawn),
            "CHECKED :- " + Clean(checkedBy),
            "APPROVED :- " + Clean(approved)
        };

        for (var i = 0; i < 4; i++)
        {
            Cell(dxf, x0 + columnWidth * i, y, x0 + columnWidth * (i + 1), y + 12.0, people[i], 2.5, TitleLayer);
        }

        // Row 6: dates
        y += 12.0;
        for (var i = 0; i < 4; i++)
        {
            Cell(dxf, x0 + columnWidth * i, y, x0 + columnWidth * (i + 1), y + 10.0, "Date :- " + date, 2.5, TitleLayer);
        }

        return y + 10.0;
    }

    /// <returns>The Y coordinate of the top edge of the revision table.</returns>
    private static double DrawRevisionTable(DxfFile dxf, double x0, double y0, double x1)
    {
        var width = x1 - x0;
        var columns = new[] { 0.0, 0.14, 0.30, 0.46, 0.64, 1.0 };
        var headers = new[] { "Rev. No.", "Date", "Done By", "Approved By", "Revision Details" };

        for (var i = 0; i < headers.Length; i++)
        {
            Cell(dxf, x0 + width * columns[i], y0, x0 + width * columns[i + 1], y0 + 9.0, headers[i], 2.5, TableLayer);
            Cell(dxf, x0 + width * columns[i], y0 + 9.0, x0 + width * columns[i + 1], y0 + 18.0, string.Empty, 2.5, TableLayer);
        }

        return y0 + 18.0;
    }

    private static void DrawToleranceTable(DxfFile dxf, ToleranceRules tolerance, double x0, double y0)
    {
        const double rowHeight = 6.0;
        const double firstColumn = 25.0;
        const double columnWidth = 20.0;
        var ranges = tolerance.RangesAboveUpTo;
        var width = firstColumn + columnWidth * ranges.Count;
        var rowCount = 2 + tolerance.Classes.Count;
        var top = y0 + rowHeight * rowCount;

        Cell(dxf, x0, top - rowHeight, x0 + width, top, Clean(tolerance.Title), 2.5, TableLayer);

        var headerY = top - rowHeight * 2.0;
        Cell(dxf, x0, headerY, x0 + firstColumn, headerY + rowHeight, "Class Of Deviation", 2.0, TableLayer);

        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            var label = range.Count >= 2
                ? Format(range[0]) + " - " + Format(range[1])
                : string.Empty;

            Cell(dxf, x0 + firstColumn + columnWidth * i, headerY, x0 + firstColumn + columnWidth * (i + 1), headerY + rowHeight, label, 2.0, TableLayer);
        }

        var row = 0;
        foreach (var pair in tolerance.Classes)
        {
            var rowY = headerY - rowHeight * (row + 1);
            Cell(dxf, x0, rowY, x0 + firstColumn, rowY + rowHeight, Clean(pair.Key), 2.0, TableLayer);

            for (var i = 0; i < ranges.Count; i++)
            {
                var value = i < pair.Value.Count ? pair.Value[i] : null;
                var text = value.HasValue ? "%%P" + Format(value.Value) : "-";
                Cell(dxf, x0 + firstColumn + columnWidth * i, rowY, x0 + firstColumn + columnWidth * (i + 1), rowY + rowHeight, text, 2.0, TableLayer);
            }

            row++;
        }
    }

    private static void Cell(DxfFile dxf, double x0, double y0, double x1, double y1, string text, double textHeight, string layer)
    {
        Rectangle(dxf, x0, y0, x1, y1, layer);

        if (!string.IsNullOrEmpty(text))
        {
            Text(dxf, x0 + 1.5, y0 + (y1 - y0 - textHeight) / 2.0, textHeight, text, layer);
        }
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

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Format(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Keeps printable ASCII only; DXF text codes such as %%C and %%P pass through.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            builder.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }

        return builder.ToString();
    }
}