using System.Globalization;
using System.IO.Compression;
using System.Text;
using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Tables;
using Microsoft.Extensions.Logging;

namespace CadToCode.Models.Dtos
{
    public enum PartOriginMode
    {
        BottomLeft = 1,
        Center = 2
    }

    public class PartManufacturingSpecDto
    {
        public string NodeId { get; set; } = string.Empty;
        public string? PartName { get; set; }
        public string? Material { get; set; }
        public decimal? Thickness { get; set; }
        public int? Quantity { get; set; }
    }

    public class PartManufacturingRequestDto
    {
        public int ProjectId { get; set; }
        public string? ProjectCode { get; set; }
        public PartOriginMode Origin { get; set; } = PartOriginMode.BottomLeft;
        public bool IncludeReferenceGeometry { get; set; }
        public bool SplitLargeComponents { get; set; } = true;
        public int SplitThreshold { get; set; } = 30;
        public double MinPartSize { get; set; } = 25.0;
        public int MinEntityCount { get; set; } = 3;
        public bool RequireClosedProfile { get; set; } = true;
        public string? DefaultMaterial { get; set; }
        public decimal? DefaultThickness { get; set; }
        public List<string> NodeIds { get; set; } = new();
        public List<PartManufacturingSpecDto> Specs { get; set; } = new();
    }

