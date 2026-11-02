using System.Globalization;
using System.IO.Compression;
using System.Text;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using IxMilia.Dxf;
using IxMilia.Dxf.Blocks;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public class PartDxfExportService : IPartDxfExportService
{
    private const int MaxBlockDepth = 8;
    private const int MaxEntitiesPerPart = 100_000;
    private const int MaxInsertInstances = 2_000;
    private const string CutLayer = "CUT";
    private const string NotesLayer = "NOTES";
    private const double Epsilon = 1e-9;

    private static readonly HashSet<string> SilentlySkipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "TEXT", "MTEXT", "HATCH", "ATTRIB", "ATTDEF", "SEQEND", "VERTEX"
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IDxfCompatibilityService _compatibility;
    private readonly IBlockBreakdownService _breakdown;
    private readonly ILogger<PartDxfExportService> _logger;

    public PartDxfExportService(
        IUnitOfWork unitOfWork,
        IFileStorageService storage,
        IDxfCompatibilityService compatibility,
        IBlockBreakdownService breakdown,
        ILogger<PartDxfExportService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _compatibility = compatibility;
        _breakdown = breakdown;
        _logger = logger;
    }

    public async Task<PartDxfPackageDto> BuildPartFilesAsync(PartDxfRequestDto request, CancellationToken cancellationToken = default)
    {
        var package = new PartDxfPackageDto { ProjectId = request.ProjectId };

        try
        {
            var source = await LoadSourceAsync(request.ProjectId, cancellationToken);
            package.ProjectTitle = source.Title;
            package.Warnings.AddRange(source.Warnings);

            var breakdown = await _breakdown.BuildBreakdownAsync(request.ProjectId, cancellationToken);
            if (!breakdown.Success)
            {
                package.ErrorMessage = breakdown.ErrorMessage ?? "The block breakdown could not be built.";
                return package;
            }

            var blocks = IndexBlocks(source.File);
            var specs = IndexSpecs(request.Specs);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var part in breakdown.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!blocks.TryGetValue(part.BlockName, out var block))
                {
                    continue;
                }

                specs.TryGetValue(part.BlockName, out var spec);

                var file = BuildPart(source, block, part.BlockName, part.Quantity, spec, usedNames, package.Warnings);
                if (file is not null)
                {
                    package.Files.Add(file);
                }
            }

            if (package.Files.Count == 0)
            {
                package.ErrorMessage = "No part geometry could be isolated from this drawing's blocks.";
                return package;
            }

            package.Success = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            package.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Part DXF package failed for project {ProjectId}.", request.ProjectId);
            package.ErrorMessage = "The part drawings could not be generated.";
        }

        return package;
    }

    public async Task<PartDxfFileDto?> BuildPartFileAsync(int projectId, PartDxfSpecDto spec, CancellationToken cancellationToken = default)
    {
        try
        {
            var source = await LoadSourceAsync(projectId, cancellationToken);
            var blocks = IndexBlocks(source.File);

            if (string.IsNullOrWhiteSpace(spec.BlockName) || !blocks.TryGetValue(spec.BlockName, out var block))
            {
                return null;
            }

            var breakdown = await _breakdown.BuildBreakdownAsync(projectId, cancellationToken);
            var quantity = breakdown.Parts
                .FirstOrDefault(p => string.Equals(p.BlockName, block.Name, StringComparison.OrdinalIgnoreCase))
                ?.Quantity ?? 1;

            return BuildPart(source, block, block.Name, quantity, spec, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new List<string>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Part DXF failed for project {ProjectId}, block {Block}.", projectId, spec.BlockName);
            return null;
        }
    }

    public async Task<byte[]?> BuildPackageZipAsync(PartDxfRequestDto request, CancellationToken cancellationToken = default)
    {
        var package = await BuildPartFilesAsync(request, cancellationToken);
        if (!package.Success || package.Files.Count == 0)
        {
            return null;
        }

        try
        {
            var source = await LoadSourceAsync(request.ProjectId, cancellationToken);

            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var assemblyEntry = zip.CreateEntry("assembly_" + SafeName(package.ProjectTitle) + ".dxf", CompressionLevel.Optimal);
                await using (var entryStream = assemblyEntry.Open())
                {
                    await entryStream.WriteAsync(source.Original, cancellationToken);
                }

                foreach (var file in package.Files)
                {
                    var entry = zip.CreateEntry("parts/" + file.FileName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(file.Content, cancellationToken);
                }
            }

            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Part DXF zip failed for project {ProjectId}.", request.ProjectId);
            return null;
        }
    }

    private async Task<SourceDrawing> LoadSourceAsync(int projectId, CancellationToken cancellationToken)
    {
        var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("The project was not found.");

        if (project.SourceFileType != SourceFileType.Dxf)
        {
            throw new InvalidOperationException("Part drawings can only be generated from a DXF drawing.");
        }

        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("The stored drawing file is missing.");
        }

        byte[] original;
        await using (var stream = await _storage.OpenReadAsync(path, cancellationToken))
        {
            if (stream is null)
            {
                throw new InvalidOperationException("The stored drawing file could not be opened.");
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            original = buffer.ToArray();
        }

        var warnings = new List<string>();

        using var prepared = await _compatibility.PrepareAsync(new MemoryStream(original, writable: false), cancellationToken);
        if (prepared.IsDwg)
        {
            throw new InvalidOperationException("DWG files are not supported. Export the drawing as DXF.");
        }

        DxfFile file;
        try
        {
            prepared.Stream.Position = 0;
            file = DxfFile.Load(prepared.Stream);
        }
        catch (Exception) when (prepared.HeaderPatched)
        {
            using var raw = new MemoryStream(original, writable: false);
            file = DxfFile.Load(raw);
            warnings.Add("The patched DXF header could not be read; the original file was used instead.");
        }

        return new SourceDrawing(file, original, project.Title, warnings);
    }

    private static Dictionary<string, DxfBlock> IndexBlocks(DxfFile file)
    {
        var blocks = new Dictionary<string, DxfBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in file.Blocks)
        {
            if (!string.IsNullOrEmpty(block.Name) && !block.Name.StartsWith('*'))
            {
                blocks[block.Name] = block;
            }
        }

        return blocks;
    }

    private static Dictionary<string, PartDxfSpecDto> IndexSpecs(IEnumerable<PartDxfSpecDto> specs)
    {
        var map = new Dictionary<string, PartDxfSpecDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            if (!string.IsNullOrWhiteSpace(spec.BlockName))
            {
                map[spec.BlockName] = spec;
            }
        }

        return map;
    }

    private PartDxfFileDto? BuildPart(
        SourceDrawing source,
        DxfBlock block,
        string blockName,
        int quantity,
        PartDxfSpecDto? spec,
        HashSet<string> usedNames,
        List<string> warnings)
    {
        var blocks = IndexBlocks(source.File);
        var sink = new Collected();

        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { blockName };
        var start = Affine.Translation(-block.BasePoint.X, -block.BasePoint.Y);

        Collect(block.Entities, start, 0, stack, blocks, sink);

        foreach (var skipped in sink.SkippedTypes.OrderBy(s => s))
        {
            warnings.Add(blockName + ": " + skipped + " entities are not supported and were left out.");
        }

        if (sink.IsEmpty || !sink.TryGetBounds(out var minX, out var minY, out var maxX, out var maxY))
        {
            warnings.Add(blockName + ": no cut geometry was found in this block.");
            return null;
        }

        var dx = -minX;
        var dy = -minY;
        var width = maxX - minX;
        var height = maxY - minY;

        var dxf = new DxfFile();
        dxf.Header.Version = DxfAcadVersion.R2000;
        dxf.Header.DefaultDrawingUnits = source.File.Header.DefaultDrawingUnits;
        dxf.Layers.Add(new DxfLayer(CutLayer));
        dxf.Layers.Add(new DxfLayer(NotesLayer));

        foreach (var line in sink.Lines)
        {
            dxf.Entities.Add(new DxfLine(
                new DxfPoint(line.X1 + dx, line.Y1 + dy, 0),
                new DxfPoint(line.X2 + dx, line.Y2 + dy, 0))
            { Layer = CutLayer });
        }

        foreach (var circle in sink.Circles)
        {
            dxf.Entities.Add(new DxfCircle(new DxfPoint(circle.Cx + dx, circle.Cy + dy, 0), circle.R) { Layer = CutLayer });
        }

        foreach (var arc in sink.Arcs)
        {
            dxf.Entities.Add(new DxfArc(new DxfPoint(arc.Cx + dx, arc.Cy + dy, 0), arc.R, arc.StartAngle, arc.EndAngle) { Layer = CutLayer });
        }

        foreach (var poly in sink.Polylines)
        {
            var vertices = poly.Points.Select(p => new DxfLwPolylineVertex { X = p.X + dx, Y = p.Y + dy, Bulge = p.Bulge });
            dxf.Entities.Add(new DxfLwPolyline(vertices) { IsClosed = poly.Closed, Layer = CutLayer });
        }

        AddNotes(dxf, blockName, quantity, spec, source.Title, width, height);

        byte[] content;
        using (var stream = new MemoryStream())
        {
            dxf.Save(stream);
            content = stream.ToArray();
        }

        return new PartDxfFileDto
        {
            BlockName = blockName,
            FileName = UniqueFileName("part_" + SafeName(blockName), usedNames),
            Quantity = quantity,
            Width = Math.Round(width, 3),
            Height = Math.Round(height, 3),
            Content = content
        };
    }

    private static void AddNotes(DxfFile dxf, string blockName, int quantity, PartDxfSpecDto? spec, string projectTitle, double width, double height)
    {
        var textHeight = Math.Clamp(Math.Max(width, height) * 0.025, 2.5, 12.0);

        var material = string.IsNullOrWhiteSpace(spec?.Material) ? "NOT SPECIFIED" : spec!.Material!.Trim();
        var thickness = spec?.Thickness is > 0
            ? " - " + spec.Thickness.Value.ToString("0.##", CultureInfo.InvariantCulture) + " mm THK"
            : string.Empty;

        var lines = new[]
        {
            "PART: " + blockName.ToUpperInvariant(),
            "MATERIAL: " + material.ToUpperInvariant() + thickness,
            "QTY: " + quantity.ToString(CultureInfo.InvariantCulture),
            "SIZE: " + width.ToString("0.##", CultureInfo.InvariantCulture) + " x " + height.ToString("0.##", CultureInfo.InvariantCulture),
            "PROJECT: " + projectTitle.ToUpperInvariant()
        };

        for (var i = 0; i < lines.Length; i++)
        {
            var y = -textHeight * 2.0 * (i + 1);
            dxf.Entities.Add(new DxfText(new DxfPoint(0, y, 0), textHeight, lines[i]) { Layer = NotesLayer });
        }
    }

    private static void Collect(
        IEnumerable<DxfEntity> entities,
        Affine matrix,
        int depth,
        HashSet<string> stack,
        Dictionary<string, DxfBlock> blocks,
        Collected sink)
    {
        foreach (var entity in entities)
        {
            if (sink.Count >= MaxEntitiesPerPart)
            {
                sink.SkippedTypes.Add("EXCESS");
                return;
            }

            switch (entity)
            {
                case DxfLine line:
                    {
                        var (x1, y1) = matrix.Apply(line.P1.X, line.P1.Y);
                        var (x2, y2) = matrix.Apply(line.P2.X, line.P2.Y);
                        sink.Lines.Add(new LineG(x1, y1, x2, y2));
                        break;
                    }

                case DxfArc arc:
                    AddArc(sink, matrix, arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle);
                    break;

                case DxfCircle circle:
                    AddCircle(sink, matrix, circle.Center.X, circle.Center.Y, circle.Radius);
                    break;

                case DxfLwPolyline lw:
                    {
                        if (lw.Vertices.Count < 2)
                        {
                            break;
                        }

                        var flip = matrix.Determinant < 0 ? -1.0 : 1.0;
                        var points = new List<PolyPoint>();
                        foreach (var vertex in lw.Vertices)
                        {
                            var (x, y) = matrix.Apply(vertex.X, vertex.Y);
                            points.Add(new PolyPoint(x, y, vertex.Bulge * flip));
                        }

                        sink.Polylines.Add(new PolyG(points, lw.IsClosed));
                        break;
                    }

                case DxfPolyline polyline:
                    {
                        if (polyline.IsPolyfaceMesh || polyline.Is3DPolygonMesh || polyline.Vertices.Count < 2)
                        {
                            break;
                        }

                        var flip = matrix.Determinant < 0 ? -1.0 : 1.0;
                        var points = new List<PolyPoint>();
                        foreach (var vertex in polyline.Vertices)
                        {
                            var (x, y) = matrix.Apply(vertex.Location.X, vertex.Location.Y);
                            points.Add(new PolyPoint(x, y, vertex.Bulge * flip));
                        }

                        sink.Polylines.Add(new PolyG(points, polyline.IsClosed));
                        break;
                    }

                case DxfInsert insert:
                    CollectInsert(insert, matrix, depth, stack, blocks, sink);
                    break;

                case DxfDimensionBase:
                    break;

                default:
                    if (!SilentlySkipped.Contains(entity.EntityTypeString))
                    {
                        sink.SkippedTypes.Add(entity.EntityTypeString);
                    }

                    break;
            }
        }
    }

    private static void CollectInsert(
        DxfInsert insert,
        Affine matrix,
        int depth,
        HashSet<string> stack,
        Dictionary<string, DxfBlock> blocks,
        Collected sink)
    {
        var name = insert.Name;
        if (string.IsNullOrEmpty(name) || !blocks.TryGetValue(name, out var child))
        {
            return;
        }

        if (depth >= MaxBlockDepth || stack.Contains(name))
        {
            sink.SkippedTypes.Add("INSERT");
            return;
        }

        var columns = Math.Max((int)insert.ColumnCount, 1);
        var rows = Math.Max((int)insert.RowCount, 1);
        if ((long)columns * rows > MaxInsertInstances)
        {
            sink.SkippedTypes.Add("INSERT");
            return;
        }

        var angle = insert.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        stack.Add(name);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var offsetX = column * insert.ColumnSpacing;
                var offsetY = row * insert.RowSpacing;
                var locationX = insert.Location.X + cos * offsetX - sin * offsetY;
                var locationY = insert.Location.Y + sin * offsetX + cos * offsetY;

                var a = cos * insert.XScaleFactor;
                var b = sin * insert.XScaleFactor;
                var c = -sin * insert.YScaleFactor;
                var d = cos * insert.YScaleFactor;
                var tx = locationX - (a * child.BasePoint.X + c * child.BasePoint.Y);
                var ty = locationY - (b * child.BasePoint.X + d * child.BasePoint.Y);

                var local = new Affine(a, b, c, d, tx, ty);
                Collect(child.Entities, matrix.Then(local), depth + 1, stack, blocks, sink);
            }
        }

        stack.Remove(name);
    }

    private static void AddCircle(Collected sink, Affine matrix, double cx, double cy, double radius)
    {
        if (matrix.IsSimilarity)
        {
            var (x, y) = matrix.Apply(cx, cy);
            sink.Circles.Add(new CircleG(x, y, radius * matrix.Scale));
            return;
        }

        var points = new List<PolyPoint>();
        for (var i = 0; i < 48; i++)
        {
            var t = 2.0 * Math.PI * i / 48.0;
            var (x, y) = matrix.Apply(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t));
            points.Add(new PolyPoint(x, y, 0));
        }

        sink.Polylines.Add(new PolyG(points, true));
    }

    private static void AddArc(Collected sink, Affine matrix, double cx, double cy, double radius, double startDeg, double endDeg)
    {
        if (matrix.IsSimilarity)
        {
            var (x, y) = matrix.Apply(cx, cy);
            var start = matrix.TransformAngle(startDeg);
            var end = matrix.TransformAngle(endDeg);

            if (matrix.Determinant < 0)
            {
                (start, end) = (end, start);
            }

            sink.Arcs.Add(new ArcG(x, y, radius * matrix.Scale, start, end));
            return;
        }

        var sweep = endDeg - startDeg;
        while (sweep <= 0)
        {
            sweep += 360.0;
        }

        var steps = Math.Max((int)Math.Ceiling(sweep / 5.0), 2);
        var points = new List<PolyPoint>();
        for (var i = 0; i <= steps; i++)
        {
            var t = (startDeg + sweep * i / steps) * Math.PI / 180.0;
            var (x, y) = matrix.Apply(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t));
            points.Add(new PolyPoint(x, y, 0));
        }

        sink.Polylines.Add(new PolyG(points, false));
    }

    private static string UniqueFileName(string baseName, HashSet<string> used)
    {
        var name = baseName + ".dxf";
        var counter = 2;

        while (!used.Add(name))
        {
            name = baseName + "_" + counter.ToString(CultureInfo.InvariantCulture) + ".dxf";
            counter++;
        }

        return name;
    }

    private static string SafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "drawing";
        }

        var builder = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private sealed record SourceDrawing(DxfFile File, byte[] Original, string Title, List<string> Warnings);

    private sealed record LineG(double X1, double Y1, double X2, double Y2);

    private sealed record CircleG(double Cx, double Cy, double R);

    private sealed record ArcG(double Cx, double Cy, double R, double StartAngle, double EndAngle);

    private sealed record PolyPoint(double X, double Y, double Bulge);

    private sealed record PolyG(List<PolyPoint> Points, bool Closed);

    private readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static Affine Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

        public double Determinant => A * D - B * C;

        public double Scale => Math.Sqrt(Math.Abs(Determinant));

        public bool IsSimilarity
        {
            get
            {
                var sx = Math.Sqrt(A * A + B * B);
                var sy = Math.Sqrt(C * C + D * D);
                if (sx < Epsilon || sy < Epsilon)
                {
                    return false;
                }

                var dot = (A * C + B * D) / (sx * sy);
                return Math.Abs(sx - sy) <= 1e-6 * Math.Max(sx, sy) && Math.Abs(dot) <= 1e-6;
            }
        }

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);

        public double TransformAngle(double degrees)
        {
            var radians = degrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var x = A * cos + C * sin;
            var y = B * cos + D * sin;
            var result = Math.Atan2(y, x) * 180.0 / Math.PI;
            return result < 0 ? result + 360.0 : result;
        }

        public Affine Then(Affine local) => new(
            A * local.A + C * local.B,
            B * local.A + D * local.B,
            A * local.C + C * local.D,
            B * local.C + D * local.D,
            A * local.Tx + C * local.Ty + Tx,
            B * local.Tx + D * local.Ty + Ty);
    }

    private sealed class Collected
    {
        public List<LineG> Lines { get; } = new();
        public List<CircleG> Circles { get; } = new();
        public List<ArcG> Arcs { get; } = new();
        public List<PolyG> Polylines { get; } = new();
        public HashSet<string> SkippedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Count => Lines.Count + Circles.Count + Arcs.Count + Polylines.Count;
        public bool IsEmpty => Count == 0;

        public bool TryGetBounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            var lowX = double.MaxValue;
            var lowY = double.MaxValue;
            var highX = double.MinValue;
            var highY = double.MinValue;

            void Extend(double x, double y)
            {
                lowX = Math.Min(lowX, x);
                lowY = Math.Min(lowY, y);
                highX = Math.Max(highX, x);
                highY = Math.Max(highY, y);
            }

            foreach (var line in Lines)
            {
                Extend(line.X1, line.Y1);
                Extend(line.X2, line.Y2);
            }

            foreach (var circle in Circles)
            {
                Extend(circle.Cx - circle.R, circle.Cy - circle.R);
                Extend(circle.Cx + circle.R, circle.Cy + circle.R);
            }

            foreach (var arc in Arcs)
            {
                ExtendArc(arc, Extend);
            }

            foreach (var poly in Polylines)
            {
                for (var i = 0; i < poly.Points.Count; i++)
                {
                    var p = poly.Points[i];
                    Extend(p.X, p.Y);

                    var hasNext = i + 1 < poly.Points.Count || poly.Closed;
                    if (!hasNext || Math.Abs(p.Bulge) < Epsilon)
                    {
                        continue;
                    }

                    var q = poly.Points[(i + 1) % poly.Points.Count];
                    foreach (var (x, y) in BulgePoints(p.X, p.Y, q.X, q.Y, p.Bulge))
                    {
                        Extend(x, y);
                    }
                }
            }

            minX = lowX;
            minY = lowY;
            maxX = highX;
            maxY = highY;

            return lowX <= highX && lowY <= highY;
        }

        private static void ExtendArc(ArcG arc, Action<double, double> extend)
        {
            var start = arc.StartAngle;
            var sweep = arc.EndAngle - arc.StartAngle;
            while (sweep <= 0)
            {
                sweep += 360.0;
            }

            void Point(double degrees)
            {
                var t = degrees * Math.PI / 180.0;
                extend(arc.Cx + arc.R * Math.Cos(t), arc.Cy + arc.R * Math.Sin(t));
            }

            Point(start);
            Point(start + sweep);

            foreach (var quadrant in new[] { 0.0, 90.0, 180.0, 270.0 })
            {
                var distance = (quadrant - start) % 360.0;
                if (distance < 0)
                {
                    distance += 360.0;
                }

                if (distance <= sweep)
                {
                    Point(quadrant);
                }
            }
        }

        private static IEnumerable<(double X, double Y)> BulgePoints(double x1, double y1, double x2, double y2, double bulge)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12)
            {
                yield break;
            }

            var theta = 4.0 * Math.Atan(bulge);
            var radius = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));
            var side = bulge > 0 ? 1.0 : -1.0;
            var offset = radius * Math.Cos(Math.Abs(theta) / 2.0);
            var cx = (x1 + x2) / 2.0 + side * (-dy / chord) * offset;
            var cy = (y1 + y2) / 2.0 + side * (dx / chord) * offset;
            var startAngle = Math.Atan2(y1 - cy, x1 - cx);

            const int steps = 16;
            for (var i = 1; i < steps; i++)
            {
                var t = startAngle + theta * i / steps;
                yield return (cx + radius * Math.Cos(t), cy + radius * Math.Sin(t));
            }
        }
    }
}