using System.Globalization;
using CadToCode.Core.Rules;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace CadToCode.Business.Services;

/// <summary>
/// Inputs for one rolled scroll (volute) casing band. The radius grows linearly with the sweep angle
/// (Archimedean spiral) from the start radius to the end radius. All sizes are in mm.
/// </summary>
public sealed class ScrollFlatPatternRequest
{
    public string Name { get; init; } = "SCROLL";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    public double StartRadiusMm { get; init; }

    public double EndRadiusMm { get; init; }

    public double SweepDegrees { get; init; } = 360.0;

    /// <summary>Width of the casing band (the axial width of the scroll).</summary>
    public double WidthMm { get; init; }

    public double ThicknessMm { get; init; }

    public double RawMaterialAllowanceMm { get; init; } = 10.0;

    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class ScrollFlatPatternResult
{
    public ScrollFlatPatternResult(DxfFile dxf, double developedLengthMm, double finishWeightKg, double rawWeightKg, string scale)
    {
        Dxf = dxf;
        DevelopedLengthMm = developedLengthMm;
        FinishWeightKg = finishWeightKg;
        RawWeightKg = rawWeightKg;
        Scale = scale;
    }

    public DxfFile Dxf { get; }

    public double DevelopedLengthMm { get; }

    public double FinishWeightKg { get; }

    public double RawWeightKg { get; }

    public string Scale { get; }
}

/// <summary>
/// Builds an A3 sheet for a scroll casing band: BEFORE BENDING strip with station marks (angle and radius
/// at each developed length) and AFTER BENDING plan view of the spiral.
/// </summary>
public sealed class ScrollFlatPatternDrawingBuilder
{
    private const double ViewMargin = 46.0;
    private const double StationStepDegrees = 45.0;

    public ScrollFlatPatternResult Build(ManufacturingRules rules, ScrollFlatPatternRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.StartRadiusMm <= 0.0 || request.EndRadiusMm <= 0.0 || request.WidthMm <= 0.0 || request.ThicknessMm <= 0.0)
        {
            throw new ArgumentException("Radii, width and thickness must be greater than zero.");
        }

        if (request.SweepDegrees <= 0.0 || request.SweepDegrees > 720.0)
        {
            throw new ArgumentException("Sweep must be between 0 and 720 degrees.");
        }

        var sweep = request.SweepDegrees * Math.PI / 180.0;
        var growth = (request.EndRadiusMm - request.StartRadiusMm) / sweep;
        var length = ArcLength(request.StartRadiusMm, growth, sweep);
        var finishWeight = rules.PlateWeightKg(length * request.WidthMm, request.ThicknessMm);
        var rawLength = Math.Ceiling(length + request.RawMaterialAllowanceMm);
        var rawWidth = Math.Ceiling(request.WidthMm + request.RawMaterialAllowanceMm);
        var rawWeight = rules.PlateWeightKg(rawLength * rawWidth, request.ThicknessMm);
        var maxRadius = Math.Max(request.StartRadiusMm, request.EndRadiusMm);

        var dxf = new DxfFile();
        SheetDrawingKit.EnsureLayers(dxf, SheetDrawingKit.OutlineLayer, SheetDrawingKit.CenterLayer, SheetDrawingKit.DimLayer,
            SheetDrawingKit.TextLayer, SheetDrawingKit.TableLayer);
        DxfDimensionWriter.Configure(dxf);

        var probe = SheetDrawingKit.Frame(new DxfFile(), rules, request.Frame);
        var divisor = SheetDrawingKit.ChooseScale(d =>
        {
            var fitsWidth = length / d <= probe.DrawingMaxX - probe.DrawingMinX - 2.0 * ViewMargin
                && 2.0 * maxRadius / d + 110.0 <= probe.DrawingMaxX - probe.DrawingMinX - 2.0 * ViewMargin;
            var needHeight = ViewMargin + request.WidthMm / d + 60.0 + 2.0 * maxRadius / d + 26.0;
            return fitsWidth && needHeight <= probe.DrawingMaxY - probe.DrawingMinY;
        });