    public class PartManufacturingFileDto
    {
        public string NodeId { get; set; } = string.Empty;
        public List<string> MemberNodeIds { get; set; } = new();
        public string PartName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Material { get; set; } = string.Empty;
        public decimal? Thickness { get; set; }
        public int Quantity { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public int EntityCount { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    public class PartManufacturingPackageDto
    {
        public int ProjectId { get; set; }
        public string ProjectCode { get; set; } = string.Empty;
        public string ProjectTitle { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public List<PartManufacturingFileDto> Files { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}

namespace CadToCode.Business.Interfaces
{
    public interface IPartManufacturingDxfService
    {
        Task<PartManufacturingPackageDto> BuildPackageAsync(PartManufacturingRequestDto request, CancellationToken cancellationToken = default);

        Task<PartManufacturingFileDto?> BuildPartAsync(PartManufacturingRequestDto request, string nodeId, CancellationToken cancellationToken = default);

        Task<byte[]?> BuildZipAsync(PartManufacturingRequestDto request, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> SaveToStorageAsync(PartManufacturingRequestDto request, CancellationToken cancellationToken = default);
    }
}

namespace CadToCode.Business.Services
{
    public sealed class PartManufacturingDxfService : IPartManufacturingDxfService
    {
        private const string CutLayer = "CUT";
        private const string ReferenceLayer = "REF";
        private const string NotesLayer = "NOTES";
        private const string NotesStyle = "NOTES";
        private const double Epsilon = 1e-9;

        private readonly IUnitOfWork _unitOfWork;
        private readonly IFileStorageService _storage;
        private readonly IEnumerable<IGeometryExtractor> _extractors;
        private readonly IComponentDecomposer _decomposer;
        private readonly IAssemblyTopologyMapper _topologyMapper;
        private readonly ILogger<PartManufacturingDxfService> _logger;

        public PartManufacturingDxfService(
            IUnitOfWork unitOfWork,
            IFileStorageService storage,
            IEnumerable<IGeometryExtractor> extractors,
            IComponentDecomposer decomposer,
            IAssemblyTopologyMapper topologyMapper,
            ILogger<PartManufacturingDxfService> logger)
        {
            _unitOfWork = unitOfWork;
            _storage = storage;
            _extractors = extractors;
            _decomposer = decomposer;
            _topologyMapper = topologyMapper;
            _logger = logger;
        }

        public async Task<PartManufacturingPackageDto> BuildPackageAsync(
            PartManufacturingRequestDto request,
            CancellationToken cancellationToken = default)
        {
            var package = new PartManufacturingPackageDto
            {
                ProjectId = request.ProjectId,
                ProjectCode = ResolveProjectCode(request)
            };

            try
            {
                var source = await LoadSourceAsync(request.ProjectId, cancellationToken);
                package.ProjectTitle = source.Title;
                package.Warnings.AddRange(source.Geometry.Warnings);

                var nodes = _decomposer.Decompose(source.Geometry);
                if (nodes.Count == 0)
                {
                    package.ErrorMessage = "No components could be isolated from this drawing.";
                    return package;
                }

                var groups = BuildGroups(nodes, package.Warnings);
                if (request.SplitLargeComponents)
                {
                    groups = ExpandLargeGroups(groups, request, package.Warnings, cancellationToken);
                }

                var specs = IndexSpecs(request.Specs);
                var wanted = new HashSet<string>(request.NodeIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var group in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (wanted.Count > 0 && !group.Members.Any(m => wanted.Contains(m.Id) || wanted.Contains(SourceNodeId(m.Id))))
                    {
                        continue;
                    }

                    var spec = FindSpec(specs, group);
                    var file = BuildPartFile(group, spec, request, package.ProjectCode, usedNames, package.Warnings);
                    if (file is not null)
                    {
                        package.Files.Add(file);
                    }
                }

                if (package.Files.Count == 0)
                {
                    package.ErrorMessage = "No cuttable geometry was found in the isolated components.";
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
                _logger.LogWarning(ex, "Manufacturing DXF package failed for project {ProjectId}.", request.ProjectId);
                package.ErrorMessage = "The manufacturing DXF files could not be generated.";
            }

            return package;
        }

        public async Task<PartManufacturingFileDto?> BuildPartAsync(
            PartManufacturingRequestDto request,
            string nodeId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return null;
            }

            var single = new PartManufacturingRequestDto
            {
                ProjectId = request.ProjectId,
                ProjectCode = request.ProjectCode,
                Origin = request.Origin,
                IncludeReferenceGeometry = request.IncludeReferenceGeometry,
                SplitLargeComponents = request.SplitLargeComponents,
                SplitThreshold = request.SplitThreshold,
                MinPartSize = request.MinPartSize,
                MinEntityCount = request.MinEntityCount,
                RequireClosedProfile = request.RequireClosedProfile,
                DefaultMaterial = request.DefaultMaterial,
                DefaultThickness = request.DefaultThickness,
                Specs = request.Specs,
                NodeIds = new List<string> { nodeId.Trim() }
            };

            var package = await BuildPackageAsync(single, cancellationToken);
            return package.Success ? package.Files.FirstOrDefault() : null;
        }

        public async Task<byte[]?> BuildZipAsync(
            PartManufacturingRequestDto request,
            CancellationToken cancellationToken = default)
        {
            var package = await BuildPackageAsync(request, cancellationToken);
            if (!package.Success || package.Files.Count == 0)
            {
                return null;
            }

            try
            {
                using var output = new MemoryStream();
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var file in package.Files)
                    {
                        var entry = zip.CreateEntry(file.FileName, CompressionLevel.Optimal);
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
                _logger.LogWarning(ex, "Manufacturing DXF zip failed for project {ProjectId}.", request.ProjectId);
                return null;
            }
        }

        public async Task<IReadOnlyList<string>> SaveToStorageAsync(
            PartManufacturingRequestDto request,
            CancellationToken cancellationToken = default)
        {
            var saved = new List<string>();
            var package = await BuildPackageAsync(request, cancellationToken);
            if (!package.Success)
            {
                return saved;
            }

            foreach (var file in package.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var stream = new MemoryStream(file.Content, writable: false);
                var path = await _storage.SaveAsync(stream, file.FileName, cancellationToken);
                saved.Add(path);
            }

            return saved;
        }

        private async Task<SourceData> LoadSourceAsync(int projectId, CancellationToken cancellationToken)
        {
            var project = await _unitOfWork.Projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException("The project was not found.");

            if (project.SourceFileType != SourceFileType.Dxf)
            {
                throw new InvalidOperationException("Manufacturing DXF files can only be generated from a DXF drawing.");
            }

            var path = await _unitOfWork.Projects.GetStoredFilePathAsync(projectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("The stored drawing file is missing.");
            }

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(SourceFileType.Dxf))
                ?? throw new InvalidOperationException("No DXF geometry extractor is registered.");

            await using var stream = await _storage.OpenReadAsync(path, cancellationToken)
                ?? throw new InvalidOperationException("The stored drawing file could not be opened.");

            var geometry = await extractor.ExtractAsync(stream, cancellationToken);
            return new SourceData(geometry, project.Title);
        }

        private List<PartGroup> BuildGroups(List<AssemblyNode> nodes, List<string> warnings)
        {
            try
            {
                _topologyMapper.Map(nodes.First().Geometry, nodes);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Topology mapping skipped; every component is exported separately.");
                warnings.Add("Identical parts could not be grouped; each component is exported with quantity 1.");
            }

            var groups = new List<PartGroup>();
            var bySharedProfile = new Dictionary<string, PartGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                if (string.IsNullOrWhiteSpace(node.SharedProfileId))
                {
                    groups.Add(new PartGroup(node));
                    continue;
                }

                if (bySharedProfile.TryGetValue(node.SharedProfileId, out var existing))
                {
                    existing.Members.Add(node);
                    continue;
                }

                var group = new PartGroup(node);
                bySharedProfile[node.SharedProfileId] = group;
                groups.Add(group);
            }

            return groups;
        }

        private List<PartGroup> ExpandLargeGroups(
            List<PartGroup> groups,
            PartManufacturingRequestDto request,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            var result = new List<PartGroup>();
            var explicitlyRequested = request.NodeIds is { Count: > 0 };

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var node = group.Representative;
                var cut = DrawingEntity.FromGeometry(node.Geometry).Where(e => e.IsOutlineCandidate).ToList();

                if (cut.Count <= request.SplitThreshold || !TryGetBounds(cut, out var bx0, out var by0, out var bx1, out var by1))
                {
                    result.Add(group);
                    continue;
                }

                var diagonal = Math.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0));
                var tolerance = Math.Max(diagonal * 2e-5, 1e-4);

                var components = SplitByConnectivity(cut, tolerance);
                var parts = new List<SubPart>();
                var skipped = 0;

                foreach (var component in components)
                {
                    if (!TryGetBounds(component, out var x0, out var y0, out var x1, out var y1))
                    {
                        continue;
                    }

                    var w = x1 - x0;
                    var h = y1 - y0;

                    if (!explicitlyRequested
                        && (Math.Max(w, h) < request.MinPartSize || component.Count < request.MinEntityCount))
                    {
                        skipped++;
                        continue;
                    }

                    parts.Add(new SubPart(component, x0, y0, x1, y1, IsClosedProfile(component, tolerance)));
                }

                if (parts.Count <= 1)
                {
                    result.Add(group);
                    continue;
                }

                var baseName = node.Labels.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && l.Length <= 40) ?? node.Name;
                var bySignature = new Dictionary<string, PartGroup>(StringComparer.Ordinal);
                var index = 0;

                foreach (var part in parts.OrderBy(p => p.MinX).ThenBy(p => p.MinY))
                {
                    var signature = string.Join("|",
                        Math.Round(part.MaxX - part.MinX, 1).ToString(CultureInfo.InvariantCulture),
                        Math.Round(part.MaxY - part.MinY, 1).ToString(CultureInfo.InvariantCulture),
                        part.Entities.Count.ToString(CultureInfo.InvariantCulture),
                        part.Entities.Count(e => e.Kind == DrawingEntityKind.Circle).ToString(CultureInfo.InvariantCulture),
                        part.Entities.Count(e => e.Kind == DrawingEntityKind.Arc).ToString(CultureInfo.InvariantCulture));

                    index++;

                    var subNode = new AssemblyNode
                    {
                        Id = node.Id + "." + index.ToString(CultureInfo.InvariantCulture),
                        Name = baseName + "." + index.ToString(CultureInfo.InvariantCulture),
                        Layer = node.Layer,
                        Bounds = new BoundingBox2D(part.MinX, part.MinY, part.MaxX, part.MaxY),
                        HasClosedProfile = part.Closed,
                        ParentId = node.Id
                    };

                    if (bySignature.TryGetValue(signature, out var existing) && existing.Representative.HasClosedProfile == part.Closed)
                    {
                        existing.Members.Add(subNode);
                        continue;
                    }

                    var subGroup = new PartGroup(subNode) { Entities = part.Entities };
                    bySignature[signature] = subGroup;
                    result.Add(subGroup);
                }

                if (skipped > 0)
                {
                    warnings.Add(node.Name + " (" + node.Id + "): split into " + parts.Count.ToString(CultureInfo.InvariantCulture)
                        + " parts; " + skipped.ToString(CultureInfo.InvariantCulture) + " small fragments skipped.");
                }
                else
                {
                    warnings.Add(node.Name + " (" + node.Id + "): split into " + parts.Count.ToString(CultureInfo.InvariantCulture) + " parts.");
                }
            }

            return result;
        }

        private static List<List<DrawingEntity>> SplitByConnectivity(List<DrawingEntity> entities, double tolerance)
        {
            var parent = new int[entities.Count];
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

            void Union(int a, int b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb)
                {
                    parent[ra] = rb;
                }
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
                            Union(index, other);
                        }
                    }
                }

