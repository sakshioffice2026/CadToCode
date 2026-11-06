using System.Globalization;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;

namespace CadToCode.Business.Services;

public sealed class GaDrawingResult
{
    public bool Success { get; init; }
    public string FileName { get; init; } = string.Empty;
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();

    public static GaDrawingResult Fail(params string[] errors) =>
        new() { Success = false, Errors = errors };
}

/// <summary>
/// Builds a General Arrangement (GA) DXF for a single-inlet centrifugal fan from the same SI values that
/// CentrifugalFanScriptComposer uses for the 3D model, so drawing and STEP always agree.
/// Sheet units are millimetres. One layer per part (ScrollCasing, BackPlate, InletPlate, Hub,
/// ImpellerBackPlate, Shroud, Blade), so the DXF can be fed straight into the manufacturing drawing flow.
/// Views: front view (looking along the shaft) and Section A-A (plane through the shaft axis).
/// </summary>
public static class CentrifugalFanGaDrawingBuilder
{
    private const int ScrollSteps = 120;
    private const int BladeSteps = 24;

    private static readonly string[] PartLayers =
    {
        "ScrollCasing", "BackPlate", "InletPlate", "Hub", "ImpellerBackPlate", "Shroud", "Blade"
    };

    private static readonly string[] RequiredKeys =
    {
        CentrifugalFanScriptComposer.ImpellerOuterDiameterM,
        CentrifugalFanScriptComposer.ImpellerInletDiameterM,
        CentrifugalFanScriptComposer.BladeWidthM,
        CentrifugalFanScriptComposer.BladeInletWidthM,
        CentrifugalFanScriptComposer.HubDiameterM,
        CentrifugalFanScriptComposer.BladeCount,
        CentrifugalFanScriptComposer.BladeInletAngleDeg,
        CentrifugalFanScriptComposer.BladeOutletAngleDeg,
        CentrifugalFanScriptComposer.BladeThicknessM,
        CentrifugalFanScriptComposer.BladeProfileCode,
        CentrifugalFanScriptComposer.PlateThicknessM,
        CentrifugalFanScriptComposer.CasingBaseDiameterM,
        CentrifugalFanScriptComposer.CasingWidthM,
        CentrifugalFanScriptComposer.CasingWallM,
        CentrifugalFanScriptComposer.OutletHeightM,
        CentrifugalFanScriptComposer.CasingInletDiameterM
    };

