using System.Globalization;
using System.Text;

namespace CadToCode.Business.Services
{
    /// <summary>Writes one dimensioned A3 landscape PDF per manufacturing sheet. No external PDF library is used.</summary>
    public sealed class GaPdfDrawingService
    {
        private const double PageWidth = 1190.55;
        private const double PageHeight = 841.89;
        private const double Margin = 30.0;
        private const double TitleBlockWidth = 380.0;
        private const double TitleBlockRowHeight = 18.0;
        private const double DimensionSpace = 90.0;
        private const double ArcStepDegrees = 5.0;

        public string FileNameFor(GaPipelineFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            return Path.ChangeExtension(file.FileName, ".pdf");
        }

        public byte[] Build(GaSheetData data, string? tolerance)
        {
            ArgumentNullException.ThrowIfNull(data);

            var template = GaDrawingTemplates.Get(data.Category);
            var toleranceText = string.IsNullOrWhiteSpace(tolerance) ? template.DefaultTolerance : tolerance.Trim();

            var content = new StringBuilder();
            DrawBorder(content);

            var shapes = CollectShapes(data);
            var titleRows = BuildTitleRows(data, template, toleranceText);
            var titleHeight = titleRows.Count * TitleBlockRowHeight + TitleBlockRowHeight;

            DrawTitleBlock(content, template, titleRows, titleHeight);
            DrawNotes(content, data, template, titleHeight);
            DrawGeometry(content, shapes, titleHeight);

            return Assemble(content.ToString());
        }

        private sealed class Shape
        {
            public string Kind { get; init; } = "L";

            public double X1 { get; init; }

            public double Y1 { get; init; }

            public double X2 { get; init; }

            public double Y2 { get; init; }

            public double Radius { get; init; }

            public double Start { get; init; }

            public double End { get; init; }
        }

        private static List<Shape> CollectShapes(GaSheetData data)
        {
            var shapes = new List<Shape>();
            var item = data.Item;

            if (item.Profile is not null && item.Profile.Count > 0)
            {
                foreach (var e in item.Profile)
                {
                    switch (e.Kind)
                    {
                        case ProfileEntityKind.Line:
                            shapes.Add(new Shape { Kind = "L", X1 = e.X1, Y1 = e.Y1, X2 = e.X2, Y2 = e.Y2 });
                            break;
                        case ProfileEntityKind.Circle:
                            shapes.Add(new Shape { Kind = "C", X1 = e.Cx, Y1 = e.Cy, Radius = e.Radius });
                            break;
                        case ProfileEntityKind.Arc:
                            shapes.Add(new Shape { Kind = "A", X1 = e.Cx, Y1 = e.Cy, Radius = e.Radius, Start = e.StartAngle, End = e.EndAngle });
                            break;
                    }
                }

                return shapes;
            }

            if (item.OuterDiameterMm is > 0.0)
            {
                var outer = item.OuterDiameterMm.Value / 2.0;
                shapes.Add(new Shape { Kind = "C", X1 = outer, Y1 = outer, Radius = outer });

                if (item.InnerDiameterMm is > 0.0 && item.InnerDiameterMm < item.OuterDiameterMm)
                {
                    shapes.Add(new Shape { Kind = "C", X1 = outer, Y1 = outer, Radius = item.InnerDiameterMm.Value / 2.0 });
                }

                if (item.HoleCount is > 0 && item.HoleDiameterMm is > 0.0 && item.HolePcdMm is > 0.0)
                {
                    var pcdRadius = item.HolePcdMm.Value / 2.0;
                    var count = item.HoleCount.Value;
                    for (var i = 0; i < count; i++)
                    {
                        var angle = 2.0 * Math.PI * i / count;
                        shapes.Add(new Shape
                        {
                            Kind = "C",
                            X1 = outer + pcdRadius * Math.Cos(angle),
                            Y1 = outer + pcdRadius * Math.Sin(angle),
                            Radius = item.HoleDiameterMm.Value / 2.0
                        });
                    }
                }

                return shapes;
            }

            var length = item.LengthMm ?? item.LargeDiameterMm;
            var width = item.WidthMm ?? item.HeightMm;

            if (length is > 0.0 && width is > 0.0)
            {
                var l = length.Value;
                var w = width.Value;
                shapes.Add(new Shape { Kind = "L", X1 = 0, Y1 = 0, X2 = l, Y2 = 0 });
                shapes.Add(new Shape { Kind = "L", X1 = l, Y1 = 0, X2 = l, Y2 = w });
                shapes.Add(new Shape { Kind = "L", X1 = l, Y1 = w, X2 = 0, Y2 = w });
                shapes.Add(new Shape { Kind = "L", X1 = 0, Y1 = w, X2 = 0, Y2 = 0 });
            }

            return shapes;
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(List<Shape> shapes)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            void Add(double x, double y)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            foreach (var s in shapes)
            {
                switch (s.Kind)
                {
                    case "L":
                        Add(s.X1, s.Y1);
                        Add(s.X2, s.Y2);
                        break;
                    case "C":
                        Add(s.X1 - s.Radius, s.Y1 - s.Radius);
                        Add(s.X1 + s.Radius, s.Y1 + s.Radius);
                        break;
                    default:
                        foreach (var (x, y) in ArcPoints(s))
                        {
                            Add(x, y);
                        }

                        break;
                }
            }

            return (minX, minY, maxX, maxY);
        }