                cells.TryAdd((cx, cy), index);
            }

            var circles = new List<int>();

            for (var i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];

                switch (entity.Kind)
                {
                    case DrawingEntityKind.Line:
                        Touch(i, entity.Start.X, entity.Start.Y);
                        Touch(i, entity.End.X, entity.End.Y);
                        break;

                    case DrawingEntityKind.Arc:
                        if (entity.IsFullCircle)
                        {
                            circles.Add(i);
                            break;
                        }

                        Touch(i, entity.Center.X + entity.Radius * Math.Cos(entity.StartAngle), entity.Center.Y + entity.Radius * Math.Sin(entity.StartAngle));
                        Touch(i, entity.Center.X + entity.Radius * Math.Cos(entity.StartAngle + entity.SweepAngle), entity.Center.Y + entity.Radius * Math.Sin(entity.StartAngle + entity.SweepAngle));
                        break;

                    case DrawingEntityKind.Circle:
                        circles.Add(i);
                        break;

                    case DrawingEntityKind.Polyline:
                        foreach (var vertex in entity.Vertices)
                        {
                            Touch(i, vertex.X, vertex.Y);
                        }

                        break;
                }
            }

            var circleSet = new HashSet<int>(circles);
            var buckets = new Dictionary<int, List<DrawingEntity>>();

            for (var i = 0; i < entities.Count; i++)
            {
                if (circleSet.Contains(i))
                {
                    continue;
                }

                var root = Find(i);
                if (!buckets.TryGetValue(root, out var list))
                {
                    list = new List<DrawingEntity>();
                    buckets[root] = list;
                }

                list.Add(entities[i]);
            }

            var components = buckets.Values.ToList();
            var bounds = components
                .Select(c => TryGetBounds(c, out var x0, out var y0, out var x1, out var y1) ? (X0: x0, Y0: y0, X1: x1, Y1: y1) : (X0: 0.0, Y0: 0.0, X1: -1.0, Y1: -1.0))
                .ToList();

            foreach (var circleIndex in circles)
            {
                var circle = entities[circleIndex];
                var best = -1;
                var bestArea = double.MaxValue;

                for (var c = 0; c < components.Count; c++)
                {
                    var b = bounds[c];
                    if (b.X1 < b.X0)
                    {
                        continue;
                    }

                    if (circle.Center.X >= b.X0 - tolerance && circle.Center.X <= b.X1 + tolerance
                        && circle.Center.Y >= b.Y0 - tolerance && circle.Center.Y <= b.Y1 + tolerance)
                    {
                        var area = (b.X1 - b.X0) * (b.Y1 - b.Y0);
                        if (area < bestArea)
                        {
                            bestArea = area;
                            best = c;
                        }
                    }
                }

                if (best >= 0)
                {
                    components[best].Add(circle);
                }
                else
                {
                    components.Add(new List<DrawingEntity> { circle });
                    bounds.Add((circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius, circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius));
                }
            }

            return components;
        }

        private static bool IsClosedProfile(List<DrawingEntity> entities, double tolerance)
        {
            if (entities.Any(e => e.Kind == DrawingEntityKind.Circle || e.IsFullCircle || (e.Kind == DrawingEntityKind.Polyline && e.IsClosed)))
            {
                return true;
            }

            if (entities.Count < 3)
            {
                return false;
            }

            var degree = new Dictionary<(long, long), int>();

            void Add(double x, double y)
            {
                var key = ((long)Math.Round(x / tolerance), (long)Math.Round(y / tolerance));
                degree[key] = degree.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            foreach (var entity in entities)
            {
                switch (entity.Kind)
                {
                    case DrawingEntityKind.Line:
                        Add(entity.Start.X, entity.Start.Y);
                        Add(entity.End.X, entity.End.Y);
                        break;

                    case DrawingEntityKind.Arc:
                        Add(entity.Center.X + entity.Radius * Math.Cos(entity.StartAngle), entity.Center.Y + entity.Radius * Math.Sin(entity.StartAngle));
                        Add(entity.Center.X + entity.Radius * Math.Cos(entity.StartAngle + entity.SweepAngle), entity.Center.Y + entity.Radius * Math.Sin(entity.StartAngle + entity.SweepAngle));
                        break;

                    case DrawingEntityKind.Polyline:
                        Add(entity.Vertices[0].X, entity.Vertices[0].Y);
                        Add(entity.Vertices[^1].X, entity.Vertices[^1].Y);
                        break;
                }
            }

            return degree.Count > 0 && degree.Values.All(v => v >= 2);
        }

        private PartManufacturingFileDto? BuildPartFile(
            PartGroup group,
            PartManufacturingSpecDto? spec,
            PartManufacturingRequestDto request,
            string projectCode,
            HashSet<string> usedNames,
            List<string> warnings)
        {
            var node = group.Representative;
            var entities = group.Entities ?? DrawingEntity.FromGeometry(node.Geometry);

            var cut = entities.Where(e => e.IsOutlineCandidate).ToList();
            var reference = request.IncludeReferenceGeometry
                ? entities.Where(e => !e.IsOutlineCandidate).ToList()
                : new List<DrawingEntity>();

            if (cut.Count == 0)
            {
                warnings.Add(node.Name + " (" + node.Id + "): no cuttable geometry found; skipped.");
                return null;
            }

            if (!TryGetBounds(cut.Concat(reference), out var minX, out var minY, out var maxX, out var maxY))
            {
                warnings.Add(node.Name + " (" + node.Id + "): bounds could not be computed; skipped.");
                return null;
            }

            var width = maxX - minX;
            var height = maxY - minY;

            var explicitlyRequested = request.NodeIds is { Count: > 0 };
            if (!explicitlyRequested)
            {
                var label = node.Name + " (" + node.Id + ")";

                if (Math.Max(width, height) < request.MinPartSize)
                {
                    warnings.Add(label + ": " + width.ToString("0.##", CultureInfo.InvariantCulture) + " x " + height.ToString("0.##", CultureInfo.InvariantCulture) + " is below the minimum part size; skipped as a fragment.");
                    return null;
                }

                if (cut.Count < request.MinEntityCount)
                {
                    warnings.Add(label + ": only " + cut.Count.ToString(CultureInfo.InvariantCulture) + " cut entities; skipped as a fragment.");
                    return null;
                }

                if (request.RequireClosedProfile && !node.HasClosedProfile)
                {
                    warnings.Add(label + ": no closed cutting profile; skipped as a fragment.");
                    return null;
                }
            }

            var dx = request.Origin == PartOriginMode.Center ? -(minX + maxX) / 2.0 : -minX;
            var dy = request.Origin == PartOriginMode.Center ? -(minY + maxY) / 2.0 : -minY;

            var dxf = new DxfFile();
            dxf.Header.Version = DxfAcadVersion.R2000;
            dxf.Header.DefaultDrawingUnits = MapUnits(node.Geometry.DetectedUnit);
            dxf.Layers.Add(new DxfLayer(CutLayer));
            dxf.Layers.Add(new DxfLayer(NotesLayer));
            dxf.Styles.Add(new DxfStyle(NotesStyle) { PrimaryFontFileName = "arial.ttf" });

            var count = 0;
            foreach (var entity in cut)
            {
                if (AddEntity(dxf, entity, dx, dy, CutLayer))
                {
                    count++;
                }
            }

            if (reference.Count > 0)
            {
                dxf.Layers.Add(new DxfLayer(ReferenceLayer));
                foreach (var entity in reference)
                {
                    AddEntity(dxf, entity, dx, dy, ReferenceLayer);
                }
            }

            var partName = ResolvePartName(node, spec);
            var material = FirstNonBlank(spec?.Material, request.DefaultMaterial) ?? "NOT SPECIFIED";
            var thickness = spec?.Thickness is > 0 ? spec.Thickness : request.DefaultThickness is > 0 ? request.DefaultThickness : null;
            var quantity = spec?.Quantity is > 0 ? spec.Quantity.Value : group.Members.Count;

            var lowestY = request.Origin == PartOriginMode.Center ? -height / 2.0 : 0.0;
            AddNotes(dxf, partName, material, thickness, quantity, projectCode, width, height, lowestY, request.Origin == PartOriginMode.Center ? -width / 2.0 : 0.0);

            byte[] content;
            using (var stream = new MemoryStream())
            {
                dxf.Save(stream);
                content = stream.ToArray();
            }

            return new PartManufacturingFileDto
            {
                NodeId = node.Id,
                MemberNodeIds = group.Members.Select(m => m.Id).ToList(),
                PartName = partName,
                FileName = UniqueFileName(
                    SafeName(node.Id.Contains('.') && string.Equals(partName, node.Name, StringComparison.Ordinal) && partName.LastIndexOf('.') > 0
                        ? partName[..partName.LastIndexOf('.')]
                        : partName)
                    + "_" + SafeName(node.Id),
                    usedNames),
                Material = material,
                Thickness = thickness,
                Quantity = quantity,
                Width = Math.Round(width, 3),
                Height = Math.Round(height, 3),
                OffsetX = Math.Round(dx, 6),
                OffsetY = Math.Round(dy, 6),
                EntityCount = count,
                Content = content
            };
        }

        private static bool AddEntity(DxfFile dxf, DrawingEntity entity, double dx, double dy, string layer)
        {
            switch (entity.Kind)
            {
                case DrawingEntityKind.Line:
                    dxf.Entities.Add(new DxfLine(
                        new DxfPoint(entity.Start.X + dx, entity.Start.Y + dy, 0),
                        new DxfPoint(entity.End.X + dx, entity.End.Y + dy, 0))
                    { Layer = layer });
                    return true;

                case DrawingEntityKind.Circle:
                    dxf.Entities.Add(new DxfCircle(
                        new DxfPoint(entity.Center.X + dx, entity.Center.Y + dy, 0),
                        entity.Radius)
                    { Layer = layer });
                    return true;

                case DrawingEntityKind.Arc:
                    {
                        var center = new DxfPoint(entity.Center.X + dx, entity.Center.Y + dy, 0);

                        if (entity.IsFullCircle)
                        {
                            dxf.Entities.Add(new DxfCircle(center, entity.Radius) { Layer = layer });
                            return true;
                        }

                        var start = NormalizeDegrees(entity.StartAngle * 180.0 / Math.PI);
                        var end = NormalizeDegrees((entity.StartAngle + entity.SweepAngle) * 180.0 / Math.PI);
                        dxf.Entities.Add(new DxfArc(center, entity.Radius, start, end) { Layer = layer });
                        return true;
                    }

                case DrawingEntityKind.Polyline:
                    {
                        var vertices = entity.Vertices.Select(v => new DxfLwPolylineVertex
                        {
                            X = v.X + dx,
                            Y = v.Y + dy,
                            Bulge = v.Bulge
                        });

                        dxf.Entities.Add(new DxfLwPolyline(vertices) { IsClosed = entity.IsClosed, Layer = layer });
                        return true;
                    }

                default:
                    return false;
            }
        }

        private static void AddNotes(
            DxfFile dxf,
            string partName,
            string material,
            decimal? thickness,
            int quantity,
            string projectCode,
            double width,
            double height,
            double baseY,
            double baseX)
        {
            var textHeight = Math.Clamp(Math.Max(width, height) * 0.02, 2.5, 150.0);

            var thicknessText = thickness is > 0
                ? thickness.Value.ToString("0.##", CultureInfo.InvariantCulture) + " MM"
                : "NOT SPECIFIED";

            var lines = new[]
            {
                "PART NAME: " + Clean(partName),
                "MATERIAL: " + Clean(material),
                "THICKNESS: " + thicknessText,
                "QUANTITY: " + quantity.ToString(CultureInfo.InvariantCulture),
                "PROJECT ID: " + Clean(projectCode),
                "SIZE: " + width.ToString("0.##", CultureInfo.InvariantCulture) + " X " + height.ToString("0.##", CultureInfo.InvariantCulture)
            };

            for (var i = 0; i < lines.Length; i++)
            {
                var y = baseY - textHeight * 2.0 * (i + 1);
                dxf.Entities.Add(new DxfText(new DxfPoint(baseX, y, 0), textHeight, lines[i]) { Layer = NotesLayer, TextStyleName = NotesStyle });
            }
        }

        private static bool TryGetBounds(
            IEnumerable<DrawingEntity> entities,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
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

            foreach (var entity in entities)
            {
                switch (entity.Kind)
                {
                    case DrawingEntityKind.Line:
                        Extend(entity.Start.X, entity.Start.Y);
                        Extend(entity.End.X, entity.End.Y);
                        break;

                    case DrawingEntityKind.Circle:
                        Extend(entity.Center.X - entity.Radius, entity.Center.Y - entity.Radius);
                        Extend(entity.Center.X + entity.Radius, entity.Center.Y + entity.Radius);
                        break;

                    case DrawingEntityKind.Arc:
                        ExtendArc(entity.Center.X, entity.Center.Y, entity.Radius, entity.StartAngle, entity.SweepAngle, Extend);
                        break;

                    case DrawingEntityKind.Polyline:
                        {
                            var vertices = entity.Vertices;
                            var last = entity.IsClosed ? vertices.Count : vertices.Count - 1;

                            for (var i = 0; i < vertices.Count; i++)
                            {
                                Extend(vertices[i].X, vertices[i].Y);
                            }

                            for (var i = 0; i < last; i++)
                            {
                                var a = vertices[i];
                                var b = vertices[(i + 1) % vertices.Count];
                                if (Math.Abs(a.Bulge) < Epsilon)
                                {
                                    continue;
                                }

                                foreach (var (x, y) in BulgePoints(a.X, a.Y, b.X, b.Y, a.Bulge))
                                {
                                    Extend(x, y);
                                }
                            }

                            break;
                        }
                }
            }

            minX = lowX;
            minY = lowY;
            maxX = highX;
            maxY = highY;

            return lowX <= highX && lowY <= highY;
        }

        private static void ExtendArc(
            double cx,
            double cy,
            double radius,
            double startRadians,
            double sweepRadians,
            Action<double, double> extend)
        {
            void Point(double angle) => extend(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));

            var twoPi = Math.PI * 2.0;
            var sweep = Math.Clamp(sweepRadians, 0.0, twoPi);

            Point(startRadians);
            Point(startRadians + sweep);

            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var angle = quadrant * Math.PI / 2.0;
                var distance = (angle - startRadians) % twoPi;
                if (distance < 0)
                {
                    distance += twoPi;
                }

                if (distance <= sweep)
                {
                    Point(angle);
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

        private static DxfUnits MapUnits(DrawingUnit unit)
        {
            return unit switch
            {
                DrawingUnit.Centimeter => DxfUnits.Centimeters,
                DrawingUnit.Meter => DxfUnits.Meters,
                DrawingUnit.Inch => DxfUnits.Inches,
                DrawingUnit.Foot => DxfUnits.Feet,
                _ => DxfUnits.Millimeters
            };
        }

        private static double NormalizeDegrees(double degrees)
        {
            var value = degrees % 360.0;
            return value < 0 ? value + 360.0 : value;
        }

        private static string ResolvePartName(AssemblyNode node, PartManufacturingSpecDto? spec)
        {
            var name = FirstNonBlank(spec?.PartName)
                ?? node.Labels.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && l.Length <= 40)
                ?? node.Label
                ?? node.Name;

            return string.IsNullOrWhiteSpace(name) ? node.Id : name.Trim();
        }

        private static string ResolveProjectCode(PartManufacturingRequestDto request)
        {
            return FirstNonBlank(request.ProjectCode)
                ?? "PRJ-" + request.ProjectId.ToString("D5", CultureInfo.InvariantCulture);
        }

        private static string? FirstNonBlank(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static Dictionary<string, PartManufacturingSpecDto> IndexSpecs(IEnumerable<PartManufacturingSpecDto>? specs)
        {
            var map = new Dictionary<string, PartManufacturingSpecDto>(StringComparer.OrdinalIgnoreCase);
            if (specs is null)
            {
                return map;
            }

            foreach (var spec in specs)
            {
                if (!string.IsNullOrWhiteSpace(spec.NodeId))
                {
                    map[spec.NodeId.Trim()] = spec;
                }
            }

            return map;
        }

        private static PartManufacturingSpecDto? FindSpec(Dictionary<string, PartManufacturingSpecDto> specs, PartGroup group)
        {
            foreach (var member in group.Members)
            {
                if (specs.TryGetValue(member.Id, out var spec)
                    || specs.TryGetValue(SourceNodeId(member.Id), out spec))
                {
                    return spec;
                }
            }

            return null;
        }

        private static string SourceNodeId(string id)
        {
            var dot = id.IndexOf('.');
            return dot > 0 ? id[..dot] : id;
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
                return "part";
            }

            var builder = new StringBuilder();
            foreach (var ch in value.Trim())
            {
                builder.Append(char.IsLetterOrDigit(ch) && ch < 128 || ch is '-' or '_' ? ch : '_');
            }

            var result = builder.ToString().Trim('_');
            return result.Length == 0 ? "part" : result.Length > 60 ? result[..60] : result;
        }

        private static string Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var ch in value.Trim().ToUpperInvariant())
            {
                builder.Append(ch is >= ' ' and <= '~' ? ch : '?');
            }

            return builder.ToString();
        }

        private sealed record SourceData(ExtractedGeometry Geometry, string Title);

        private sealed record SubPart(List<DrawingEntity> Entities, double MinX, double MinY, double MaxX, double MaxY, bool Closed);

        private sealed class PartGroup
        {
            public PartGroup(AssemblyNode first)
            {
                Members = new List<AssemblyNode> { first };
            }

            public List<AssemblyNode> Members { get; }

            public IReadOnlyList<DrawingEntity>? Entities { get; set; }

            public AssemblyNode Representative => Members[0];
        }
    }
}