        var scaleText = SheetDrawingKit.ScaleText(divisor);
        var area = SheetDrawingKit.Frame(dxf, rules, SheetDrawingKit.WithScale(request.Frame, scaleText));

        var x0 = area.DrawingMinX + ViewMargin;
        var y0 = area.DrawingMinY + ViewMargin;
        var stripLength = length / divisor;
        var stripWidth = request.WidthMm / divisor;

        SheetDrawingKit.Rectangle(dxf, x0, y0, x0 + stripLength, y0 + stripWidth, SheetDrawingKit.OutlineLayer);
        DxfDimensionWriter.Horizontal(dxf, x0, x0 + stripLength, y0 - 12.0, y0, SheetDrawingKit.Format(Math.Round(length, 1)), SheetDrawingKit.DimLayer);
        DxfDimensionWriter.Vertical(dxf, y0, y0 + stripWidth, x0 - 12.0, x0, SheetDrawingKit.Format(request.WidthMm), SheetDrawingKit.DimLayer);
        SheetDrawingKit.Text(dxf, x0, y0 - 28.0, 3.5, "BEFORE BENDING", SheetDrawingKit.TextLayer);

        for (var degrees = 0.0; degrees <= request.SweepDegrees + 1e-9; degrees += StationStepDegrees)
        {
            var theta = degrees * Math.PI / 180.0;
            var station = ArcLength(request.StartRadiusMm, growth, theta) / divisor;
            var radius = request.StartRadiusMm + growth * theta;
            var x = x0 + station;

            if (degrees > 0.0 && degrees < request.SweepDegrees - 1e-9)
            {
                SheetDrawingKit.Line(dxf, x, y0, x, y0 + stripWidth, SheetDrawingKit.CenterLayer);
            }

            var label = SheetDrawingKit.Format(Math.Round(degrees, 1)) + "DEG R" + SheetDrawingKit.Format(Math.Round(radius, 1));
            SheetDrawingKit.Text(dxf, Math.Min(x, x0 + stripLength - 24.0), y0 + stripWidth + 3.0, 2.5, label, SheetDrawingKit.TextLayer);
        }

        var cx = x0 + maxRadius / divisor;
        var cy = y0 + stripWidth + 60.0 + maxRadius / divisor;
        DrawPlan(dxf, request, growth, sweep, divisor, cx, cy);
        SheetDrawingKit.Text(dxf, x0, y0 + stripWidth + 30.0, 3.5, "AFTER BENDING (PLAN)", SheetDrawingKit.TextLayer);

        var callout = SheetDrawingKit.Callout(rules.Sheet, request.Name, request.Material, request.ThicknessMm, request.Qty, finishWeight);
        SheetDrawingKit.DrawCallout(dxf, callout, cx + maxRadius / divisor + 30.0, cy + maxRadius / divisor);

        var rmSize = SheetDrawingKit.Format(rawLength) + " X " + SheetDrawingKit.Format(rawWidth) + " X " + SheetDrawingKit.Format(request.ThicknessMm);
        SheetDrawingKit.Bom(dxf, rules.Sheet, area, request.ItemNo, rmSize, rawWeight, finishWeight);

        return new ScrollFlatPatternResult(dxf, length, finishWeight, rawWeight, scaleText);
    }