    public static GaDrawingResult Build(IReadOnlyDictionary<string, double> valuesSi, string? projectTitle = null)
    {
        ArgumentNullException.ThrowIfNull(valuesSi);

        var values = new Dictionary<string, double>(valuesSi, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var key in RequiredKeys)
        {
            if (!values.TryGetValue(key, out var v))
                errors.Add($"Missing required value '{key}'.");
            else if (!double.IsFinite(v))
                errors.Add($"Value '{key}' must be a finite number.");
        }

        if (errors.Count > 0)
            return GaDrawingResult.Fail(errors.ToArray());

        double Mm(string key) => values[key] * 1000.0;

        var d2 = Mm(CentrifugalFanScriptComposer.ImpellerOuterDiameterM);
        var d1 = Mm(CentrifugalFanScriptComposer.ImpellerInletDiameterM);
        var b2 = Mm(CentrifugalFanScriptComposer.BladeWidthM);
        var b1 = Mm(CentrifugalFanScriptComposer.BladeInletWidthM);
        var hubD = Mm(CentrifugalFanScriptComposer.HubDiameterM);
        var thk = Mm(CentrifugalFanScriptComposer.BladeThicknessM);
        var plate = Mm(CentrifugalFanScriptComposer.PlateThicknessM);
        var d3 = Mm(CentrifugalFanScriptComposer.CasingBaseDiameterM);
        var width = Mm(CentrifugalFanScriptComposer.CasingWidthM);
        var wall = Mm(CentrifugalFanScriptComposer.CasingWallM);
        var hOut = Mm(CentrifugalFanScriptComposer.OutletHeightM);
        var dIn = Mm(CentrifugalFanScriptComposer.CasingInletDiameterM);
        var beta1 = values[CentrifugalFanScriptComposer.BladeInletAngleDeg];
        var beta2 = values[CentrifugalFanScriptComposer.BladeOutletAngleDeg];
        var profile = (int)Math.Round(values[CentrifugalFanScriptComposer.BladeProfileCode]);
        var bladeCount = (int)Math.Round(values[CentrifugalFanScriptComposer.BladeCount]);

        foreach (var (name, v) in new[]
                 {
                     ("impeller outer diameter", d2), ("impeller inlet diameter", d1), ("blade width", b2),
                     ("blade inlet width", b1), ("hub diameter", hubD), ("blade thickness", thk),
                     ("plate thickness", plate), ("casing base diameter", d3), ("casing width", width),
                     ("casing wall", wall), ("outlet height", hOut), ("casing inlet diameter", dIn)
                 })
        {
            if (v <= 0) errors.Add($"The {name} must be greater than zero.");
        }

        if (bladeCount < 3 || bladeCount > 64)
            errors.Add("Blade count must be between 3 and 64.");

        if (beta1 <= 0 || beta1 >= 180 || beta2 <= 0 || beta2 >= 180)
            errors.Add("Blade angles must be between 0 and 180 degrees (exclusive).");

        if (errors.Count > 0)
            return GaDrawingResult.Fail(errors.ToArray());

        var r1 = d1 / 2.0;
        var r2 = d2 / 2.0;
        var r3 = d3 / 2.0;
        var hubR = hubD / 2.0;
        var bladeHeight = Math.Max(b1, b2);
        var impellerHeight = 2.0 * plate + bladeHeight;
        var impellerZ = wall + (width - impellerHeight) / 2.0;
        var bladeZ = impellerZ + plate;
        var shroudZ = bladeZ + bladeHeight;
        var casingTopZ = wall + width;
        var overallZ = casingTopZ + wall;
        var rMaxOuter = r3 + hOut + wall;

        var doc = new DxfDocument(netDxf.Header.DxfVersion.AutoCad2010);

        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        var palette = new[]
        {
            AciColor.Cyan, AciColor.Yellow, AciColor.Green, AciColor.Red, AciColor.Magenta, AciColor.Blue, AciColor.Default
        };

        for (var i = 0; i < PartLayers.Length; i++)
            layers[PartLayers[i]] = new Layer(PartLayers[i]) { Color = palette[i] };

        layers["Centre"] = new Layer("Centre") { Color = AciColor.Red };
        layers["Dims"] = new Layer("Dims") { Color = AciColor.Green };
        layers["Text"] = new Layer("Text") { Color = AciColor.Default };
        layers["Frame"] = new Layer("Frame") { Color = AciColor.Default };

        var textH = Math.Max(8.0, rMaxOuter / 60.0);
        var fan = new FanGeometry(r1, r2, r3, hubR, hOut, wall);

        // ---------------------------------------------------------------- front view (centre at origin)
        var outerSpiral = new List<Vector2>();
        var innerSpiral = new List<Vector2>();

        for (var i = 0; i <= ScrollSteps; i++)
        {
            var t = (double)i / ScrollSteps;
            var a = 2.0 * Math.PI * t;
            var r = r3 + hOut * t;
            innerSpiral.Add(new Vector2(r * Math.Cos(a), r * Math.Sin(a)));
            outerSpiral.Add(new Vector2((r + wall) * Math.Cos(a), (r + wall) * Math.Sin(a)));
        }

        AddPolyline(doc, layers["ScrollCasing"], outerSpiral, false);
        AddPolyline(doc, layers["ScrollCasing"], innerSpiral, false);
        AddLine(doc, layers["ScrollCasing"], innerSpiral[0], outerSpiral[0]);
        AddLine(doc, layers["ScrollCasing"], innerSpiral[ScrollSteps], outerSpiral[ScrollSteps]);

        // Discharge flange side lines (tangent run at the end of the spiral).
        var endR = r3 + hOut;
        var dischargeLen = Math.Max(hOut * 0.5, 2.0 * wall);
        AddLine(doc, layers["ScrollCasing"], new Vector2(endR, 0), new Vector2(endR, -dischargeLen));
        AddLine(doc, layers["ScrollCasing"], new Vector2(endR + wall, 0), new Vector2(endR + wall, -dischargeLen));
        AddLine(doc, layers["ScrollCasing"],
            new Vector2(endR, -dischargeLen), new Vector2(endR + wall, -dischargeLen));

        doc.Entities.Add(new Circle(new Vector2(0, 0), dIn / 2.0) { Layer = layers["InletPlate"] });
        doc.Entities.Add(new Circle(new Vector2(0, 0), r2) { Layer = layers["ImpellerBackPlate"] });
        doc.Entities.Add(new Circle(new Vector2(0, 0), r1) { Layer = layers["Shroud"] });
        doc.Entities.Add(new Circle(new Vector2(0, 0), hubR) { Layer = layers["Hub"] });

        AddBlades(doc, layers["Blade"], r1, r2, beta1, beta2, thk, profile, bladeCount);

        AddCentreCross(doc, layers["Centre"], new Vector2(0, 0), r2 * 1.15);

        // Front view dimensions.
        AddHorizontalDim(doc, layers["Dims"], -r2, r2, -rMaxOuter - 3.0 * textH, "D2 (impeller OD) " + F(d2), textH);
        AddHorizontalDim(doc, layers["Dims"], -r1, r1, -rMaxOuter - 7.0 * textH, "D1 (impeller inlet) " + F(d1), textH);
        AddHorizontalDim(doc, layers["Dims"], -hubR, hubR, -rMaxOuter - 11.0 * textH, "Hub dia " + F(hubD), textH);
        AddText(doc, layers["Dims"], $"Casing base dia {F(d3)}  Outlet height {F(hOut)}  Inlet dia {F(dIn)}",
            new Vector2(-rMaxOuter, -rMaxOuter - 15.0 * textH), textH);
        AddText(doc, layers["Text"], "FRONT VIEW (ALONG SHAFT)", new Vector2(-rMaxOuter, rMaxOuter + 4.0 * textH), textH * 1.4);

        // ---------------------------------------------------------------- Section A-A (right of front view)
        var sx = rMaxOuter + 6.0 * textH + 80.0;
        double X(double z) => sx + z;

        // +Y side shows the plane at 0 deg (radius r3); -Y side shows 180 deg (radius r3 + hOut/2).
        var rTop = r3;
        var rBot = r3 + hOut * 0.5;

        // Back plate
        AddRectMirror(doc, layers["BackPlate"], X(0), X(wall), 0, rTop + wall, 0, rBot + wall);
        // Scroll casing wall (radial thickness = wall, axial length = width)
        AddRect(doc, layers["ScrollCasing"], X(wall), X(casingTopZ), rTop, rTop + wall);
        AddRect(doc, layers["ScrollCasing"], X(wall), X(casingTopZ), -(rBot + wall), -rBot);
        // Inlet plate with inlet bore
        AddRect(doc, layers["InletPlate"], X(casingTopZ), X(overallZ), dIn / 2.0, rTop + wall);
        AddRect(doc, layers["InletPlate"], X(casingTopZ), X(overallZ), -(rBot + wall), -dIn / 2.0);
        // Hub
        AddRectMirror(doc, layers["Hub"], X(impellerZ), X(impellerZ + plate + bladeHeight), 0, hubR, 0, hubR);
        // Impeller back plate
        AddRectMirror(doc, layers["ImpellerBackPlate"], X(impellerZ), X(impellerZ + plate), hubR, r2, hubR, r2);
        // Shroud
        AddRectMirror(doc, layers["Shroud"], X(shroudZ), X(shroudZ + plate), r1, r2, r1, r2);
        // Blade (cut at section, drawn as outline with diagonal hatch lines)
        AddRectMirror(doc, layers["Blade"], X(bladeZ), X(bladeZ + bladeHeight), r1, r2, r1, r2);
        AddHatch(doc, layers["Blade"], X(bladeZ), X(bladeZ + bladeHeight), r1, r2, 1);
        AddHatch(doc, layers["Blade"], X(bladeZ), X(bladeZ + bladeHeight), -r2, -r1, -1);

        // Shaft centreline (horizontal, along Z)
        AddLine(doc, layers["Centre"], new Vector2(X(-0.1 * overallZ), 0), new Vector2(X(overallZ * 1.1), 0), true);

        AddText(doc, layers["Text"], "SECTION A-A (THROUGH SHAFT AXIS)", new Vector2(sx, rMaxOuter + 4.0 * textH), textH * 1.4);

        // Section dimensions
        AddVerticalDim(doc, layers["Dims"], -rMaxOuter - 11.0 * textH, 0, overallZ, "Overall width " + F(overallZ), textH, sx);
        AddText(doc, layers["Dims"],
            $"Casing width {F(width)}  Wall {F(wall)}  Plate {F(plate)}  Blade width {F(b2)}/{F(b1)}",
            new Vector2(sx, -rMaxOuter - 3.0 * textH), textH);
        AddText(doc, layers["Dims"],
            $"Blades {bladeCount} nos  Inlet angle {beta1.ToString("0.##", CultureInfo.InvariantCulture)} deg  " +
            $"Outlet angle {beta2.ToString("0.##", CultureInfo.InvariantCulture)} deg  Blade thk {F(thk)}",
            new Vector2(sx, -rMaxOuter - 7.0 * textH), textH);

        // ---------------------------------------------------------------- title block and frame
        var left = -rMaxOuter - 6.0 * textH;
        var right = sx + overallZ + 14.0 * textH;
        var bottom = -rMaxOuter - 22.0 * textH;
        var top = rMaxOuter + 10.0 * textH;

        AddPolyline(doc, layers["Frame"],
            new List<Vector2>
            {
                new(left, bottom), new(right, bottom), new(right, top), new(left, top)
            }, true);

        var tbTop = bottom + 8.0 * textH;
        var tbLeft = right - 48.0 * textH;
        AddPolyline(doc, layers["Frame"],
            new List<Vector2> { new(tbLeft, bottom), new(right, bottom), new(right, tbTop), new(tbLeft, tbTop) }, true);

        var title = string.IsNullOrWhiteSpace(projectTitle) ? "CENTRIFUGAL FAN" : projectTitle!.Trim().ToUpperInvariant();
        AddText(doc, layers["Text"], title + " - GENERAL ARRANGEMENT", new Vector2(tbLeft + textH, tbTop - 2.5 * textH), textH * 1.3);
        AddText(doc, layers["Text"], "ALL DIMENSIONS IN MM", new Vector2(tbLeft + textH, tbTop - 4.5 * textH), textH);
        AddText(doc, layers["Text"], "SCALE 1:1 (AS DRAWN)  FIRST ANGLE", new Vector2(tbLeft + textH, tbTop - 6.5 * textH), textH);

        // Part list
        var listX = left + 2.0 * textH;
        var listY = bottom + 6.0 * textH;
        AddText(doc, layers["Text"], "PART LIST", new Vector2(listX, listY + 2.0 * textH), textH * 1.1);
        AddText(doc, layers["Text"],
            "1 Scroll casing   2 Back plate   3 Inlet plate   4 Hub   5 Impeller back plate   6 Shroud   7 Blade x" + bladeCount,
            new Vector2(listX, listY), textH);

        AddItemBalloons(doc, layers["Text"], fan, sx, overallZ, bladeZ, shroudZ, impellerZ, textH);

        using var ms = new MemoryStream();
        doc.Save(ms);

        return new GaDrawingResult
        {
            Success = true,
            FileName = "CentrifugalFan_GA.dxf",
            Content = ms.ToArray(),
            Layers = PartLayers
        };
    }

