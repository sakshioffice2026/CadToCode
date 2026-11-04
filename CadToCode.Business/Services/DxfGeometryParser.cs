using System.Globalization;
using System.Text;
using netDxf;
using netDxf.Entities;
using netDxf.Units;
using Vector3 = netDxf.Vector3;

namespace CadToCode.Business.Services;

public sealed record DxfLineData(string Handle, string Layer, double X1, double Y1, double X2, double Y2)
{
    public double Length
    {
        get
        {
            var dx = X2 - X1;
            var dy = Y2 - Y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

public sealed record DxfCircleData(string Handle, string Layer, double CenterX, double CenterY, double Radius)
{
    public double Diameter => Radius * 2.0;
}

public sealed record DxfArcData(
    string Handle,
    string Layer,
    double CenterX,
    double CenterY,
    double Radius,
    double StartAngleDegrees,
    double EndAngleDegrees)
{
    public double SweepDegrees
    {
        get
        {
            var sweep = (EndAngleDegrees - StartAngleDegrees) % 360.0;

            if (sweep <= 1e-9)
            {
                sweep += 360.0;
            }

            return sweep;
        }
    }

    public (double X, double Y) StartPoint => PointAt(StartAngleDegrees);

    public (double X, double Y) EndPoint => PointAt(EndAngleDegrees);

    private (double X, double Y) PointAt(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return (CenterX + Radius * Math.Cos(radians), CenterY + Radius * Math.Sin(radians));
    }
}

public sealed class DxfParseResult
{
    public string Units { get; init; } = "Unitless";

    public IReadOnlyList<DxfLineData> Lines { get; init; } = Array.Empty<DxfLineData>();

    public IReadOnlyList<DxfCircleData> Circles { get; init; } = Array.Empty<DxfCircleData>();

    public IReadOnlyList<DxfArcData> Arcs { get; init; } = Array.Empty<DxfArcData>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Loose lines and arcs joined end to end into continuous contours (polylines with bulges).</summary>
    public IReadOnlyList<DxfContour> Contours { get; init; } = Array.Empty<DxfContour>();

    public double MinX { get; init; }

    public double MinY { get; init; }

    public double MaxX { get; init; }

    public double MaxY { get; init; }

    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;

    public int EntityCount => Lines.Count + Circles.Count + Arcs.Count;

    public bool IsEmpty => EntityCount == 0;

    public string ToCoordinateTable(int maxEntitiesPerKind = 200)
    {
        var cap = Math.Max(1, maxEntitiesPerKind);
        var sb = new StringBuilder();

        sb.AppendLine("Source: DXF (exact coordinates, millimeters unless stated otherwise)");
        sb.AppendLine("Units: " + Units);

        if (IsEmpty)
        {
            sb.AppendLine("No Line, Circle or Arc entities were found.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("Bounding box: X " + F(MinX) + " to " + F(MaxX) + ", Y " + F(MinY) + " to " + F(MaxY)
            + " (width " + F(Width) + ", height " + F(Height) + ")");

        sb.AppendLine();
        sb.AppendLine("LINES (" + Lines.Count + ") start(x, y) -> end(x, y), length, layer");

        for (var i = 0; i < Math.Min(cap, Lines.Count); i++)
        {
            var l = Lines[i];
            sb.AppendLine("L" + (i + 1) + ": (" + F(l.X1) + ", " + F(l.Y1) + ") -> (" + F(l.X2) + ", " + F(l.Y2)
                + "), length " + F(l.Length) + ", layer " + l.Layer);
        }

        AppendOmitted(sb, Lines.Count, cap);

        sb.AppendLine();
        sb.AppendLine("CIRCLES (" + Circles.Count + ") center(x, y), radius, diameter, layer");

        for (var i = 0; i < Math.Min(cap, Circles.Count); i++)
        {
            var c = Circles[i];
            sb.AppendLine("C" + (i + 1) + ": center (" + F(c.CenterX) + ", " + F(c.CenterY) + "), radius " + F(c.Radius)
                + ", diameter " + F(c.Diameter) + ", layer " + c.Layer);
        }

        AppendOmitted(sb, Circles.Count, cap);

        sb.AppendLine();
        sb.AppendLine("ARCS (" + Arcs.Count + ") center(x, y), radius, start angle, end angle (degrees, counter-clockwise), layer");

        for (var i = 0; i < Math.Min(cap, Arcs.Count); i++)
        {
            var a = Arcs[i];
            var s = a.StartPoint;
            var e = a.EndPoint;
            sb.AppendLine("A" + (i + 1) + ": center (" + F(a.CenterX) + ", " + F(a.CenterY) + "), radius " + F(a.Radius)
                + ", start " + F(a.StartAngleDegrees) + ", end " + F(a.EndAngleDegrees)
                + ", sweep " + F(a.SweepDegrees)
                + ", from (" + F(s.X) + ", " + F(s.Y) + ") to (" + F(e.X) + ", " + F(e.Y) + ")"
                + ", layer " + a.Layer);
        }

        AppendOmitted(sb, Arcs.Count, cap);

        if (Contours.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CONTOURS (" + Contours.Count + ") joined from lines and arcs, closed: " + Contours.Count(k => k.IsClosed));

            for (var i = 0; i < Math.Min(cap, Contours.Count); i++)
            {
                var c = Contours[i];
                sb.AppendLine("P" + (i + 1) + ": " + (c.IsClosed ? "closed" : "open") + ", " + c.SegmentCount
                    + " segments, " + c.Points.Count + " vertices, layer " + c.Layer);
            }

            AppendOmitted(sb, Contours.Count, cap);
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parser warnings:");

            foreach (var warning in Warnings)
            {
                sb.AppendLine("- " + warning);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendOmitted(StringBuilder sb, int total, int cap)
    {
        if (total > cap)
        {
            sb.AppendLine("... " + (total - cap) + " more not listed");
        }
    }

    private static string F(double value)
    {
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);

        if (rounded == 0.0)
        {
            rounded = 0.0;
        }

        return rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

public sealed record DxfPartRequest(string PartName, string Layer);

public sealed record DxfPartFileResult(string PartName, string Layer, string FilePath, int EntityCount);

public sealed record DxfSheetOptions(
    string? ProjectName,
    string? Material,
    string? DrawingNumberPrefix);

public interface IDxfGeometryParser
{
    Task<DxfParseResult> ParseAsync(Stream dxfStream, CancellationToken cancellationToken = default);

    DxfParseResult Parse(Stream dxfStream, CancellationToken cancellationToken = default);

    IReadOnlyList<DxfPartFileResult> SplitByLayers(
        Stream dxfStream,
        IEnumerable<DxfPartRequest> parts,
        string outputFolder,
        CancellationToken cancellationToken = default,
        DxfSheetOptions? sheetOptions = null);
}

public sealed class DxfGeometryParser : IDxfGeometryParser
{
    private const double Epsilon = 1e-9;

    public async Task<DxfParseResult> ParseAsync(Stream dxfStream, CancellationToken cancellationToken = default)
    {
        if (dxfStream is null)
        {
            throw new ArgumentNullException(nameof(dxfStream));
        }

        var buffer = new MemoryStream();
        await dxfStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        return await Task.Run(() => Parse(buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public DxfParseResult Parse(Stream dxfStream, CancellationToken cancellationToken = default)
    {
        if (dxfStream is null)
        {
            throw new ArgumentNullException(nameof(dxfStream));
        }

        DxfDocument? document;

        try
        {
            document = DxfDocument.Load(dxfStream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The DXF file could not be read: " + ex.Message, ex);
        }

        if (document is null)
        {
            throw new InvalidOperationException("The DXF file could not be read. Export it again as ASCII or binary DXF (AutoCAD R12 or newer).");
        }

        var warnings = new List<string>();
        var lines = new List<DxfLineData>();
        var circles = new List<DxfCircleData>();
        var arcs = new List<DxfArcData>();

        var skippedNormals = 0;
        var skippedDegenerate = 0;

        var sourceLines = new List<Line>(document.Entities.Lines);
        var sourceArcs = new List<Arc>(document.Entities.Arcs);

        foreach (var poly in document.Entities.Polylines2D)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<EntityObject> pieces;

            try
            {
                pieces = poly.Explode();
            }
            catch (Exception)
            {
                skippedDegenerate++;
                continue;
            }

            foreach (var piece in pieces)
            {
                if (piece is Line pieceLine)
                {
                    sourceLines.Add(pieceLine);
                }
                else if (piece is Arc pieceArc)
                {
                    sourceArcs.Add(pieceArc);
                }
            }
        }

        foreach (var line in sourceLines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var data = new DxfLineData(
                line.Handle ?? string.Empty,
                LayerName(line),
                line.StartPoint.X,
                line.StartPoint.Y,
                line.EndPoint.X,
                line.EndPoint.Y);

            if (!AllFinite(data.X1, data.Y1, data.X2, data.Y2) || data.Length <= Epsilon)
            {
                skippedDegenerate++;
                continue;
            }

            lines.Add(data);
        }

        foreach (var circle in document.Entities.Circles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetPlanarCenter(circle.Normal, circle.Center, out var cx, out var cy, out var flipped))
            {
                skippedNormals++;
                continue;
            }

            if (!AllFinite(cx, cy, circle.Radius) || circle.Radius <= Epsilon)
            {
                skippedDegenerate++;
                continue;
            }

            circles.Add(new DxfCircleData(circle.Handle ?? string.Empty, LayerName(circle), cx, cy, circle.Radius));
            _ = flipped;
        }

        foreach (var arc in sourceArcs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetPlanarCenter(arc.Normal, arc.Center, out var cx, out var cy, out var flipped))
            {
                skippedNormals++;
                continue;
            }

            var start = arc.StartAngle;
            var end = arc.EndAngle;

            if (flipped)
            {
                var mirroredStart = 180.0 - end;
                var mirroredEnd = 180.0 - start;
                start = mirroredStart;
                end = mirroredEnd;
            }

            start = NormalizeDegrees(start);
            end = NormalizeDegrees(end);

            if (!AllFinite(cx, cy, arc.Radius, start, end) || arc.Radius <= Epsilon)
            {
                skippedDegenerate++;
                continue;
            }

            arcs.Add(new DxfArcData(arc.Handle ?? string.Empty, LayerName(arc), cx, cy, arc.Radius, start, end));
        }

        if (skippedNormals > 0)
        {
            warnings.Add(skippedNormals + " circle or arc entities lie in a tilted 3D plane and were skipped.");
        }

        if (skippedDegenerate > 0)
        {
            warnings.Add(skippedDegenerate + " zero-length or invalid entities were skipped.");
        }

        var units = document.DrawingVariables.InsUnits.ToString();

        var result = new DxfParseResult
        {
            Units = units,
            Lines = lines,
            Circles = circles,
            Arcs = arcs,
            Warnings = warnings
        };

        if (lines.Count + circles.Count + arcs.Count == 0)
        {
            return result;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        void Include(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var l in lines)
        {
            Include(l.X1, l.Y1);
            Include(l.X2, l.Y2);
        }

        foreach (var c in circles)
        {
            Include(c.CenterX - c.Radius, c.CenterY - c.Radius);
            Include(c.CenterX + c.Radius, c.CenterY + c.Radius);
        }

        foreach (var a in arcs)
        {
            var s = a.StartPoint;
            var e = a.EndPoint;
            Include(s.X, s.Y);
            Include(e.X, e.Y);

            foreach (var quadrant in new[] { 0.0, 90.0, 180.0, 270.0 })
            {
                if (AngleWithinArc(quadrant, a.StartAngleDegrees, a.SweepDegrees))
                {
                    var radians = quadrant * Math.PI / 180.0;
                    Include(a.CenterX + a.Radius * Math.Cos(radians), a.CenterY + a.Radius * Math.Sin(radians));
                }
            }
        }

        return new DxfParseResult
        {
            Units = units,
            Lines = lines,
            Circles = circles,
            Arcs = arcs,
            Warnings = warnings,
            Contours = DxfContourJoiner.Join(lines, arcs, Math.Max(maxX - minX, maxY - minY)),
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }

    public IReadOnlyList<DxfPartFileResult> SplitByLayers(
        Stream dxfStream,
        IEnumerable<DxfPartRequest> parts,
        string outputFolder,
        CancellationToken cancellationToken = default,
        DxfSheetOptions? sheetOptions = null)
    {
        if (dxfStream is null)
        {
            throw new ArgumentNullException(nameof(dxfStream));
        }

        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            throw new ArgumentException("Output folder is required.", nameof(outputFolder));
        }

        var document = LoadDocument(dxfStream);

        Directory.CreateDirectory(outputFolder);

        var results = new List<DxfPartFileResult>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (part is null || string.IsNullOrWhiteSpace(part.Layer))
            {
                continue;
            }

            if (IsNonPartLayer(part.Layer))
            {
                continue;
            }

            var entities = CollectLayerEntities(document, part.Layer);

            if (entities.Count == 0)
            {
                continue;
            }

            var partDocument = new DxfDocument();
            partDocument.DrawingVariables.InsUnits = DrawingUnits.Millimeters;

            var added = 0;

            foreach (var copy in BuildNormalizedViews(entities))
            {
                partDocument.Entities.Add(copy);
                added++;
            }

            if (added == 0)
            {
                continue;
            }

            var baseName = SanitizeFileName(part.PartName);
            var uniqueName = baseName;
            var counter = 2;

            while (!usedNames.Add(uniqueName))
            {
                uniqueName = baseName + "_" + counter.ToString(CultureInfo.InvariantCulture);
                counter++;
            }

            var filePath = Path.Combine(outputFolder, uniqueName + ".dxf");

            if (!partDocument.Save(filePath))
            {
                continue;
            }

            results.Add(new DxfPartFileResult(part.PartName, part.Layer, filePath, added));
        }

        return results;
    }

    public static string SanitizeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();

        foreach (var ch in name ?? string.Empty)
        {
            sb.Append(char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');

        if (cleaned.Length > 100)
        {
            cleaned = cleaned.Substring(0, 100).TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            return "part";
        }

        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        if (Array.IndexOf(reserved, cleaned.ToUpperInvariant()) >= 0)
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    private static DxfDocument LoadDocument(Stream dxfStream)
    {
        DxfDocument? document;

        try
        {
            document = DxfDocument.Load(dxfStream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The DXF file could not be read: " + ex.Message, ex);
        }

        if (document is null)
        {
            throw new InvalidOperationException("The DXF file could not be read. Export it again as ASCII or binary DXF (AutoCAD R12 or newer).");
        }

        return document;
    }

    private const int MaxClusterableEntities = 4000;

    private static readonly string[] NonPartLayerMarkers = { "TITLE", "DEFPOINTS", "VIEWPORT" };

    private static bool IsNonPartLayer(string layer)
    {
        var name = (layer ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return true;
        }

        if (name.StartsWith("DIM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var marker in NonPartLayerMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PlacedEntity
    {
        public PlacedEntity(EntityObject entity, double minX, double minY, double maxX, double maxY)
        {
            Entity = entity;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public EntityObject Entity { get; }

        public double MinX { get; }

        public double MinY { get; }

        public double MaxX { get; }

        public double MaxY { get; }
    }

    private sealed class ViewCluster
    {
        public List<PlacedEntity> Items { get; } = new();

        public double MinX { get; private set; } = double.MaxValue;

        public double MinY { get; private set; } = double.MaxValue;

        public double MaxX { get; private set; } = double.MinValue;

        public double MaxY { get; private set; } = double.MinValue;

        public double Width => MaxX - MinX;

        public double Height => MaxY - MinY;

        public void Add(PlacedEntity item)
        {
            Items.Add(item);

            if (item.MinX < MinX) MinX = item.MinX;
            if (item.MinY < MinY) MinY = item.MinY;
            if (item.MaxX > MaxX) MaxX = item.MaxX;
            if (item.MaxY > MaxY) MaxY = item.MaxY;
        }
    }

    private static List<EntityObject> BuildNormalizedViews(List<EntityObject> entities)
    {
        var placed = new List<PlacedEntity>();

        foreach (var entity in entities)
        {
            if (entity.Clone() is not EntityObject copy)
            {
                continue;
            }

            if (!TryGetBounds(copy, out var minX, out var minY, out var maxX, out var maxY))
            {
                continue;
            }

            placed.Add(new PlacedEntity(copy, minX, minY, maxX, maxY));
        }

        var result = new List<EntityObject>();

        if (placed.Count == 0)
        {
            return result;
        }

        var overallMinX = placed.Min(p => p.MinX);
        var overallMinY = placed.Min(p => p.MinY);
        var overallMaxX = placed.Max(p => p.MaxX);
        var overallMaxY = placed.Max(p => p.MaxY);
        var extent = Math.Max(overallMaxX - overallMinX, overallMaxY - overallMinY);
        var linkGap = Math.Max(1.0, extent * 0.01);

        var clusters = RemoveDuplicateClusters(BuildClusters(placed, linkGap));

        clusters.Sort((a, b) =>
        {
            var byX = a.MinX.CompareTo(b.MinX);
            return byX != 0 ? byX : a.MinY.CompareTo(b.MinY);
        });

        var largestSide = clusters.Max(c => Math.Max(c.Width, c.Height));
        var layoutGap = Math.Max(10.0, largestSide * 0.05);
        var cursorX = 0.0;

        foreach (var cluster in clusters)
        {
            var shift = new Vector3(cursorX - cluster.MinX, -cluster.MinY, 0.0);

            foreach (var item in cluster.Items)
            {
                try
                {
                    item.Entity.TransformBy(Matrix3.Identity, shift);
                    result.Add(item.Entity);
                }
                catch (Exception)
                {
                    // An entity that cannot be moved is left out rather than written at its old position.
                }
            }

            cursorX += cluster.Width + layoutGap;
        }

        return result;
    }

    private static List<ViewCluster> BuildClusters(List<PlacedEntity> items, double gap)
    {
        var clusters = new List<ViewCluster>();

        if (items.Count > MaxClusterableEntities)
        {
            var single = new ViewCluster();

            foreach (var item in items)
            {
                single.Add(item);
            }

            clusters.Add(single);
            return clusters;
        }

        var assigned = new bool[items.Count];

        for (var seed = 0; seed < items.Count; seed++)
        {
            if (assigned[seed])
            {
                continue;
            }

            var cluster = new ViewCluster();
            var queue = new Queue<int>();

            assigned[seed] = true;
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var current = items[queue.Dequeue()];
                cluster.Add(current);

                for (var other = 0; other < items.Count; other++)
                {
                    if (assigned[other])
                    {
                        continue;
                    }

                    var candidate = items[other];

                    if (current.MinX - gap <= candidate.MaxX
                        && candidate.MinX - gap <= current.MaxX
                        && current.MinY - gap <= candidate.MaxY
                        && candidate.MinY - gap <= current.MaxY)
                    {
                        assigned[other] = true;
                        queue.Enqueue(other);
                    }
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static List<ViewCluster> RemoveDuplicateClusters(List<ViewCluster> clusters)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<ViewCluster>();

        foreach (var cluster in clusters)
        {
            var signature = string.Join(
                "|",
                cluster.Items.Count.ToString(CultureInfo.InvariantCulture),
                Math.Round(cluster.Width, 1).ToString("0.0", CultureInfo.InvariantCulture),
                Math.Round(cluster.Height, 1).ToString("0.0", CultureInfo.InvariantCulture),
                cluster.Items.Count(i => i.Entity is Line).ToString(CultureInfo.InvariantCulture),
                cluster.Items.Count(i => i.Entity is Circle).ToString(CultureInfo.InvariantCulture),
                cluster.Items.Count(i => i.Entity is Arc).ToString(CultureInfo.InvariantCulture),
                cluster.Items.Count(i => i.Entity is Polyline2D).ToString(CultureInfo.InvariantCulture));

            if (seen.Add(signature))
            {
                kept.Add(cluster);
            }
        }

        return kept;
    }

    private static bool TryGetBounds(EntityObject entity, out double minX, out double minY, out double maxX, out double maxY)
    {
        var loX = double.MaxValue;
        var loY = double.MaxValue;
        var hiX = double.MinValue;
        var hiY = double.MinValue;

        void Include(double x, double y)
        {
            if (x < loX) loX = x;
            if (y < loY) loY = y;
            if (x > hiX) hiX = x;
            if (y > hiY) hiY = y;
        }

        switch (entity)
        {
            case Line line:
                Include(line.StartPoint.X, line.StartPoint.Y);
                Include(line.EndPoint.X, line.EndPoint.Y);
                break;

            case Circle circle:
                {
                    var c = MathHelper.Transform(circle.Center, circle.Normal, CoordinateSystem.Object, CoordinateSystem.World);
                    Include(c.X - circle.Radius, c.Y - circle.Radius);
                    Include(c.X + circle.Radius, c.Y + circle.Radius);
                    break;
                }

            case Arc arc:
                {
                    var c = MathHelper.Transform(arc.Center, arc.Normal, CoordinateSystem.Object, CoordinateSystem.World);
                    var upright = Math.Abs(arc.Normal.X) < 1e-9 && Math.Abs(arc.Normal.Y) < 1e-9 && arc.Normal.Z > 0.0;

                    if (!upright)
                    {
                        Include(c.X - arc.Radius, c.Y - arc.Radius);
                        Include(c.X + arc.Radius, c.Y + arc.Radius);
                        break;
                    }

                    var start = NormalizeDegrees(arc.StartAngle);
                    var sweep = NormalizeDegrees(arc.EndAngle - arc.StartAngle);

                    if (sweep <= 1e-9)
                    {
                        sweep = 360.0;
                    }

                    var startRad = start * Math.PI / 180.0;
                    var endRad = (start + sweep) * Math.PI / 180.0;

                    Include(c.X + arc.Radius * Math.Cos(startRad), c.Y + arc.Radius * Math.Sin(startRad));
                    Include(c.X + arc.Radius * Math.Cos(endRad), c.Y + arc.Radius * Math.Sin(endRad));

                    foreach (var quadrant in new[] { 0.0, 90.0, 180.0, 270.0 })
                    {
                        if (AngleWithinArc(quadrant, start, sweep))
                        {
                            var radians = quadrant * Math.PI / 180.0;
                            Include(c.X + arc.Radius * Math.Cos(radians), c.Y + arc.Radius * Math.Sin(radians));
                        }
                    }

                    break;
                }

            case Polyline2D poly:
                {
                    List<Vector2> points;

                    try
                    {
                        points = poly.PolygonalVertexes(6);
                    }
                    catch (Exception)
                    {
                        points = poly.Vertexes.Select(v => v.Position).ToList();
                    }

                    foreach (var point in points)
                    {
                        var world = MathHelper.Transform(point, poly.Normal, poly.Elevation);
                        Include(world.X, world.Y);
                    }

                    break;
                }
        }

        minX = loX;
        minY = loY;
        maxX = hiX;
        maxY = hiY;

        return AllFinite(loX, loY, hiX, hiY) && hiX >= loX && hiY >= loY;
    }

    private static List<EntityObject> CollectLayerEntities(DxfDocument document, string layer)
    {
        var result = new List<EntityObject>();
        var entities = document.Entities;

        if (entities is null)
        {
            return result;
        }

        AddLayerMatches(result, entities.Lines, layer);
        AddLayerMatches(result, entities.Polylines2D, layer);
        AddLayerMatches(result, entities.Circles, layer);
        AddLayerMatches(result, entities.Arcs, layer);

        return result;
    }

    private static void AddLayerMatches<T>(List<EntityObject> target, IEnumerable<T>? source, string layer)
        where T : EntityObject
    {
        if (source is null)
        {
            return;
        }

        foreach (var entity in source)
        {
            if (entity is not null
                && string.Equals(LayerName(entity), layer, StringComparison.OrdinalIgnoreCase))
            {
                target.Add(entity);
            }
        }
    }

    private static string LayerName(EntityObject entity)
    {
        var name = entity.Layer?.Name;
        return string.IsNullOrWhiteSpace(name) ? "0" : name;
    }

    private static bool TryGetPlanarCenter(Vector3 normal, Vector3 center, out double x, out double y, out bool flipped)
    {
        x = center.X;
        y = center.Y;
        flipped = false;

        if (Math.Abs(normal.X) <= 1e-6 && Math.Abs(normal.Y) <= 1e-6)
        {
            if (normal.Z >= 0.0)
            {
                return true;
            }

            x = -center.X;
            flipped = true;
            return true;
        }

        return false;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var value = degrees % 360.0;

        if (value < 0.0)
        {
            value += 360.0;
        }

        return value;
    }

    private static bool AngleWithinArc(double angle, double startDegrees, double sweepDegrees)
    {
        var offset = NormalizeDegrees(angle - startDegrees);
        return offset <= sweepDegrees + 1e-9;
    }

    private static bool AllFinite(params double[] values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value))
            {
                return false;
            }
        }

        return true;
    }
}