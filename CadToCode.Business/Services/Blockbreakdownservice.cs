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
                    Extend(arc.Center.X - arc.Radius, arc.Center.Y - arc.Radius);
                    Extend(arc.Center.X + arc.Radius, arc.Center.Y + arc.Radius);
                    break;

                case DxfCircle circle:
                    part.CircleCount++;
                    Extend(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius);
                    Extend(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius);
                    break;

                case DxfLwPolyline lwPolyline:
                    part.PolylineCount++;
                    foreach (var vertex in lwPolyline.Vertices)
                    {
                        Extend(vertex.X, vertex.Y);
                    }

                    break;

                case DxfPolyline polyline:
                    part.PolylineCount++;
                    foreach (var vertex in polyline.Vertices)
                    {
                        Extend(vertex.Location.X, vertex.Location.Y);
                    }

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