        private static List<(double X, double Y)> ArcPoints(Shape s)
        {
            var end = s.End;
            while (end <= s.Start)
            {
                end += 360.0;
            }

            var steps = Math.Max(2, (int)Math.Ceiling((end - s.Start) / ArcStepDegrees));
            var list = new List<(double, double)>(steps + 1);

            for (var i = 0; i <= steps; i++)
            {
                var angle = (s.Start + (end - s.Start) * i / steps) * Math.PI / 180.0;
                list.Add((s.X1 + s.Radius * Math.Cos(angle), s.Y1 + s.Radius * Math.Sin(angle)));
            }

            return list;
        }

        private static void DrawGeometry(StringBuilder sb, List<Shape> shapes, double titleHeight)
        {
            var areaLeft = Margin + 20.0;
            var areaBottom = Margin + DimensionSpace;
            var areaWidth = PageWidth - 2.0 * Margin - DimensionSpace - 40.0;
            var areaHeight = PageHeight - 2.0 * Margin - DimensionSpace - 60.0;

            if (shapes.Count == 0)
            {
                Text(sb, "F2", 14, areaLeft + 20.0, areaBottom + areaHeight / 2.0, "NO GEOMETRY AVAILABLE FOR THIS PART");
                return;
            }

            var (minX, minY, maxX, maxY) = Bounds(shapes);
            var bw = Math.Max(maxX - minX, 0.001);
            var bh = Math.Max(maxY - minY, 0.001);

            var usableWidth = areaWidth;
            var usableHeight = areaHeight;
            var scale = Math.Min(usableWidth / bw, usableHeight / bh);
            var ox = areaLeft;
            var oy = areaBottom;

            double Px(double x) => ox + (x - minX) * scale;
            double Py(double y) => oy + (y - minY) * scale;

            sb.Append("0.8 w\n0 G\n");

            foreach (var s in shapes)
            {
                switch (s.Kind)
                {
                    case "L":
                        Line(sb, Px(s.X1), Py(s.Y1), Px(s.X2), Py(s.Y2));
                        break;
                    case "C":
                        Circle(sb, Px(s.X1), Py(s.Y1), s.Radius * scale);
                        break;
                    default:
                        var pts = ArcPoints(s);
                        for (var i = 1; i < pts.Count; i++)
                        {
                            Line(sb, Px(pts[i - 1].X), Py(pts[i - 1].Y), Px(pts[i].X), Py(pts[i].Y));
                        }

                        break;
                }
            }

            var left = Px(minX);
            var right = Px(maxX);
            var bottom = Py(minY);
            var top = Py(maxY);

            sb.Append("0.4 w\n");

            var dimY = bottom - 40.0;
            Line(sb, left, bottom - 6.0, left, dimY - 6.0);
            Line(sb, right, bottom - 6.0, right, dimY - 6.0);
            Line(sb, left, dimY, right, dimY);
            Arrow(sb, left, dimY, 1.0, 0.0);
            Arrow(sb, right, dimY, -1.0, 0.0);
            Text(sb, "F1", 10, (left + right) / 2.0 - 20.0, dimY + 4.0, Num(bw));

            var dimX = right + 40.0;
            Line(sb, right + 6.0, bottom, dimX + 6.0, bottom);
            Line(sb, right + 6.0, top, dimX + 6.0, top);
            Line(sb, dimX, bottom, dimX, top);
            Arrow(sb, dimX, bottom, 0.0, 1.0);
            Arrow(sb, dimX, top, 0.0, -1.0);
            Text(sb, "F1", 10, dimX + 6.0, (bottom + top) / 2.0, Num(bh));

            var circles = shapes.Where(s => s.Kind == "C").OrderByDescending(s => s.Radius).ToList();
            if (circles.Count > 0)
            {
                var biggest = circles[0];
                Text(sb, "F1", 10, Px(biggest.X1) - 20.0, Py(biggest.Y1) + biggest.Radius * scale + 8.0, "DIA " + Num(biggest.Radius * 2.0));

                var holes = circles.Skip(1).GroupBy(c => Math.Round(c.Radius, 2)).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (holes is not null)
                {
                    Text(sb, "F1", 10, left, top + 14.0, holes.Count().ToString(CultureInfo.InvariantCulture) + " HOLES DIA " + Num(holes.Key * 2.0));
                }
            }

            sb.Append("0 w\n");
        }

