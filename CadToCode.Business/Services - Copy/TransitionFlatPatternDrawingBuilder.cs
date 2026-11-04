using CadToCode.Core.Rules;
using IxMilia.Dxf;

namespace CadToCode.Business.Services;

/// <summary>Inputs for one concentric rectangle-to-round transition piece. Sizes are mean dimensions in mm.</summary>
public sealed class TransitionFlatPatternRequest
{
    public string Name { get; init; } = "TRANSITION";

    public string Material { get; init; } = "MS";

    public int ItemNo { get; init; } = 1;

    public int Qty { get; init; } = 1;

    /// <summary>Rectangular end length (along X).</summary>
    public double RectLengthMm { get; init; }

    /// <summary>Rectangular end width (along Y).</summary>
    public double RectWidthMm { get; init; }

    /// <summary>Round end diameter.</summary>
    public double RoundDiameterMm { get; init; }

    public double HeightMm { get; init; }

    public double ThicknessMm { get; init; }

    /// <summary>Triangulation steps in each quarter of the round end.</summary>
    public int SegmentsPerQuarter { get; init; } = 6;

    public double RawMaterialAllowanceMm { get; init; } = 10.0;

    public SheetFrameRequest Frame { get; init; } = new();
}

public sealed class TransitionFlatPatternResult
{
    public TransitionFlatPatternResult(DxfFile dxf, double finishWeightKg, double rawWeightKg, string scale)
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
/// Builds an A3 sheet for a rectangle-to-round transition: the developed pattern by triangulation
/// (BEFORE BENDING), plus elevation and plan (AFTER BENDING). The seam is on the edge from the first
/// rectangle corner to the start of the round end.
/// </summary>
public sealed class TransitionFlatPatternDrawingBuilder
{
    private const double ViewMargin = 46.0;

    private sealed record Vertex(string Id, double X, double Y, double Z);

    private sealed record Triangle(Vertex A, Vertex B, Vertex C);

    public TransitionFlatPatternResult Build(ManufacturingRules rules, TransitionFlatPatternRequest request)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(request);

        if (request.RectLengthMm <= 0.0 || request.RectWidthMm <= 0.0 || request.RoundDiameterMm <= 0.0 ||
            request.HeightMm <= 0.0 || request.ThicknessMm <= 0.0 || request.SegmentsPerQuarter < 1)
        {
            throw new ArgumentException("All sizes must be greater than zero and at least one segment per quarter is needed.");
        }

        var triangles = BuildTriangles(request);
        var layout = Unfold(triangles);

        var totalArea = 0.0;
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var tri in layout)
        {
            totalArea += Math.Abs((tri.B.X - tri.A.X) * (tri.C.Y - tri.A.Y) - (tri.C.X - tri.A.X) * (tri.B.Y - tri.A.Y)) / 2.0;
            foreach (var p in new[] { tri.A, tri.B, tri.C })
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
        }

        var patternWidth = maxX - minX;
        var patternHeight = maxY - minY;
        var finishWeight = rules.PlateWeightKg(totalArea, request.ThicknessMm);
        var rawWidth = Math.Ceiling(patternWidth + request.RawMaterialAllowanceMm);
        var rawHeight = Math.Ceiling(patternHeight + request.RawMaterialAllowanceMm);
        var rawWeight = rules.PlateWeightKg(rawWidth * rawHeight, request.ThicknessMm);

        var dxf = new DxfFile();
        SheetDrawingKit.EnsureLayers(dxf, SheetDrawingKit.OutlineLayer, SheetDrawingKit.FoldLayer, SheetDrawingKit.CenterLayer,
            SheetDrawingKit.DimLayer, SheetDrawingKit.TextLayer, SheetDrawingKit.TableLayer);
        DxfDimensionWriter.Configure(dxf);

