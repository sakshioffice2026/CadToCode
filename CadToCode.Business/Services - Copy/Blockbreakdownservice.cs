using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using IxMilia.Dxf;
using IxMilia.Dxf.Blocks;
using IxMilia.Dxf.Entities;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace CadToCode.Business.Services;

public class BlockBreakdownService : IBlockBreakdownService
{
    private const int MaxBlockDepth = 8;
    private const int MaxPlacementsPerBlock = 500;
    private const int MaxLabelsPerBlock = 10;
    private const long MaxQuantity = 1_000_000;
    private const int MaxStandaloneParts = 500;

    private static readonly Regex MTextCodes = new(
        @"\\[fFHhCcQqWwAaTtSs][^;]*;|\\[LlOoKk~]|\{|\}",
        RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IDxfCompatibilityService _compatibility;
    private readonly ILogger<BlockBreakdownService> _logger;

    public BlockBreakdownService(
        IUnitOfWork unitOfWork,
        IFileStorageService storage,
        IDxfCompatibilityService compatibility,
        ILogger<BlockBreakdownService> logger)
    {
        _unitOfWork = unitOfWork;
        _storage = storage;
        _compatibility = compatibility;
        _logger = logger;
    }

    public async Task<ManufacturingBreakdownDto> BuildBreakdownAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var result = new ManufacturingBreakdownDto { ProjectId = projectId };

        var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken);
        if (project is null)
        {
            result.ErrorMessage = "The project was not found.";
            return result;
        }

        result.ProjectTitle = project.Title;

        if (project.SourceFileType != SourceFileType.Dxf)
        {
            result.ErrorMessage = "Block-based breakdown needs a DXF drawing. Image drawings have no blocks.";
            return result;
        }