        private static List<(string Label, string Value)> BuildTitleRows(GaSheetData data, GaDrawingTemplate template, string tolerance)
        {
            var rows = new List<(string, string)>();

            foreach (var field in template.TitleBlockFields)
            {
                string value = field switch
                {
                    "DRAWING NO" => data.DrawingNo,
                    "PART" => data.Name,
                    "MATERIAL" => data.Material,
                    "THICKNESS" => data.ThicknessMm.HasValue ? Num(data.ThicknessMm.Value) + " MM" : "NOT STATED",
                    "QTY" => data.Qty.ToString(CultureInfo.InvariantCulture),
                    "FINISH WT" => data.FinishWeightKg.HasValue ? Num(data.FinishWeightKg.Value) + " KG" : "-",
                    "TOLERANCE" => tolerance,
                    "DATE" => string.IsNullOrWhiteSpace(data.Frame.Date) ? DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : data.Frame.Date,
                    _ => string.Empty
                };

                rows.Add((field, value));
            }

            if (template.ShowRawMaterial && !string.IsNullOrWhiteSpace(data.RmSize))
            {
                rows.Add(("RM SIZE", data.RmSize));
            }

            if (template.ShowRawMaterial && data.RmWeightKg.HasValue)
            {
                rows.Add(("RM WT", Num(data.RmWeightKg.Value) + " KG"));
            }

            return rows;
        }

        private static void DrawTitleBlock(StringBuilder sb, GaDrawingTemplate template, List<(string Label, string Value)> rows, double height)
        {
            var x = PageWidth - Margin - TitleBlockWidth;
            var y = Margin;
            var labelWidth = 110.0;

            sb.Append("0.8 w\n0 G\n");
            Rect(sb, x, y, TitleBlockWidth, height);

            Text(sb, "F2", 11, x + 8.0, y + height - 13.0, template.Title);
            Line(sb, x, y + height - TitleBlockRowHeight, x + TitleBlockWidth, y + height - TitleBlockRowHeight);

            var rowY = y + height - TitleBlockRowHeight;
            foreach (var (label, value) in rows)
            {
                rowY -= TitleBlockRowHeight;
                Line(sb, x, rowY, x + TitleBlockWidth, rowY);
                Text(sb, "F2", 9, x + 6.0, rowY + 5.0, label);
                Text(sb, "F1", 9, x + labelWidth + 6.0, rowY + 5.0, value);
            }

            Line(sb, x + labelWidth, y, x + labelWidth, y + height - TitleBlockRowHeight);
        }