        var probe = SheetDrawingKit.Frame(new DxfFile(), rules, request.Frame);
        var afterWidth = Math.Max(request.RectLengthMm, request.RoundDiameterMm);
        var divisor = SheetDrawingKit.ChooseScale(d =>
        {
            var width = patternWidth / d + 60.0 + afterWidth / d + 90.0 + 2.0 * ViewMargin;
            var patternNeed = patternHeight / d + ViewMargin + 10.0;
            var afterNeed = ViewMargin + request.HeightMm / d + 60.0 + request.RectWidthMm / d + 30.0;
            return width <= probe.DrawingMaxX - probe.DrawingMinX
                && Math.Max(patternNeed, afterNeed) <= probe.DrawingMaxY - probe.DrawingMinY - 26.0;
        });

        var scaleText = SheetDrawingKit.ScaleText(divisor);
        var area = SheetDrawingKit.Frame(dxf, rules, SheetDrawingKit.WithScale(request.Frame, scaleText));

        var ox = area.DrawingMinX + ViewMargin - minX / divisor;
        var oy = area.DrawingMinY + ViewMargin - minY / divisor;
        DrawPattern(dxf, layout, divisor, ox, oy);

        var x0 = area.DrawingMinX + ViewMargin;
        var y0 = area.DrawingMinY + ViewMargin;
        DxfDimensionWriter.Horizontal(dxf, x0, x0 + patternWidth / divisor, y0 - 12.0, y0, SheetDrawingKit.Format(Math.Round(patternWidth, 1)), SheetDrawingKit.DimLayer);
        SheetDrawingKit.Text(dxf, x0, y0 - 28.0, 3.5, "BEFORE BENDING", SheetDrawingKit.TextLayer);

        var ax = x0 + patternWidth / divisor + 60.0;
        var elevationCx = ax + afterWidth / divisor / 2.0;
        DrawElevation(dxf, request, divisor, elevationCx, y0);

        var planCy = y0 + request.HeightMm / divisor + 60.0 + request.RectWidthMm / divisor / 2.0;
        DrawPlan(dxf, request, divisor, elevationCx, planCy);

        var callout = SheetDrawingKit.Callout(rules.Sheet, request.Name, request.Material, request.ThicknessMm, request.Qty, finishWeight);
        SheetDrawingKit.DrawCallout(dxf, callout, ax + afterWidth / divisor + 30.0, y0 + request.HeightMm / divisor);

        SheetDrawingKit.Text(dxf, ax, y0 + 2.0 + request.HeightMm / divisor + 18.0, 3.0,
            "SEAM AT FIRST EDGE. " + request.SegmentsPerQuarter * 4 + " SEGMENTS ON ROUND END.", SheetDrawingKit.TextLayer);

        var rmSize = SheetDrawingKit.Format(rawWidth) + " X " + SheetDrawingKit.Format(rawHeight) + " X " + SheetDrawingKit.Format(request.ThicknessMm);
        SheetDrawingKit.Bom(dxf, rules.Sheet, area, request.ItemNo, rmSize, rawWeight, finishWeight);