    // ================================================================ helpers

    private readonly record struct FanGeometry(double R1, double R2, double R3, double HubR, double HOut, double Wall);

    private static string F(double mm) => mm.ToString("0.#", CultureInfo.InvariantCulture);

    private static void AddLine(DxfDocument doc, Layer layer, Vector2 a, Vector2 b, bool centre = false)
    {
        var line = new Line(a, b) { Layer = layer };
        if (centre) line.Linetype = Linetype.Center;
        doc.Entities.Add(line);
    }

    private static void AddPolyline(DxfDocument doc, Layer layer, IReadOnlyList<Vector2> points, bool closed)
    {
        var vertices = points.Select(p => new Polyline2DVertex(p.X, p.Y)).ToList();
        doc.Entities.Add(new Polyline2D(vertices, closed) { Layer = layer });
    }

    private static void AddRect(DxfDocument doc, Layer layer, double x0, double x1, double y0, double y1)
    {
        AddPolyline(doc, layer,
            new List<Vector2> { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) }, true);
    }

    private static void AddRectMirror(DxfDocument doc, Layer layer, double x0, double x1,
        double topInner, double topOuter, double botInner, double botOuter)
    {
        AddRect(doc, layer, x0, x1, topInner, topOuter);
        AddRect(doc, layer, x0, x1, -botOuter, -botInner);
    }

    private static void AddHatch(DxfDocument doc, Layer layer, double x0, double x1, double y0, double y1, int sign)
    {
        const int lines = 6;
        var w = x1 - x0;
        var h = y1 - y0;

        for (var i = 1; i < lines; i++)
        {
            var f = (double)i / lines;
            var ax = x0;
            var ay = y0 + h * f;
            var bx = x0 + w * f;
            var by = y0;
            AddLine(doc, layer, new Vector2(ax, ay), new Vector2(bx, by));
        }
    }

    private static void AddCentreCross(DxfDocument doc, Layer layer, Vector2 c, double half)
    {
        AddLine(doc, layer, new Vector2(c.X - half, c.Y), new Vector2(c.X + half, c.Y), true);
        AddLine(doc, layer, new Vector2(c.X, c.Y - half), new Vector2(c.X, c.Y + half), true);
    }

    private static void AddText(DxfDocument doc, Layer layer, string text, Vector2 at, double height)
    {
        doc.Entities.Add(new Text(text, at, height) { Layer = layer });
    }

    private static void AddHorizontalDim(DxfDocument doc, Layer layer, double xa, double xb, double y, string label, double h)
    {
        AddLine(doc, layer, new Vector2(xa, y), new Vector2(xb, y));
        AddLine(doc, layer, new Vector2(xa, y - h), new Vector2(xa, y + h));
        AddLine(doc, layer, new Vector2(xb, y - h), new Vector2(xb, y + h));
        AddText(doc, layer, label, new Vector2(xa, y + 0.8 * h), h);
    }

    private static void AddVerticalDim(DxfDocument doc, Layer layer, double y, double za, double zb, string label, double h, double sx)
    {
        // Dimension drawn along the horizontal (Z) axis of the section view, at the given Y below the part.
        AddLine(doc, layer, new Vector2(sx + za, y), new Vector2(sx + zb, y));
        AddLine(doc, layer, new Vector2(sx + za, y - h), new Vector2(sx + za, y + h));
        AddLine(doc, layer, new Vector2(sx + zb, y - h), new Vector2(sx + zb, y + h));
        AddText(doc, layer, label, new Vector2(sx + za, y - 2.0 * h), h);
    }

    private static void AddItemBalloons(DxfDocument doc, Layer layer, FanGeometry fan, double sx, double overallZ,
        double bladeZ, double shroudZ, double impellerZ, double textH)
    {
        var rr = textH * 1.2;

        void Balloon(int n, double x, double y)
        {
            doc.Entities.Add(new Circle(new Vector2(x, y), rr) { Layer = layer });
            AddText(doc, layer, n.ToString(CultureInfo.InvariantCulture), new Vector2(x - rr * 0.35, y - rr * 0.4), textH);
        }

        var top = fan.R3 + fan.Wall + 6.0 * textH;
        Balloon(1, sx + overallZ * 0.5, top + 3.0 * textH);
        Balloon(2, sx + 0.5 * fan.Wall, top + 6.0 * textH);
        Balloon(3, sx + overallZ - 0.5 * fan.Wall, top + 6.0 * textH);
        Balloon(4, sx + impellerZ, -fan.HubR - 3.0 * textH);
        Balloon(5, sx + impellerZ + 1.0, fan.R2 + 3.0 * textH);
        Balloon(6, sx + shroudZ, fan.R2 + 3.0 * textH);
        Balloon(7, sx + bladeZ, fan.R1 - 3.0 * textH);
    }

    private static void AddBlades(DxfDocument doc, Layer layer, double r1, double r2,
        double beta1, double beta2, double thickness, int profile, int count)
    {
        var radii = new double[BladeSteps + 1];
        var angles = new double[BladeSteps + 1];

        for (var k = 0; k <= BladeSteps; k++)
            radii[k] = r1 + (r2 - r1) * k / BladeSteps;

        angles[0] = 0.0;

        for (var k = 1; k <= BladeSteps; k++)
        {
            var sMid = ((double)k - 0.5) / BladeSteps;
            var betaMid = (beta1 + (beta2 - beta1) * sMid) * Math.PI / 180.0;
            var rMid = 0.5 * (radii[k] + radii[k - 1]);
            var dTheta = -(radii[k] - radii[k - 1]) / (rMid * Math.Tan(betaMid));
            angles[k] = angles[k - 1] + dTheta;
        }

        var left = new Vector2[BladeSteps + 1];
        var right = new Vector2[BladeSteps + 1];

        for (var k = 0; k <= BladeSteps; k++)
        {
            var s = (double)k / BladeSteps;
            var kPrev = Math.Max(k - 1, 0);
            var kNext = Math.Min(k + 1, BladeSteps);
            var dx = radii[kNext] * Math.Cos(angles[kNext]) - radii[kPrev] * Math.Cos(angles[kPrev]);
            var dy = radii[kNext] * Math.Sin(angles[kNext]) - radii[kPrev] * Math.Sin(angles[kPrev]);
            var length = Math.Sqrt(dx * dx + dy * dy);
            var nx = -dy / length;
            var ny = dx / length;
            var shape = profile == 1 ? Math.Max(0.15, Math.Sin(Math.PI * Math.Pow(s, 0.6))) : 1.0;
            var half = 0.5 * thickness * shape;
            var cx = radii[k] * Math.Cos(angles[k]);
            var cy = radii[k] * Math.Sin(angles[k]);
            left[k] = new Vector2(cx + nx * half, cy + ny * half);
            right[k] = new Vector2(cx - nx * half, cy - ny * half);
        }

        for (var i = 0; i < count; i++)
        {
            var rot = 2.0 * Math.PI * i / count;
            var cosR = Math.Cos(rot);
            var sinR = Math.Sin(rot);
            var outline = new List<Vector2>();

            for (var k = 0; k <= BladeSteps; k++)
                outline.Add(new Vector2(left[k].X * cosR - left[k].Y * sinR, left[k].X * sinR + left[k].Y * cosR));

            for (var k = BladeSteps; k >= 0; k--)
                outline.Add(new Vector2(right[k].X * cosR - right[k].Y * sinR, right[k].X * sinR + right[k].Y * cosR));

            AddPolyline(doc, layer, outline, true);
        }
    }
}