using System.Globalization;
using System.Text;
using netDxf;
using netDxf.Entities;
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

public sealed record DxfPartFileResult(string PartName, string Layer, string FilePath, int EntityCount, string? PdfFilePath = null);

public sealed record DxfSheetOptions(
    string? ProjectName,
    string? Material,
    string? DrawingNumberPrefix,
    PdfPaperSize PaperSize = PdfPaperSize.A3Landscape);

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

    private readonly IPdfDrawingSheetService? _pdfSheetService;

    public DxfGeometryParser(IPdfDrawingSheetService? pdfSheetService = null)
    {
        _pdfSheetService = pdfSheetService;
    }

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

        foreach (var line in document.Entities.Lines)
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

        foreach (var arc in document.Entities.Arcs)
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

            var entities = CollectLayerEntities(document, part.Layer);

            if (entities.Count == 0)
            {
                continue;
            }

            var partDocument = new DxfDocument();
            partDocument.DrawingVariables.InsUnits = document.DrawingVariables.InsUnits;

            var added = 0;

            foreach (var entity in entities)
            {
                if (entity.Clone() is EntityObject copy)
                {
                    partDocument.Entities.Add(copy);
                    added++;
                }
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

            string? pdfPath = null;

            if (_pdfSheetService is not null)
            {
                pdfPath = TryRenderPdf(filePath, uniqueName, part, sheetOptions, cancellationToken);
            }

            results.Add(new DxfPartFileResult(part.PartName, part.Layer, filePath, added, pdfPath));
        }

        return results;
    }

    private string? TryRenderPdf(
        string dxfPath,
        string uniqueName,
        DxfPartRequest part,
        DxfSheetOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            DxfParseResult geometry;

            using (var dxfStream = new FileStream(dxfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                geometry = Parse(dxfStream, cancellationToken);
            }

            var metadata = new PdfSheetMetadata(
                part.PartName,
                options?.ProjectName,
                BuildDrawingNumber(options?.DrawingNumberPrefix, uniqueName),
                options?.Material,
                DateTime.Today,
                part.Layer);

            var pdfPath = Path.ChangeExtension(dxfPath, ".pdf");

            _pdfSheetService!.RenderToFile(
                geometry,
                metadata,
                pdfPath,
                options?.PaperSize ?? PdfPaperSize.A3Landscape);

            return pdfPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildDrawingNumber(string? prefix, string name)
    {
        return string.IsNullOrWhiteSpace(prefix)
            ? name
            : prefix.Trim() + "-" + name;
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