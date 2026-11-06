using System.Globalization;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace CadToCode.Business.Services;

/// <summary>
/// Builds a general arrangement (GA) DXF for a centrifugal fan directly from the design values
/// (metres / degrees, same keys as CentrifugalFanCalculator). Views: front, section A-A, with
/// dimensions, balloons, bill of materials and title block. Output units are millimetres.
/// </summary>
public sealed class CentrifugalFanGaDrawingBuilder : IGaDrawingBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public bool CanHandle(string? equipmentKey)
    {
        return !string.IsNullOrWhiteSpace(equipmentKey)
               && equipmentKey.Contains("centrifugal", StringComparison.OrdinalIgnoreCase);
    }

    public GaDrawingBuild Build(CadAssistGaRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        var values = new Dictionary<string, double>(request.Values ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("impeller_outer_diameter_m", out var outer) || !double.IsFinite(outer) || outer <= 0)
        {
            return new GaDrawingBuild { Success = false, Error = "The impeller outer diameter is required to draw the GA." };
        }

        var g = new Geo();
        g.D2 = outer * 1000.0;
        g.D1 = Mm(values, 0.6 * g.D2, warnings, "Impeller inlet diameter", "impeller_inlet_diameter_m");
        var dh = Mm(values, 0.2 * g.D2, warnings, "Hub diameter", "hub_diameter_m");
        g.Bw = Mm(values, 0.22 * g.D2, warnings, "Blade width", "blade_width_m", "impeller_width_m");
        g.Bwi = Mm(values, g.Bw, warnings, "Blade inlet width", "blade_inlet_width_m");
        g.Tb = Mm(values, 3.0, warnings, "Blade thickness", "blade_thickness_m");
        g.Tp = Mm(values, 6.0, warnings, "Back plate thickness", "plate_thickness_m");
        var dc = Mm(values, 1.2 * g.D2, warnings, "Casing base diameter", "casing_base_diameter_m");
        g.W = Mm(values, 0.5 * g.D2, warnings, "Casing width", "casing_width_m");
        g.Tw = Mm(values, 5.0, warnings, "Casing wall thickness", "casing_wall_thickness_m", "casing_wall_m");
        g.H = Mm(values, 0.5 * g.D2, warnings, "Outlet height", "outlet_height_m");
        var dci = Mm(values, 0.55 * g.D2, warnings, "Casing inlet diameter", "casing_inlet_diameter_m", "inlet_eye_diameter_m");

        g.N = 12;
        if (values.TryGetValue("blade_count", out var n) && double.IsFinite(n) && n >= 3)
        {
            g.N = (int)Math.Round(Math.Min(n, 60));
        }
        else
        {
            warnings.Add("Blade count not provided; assumed 12.");
        }

        g.B1 = Angle(values, 30, warnings, "Blade inlet angle", "blade_inlet_angle_deg");
        g.B2 = Angle(values, 60, warnings, "Blade outlet angle", "blade_outlet_angle_deg");

        g.R2 = g.D2 / 2.0;
        g.R1 = g.D1 / 2.0;
        g.Rh = dh / 2.0;
        g.Rci = dci / 2.0;

        if (g.R1 >= g.R2)
        {
            g.R1 = 0.6 * g.R2;
            warnings.Add("Impeller inlet diameter was not smaller than the outer diameter; adjusted.");
        }

        if (g.Rh >= g.R1)
        {
            g.Rh = 0.3 * g.R1;
            warnings.Add("Hub diameter was not smaller than the impeller inlet; adjusted.");
        }

        if (dc < g.D2 * 1.05)
        {
            dc = g.D2 * 1.1;
            warnings.Add("Casing base diameter was too close to the impeller; adjusted.");
        }

        g.Rb = dc / 2.0;
        g.Rs = Math.Max(g.Rh, 20.0) * 0.5;
        g.Rtop = g.Rb + g.H / 4.0;
        g.Rbot = g.Rb + 3.0 * g.H / 4.0;
        g.Ld = g.Rb + 0.85 * g.H;

        warnings.Add("Volute is drawn as a schematic spiral and blades as a variable-angle curve; verify against the final design.");

        var ts = Math.Max(g.D2 / 120.0, 3.0);
        var c = new Canvas(ts);

        DrawFront(c, g);
        DrawSection(c, g);
        DrawSheet(c, g, request);

        byte[] bytes;

        using (var ms = new MemoryStream())
        {
            c.Doc.Save(ms);
            bytes = ms.ToArray();
        }

        var parts = new List<CadAssistGaPartDto>
        {
            new() { Balloon = 1, Key = "casing", Name = "CASING (VOLUTE)", Quantity = 1, Material = request.Material },
            new() { Balloon = 2, Key = "blade", Name = "IMPELLER BLADE", Quantity = g.N, Material = request.Material },
            new() { Balloon = 3, Key = "back-plate", Name = "BACK PLATE", Quantity = 1, Material = request.Material },
            new() { Balloon = 4, Key = "hub", Name = "HUB", Quantity = 1, Material = request.Material },
            new() { Balloon = 5, Key = "shaft", Name = "SHAFT", Quantity = 1, Material = request.Material }
        };

        return new GaDrawingBuild
        {
            Success = true,
            DxfContent = bytes,
            FileName = SafeFileName(request.DrawingNumber),
            Parts = parts,
            Warnings = warnings
        };
    }

    // ------------------------------------------------------------------ views

    private static void DrawFront(Canvas c, Geo g)
    {
        var ext = g.R2 + 80.0;
        c.Ln("CENTERLINE", -ext, 0, ext, 0);
        c.Ln("CENTERLINE", 0, -ext, 0, ext);

        c.Circ("BACKPLATE", 0, 0, g.R2);
        c.Circ("BLADES", 0, 0, g.R1);
        c.Circ("HUB", 0, 0, g.Rh);
        c.Circ("SHAFT", 0, 0, g.Rs);
        c.Circ("CASING", 0, 0, g.Rci);

        var center = BladeCenterline(g.R1, g.R2, g.B1, g.B2);
        var outline = BladeOutline(center, g.Tb);

        for (var k = 0; k < g.N; k++)
        {
            c.Poly("BLADES", Rotate(outline, 360.0 * k / g.N), true);
        }

        var spiral = new List<Vector2>();
        const int steps = 90;

        for (var i = 0; i <= steps; i++)
        {
            var th = 2.0 * Math.PI * i / steps;
            var r = g.Rb + g.H * th / (2.0 * Math.PI);
            spiral.Add(new Vector2(r * Math.Cos(th), r * Math.Sin(th)));
        }

        c.Poly("CASING", spiral, false);

        c.Ln("CASING", g.Rb, 0, g.Rb, g.Ld);
        c.Ln("CASING", g.Rb + g.H, 0, g.Rb + g.H, g.Ld);
        c.Ln("CASING", g.Rb, g.Ld, g.Rb + g.H, g.Ld);

        // diameter call-outs in a column on the left
        var xt = -(g.Rb + g.H / 2.0) - 60.0;
        var yStart = g.Rb * 0.8;
        var gap = c.Ts * 4.0;

        var callouts = new (double R, double Ang, string Text)[]
        {
            (g.Rb, 120, "%%c" + Fmt(2 * g.Rb) + " CASING BASE"),
            (g.R2, 150, "%%c" + Fmt(2 * g.R2) + " IMPELLER OD"),
            (g.R1, 180, "%%c" + Fmt(2 * g.R1) + " IMPELLER INLET"),
            (g.Rci, 210, "%%c" + Fmt(2 * g.Rci) + " CASING INLET"),
            (g.Rh, 240, "%%c" + Fmt(2 * g.Rh) + " HUB")
        };

        for (var i = 0; i < callouts.Length; i++)
        {
            var a = callouts[i].Ang * Math.PI / 180.0;
            var px = callouts[i].R * Math.Cos(a);
            var py = callouts[i].R * Math.Sin(a);
            var yt = yStart - i * gap;

            c.Ln("DIMENSIONS", px, py, xt, yt);
            c.Txt("TEXT", callouts[i].Text, xt - c.Ts * 0.5, yt, c.Ts, TextAlignment.MiddleRight);
        }

        c.DimH(g.Rb, g.Rb + g.H, g.Ld, 60.0, Fmt(g.H));

        // balloons
        var casingAng = 100.0 * Math.PI / 180.0;
        var casingR = g.Rb + g.H * 100.0 / 360.0;
        c.Balloon(1,
            (casingR + 150.0) * Math.Cos(casingAng), (casingR + 150.0) * Math.Sin(casingAng),
            casingR * Math.Cos(casingAng), casingR * Math.Sin(casingAng));

        var mid = center[center.Count / 2];
        var midAng = Math.Atan2(mid.Y, mid.X);
        var bR = g.R2 + 60.0;
        c.Balloon(2, bR * Math.Cos(midAng), bR * Math.Sin(midAng), mid.X, mid.Y);

        var labelY = -g.Rbot - 120.0;
        c.Txt("TEXT", "FRONT VIEW", 0, labelY, c.Ts * 1.4);
        c.Txt("TEXT", g.N.ToString(Inv) + " BACKWARD CURVED BLADES, THK " + Fmt(g.Tb), 0, labelY - c.Ts * 2.6, c.Ts);
        c.Txt("TEXT", "INLET ANGLE " + Fmt(g.B1) + " DEG / OUTLET ANGLE " + Fmt(g.B2) + " DEG", 0, labelY - c.Ts * 4.4, c.Ts);
    }

    private static void DrawSection(Canvas c, Geo g)
    {
        var x0 = g.SideX0();
        var xw = x0 + g.W;
        var xp = g.Xp(x0);
        var hubL = g.HubLen();
        var shaftExt = g.ShaftExt();
        var xs0 = xp - hubL - 80.0;
        var xs1 = xw + shaftExt;

        c.Ln("CENTERLINE", x0 - 80, 0, xs1 + 80, 0);

        // casing walls
        c.Rect("CASING", x0, g.Rtop - g.Tw, xw, g.Rtop);
        c.Rect("CASING", x0, -g.Rbot, xw, -g.Rbot + g.Tw);
        c.Rect("CASING", x0, g.Rci, x0 + g.Tw, g.Rtop);
        c.Rect("CASING", x0, -g.Rbot, x0 + g.Tw, -g.Rci);

        var hole = g.Rs + 10.0;
        c.Rect("CASING", xw - g.Tw, hole, xw, g.Rtop);
        c.Rect("CASING", xw - g.Tw, -g.Rbot, xw, -hole);

        // back plate
        c.Rect("BACKPLATE", xp, g.Rh, xp + g.Tp, g.R2);
        c.Rect("BACKPLATE", xp, -g.R2, xp + g.Tp, -g.Rh);

        // blades
        c.Poly("BLADES", new[]
        {
            new Vector2(xp, g.R1), new Vector2(xp - g.Bwi, g.R1),
            new Vector2(xp - g.Bw, g.R2), new Vector2(xp, g.R2)
        }, true);

        c.Poly("BLADES", new[]
        {
            new Vector2(xp, -g.R1), new Vector2(xp - g.Bwi, -g.R1),
            new Vector2(xp - g.Bw, -g.R2), new Vector2(xp, -g.R2)
        }, true);

        // hub and shaft
        c.Rect("HUB", xp - hubL, -g.Rh, xp + g.Tp, g.Rh);
        c.Rect("SHAFT", xs0, -g.Rs, xs1, g.Rs);

        // dimensions
        c.DimH(x0, xw, -g.Rbot, -70.0, Fmt(g.W));
        c.DimH(xp - g.Bw, xp, g.R2, 60.0, Fmt(g.Bw));
        c.DimV(-g.R2, g.R2, x0, -150.0, "%%c" + Fmt(2 * g.R2));
        c.DimV(-g.Rbot, g.Rtop, x0, -300.0, Fmt(g.Rbot + g.Rtop));

        // balloons
        var by = g.Rtop + 160.0;
        c.Balloon(1, x0 + g.W / 2.0, by, x0 + g.W / 2.0, g.Rtop - g.Tw / 2.0);
        c.Balloon(3, xp + 90.0, by, xp + g.Tp / 2.0, (g.Rh + g.R2) / 2.0);
        c.Balloon(4, xp - g.Bw - 80.0, by, xp - hubL / 2.0, g.Rh);
        c.Balloon(5, xw + shaftExt / 2.0 + 60.0, g.Rs + 250.0, xw + shaftExt / 2.0, g.Rs);

        var labelY = -g.Rbot - 120.0;
        c.Txt("TEXT", "SECTION A-A", x0 + g.W / 2.0, labelY, c.Ts * 1.4);
        c.Txt("TEXT", "PLATE THK " + Fmt(g.Tp) + "   CASING WALL " + Fmt(g.Tw), x0 + g.W / 2.0, labelY - c.Ts * 2.6, c.Ts);
    }

    private static void DrawSheet(Canvas c, Geo g, CadAssistGaRequest request)
    {
        var ts = c.Ts;
        var x0 = g.SideX0();
        var right = x0 + g.W + g.ShaftExt() + 300.0;
        var left = -(g.Rb + g.H / 2.0) - 60.0 - 22.0 * ts;
        var top = Math.Max(g.Ld, g.Rtop) + 360.0;

        var rowH = 2.2 * ts;
        var bomH = 6 * rowH;
        var titleH = 9.0 * ts;
        var bottom = -g.Rbot - 220.0 - bomH - titleH - 40.0;

        c.Rect("TITLEBLOCK", left, bottom, right, top);
        c.Rect("TITLEBLOCK", left + 12, bottom + 12, right - 12, top - 12);

        var cols = new[] { 4.0 * ts, 22.0 * ts, 4.0 * ts, 12.0 * ts };
        var totalW = cols.Sum();
        var xr = right - 20.0;
        var xl = xr - totalW;
        var yTitle0 = bottom + 20.0;
        var yBom0 = yTitle0 + titleH;

        // BOM grid
        c.Rect("TITLEBLOCK", xl, yBom0, xr, yBom0 + bomH);

        for (var r = 1; r < 6; r++)
        {
            c.Ln("TITLEBLOCK", xl, yBom0 + r * rowH, xr, yBom0 + r * rowH);
        }

        var cx = xl;

        for (var i = 0; i < cols.Length - 1; i++)
        {
            cx += cols[i];
            c.Ln("TITLEBLOCK", cx, yBom0, cx, yBom0 + bomH);
        }

        var headers = new[] { "ITEM", "DESCRIPTION", "QTY", "MATERIAL" };
        var rows = new[]
        {
            new[] { "1", "CASING (VOLUTE)", "1", request.Material },
            new[] { "2", "IMPELLER BLADE", g.N.ToString(Inv), request.Material },
            new[] { "3", "BACK PLATE", "1", request.Material },
            new[] { "4", "HUB", "1", request.Material },
            new[] { "5", "SHAFT", "1", request.Material }
        };

        var yHead = yBom0 + 5 * rowH + rowH / 2.0;
        var colX = xl;

        for (var i = 0; i < cols.Length; i++)
        {
            c.Txt("TEXT", headers[i], colX + ts * 0.5, yHead, ts * 0.9, TextAlignment.MiddleLeft);
            colX += cols[i];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            var y = yBom0 + (4 - r) * rowH + rowH / 2.0;
            colX = xl;

            for (var i = 0; i < cols.Length; i++)
            {
                c.Txt("TEXT", rows[r][i], colX + ts * 0.5, y, ts * 0.9, TextAlignment.MiddleLeft);
                colX += cols[i];
            }
        }

        // title block
        c.Rect("TITLEBLOCK", xl, yTitle0, xr, yTitle0 + titleH);

        var lines = new[]
        {
            request.Title,
            "DRG NO: " + request.DrawingNumber,
            "MATERIAL: " + request.Material,
            "SCALE: NTS   UNITS: MM",
            "DATE: " + DateTime.UtcNow.ToString("yyyy-MM-dd", Inv)
        };

        for (var i = 0; i < lines.Length; i++)
        {
            var y = yTitle0 + titleH - (i + 0.5) * (titleH / lines.Length);
            c.Txt("TEXT", lines[i], xl + ts * 0.5, y, i == 0 ? ts * 1.0 : ts * 0.9, TextAlignment.MiddleLeft);

            if (i > 0)
            {
                c.Ln("TITLEBLOCK", xl, yTitle0 + titleH - i * (titleH / lines.Length), xr, yTitle0 + titleH - i * (titleH / lines.Length));
            }
        }
    }

    // --------------------------------------------------------------- geometry

    private static List<Vector2> BladeCenterline(double r1, double r2, double b1Deg, double b2Deg)
    {
        const int steps = 32;
        var pts = new List<Vector2>();
        var phi = 0.0;
        var prevR = r1;

        for (var i = 0; i <= steps; i++)
        {
            var r = r1 + (r2 - r1) * i / steps;

            if (i > 0)
            {
                var rm = (r + prevR) / 2.0;
                var t = (rm - r1) / (r2 - r1);
                var beta = (b1Deg + (b2Deg - b1Deg) * t) * Math.PI / 180.0;
                phi -= (r - prevR) / (rm * Math.Tan(beta));
            }

            pts.Add(new Vector2(r * Math.Cos(phi), r * Math.Sin(phi)));
            prevR = r;
        }

        return pts;
    }

    private static List<Vector2> BladeOutline(List<Vector2> center, double thickness)
    {
        var left = new List<Vector2>();
        var right = new List<Vector2>();

        for (var i = 0; i < center.Count; i++)
        {
            var a = center[Math.Max(i - 1, 0)];
            var b = center[Math.Min(i + 1, center.Count - 1)];
            var tx = b.X - a.X;
            var ty = b.Y - a.Y;
            var len = Math.Sqrt(tx * tx + ty * ty);

            if (len < 1e-9)
            {
                len = 1.0;
            }

            var nx = -ty / len * thickness / 2.0;
            var ny = tx / len * thickness / 2.0;

            left.Add(new Vector2(center[i].X + nx, center[i].Y + ny));
            right.Add(new Vector2(center[i].X - nx, center[i].Y - ny));
        }

        right.Reverse();
        left.AddRange(right);
        return left;
    }

    private static List<Vector2> Rotate(List<Vector2> pts, double degrees)
    {
        var a = degrees * Math.PI / 180.0;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        return pts.Select(p => new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)).ToList();
    }

    // ----------------------------------------------------------------- values

    private static double Mm(
        IReadOnlyDictionary<string, double> values,
        double fallbackMm,
        List<string> warnings,
        string label,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var m) && double.IsFinite(m) && m > 0)
            {
                return m * 1000.0;
            }
        }

        warnings.Add(label + " not provided; assumed " + Fmt(fallbackMm) + " mm.");
        return fallbackMm;
    }

    private static double Angle(
        IReadOnlyDictionary<string, double> values,
        double fallback,
        List<string> warnings,
        string label,
        string key)
    {
        if (values.TryGetValue(key, out var a) && double.IsFinite(a) && a > 0)
        {
            return Math.Min(Math.Max(a, 5.0), 85.0);
        }

        warnings.Add(label + " not provided; assumed " + Fmt(fallback) + " deg.");
        return fallback;
    }

    private static string Fmt(double v) => v.ToString("0.##", Inv);

    private static string SafeFileName(string? drawingNumber)
    {
        var name = string.IsNullOrWhiteSpace(drawingNumber) ? "centrifugal-fan-ga" : drawingNumber.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + ".dxf";
    }

    // ----------------------------------------------------------------- helpers

    private sealed class Geo
    {
        public double D2, D1, R2, R1, Rh, Rs, Rci, Rb, H, W, Tw, Tp, Tb, Bw, Bwi, B1, B2, Rtop, Rbot, Ld;
        public int N;

        public double SideX0() => Rb + H + 500.0;

        public double Xp(double x0) => x0 + W - Tw - Tp - 5.0;

        public double HubLen() => 0.6 * Bw;

        public double ShaftExt() => Math.Max(0.6 * Rh * 2.0, 100.0);
    }

    private sealed class Canvas
    {
        private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);

        public Canvas(double ts)
        {
            Ts = ts;
        }

        public DxfDocument Doc { get; } = new(netDxf.Header.DxfVersion.AutoCad2010);

        public double Ts { get; }

        private Layer L(string name)
        {
            if (_layers.TryGetValue(name, out var layer))
            {
                return layer;
            }

            layer = new Layer(name);

            switch (name)
            {
                case "CASING": layer.Color = AciColor.FromCadIndex(5); break;
                case "BLADES": layer.Color = AciColor.FromCadIndex(3); break;
                case "BACKPLATE": layer.Color = AciColor.FromCadIndex(1); break;
                case "HUB": layer.Color = AciColor.FromCadIndex(6); break;
                case "SHAFT": layer.Color = AciColor.FromCadIndex(4); break;
                case "DIMENSIONS": layer.Color = AciColor.FromCadIndex(2); break;
                case "BALLOONS": layer.Color = AciColor.FromCadIndex(2); break;
                case "CENTERLINE":
                    layer.Color = AciColor.FromCadIndex(1);
                    layer.Linetype = Linetype.Center;
                    break;
            }

            _layers[name] = layer;
            return layer;
        }

        private static bool Fin(params double[] v) => v.All(double.IsFinite);

        public void Ln(string layer, double x1, double y1, double x2, double y2)
        {
            if (!Fin(x1, y1, x2, y2))
            {
                return;
            }

            Doc.Entities.Add(new Line(new Vector2(x1, y1), new Vector2(x2, y2)) { Layer = L(layer) });
        }

        public void Circ(string layer, double cx, double cy, double r)
        {
            if (!Fin(cx, cy, r) || r <= 0)
            {
                return;
            }

            Doc.Entities.Add(new Circle(new Vector2(cx, cy), r) { Layer = L(layer) });
        }

        public void Poly(string layer, IEnumerable<Vector2> pts, bool closed)
        {
            var list = pts.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();

            if (list.Count < 2)
            {
                return;
            }

            Doc.Entities.Add(new Polyline2D(list, closed) { Layer = L(layer) });
        }

        public void Rect(string layer, double x0, double y0, double x1, double y1)
        {
            Poly(layer, new[]
            {
                new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1)
            }, true);
        }

        public void Txt(string layer, string text, double x, double y, double height,
            TextAlignment alignment = TextAlignment.MiddleCenter, double rotation = 0.0)
        {
            if (string.IsNullOrEmpty(text) || !Fin(x, y, height))
            {
                return;
            }

            Doc.Entities.Add(new Text(text, new Vector2(x, y), height)
            {
                Layer = L(layer),
                Alignment = alignment,
                Rotation = rotation
            });
        }

        private void Arrow(double x, double y, double dx, double dy)
        {
            var len = Math.Sqrt(dx * dx + dy * dy);

            if (len < 1e-9)
            {
                return;
            }

            dx /= len;
            dy /= len;

            var a = Ts * 0.9;
            var px = -dy;
            var py = dx;

            Ln("DIMENSIONS", x, y, x + dx * a + px * a * 0.3, y + dy * a + py * a * 0.3);
            Ln("DIMENSIONS", x, y, x + dx * a - px * a * 0.3, y + dy * a - py * a * 0.3);
        }

        public void DimH(double x1, double x2, double yBase, double offset, string text)
        {
            if (x1 > x2)
            {
                (x1, x2) = (x2, x1);
            }

            var s = offset >= 0 ? 1.0 : -1.0;
            var yd = yBase + offset;
            var gap = Ts * 0.4;
            var ext = Ts * 0.6;

            Ln("DIMENSIONS", x1, yBase + s * gap, x1, yd + s * ext);
            Ln("DIMENSIONS", x2, yBase + s * gap, x2, yd + s * ext);
            Ln("DIMENSIONS", x1, yd, x2, yd);
            Arrow(x1, yd, 1, 0);
            Arrow(x2, yd, -1, 0);
            Txt("TEXT", text, (x1 + x2) / 2.0, yd + Ts * 0.3, Ts, TextAlignment.BottomCenter);
        }

        public void DimV(double y1, double y2, double xBase, double offset, string text)
        {
            if (y1 > y2)
            {
                (y1, y2) = (y2, y1);
            }

            var s = offset >= 0 ? 1.0 : -1.0;
            var xd = xBase + offset;
            var gap = Ts * 0.4;
            var ext = Ts * 0.6;

            Ln("DIMENSIONS", xBase + s * gap, y1, xd + s * ext, y1);
            Ln("DIMENSIONS", xBase + s * gap, y2, xd + s * ext, y2);
            Ln("DIMENSIONS", xd, y1, xd, y2);
            Arrow(xd, y1, 0, 1);
            Arrow(xd, y2, 0, -1);
            Txt("TEXT", text, xd - Ts * 0.3, (y1 + y2) / 2.0, Ts, TextAlignment.BottomCenter, 90.0);
        }

        public void Balloon(int number, double cx, double cy, double tipX, double tipY)
        {
            var r = Ts * 1.3;

            Circ("BALLOONS", cx, cy, r);
            Txt("BALLOONS", number.ToString(Inv), cx, cy, Ts);

            var dx = tipX - cx;
            var dy = tipY - cy;
            var d = Math.Sqrt(dx * dx + dy * dy);

            if (d > r)
            {
                Ln("BALLOONS", cx + dx / d * r, cy + dy / d * r, tipX, tipY);
                Circ("BALLOONS", tipX, tipY, Ts * 0.25);
            }
        }
    }
}