        private static void DrawNotes(StringBuilder sb, GaSheetData data, GaDrawingTemplate template, double titleHeight)
        {
            var x = Margin + 10.0;
            var y = Margin + 70.0;

            Text(sb, "F2", 9, x, y, "NOTES");

            var lines = template.DefaultNotes.Concat(data.Notes.Take(4)).ToList();
            for (var i = 0; i < lines.Count && i < 8; i++)
            {
                y -= 11.0;
                Text(sb, "F1", 8, x, y, (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + lines[i]);
            }
        }

        private static void DrawBorder(StringBuilder sb)
        {
            sb.Append("1.2 w\n0 G\n");
            Rect(sb, Margin, Margin, PageWidth - 2.0 * Margin, PageHeight - 2.0 * Margin);
        }

        private static void Rect(StringBuilder sb, double x, double y, double w, double h)
        {
            sb.Append(Num(x)).Append(' ').Append(Num(y)).Append(' ').Append(Num(w)).Append(' ').Append(Num(h)).Append(" re S\n");
        }

        private static void Line(StringBuilder sb, double x1, double y1, double x2, double y2)
        {
            sb.Append(Num(x1)).Append(' ').Append(Num(y1)).Append(" m ")
              .Append(Num(x2)).Append(' ').Append(Num(y2)).Append(" l S\n");
        }

        private static void Circle(StringBuilder sb, double cx, double cy, double r)
        {
            const double k = 0.5522847498;
            var c = r * k;

            sb.Append(Num(cx + r)).Append(' ').Append(Num(cy)).Append(" m\n");
            sb.Append(Num(cx + r)).Append(' ').Append(Num(cy + c)).Append(' ').Append(Num(cx + c)).Append(' ').Append(Num(cy + r)).Append(' ').Append(Num(cx)).Append(' ').Append(Num(cy + r)).Append(" c\n");
            sb.Append(Num(cx - c)).Append(' ').Append(Num(cy + r)).Append(' ').Append(Num(cx - r)).Append(' ').Append(Num(cy + c)).Append(' ').Append(Num(cx - r)).Append(' ').Append(Num(cy)).Append(" c\n");
            sb.Append(Num(cx - r)).Append(' ').Append(Num(cy - c)).Append(' ').Append(Num(cx - c)).Append(' ').Append(Num(cy - r)).Append(' ').Append(Num(cx)).Append(' ').Append(Num(cy - r)).Append(" c\n");
            sb.Append(Num(cx + c)).Append(' ').Append(Num(cy - r)).Append(' ').Append(Num(cx + r)).Append(' ').Append(Num(cy - c)).Append(' ').Append(Num(cx + r)).Append(' ').Append(Num(cy)).Append(" c S\n");
        }

        private static void Arrow(StringBuilder sb, double x, double y, double dx, double dy)
        {
            const double length = 8.0;
            const double half = 2.5;
            var px = -dy;
            var py = dx;

            Line(sb, x, y, x + dx * length + px * half, y + dy * length + py * half);
            Line(sb, x, y, x + dx * length - px * half, y + dy * length - py * half);
        }

        private static void Text(StringBuilder sb, string font, double size, double x, double y, string text)
        {
            sb.Append("BT /").Append(font).Append(' ').Append(Num(size)).Append(" Tf ")
              .Append(Num(x)).Append(' ').Append(Num(y)).Append(" Td (")
              .Append(Escape(text)).Append(") Tj ET\n");
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);

            foreach (var ch in text)
            {
                if (ch == '\\' || ch == '(' || ch == ')')
                {
                    sb.Append('\\').Append(ch);
                }
                else if (ch >= 32 && ch < 127)
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('?');
                }
            }

            return sb.ToString();
        }

        private static string Num(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static byte[] Assemble(string content)
        {
            var sb = new StringBuilder();
            var offsets = new List<int>();

            void Obj(string body)
            {
                offsets.Add(sb.Length);
                sb.Append(offsets.Count.ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
            }

            sb.Append("%PDF-1.4\n");
            Obj("<< /Type /Catalog /Pages 2 0 R >>");
            Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + Num(PageWidth) + " " + Num(PageHeight)
                + "] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>");
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
            Obj("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "endstream");

            var xref = sb.Length;
            sb.Append("xref\n0 ").Append((offsets.Count + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("0000000000 65535 f \n");

            foreach (var offset in offsets)
            {
                sb.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }

            sb.Append("trailer\n<< /Size ").Append((offsets.Count + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