    private static void DrawPlan(DxfFile dxf, ScrollFlatPatternRequest request, double growth, double sweep, double divisor, double cx, double cy)
    {
        var steps = Math.Max(24, (int)Math.Ceiling(sweep * 180.0 / Math.PI / 2.0));
        var segments = Math.Max(8, (int)Math.Ceiling(request.SweepDegrees / 15.0));
        for (var i = 0; i < segments; i++)
        {
            var t0 = sweep * i / segments;
            var t1 = sweep * (i + 0.5) / segments;
            var t2 = sweep * (i + 1) / segments;
            AddSpiralArc(dxf, request, growth, divisor, cx, cy, t0, t1, t2);
        }


        var startRadius = request.StartRadiusMm / divisor;
        var endRadius = request.EndRadiusMm / divisor;
        SheetDrawingKit.Line(dxf, cx, cy, cx + startRadius, cy, SheetDrawingKit.CenterLayer);
        SheetDrawingKit.Line(dxf, cx, cy, cx + endRadius * Math.Cos(sweep), cy + endRadius * Math.Sin(sweep), SheetDrawingKit.CenterLayer);
        SheetDrawingKit.Line(dxf, cx - 6.0, cy, cx + 6.0, cy, SheetDrawingKit.CenterLayer);
        SheetDrawingKit.Line(dxf, cx, cy - 6.0, cx, cy + 6.0, SheetDrawingKit.CenterLayer);

        SheetDrawingKit.Text(dxf, cx + startRadius + 2.0, cy + 2.0, 3.0, "R" + SheetDrawingKit.Format(request.StartRadiusMm), SheetDrawingKit.TextLayer);
        SheetDrawingKit.Text(dxf, cx + endRadius * Math.Cos(sweep) + 2.0, cy + endRadius * Math.Sin(sweep) + 2.0, 3.0,
            "R" + SheetDrawingKit.Format(request.EndRadiusMm), SheetDrawingKit.TextLayer);
    }

    /// <summary>
    /// Adds one true ARC through three points of the spiral (start, middle, end of the segment).
    /// </summary>
    private static void AddSpiralArc(DxfFile dxf, ScrollFlatPatternRequest request, double growth, double divisor, double cx, double cy, double t0, double t1, double t2)
    {
        (double X, double Y) Point(double t)
        {
            var r = (request.StartRadiusMm + growth * t) / divisor;
            return (cx + r * Math.Cos(t), cy + r * Math.Sin(t));
        }

        var a = Point(t0);
        var b = Point(t1);
        var c = Point(t2);

        var bx = b.X - a.X;
        var by = b.Y - a.Y;
        var ccx = c.X - a.X;
        var ccy = c.Y - a.Y;
        var d = 2.0 * (bx * ccy - by * ccx);

        if (Math.Abs(d) < 1e-9)
        {
            SheetDrawingKit.Line(dxf, a.X, a.Y, c.X, c.Y, SheetDrawingKit.OutlineLayer);
            return;
        }

        var b2 = bx * bx + by * by;
        var c2 = ccx * ccx + ccy * ccy;
        var ux = (ccy * b2 - by * c2) / d;
        var uy = (bx * c2 - ccx * b2) / d;
        var radius = Math.Sqrt(ux * ux + uy * uy);

        var centerX = a.X + ux;
        var centerY = a.Y + uy;
        var startAngle = Math.Atan2(a.Y - centerY, a.X - centerX) * 180.0 / Math.PI;
        var endAngle = Math.Atan2(c.Y - centerY, c.X - centerX) * 180.0 / Math.PI;

        var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
        if (cross < 0.0)
        {
            (startAngle, endAngle) = (endAngle, startAngle);
        }

        if (startAngle < 0.0)
        {
            startAngle += 360.0;
        }

        if (endAngle < 0.0)
        {
            endAngle += 360.0;
        }

        dxf.Entities.Add(new DxfArc(new DxfPoint(centerX, centerY, 0), radius, startAngle, endAngle) { Layer = SheetDrawingKit.OutlineLayer });
    }

    /// <summary>Arc length of r(t) = r0 + growth * t from 0 to angle, by Simpson's rule.</summary>
    private static double ArcLength(double startRadius, double growth, double angle)
    {
        if (angle <= 0.0)
        {
            return 0.0;
        }

        const int steps = 720;
        var h = angle / steps;
        double Speed(double t)
        {
            var r = startRadius + growth * t;
            return Math.Sqrt(r * r + growth * growth);
        }

        var sum = Speed(0.0) + Speed(angle);
        for (var i = 1; i < steps; i++)
        {
            sum += Speed(h * i) * (i % 2 == 0 ? 2.0 : 4.0);
        }

        return sum * h / 3.0;
    }
}