        var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(path))
        {
            result.ErrorMessage = "The stored drawing file is missing.";
            return result;
        }

        try
        {
            byte[] original;
            await using (var stream = await _storage.OpenReadAsync(path, cancellationToken))
            {
                if (stream is null)
                {
                    result.ErrorMessage = "The stored drawing file could not be opened.";
                    return result;
                }

                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                original = buffer.ToArray();
            }

            using var prepared = await _compatibility.PrepareAsync(new MemoryStream(original, writable: false), cancellationToken);
            if (prepared.IsDwg)
            {
                result.ErrorMessage = "DWG files are not supported. Export the drawing as DXF.";
                return result;
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
                result.Warnings.Add("The patched DXF header could not be read; the original file was used instead.");
            }

            await Task.Run(() => Fill(result, file, cancellationToken), cancellationToken);
            result.Success = true;

            if (result.Parts.Count == 0)
            {
                result.Warnings.Add("No named blocks are placed in this drawing. Parts cannot be identified by block.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manufacturing breakdown failed for project {ProjectId}.", projectId);
            result.Success = false;
            result.ErrorMessage = "The drawing could not be read for block breakdown.";
        }

        return result;
    }

    private static void Fill(ManufacturingBreakdownDto result, DxfFile file, CancellationToken cancellationToken)
    {
        var blocks = new Dictionary<string, DxfBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in file.Blocks)
        {
            if (!string.IsNullOrEmpty(block.Name) && !block.Name.StartsWith('*'))
            {
                blocks[block.Name] = block;
            }
        }

        var modelEntities = file.Entities.Where(e => !e.IsInPaperSpace).ToList();
        IEnumerable<DxfEntity> source = modelEntities.Count > 0 ? modelEntities : file.Entities;

        var usage = new Dictionary<string, BlockUsage>(StringComparer.OrdinalIgnoreCase);
        Walk(source, 1, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), usage, blocks, true, cancellationToken);

        result.TotalBlockDefinitions = blocks.Count;

        foreach (var pair in usage.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var block = blocks[pair.Key];
            var info = pair.Value;
            var part = Describe(block);

            part.BlockName = block.Name;
            part.Layer = info.Layer;
            part.Quantity = (int)Math.Min(info.Quantity, MaxQuantity);
            part.Placements = info.Placements;
            part.Attributes = info.Attributes;

            result.Parts.Add(part);
            result.TotalInstances += part.Quantity;
        }

        if (result.Parts.Count == 0)
        {
            AddStandaloneParts(result, source, cancellationToken);
        }
    }

    private sealed record StandaloneItem(DxfEntity Entity, List<(double X, double Y)> Points, (double X, double Y)? Center);

    private static void AddStandaloneParts(
        ManufacturingBreakdownDto result,
        IEnumerable<DxfEntity> source,
        CancellationToken cancellationToken)
    {
        var items = new List<StandaloneItem>();

        foreach (var entity in source)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (entity)
            {
                case DxfLine line:
                    items.Add(new StandaloneItem(entity, new List<(double X, double Y)> { (line.P1.X, line.P1.Y), (line.P2.X, line.P2.Y) }, null));
                    break;

                case DxfArc arc:
                    {
                        var start = arc.StartAngle * Math.PI / 180.0;
                        var end = arc.EndAngle * Math.PI / 180.0;
                        items.Add(new StandaloneItem(entity, new List<(double X, double Y)>
                        {
                            (arc.Center.X + arc.Radius * Math.Cos(start), arc.Center.Y + arc.Radius * Math.Sin(start)),
                            (arc.Center.X + arc.Radius * Math.Cos(end), arc.Center.Y + arc.Radius * Math.Sin(end))
                        }, null));
                        break;
                    }

                case DxfCircle circle:
                    items.Add(new StandaloneItem(entity, new List<(double X, double Y)>(), (circle.Center.X, circle.Center.Y)));
                    break;

                case DxfEllipse ellipse:
                    items.Add(new StandaloneItem(entity, new List<(double X, double Y)>(), (ellipse.Center.X, ellipse.Center.Y)));
                    break;

                case DxfLwPolyline lwPolyline:
                    if (lwPolyline.Vertices.Any())
                    {
                        items.Add(new StandaloneItem(entity, lwPolyline.Vertices.Select(v => (v.X, v.Y)).ToList(), null));
                    }

                    break;

                case DxfPolyline polyline:
                    if (polyline.Vertices.Any())
                    {
                        items.Add(new StandaloneItem(entity, polyline.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList(), null));
                    }

                    break;
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        double lowX = double.MaxValue, lowY = double.MaxValue, highX = double.MinValue, highY = double.MinValue;
        foreach (var item in items)
        {
            foreach (var point in item.Points)
            {
                lowX = Math.Min(lowX, point.X);
                lowY = Math.Min(lowY, point.Y);
                highX = Math.Max(highX, point.X);
                highY = Math.Max(highY, point.Y);
            }

            if (item.Center is { } center)
            {
                lowX = Math.Min(lowX, center.X);
                lowY = Math.Min(lowY, center.Y);
                highX = Math.Max(highX, center.X);
                highY = Math.Max(highY, center.Y);
            }
        }

        var diagonal = Math.Sqrt((highX - lowX) * (highX - lowX) + (highY - lowY) * (highY - lowY));
        var tolerance = Math.Max(diagonal * 2e-5, 1e-4);

        var parent = new int[items.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        var cells = new Dictionary<(long, long), int>();

        void Touch(int index, double x, double y)
        {
            var cx = (long)Math.Floor(x / tolerance);
            var cy = (long)Math.Floor(y / tolerance);

            for (var ox = -1; ox <= 1; ox++)
            {
                for (var oy = -1; oy <= 1; oy++)
                {
                    if (cells.TryGetValue((cx + ox, cy + oy), out var other))
                    {
                        var ra = Find(index);
                        var rb = Find(other);
                        if (ra != rb)
                        {
                            parent[ra] = rb;
                        }
                    }
                }
            }

            cells.TryAdd((cx, cy), index);
        }

        for (var i = 0; i < items.Count; i++)
        {
            foreach (var point in items[i].Points)
            {
                Touch(i, point.X, point.Y);
            }
        }

        var buckets = new Dictionary<int, List<StandaloneItem>>();
        var loops = new List<StandaloneItem>();

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Center is not null)
            {
                loops.Add(items[i]);
                continue;
            }

            var root = Find(i);
            if (!buckets.TryGetValue(root, out var list))
            {
                list = new List<StandaloneItem>();
                buckets[root] = list;
            }

            list.Add(items[i]);
        }

        var components = buckets.Values.ToList();
        var bounds = components.Select(c => ComponentBounds(c)).ToList();

        foreach (var loop in loops)
        {
            var center = loop.Center!.Value;
            var best = -1;
            var bestArea = double.MaxValue;

            for (var c = 0; c < components.Count; c++)
            {
                var b = bounds[c];
                if (center.X >= b.MinX - tolerance && center.X <= b.MaxX + tolerance
                    && center.Y >= b.MinY - tolerance && center.Y <= b.MaxY + tolerance)
                {
                    var area = (b.MaxX - b.MinX) * (b.MaxY - b.MinY);
                    if (area < bestArea)
                    {
                        bestArea = area;
                        best = c;
                    }
                }
            }

            if (best >= 0)
            {
                components[best].Add(loop);
            }
            else
            {
                components.Add(new List<StandaloneItem> { loop });
                bounds.Add(ComponentBounds(components[^1]));
            }
        }

        var ordered = components
            .Select(c => (Items: c, Bounds: ComponentBounds(c)))
            .OrderBy(c => c.Bounds.MinX)
            .ThenBy(c => c.Bounds.MinY)
            .Take(MaxStandaloneParts)
            .ToList();

        var index = 0;
        foreach (var component in ordered)
        {
            index++;

            var part = new BlockPartDto
            {
                BlockName = "MODEL-" + index,
                Layer = component.Items[0].Entity.Layer,
                Quantity = 1,
                Placements = new List<BlockPlacementDto>(),
                Attributes = new Dictionary<string, string>(),
                Labels = new List<string>(),
                Width = Math.Round(component.Bounds.MaxX - component.Bounds.MinX, 3),
                Height = Math.Round(component.Bounds.MaxY - component.Bounds.MinY, 3)
            };

            foreach (var item in component.Items)
            {
                switch (item.Entity)
                {
                    case DxfLine:
                        part.LineCount++;
                        break;
                    case DxfArc:
                        part.ArcCount++;
                        break;
                    case DxfCircle:
                        part.CircleCount++;
                        break;
                    case DxfLwPolyline:
                    case DxfPolyline:
                    case DxfEllipse:
                        part.PolylineCount++;
                        break;
                }
            }

            result.Parts.Add(part);
            result.TotalInstances += part.Quantity;
        }

        result.Warnings.Add("No named blocks are placed; parts were derived from connected model-space geometry (MODEL-n).");
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) ComponentBounds(List<StandaloneItem> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        void Extend(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var item in items)
        {
            switch (item.Entity)
            {
                case DxfLine line:
                    Extend(line.P1.X, line.P1.Y);
                    Extend(line.P2.X, line.P2.Y);
                    break;

                case DxfArc arc:
                    ExtendArc(arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle, Extend);
                    break;

                case DxfCircle circle:
                    Extend(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius);
                    Extend(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius);
                    break;

                case DxfLwPolyline lwPolyline:
                    ExtendBulgedPath(
                        lwPolyline.Vertices.Select(v => (v.X, v.Y, v.Bulge)).ToList(),
                        lwPolyline.IsClosed,
                        Extend);
                    break;

                case DxfPolyline polyline:
                    ExtendBulgedPath(
                        polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(),
                        polyline.IsClosed,
                        Extend);
                    break;

                case DxfEllipse ellipse:
                    ExtendEllipse(ellipse, Extend);
                    break;
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private static void Walk(
        IEnumerable<DxfEntity> entities,
        long multiplier,
        int depth,
        HashSet<string> stack,
        Dictionary<string, BlockUsage> usage,
        Dictionary<string, DxfBlock> blocks,
        bool topLevel,
        CancellationToken cancellationToken)
    {
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entity is not DxfInsert insert)
            {
                continue;
            }

            var name = insert.Name;
            if (string.IsNullOrEmpty(name) || !blocks.TryGetValue(name, out var block))
            {
                continue;
            }

            var columns = Math.Max((int)insert.ColumnCount, 1);
            var rows = Math.Max((int)insert.RowCount, 1);
            long instances = (long)columns * rows;

            if (!usage.TryGetValue(name, out var info))
            {
                info = new BlockUsage { Layer = insert.Layer };
                usage[name] = info;
            }

            info.Quantity = Math.Min(info.Quantity + multiplier * instances, MaxQuantity);

            if (topLevel)
            {
                CollectAttributes(insert, info);
                CollectPlacements(insert, columns, rows, info);
            }

            if (depth < MaxBlockDepth && !stack.Contains(name))
            {
                stack.Add(name);
                Walk(block.Entities, Math.Min(multiplier * instances, MaxQuantity), depth + 1, stack, usage, blocks, false, cancellationToken);
                stack.Remove(name);
            }
        }
    }

    private static void CollectAttributes(DxfInsert insert, BlockUsage info)
    {
        var index = 0;
        foreach (var attribute in insert.Attributes)
        {
            index++;
            var value = Clean(attribute.Value);
            if (value.Length == 0)
            {
                continue;
            }

            var key = "Attribute " + index;
            if (!info.Attributes.ContainsKey(key))
            {
                info.Attributes[key] = value;
            }
        }
    }

    private static void CollectPlacements(DxfInsert insert, int columns, int rows, BlockUsage info)
    {
        var angle = insert.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (info.Placements.Count >= MaxPlacementsPerBlock)
                {
                    return;
                }

                var offsetX = column * insert.ColumnSpacing;
                var offsetY = row * insert.RowSpacing;

                info.Placements.Add(new BlockPlacementDto
                {
                    X = Math.Round(insert.Location.X + cos * offsetX - sin * offsetY, 3),
                    Y = Math.Round(insert.Location.Y + sin * offsetX + cos * offsetY, 3),
                    Rotation = Math.Round(insert.Rotation, 3),
                    ScaleX = insert.XScaleFactor,
                    ScaleY = insert.YScaleFactor
                });
            }
        }
    }

    private static BlockPartDto Describe(DxfBlock block)
    {
        var part = new BlockPartDto();
        var labels = new List<string>();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        void Extend(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var entity in block.Entities)
        {
            switch (entity)
            {
                case DxfLine line:
                    part.LineCount++;
                    Extend(line.P1.X, line.P1.Y);
                    Extend(line.P2.X, line.P2.Y);
                    break;

                case DxfArc arc:
                    part.ArcCount++;
                    ExtendArc(arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle, Extend);
                    break;

                case DxfCircle circle:
                    part.CircleCount++;
                    Extend(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius);
                    Extend(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius);
                    break;

                case DxfLwPolyline lwPolyline:
                    part.PolylineCount++;
                    ExtendBulgedPath(
                        lwPolyline.Vertices.Select(v => (v.X, v.Y, v.Bulge)).ToList(),
                        lwPolyline.IsClosed,
                        Extend);
                    break;

                case DxfPolyline polyline:
                    part.PolylineCount++;
                    ExtendBulgedPath(
                        polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(),
                        polyline.IsClosed,
                        Extend);
                    break;

                case DxfEllipse ellipse:
                    ExtendEllipse(ellipse, Extend);
                    break;

                case DxfText text:
                    AddLabel(labels, text.Value);
                    break;

                case DxfMText mText:
                    AddLabel(labels, GetMTextValue(mText));
                    break;
            }
        }

        if (minX <= maxX && minY <= maxY)
        {
            part.Width = Math.Round(maxX - minX, 3);
            part.Height = Math.Round(maxY - minY, 3);
        }

        part.Labels = labels;
        return part;
    }

    private static void ExtendArc(
        double cx,
        double cy,
        double radius,
        double startDegrees,
        double endDegrees,
        Action<double, double> extend)
    {
        var twoPi = Math.PI * 2.0;
        var start = startDegrees * Math.PI / 180.0;
        var sweep = ((endDegrees - startDegrees) * Math.PI / 180.0) % twoPi;
        if (sweep <= 1e-12)
        {
            sweep += twoPi;
        }

        extend(cx + radius * Math.Cos(start), cy + radius * Math.Sin(start));
        extend(cx + radius * Math.Cos(start + sweep), cy + radius * Math.Sin(start + sweep));

        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var angle = quadrant * Math.PI / 2.0;
            var distance = (angle - start) % twoPi;
            if (distance < 0)
            {
                distance += twoPi;
            }

            if (distance <= sweep)
            {
                extend(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
            }
        }
    }

    private static void ExtendBulgedPath(
        IReadOnlyList<(double X, double Y, double Bulge)> vertices,
        bool closed,
        Action<double, double> extend)
    {
        foreach (var vertex in vertices)
        {
            extend(vertex.X, vertex.Y);
        }

        var last = closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            if (Math.Abs(a.Bulge) < 1e-9)
            {
                continue;
            }

            ExtendBulge(a.X, a.Y, b.X, b.Y, a.Bulge, extend);
        }
    }

    private static void ExtendBulge(
        double x1,
        double y1,
        double x2,
        double y2,
        double bulge,
        Action<double, double> extend)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12)
        {
            return;
        }

        var theta = 4.0 * Math.Atan(bulge);
        var half = Math.Abs(theta) / 2.0;
        var radius = chord / (2.0 * Math.Sin(half));
        var side = bulge > 0 ? 1.0 : -1.0;
        var offset = radius * Math.Cos(half);
        var cx = (x1 + x2) / 2.0 + side * (-dy / chord) * offset;
        var cy = (y1 + y2) / 2.0 + side * (dx / chord) * offset;
        var startAngle = Math.Atan2(y1 - cy, x1 - cx);

        var twoPi = Math.PI * 2.0;
        var sweep = Math.Abs(theta);

        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var angle = quadrant * Math.PI / 2.0;
            var distance = ((angle - startAngle) * side) % twoPi;
            if (distance < 0)
            {
                distance += twoPi;
            }

            if (distance <= sweep)
            {
                extend(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
            }
        }
    }

    private static void ExtendEllipse(DxfEllipse ellipse, Action<double, double> extend)
    {
        var majorX = ellipse.MajorAxis.X;
        var majorY = ellipse.MajorAxis.Y;
        var direction = ellipse.Normal.Z < 0 ? -1.0 : 1.0;
        var minorX = -majorY * ellipse.MinorAxisRatio * direction;
        var minorY = majorX * ellipse.MinorAxisRatio * direction;

        var start = ellipse.StartParameter;
        var end = ellipse.EndParameter;
        while (end <= start)
        {
            end += 2 * Math.PI;
        }

        var steps = Math.Max(8, (int)Math.Ceiling((end - start) * 180.0 / Math.PI / 1.0));
        for (var i = 0; i <= steps; i++)
        {
            var t = start + (end - start) * i / steps;
            extend(
                ellipse.Center.X + majorX * Math.Cos(t) + minorX * Math.Sin(t),
                ellipse.Center.Y + majorY * Math.Cos(t) + minorY * Math.Sin(t));
        }
    }

    private static void AddLabel(List<string> labels, string? raw)
    {
        if (labels.Count >= MaxLabelsPerBlock)
        {
            return;
        }

        var text = Clean(raw);
        if (text.Length == 0 || labels.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        labels.Add(text);
    }

    private static string GetMTextValue(DxfMText mText)
    {
        var builder = new StringBuilder();
        foreach (var chunk in mText.ExtendedText)
        {
            builder.Append(chunk);
        }

        builder.Append(mText.Text);
        return builder.ToString();
    }

    private static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace("\\P", " ").Replace("\\\\", "\\");
        text = MTextCodes.Replace(text, string.Empty);
        text = text
            .Replace("%%c", "⌀", StringComparison.OrdinalIgnoreCase)
            .Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
            .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase);

        return Whitespace.Replace(text, " ").Trim();
    }

    private sealed class BlockUsage
    {
        public string? Layer { get; set; }
        public long Quantity { get; set; }
        public List<BlockPlacementDto> Placements { get; } = new();
        public Dictionary<string, string> Attributes { get; set; } = new();
    }
}