        return new TransitionFlatPatternResult(dxf, finishWeight, rawWeight, scaleText);
    }

    private static List<Triangle> BuildTriangles(TransitionFlatPatternRequest request)
    {
        var halfLength = request.RectLengthMm / 2.0;
        var halfWidth = request.RectWidthMm / 2.0;
        var radius = request.RoundDiameterMm / 2.0;
        var n = request.SegmentsPerQuarter;

        Vertex Corner(string id, double sx, double sy) => new(id, sx * halfLength, sy * halfWidth, 0.0);

        Vertex Round(string id, int k)
        {
            var angle = k * Math.PI / 2.0 / n;
            return new Vertex(id, radius * Math.Cos(angle), radius * Math.Sin(angle), request.HeightMm);
        }

        var corners = new[]
        {
            Corner("C0", 1, -1), Corner("C1", 1, 1), Corner("C2", -1, 1), Corner("C3", -1, -1)
        };

        var result = new List<Triangle>();
        var q = new Vertex[4 * n + 1];
        for (var k = 0; k < 4 * n; k++)
        {
            q[k] = Round("Q" + k, k);
        }

        q[4 * n] = Round("Qe", 0);

        for (var quarter = 0; quarter < 4; quarter++)
        {
            var current = corners[(quarter + 1) % 4];
            var previous = quarter == 0 ? corners[0] : corners[quarter];
            var flatStart = q[quarter * n];

            // flat triangle between two corners and the round point at the start of this quarter
            result.Add(new Triangle(previous, current, flatStart));

            // conical corner
            for (var j = 0; j < n; j++)
            {
                result.Add(new Triangle(current, q[quarter * n + j], q[quarter * n + j + 1]));
            }
        }

        return result;
    }

    private sealed record Placed(Vertex Source, double X, double Y);

    private sealed record PlacedTriangle(Placed A, Placed B, Placed C);

    private static List<PlacedTriangle> Unfold(List<Triangle> triangles)
    {
        var result = new List<PlacedTriangle>();
        var first = triangles[0];
        var a = new Placed(first.A, 0.0, 0.0);
        var b = new Placed(first.B, Distance(first.A, first.B), 0.0);
        var c = ThirdPoint(a, b, first.C, 1.0);
        result.Add(new PlacedTriangle(a, b, c));

        for (var i = 1; i < triangles.Count; i++)
        {
            var previous = result[^1];
            var tri = triangles[i];
            var vertices = new[] { tri.A, tri.B, tri.C };
            var placedPrevious = new[] { previous.A, previous.B, previous.C };

            var shared = new List<Placed>();
            Vertex? added = null;
            foreach (var v in vertices)
            {
                var match = placedPrevious.FirstOrDefault(p => p.Source.Id == v.Id);
                if (match is not null)
                {
                    shared.Add(match);
                }
                else
                {
                    added = v;
                }
            }

            if (shared.Count != 2 || added is null)
            {
                // seam triangle: re-anchor on the nearest previous placed vertices by 3D identity
                shared = vertices
                    .Select(v => placedPrevious.FirstOrDefault(p => Distance(p.Source, v) < 1e-9))
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .Take(2)
                    .ToList();
                added = vertices.First(v => shared.All(s => Distance(s.Source, v) > 1e-9));
            }

            var opposite = placedPrevious.First(p => !shared.Contains(p));
            var side = Side(shared[0], shared[1], opposite) > 0.0 ? -1.0 : 1.0;
            var placed = ThirdPoint(shared[0], shared[1], added, side);
            result.Add(new PlacedTriangle(shared[0], shared[1], placed));
        }

        return result;
    }

    private static Placed ThirdPoint(Placed a, Placed b, Vertex target, double side)
    {
        var d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        var da = Distance(a.Source, target);
        var db = Distance(b.Source, target);
        var along = (da * da - db * db + d * d) / (2.0 * d);
        var height = Math.Sqrt(Math.Max(da * da - along * along, 0.0));
        var ux = (b.X - a.X) / d;
        var uy = (b.Y - a.Y) / d;
        return new Placed(target, a.X + ux * along - uy * height * side, a.Y + uy * along + ux * height * side);
    }

    private static double Side(Placed a, Placed b, Placed p)
    {
        return (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
    }

    private static double Distance(Vertex a, Vertex b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void DrawPattern(DxfFile dxf, List<PlacedTriangle> layout, double divisor, double ox, double oy)
    {
        var edgeCount = new Dictionary<(long, long, long, long), int>();
        var edges = new List<(Placed A, Placed B)>();

        (long, long, long, long) Key(Placed p, Placed q)
        {
            var a = ((long)Math.Round(p.X * 100.0), (long)Math.Round(p.Y * 100.0));
            var b = ((long)Math.Round(q.X * 100.0), (long)Math.Round(q.Y * 100.0));
            return a.Item1 < b.Item1 || (a.Item1 == b.Item1 && a.Item2 <= b.Item2)
                ? (a.Item1, a.Item2, b.Item1, b.Item2)
                : (b.Item1, b.Item2, a.Item1, a.Item2);
        }

        foreach (var tri in layout)
        {
            foreach (var (p, q) in new[] { (tri.A, tri.B), (tri.B, tri.C), (tri.C, tri.A) })
            {
                var key = Key(p, q);
                edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
                edges.Add((p, q));
            }
        }

        var drawn = new HashSet<(long, long, long, long)>();
        foreach (var (p, q) in edges)
        {
            var key = Key(p, q);
            if (!drawn.Add(key))
            {
                continue;
            }

            var layer = edgeCount[key] == 1 ? SheetDrawingKit.OutlineLayer : SheetDrawingKit.FoldLayer;
            SheetDrawingKit.Line(dxf, ox + p.X / divisor, oy + p.Y / divisor, ox + q.X / divisor, oy + q.Y / divisor, layer);
        }
    }

    private static void DrawElevation(DxfFile dxf, TransitionFlatPatternRequest request, double divisor, double cx, double y0)
    {
        var bottom = request.RectLengthMm / 2.0 / divisor;
        var top = request.RoundDiameterMm / 2.0 / divisor;
        var height = request.HeightMm / divisor;

        SheetDrawingKit.Line(dxf, cx - bottom, y0, cx + bottom, y0, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Line(dxf, cx - top, y0 + height, cx + top, y0 + height, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Line(dxf, cx - bottom, y0, cx - top, y0 + height, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Line(dxf, cx + bottom, y0, cx + top, y0 + height, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Line(dxf, cx, y0 - 6.0, cx, y0 + height + 6.0, SheetDrawingKit.CenterLayer);

        DxfDimensionWriter.Horizontal(dxf, cx - bottom, cx + bottom, y0 - 12.0, y0, SheetDrawingKit.Format(request.RectLengthMm), SheetDrawingKit.DimLayer);
        DxfDimensionWriter.Horizontal(dxf, cx - top, cx + top, y0 + height + 12.0, y0 + height, "%%C" + SheetDrawingKit.Format(request.RoundDiameterMm), SheetDrawingKit.DimLayer);
        DxfDimensionWriter.Vertical(dxf, y0, y0 + height, cx + Math.Max(bottom, top) + 14.0, cx + bottom, SheetDrawingKit.Format(request.HeightMm), SheetDrawingKit.DimLayer);
        SheetDrawingKit.Text(dxf, cx - bottom, y0 - 28.0, 3.5, "AFTER BENDING", SheetDrawingKit.TextLayer);
    }

    private static void DrawPlan(DxfFile dxf, TransitionFlatPatternRequest request, double divisor, double cx, double cy)
    {
        var halfLength = request.RectLengthMm / 2.0 / divisor;
        var halfWidth = request.RectWidthMm / 2.0 / divisor;
        SheetDrawingKit.Rectangle(dxf, cx - halfLength, cy - halfWidth, cx + halfLength, cy + halfWidth, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Circle(dxf, cx, cy, request.RoundDiameterMm / 2.0 / divisor, SheetDrawingKit.OutlineLayer);
        SheetDrawingKit.Line(dxf, cx - halfLength - 6.0, cy, cx + halfLength + 6.0, cy, SheetDrawingKit.CenterLayer);
        SheetDrawingKit.Line(dxf, cx, cy - halfWidth - 6.0, cx, cy + halfWidth + 6.0, SheetDrawingKit.CenterLayer);
        DxfDimensionWriter.Vertical(dxf, cy - halfWidth, cy + halfWidth, cx - halfLength - 12.0, cx - halfLength, SheetDrawingKit.Format(request.RectWidthMm), SheetDrawingKit.DimLayer);
    }
}
