using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.Database;
using CadToCode.Database.Entities;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Entities;

namespace CadToCode.Business.Services;

public sealed class ManufacturingDrawingService : IManufacturingDrawingService
{
    private const string OutputFolder = "ManufacturingDrawings";
    private const int ClusterThreshold = 40;
    private const int MaxCellsPerEntity = 400;
    private const double DefaultStepThickness = 10.0;
    private const double MaxStepThickness = 10000.0;

    private readonly ApplicationDbContext _db;
    private readonly ComponentSettings _settings;
    private readonly string _storageRoot;
    private readonly string? _webRoot;

    public ManufacturingDrawingService(
        ApplicationDbContext db,
        IOptions<ComponentSettings> settings,
        IOptions<UploadOptions> upload,
        string? webRootPath = null)
    {
        _db = db;
        _settings = settings.Value ?? new ComponentSettings();
        _webRoot = webRootPath;

        var root = upload.Value?.StorageRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "App_Data", "uploads");
        }

        _storageRoot = Path.GetFullPath(root);
    }


    // ------------------------------------------------------------ public API

    public async Task<CadAssistManufacturingResult> GenerateFromGaDxfAsync(
        Stream gaDxfStream,
        string gaFileName,
        CadAssistManufacturingRequest request,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(gaFileName ?? string.Empty);

        if (!Path.GetExtension(safeName).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Only .dxf GA drawings are supported for manufacturing drawings.");
        }

        var sourceDir = Path.Combine(_storageRoot, OutputFolder, "Sources");
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, Guid.NewGuid().ToString("N") + ".dxf");

        await using (var target = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            await gaDxfStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        var job = new DxfJob
        {
            OriginalFileName = safeName,
            StoredFilePath = sourcePath,
            Status = "Parsed"
        };

        _db.DxfJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await GenerateCoreAsync(job, sourcePath, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CadAssistManufacturingResult> GenerateFromJobAsync(
        CadAssistManufacturingRequest request,
        CancellationToken cancellationToken = default)
    {
        var job = await _db.DxfJobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken).ConfigureAwait(false);

        if (job == null)
        {
            return Fail("The GA drawing job was not found.");
        }

        var sourcePath = ResolveSourcePath(job.StoredFilePath);

        if (sourcePath == null)
        {
            return Fail("The original GA DXF file is missing.");
        }

        return await GenerateCoreAsync(job, sourcePath, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<(Stream Content, string FileName)?> OpenDrawingAsync(
        int jobId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);

        if (name.Length == 0
            || !string.Equals(name, fileName, StringComparison.Ordinal)
            || !name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var path = Path.Combine(JobDirectory(jobId), name);

        if (!File.Exists(path))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult<(Stream Content, string FileName)?>((stream, name));
    }

    public Task<(Stream Content, string FileName)?> OpenZipAsync(
        int jobId,
        CancellationToken cancellationToken = default)
    {
        var dir = JobDirectory(jobId);

        if (!Directory.Exists(dir))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var files = Directory.GetFiles(dir, "*.dxf");

        if (files.Length == 0)
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        ms.Position = 0;
        return Task.FromResult<(Stream Content, string FileName)?>((ms, $"Job_{jobId}_Manufacturing_Drawings.zip"));
    }

    // ------------------------------------------------------------- STEP files

    public async Task<CadAssistStepResult> GenerateStepFilesAsync(
        CadAssistStepRequest request,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        var requested = request.Thickness;
        var useDefault = !requested.HasValue || !double.IsFinite(requested.Value) || requested.Value <= 0;
        var thickness = useDefault ? DefaultStepThickness : requested!.Value;

        var result = new CadAssistStepResult
        {
            JobId = request.JobId,
            Thickness = thickness,
            UsedDefaultThickness = useDefault
        };

        if (thickness > MaxStepThickness)
        {
            result.Error = "The thickness must be between 0 and " + MaxStepThickness + " mm.";
            return result;
        }

        var job = await _db.DxfJobs.FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken).ConfigureAwait(false);

        if (job == null)
        {
            result.Error = "The manufacturing drawings were not found. Generate them first.";
            return result;
        }

        var sourcePath = ResolveSourcePath(job.StoredFilePath);

        if (sourcePath == null)
        {
            result.Error = "The original GA DXF file is missing.";
            return result;
        }

        DxfDocument? doc;

        try
        {
            doc = await Task.Run(() => DxfDocument.Load(sourcePath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Error = "Could not read the GA DXF: " + ex.Message;
            return result;
        }

        if (doc == null)
        {
            result.Error = "Could not read the GA DXF.";
            return result;
        }

        Plan plan;

        try
        {
            var planRequest = new CadAssistManufacturingRequest
            {
                ProjectId = request.ProjectId,
                SessionId = request.SessionId,
                JobId = job.Id,
                IncludeAllParts = true,
                GaFileName = job.OriginalFileName
            };

            plan = await Task.Run(() => BuildPlan(doc, planRequest, job.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = "The GA DXF could not be analysed: " + ex.Message;
            return result;
        }

        if (plan.Sheets.Count == 0)
        {
            result.Error = "No parts could be detected in this GA DXF.";
            return result;
        }

        var outDir = StepDirectory(job.Id);
        Directory.CreateDirectory(outDir);

        foreach (var old in Directory.GetFiles(outDir, "*.step"))
        {
            File.Delete(old);
        }

        var index = 0;
        var assemblyParts = new List<ManufacturingStepBuilder.AssemblyPart>();

        foreach (var sheet in plan.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var baseName = $"{index:00}_{Sanitize((sheet.DrgNo.Length > 0 && !sheet.DrgNo.StartsWith("J", StringComparison.Ordinal) ? sheet.DrgNo + "_" : string.Empty) + sheet.Name)}";
            var fileName = baseName + ".step";
            var entities = ToDrawingEntities(DropOutliers(sheet.Items));

            var outcome = ManufacturingStepBuilder.Build(
                entities,
                sheet.Name,
                sheet.DrgNo,
                thickness,
                Path.Combine(outDir, fileName));

            if (!outcome.Success)
            {
                result.Skipped.Add(sheet.Name + ": " + outcome.Error);
                continue;
            }

            if (outcome.Warning != null)
            {
                result.Warnings.Add(outcome.Warning);
            }

            assemblyParts.Add(new ManufacturingStepBuilder.AssemblyPart(sheet.Name, sheet.DrgNo, entities));

            result.Parts.Add(new CadAssistStepPartDto
            {
                Key = sheet.Key,
                Name = sheet.Name,
                DrawingNumber = sheet.DrgNo,
                FileName = fileName,
                Thickness = thickness,
                DownloadUrl = $"/CadAssist/StepDownload?jobId={job.Id}&file={Uri.EscapeDataString(fileName)}"
            });
        }

        if (result.Parts.Count == 0)
        {
            result.Error = "No STEP files could be created. " + string.Join(" ", result.Skipped);
            return result;
        }

        if (assemblyParts.Count > 0)
        {
            const string assemblyFileName = "00_ASSEMBLY.step";

            var assemblyOutcome = ManufacturingStepBuilder.BuildAssembly(
                assemblyParts,
                "ASSEMBLY",
                "GA",
                thickness,
                Path.Combine(outDir, assemblyFileName));

            if (assemblyOutcome.Success)
            {
                if (assemblyOutcome.Warning != null)
                {
                    result.Warnings.Add(assemblyOutcome.Warning);
                }

                result.Parts.Insert(0, new CadAssistStepPartDto
                {
                    Key = "Assembly",
                    Name = "ASSEMBLY (all parts at GA positions)",
                    DrawingNumber = "GA",
                    FileName = assemblyFileName,
                    Thickness = thickness,
                    DownloadUrl = $"/CadAssist/StepDownload?jobId={job.Id}&file={Uri.EscapeDataString(assemblyFileName)}"
                });
            }
            else
            {
                result.Skipped.Add("ASSEMBLY: " + assemblyOutcome.Error);
            }
        }

        result.Success = true;
        result.ZipDownloadUrl = $"/CadAssist/StepZip?jobId={job.Id}";
        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    public async Task<CadAssistStepResult> SaveTemplateStepAsync(
        int jobId,
        string fileName,
        string stepContent,
        string title,
        CancellationToken cancellationToken = default)
    {
        var result = new CadAssistStepResult { JobId = jobId };
        var name = Path.GetFileName(fileName ?? string.Empty);

        if (name.Length == 0
            || !name.EndsWith(".step", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(stepContent))
        {
            result.Error = "The template STEP content is empty.";
            return result;
        }

        var job = await _db.DxfJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

        if (job == null)
        {
            result.Error = "The manufacturing drawings were not found. Generate them first.";
            return result;
        }

        var dir = StepDirectory(jobId);
        Directory.CreateDirectory(dir);

        foreach (var old in Directory.GetFiles(dir, "*.step"))
        {
            File.Delete(old);
        }

        await File.WriteAllTextAsync(
            Path.Combine(dir, name),
            stepContent,
            new System.Text.UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        result.Parts.Add(new CadAssistStepPartDto
        {
            Key = "Assembly",
            Name = title,
            DrawingNumber = string.Empty,
            FileName = name,
            Thickness = 0,
            DownloadUrl = $"/CadAssist/StepDownload?jobId={jobId}&file={Uri.EscapeDataString(name)}"
        });

        result.Success = true;
        result.ZipDownloadUrl = $"/CadAssist/StepZip?jobId={jobId}";
        return result;
    }
    public async Task<CadAssistStepResult> SaveTemplateStepsAsync(
    int jobId,
    IReadOnlyList<(string FileName, string StepContent, string Title)> files,
    CancellationToken cancellationToken = default)
    {
        var result = new CadAssistStepResult { JobId = jobId };

        if (files is null || files.Count == 0)
        {
            result.Error = "The template STEP content is empty.";
            return result;
        }

        foreach (var item in files)
        {
            var checkName = Path.GetFileName(item.FileName ?? string.Empty);

            if (checkName.Length == 0
                || !checkName.EndsWith(".step", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(item.StepContent))
            {
                result.Error = "The template STEP content is empty.";
                return result;
            }
        }

        var job = await _db.DxfJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

        if (job == null)
        {
            result.Error = "The manufacturing drawings were not found. Generate them first.";
            return result;
        }

        var dir = StepDirectory(jobId);
        Directory.CreateDirectory(dir);

        foreach (var old in Directory.GetFiles(dir, "*.step"))
        {
            File.Delete(old);
        }

        var index = 0;

        foreach (var item in files)
        {
            var name = Path.GetFileName(item.FileName);

            await File.WriteAllTextAsync(
                Path.Combine(dir, name),
                item.StepContent,
                new System.Text.UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            result.Parts.Add(new CadAssistStepPartDto
            {
                Key = index == 0 ? "Assembly" : Path.GetFileNameWithoutExtension(name),
                Name = item.Title,
                DrawingNumber = string.Empty,
                FileName = name,
                Thickness = 0,
                DownloadUrl = $"/CadAssist/StepDownload?jobId={jobId}&file={Uri.EscapeDataString(name)}"
            });

            index++;
        }

        result.Success = true;
        result.ZipDownloadUrl = $"/CadAssist/StepZip?jobId={jobId}";
        return result;
    }
    public Task<(Stream Content, string FileName)?> OpenStepAsync(
        int jobId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);

        if (name.Length == 0
            || !string.Equals(name, fileName, StringComparison.Ordinal)
            || !name.EndsWith(".step", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var path = Path.Combine(StepDirectory(jobId), name);

        if (!File.Exists(path))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult<(Stream Content, string FileName)?>((stream, name));
    }

    public Task<(Stream Content, string FileName)?> OpenStepZipAsync(
        int jobId,
        CancellationToken cancellationToken = default)
    {
        var dir = StepDirectory(jobId);

        if (!Directory.Exists(dir))
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var files = Directory.GetFiles(dir, "*.step");

        if (files.Length == 0)
        {
            return Task.FromResult<(Stream Content, string FileName)?>(null);
        }

        var ms = new MemoryStream();

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }

        ms.Position = 0;
        return Task.FromResult<(Stream Content, string FileName)?>((ms, $"Job_{jobId}_Step_Files.zip"));
    }

    private string StepDirectory(int jobId)
    {
        return Path.Combine(JobDirectory(jobId), "Step");
    }

    private static List<DrawingEntity> ToDrawingEntities(IEnumerable<EntityObject> items)
    {
        var list = new List<DrawingEntity>();

        foreach (var e in items)
        {
            try
            {
                var layer = e.Layer?.Name;
                var lineType = e.Linetype?.Name;

                switch (e)
                {
                    case Line line:
                        if (Math.Abs(line.StartPoint.X - line.EndPoint.X) > 1e-9 || Math.Abs(line.StartPoint.Y - line.EndPoint.Y) > 1e-9)
                        {
                            list.Add(DrawingEntity.CreateLine(line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y, layer, lineType));
                        }

                        break;

                    case Arc arc:
                        if (arc.Radius > 1e-9)
                        {
                            list.Add(DrawingEntity.CreateArc(arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle, layer, lineType));
                        }

                        break;

                    case Circle circle:
                        if (circle.Radius > 1e-9)
                        {
                            list.Add(DrawingEntity.CreateCircle(circle.Center.X, circle.Center.Y, circle.Radius, layer, lineType));
                        }

                        break;

                    case Polyline2D poly2:
                        {
                            var vertices = poly2.Vertexes
                                .Select(v => new DrawingVertex(v.Position.X, v.Position.Y, v.Bulge))
                                .ToList();

                            if (vertices.Count >= 2)
                            {
                                list.Add(DrawingEntity.CreatePolyline(vertices, poly2.IsClosed, layer, lineType));
                            }

                            break;
                        }

                    case Polyline3D poly3:
                        {
                            var vertices = poly3.Vertexes
                                .Select(v => new DrawingVertex(v.X, v.Y))
                                .ToList();

                            if (vertices.Count >= 2)
                            {
                                list.Add(DrawingEntity.CreatePolyline(vertices, poly3.IsClosed, layer, lineType));
                            }

                            break;
                        }

                    case Ellipse ellipse:
                        {
                            var vertices = ellipse.PolygonalVertexes(72)
                                .Select(v => new DrawingVertex(v.X, v.Y))
                                .ToList();

                            if (vertices.Count >= 3)
                            {
                                list.Add(DrawingEntity.CreatePolyline(vertices, ellipse.IsFullEllipse, layer, lineType));
                            }

                            break;
                        }
                }
            }
            catch
            {
                // an entity that cannot be converted is left out of the outline
            }
        }

        return list;
    }

    // -------------------------------------------------------------- pipeline

    private async Task<CadAssistManufacturingResult> GenerateCoreAsync(
        DxfJob job,
        string sourcePath,
        CadAssistManufacturingRequest request,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        var result = new CadAssistManufacturingResult
        {
            JobId = job.Id,
            GaFileName = job.OriginalFileName
        };

        DxfDocument? doc;

        try
        {
            doc = await Task.Run(() => DxfDocument.Load(sourcePath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Error = "Could not read the GA DXF: " + ex.Message;
            return result;
        }

        if (doc == null)
        {
            result.Error = "Could not read the GA DXF.";
            return result;
        }

        Plan plan;

        try
        {
            plan = await Task.Run(() => BuildPlan(doc, request, job.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = "The GA DXF could not be analysed: " + ex.Message;
            return result;
        }

        result.Skipped.AddRange(plan.Skipped);
        result.Warnings.AddRange(plan.Warnings);

        if (plan.Sheets.Count == 0)
        {
            result.Error = "No parts could be detected in this GA DXF.";
            return result;
        }

        var outDir = JobDirectory(job.Id);
        Directory.CreateDirectory(outDir);

        foreach (var old in Directory.GetFiles(outDir, "*.dxf"))
        {
            File.Delete(old);
        }

        var index = 0;

        foreach (var sheet in plan.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;

            var partDoc = new DxfDocument(doc.DrawingVariables.AcadVer);
            var count = BuildSheet(partDoc, sheet.Items, sheet.Name, sheet.DrgNo, job.OriginalFileName);
            var fileName = $"{index:00}_{Sanitize((sheet.DrgNo.Length > 0 && !sheet.DrgNo.StartsWith("J", StringComparison.Ordinal) ? sheet.DrgNo + "_" : string.Empty) + sheet.Name)}.dxf";

            partDoc.Save(Path.Combine(outDir, fileName));

            result.Parts.Add(new CadAssistManufacturingPartDto
            {
                Key = sheet.Key,
                Name = sheet.Name,
                DrawingNumber = sheet.DrgNo,
                EntityCount = count,
                FileName = fileName,
                DownloadUrl = $"/CadAssist/ManufacturingDownload?jobId={job.Id}&file={Uri.EscapeDataString(fileName)}"
            });
        }

        job.Status = "Generated";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        result.Success = true;
        result.ZipDownloadUrl = $"/CadAssist/ManufacturingZip?jobId={job.Id}";
        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    private Plan BuildPlan(DxfDocument doc, CadAssistManufacturingRequest request, int jobId, CancellationToken ct)
    {
        var plan = new Plan();
        var flat = new List<EntityObject>();

        foreach (var e in doc.Entities.All.ToList())
        {
            Flatten(e, flat, 0);
        }

        ct.ThrowIfCancellationRequested();

        var settings = _settings;
        var analysis = Analyze(flat, settings, false, null);

        if (analysis.Components.Count == 0)
        {
            settings = BuildGenericSettings(flat);
            analysis = Analyze(flat, settings, true, DimensionLayerNames(doc));
            plan.Warnings.Add("The configured layer rules found no parts, so generic layer rules were used.");
        }

        var clustered = false;

        if ((analysis.Components.Count <= 1 && analysis.AllGeometry.Count >= ClusterThreshold)
            || (analysis.Components.Count == 0 && analysis.AllGeometry.Count > 0))
        {
            var clusters = ClusterComponents(analysis, Math.Max(1, settings.MinEntities));

            if (clusters.Count > 1 || (clusters.Count == 1 && analysis.Components.Count == 0))
            {
                analysis.Components = clusters;
                clustered = true;
                plan.Warnings.Add("The drawing has no usable layer structure, so parts were detected by geometry grouping.");
            }
        }

        if (analysis.Components.Count == 0)
        {
            return plan;
        }

        plan.Skipped.AddRange(analysis.SkippedSmall);
        ct.ThrowIfCancellationRequested();

        var assigned = AssignAnnotations(analysis, settings);

        if (assigned.Unmatched > 0)
        {
            plan.Warnings.Add(assigned.Unmatched + " dimension(s) could not be matched to a part.");
        }

        var compDrg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var compName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in analysis.Callouts)
        {
            var rule = RuleFor(c.Name, settings);

            if (rule?.Component != null && c.DrgNo.Length > 0)
            {
                compDrg[rule.Component] = c.DrgNo;
            }
        }

        if (clustered)
        {
            foreach (var c in analysis.Callouts)
            {
                var near = NearestComponent(analysis, c.X, c.Y, settings.TextMaxDistance);

                if (near == null)
                {
                    continue;
                }

                if (c.DrgNo.Length > 0 && !compDrg.ContainsKey(near))
                {
                    compDrg[near] = c.DrgNo;
                }

                if (c.Name.Length > 0 && !compName.ContainsKey(near))
                {
                    compName[near] = c.Name.ToUpperInvariant();
                }
            }
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var component in analysis.Components)
        {
            var key = component.Key;
            var title = compName.TryGetValue(key, out var named) ? named : key;
            compDrg.TryGetValue(key, out var drg);

            if (!Wanted(request, title, drg ?? string.Empty, key))
            {
                continue;
            }

            index++;
            var unique = title;
            var n = 2;

            while (!usedNames.Add(unique))
            {
                unique = title + "_" + n++;
            }

            var items = new List<EntityObject>();

            foreach (var e in component.Value)
            {
                AddClone(items, e);
            }

            foreach (var d in assigned.Dims[key])
            {
                if (d.Block != null)
                {
                    foreach (var be in d.Block.Entities)
                    {
                        AddClone(items, be);
                    }
                }
            }

            foreach (var t in assigned.Notes[key])
            {
                AddClone(items, t);
            }

            plan.Sheets.Add(new PartSheet
            {
                Key = "C:" + key,
                Name = unique,
                DrgNo = string.IsNullOrEmpty(drg) ? $"J{jobId}-{index:00}" : drg,
                Items = items
            });
        }

        if (clustered)
        {
            return plan;
        }

        var seen = new Dictionary<string, int>();

        foreach (var c in analysis.Callouts)
        {
            ct.ThrowIfCancellationRequested();

            var rule = RuleFor(c.Name, settings);

            if (rule?.Component != null && analysis.Components.ContainsKey(rule.Component))
            {
                continue;
            }

            var key = "K:" + c.Name + "|" + c.DrgNo;
            seen[key] = seen.TryGetValue(key, out var count) ? count + 1 : 1;

            if (seen[key] > 1)
            {
                key += "#" + seen[key];
            }

            if (!Wanted(request, c.Name, c.DrgNo, key))
            {
                continue;
            }

            var hw = rule != null && rule.HalfWidth > 0 ? rule.HalfWidth : settings.DefaultHalfSize;
            var hh = rule != null && rule.HalfHeight > 0 ? rule.HalfHeight : settings.DefaultHalfSize;
            double cx = c.X, cy = c.Y;

            if (rule != null && rule.Snap && !c.HasLeader)
            {
                SnapToNearest(analysis, c.X, c.Y, rule.SnapMax, out cx, out cy);
            }

            var win = new Bounds();
            win.Add(cx - hw, cy - hh);
            win.Add(cx + hw, cy + hh);

            var items = CropEntities(analysis, win);

            if (items.Count == 0)
            {
                plan.Skipped.Add(c.Name);
                continue;
            }

            index++;

            plan.Sheets.Add(new PartSheet
            {
                Key = key,
                Name = c.Name,
                DrgNo = c.DrgNo.Length > 0 ? c.DrgNo : $"J{jobId}-{index:00}",
                Items = items
            });
        }

        return plan;
    }

    private static bool Wanted(CadAssistManufacturingRequest request, string name, string drg, string key)
    {
        if (request.IncludeAllParts || request.PartKeys == null || request.PartKeys.Count == 0)
        {
            return true;
        }

        foreach (var wanted in request.PartKeys)
        {
            if (string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            var w = wanted.Trim();

            if (name.Contains(w, StringComparison.OrdinalIgnoreCase)
                || (drg.Length > 0 && drg.Contains(w, StringComparison.OrdinalIgnoreCase))
                || key.Equals(w, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string JobDirectory(int jobId)
    {
        return Path.Combine(_storageRoot, OutputFolder, "Job_" + jobId);
    }

    private string? ResolveSourcePath(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (Path.IsPathRooted(stored) && File.Exists(stored))
        {
            return stored;
        }

        var relative = stored.Replace('/', Path.DirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(_webRoot))
        {
            var web = Path.Combine(_webRoot, relative);

            if (File.Exists(web))
            {
                return web;
            }
        }

        var local = Path.Combine(_storageRoot, relative);
        return File.Exists(local) ? local : null;
    }

    private static CadAssistManufacturingResult Fail(string error)
    {
        return new CadAssistManufacturingResult { Success = false, Error = error };
    }

    // ------------------------------------------------------------- data types

    private sealed class Callout
    {
        public string Name = "";
        public string DrgNo = "";
        public double X, Y;
        public bool HasLeader;
    }

    private sealed class Analysis
    {
        public Dictionary<string, List<EntityObject>> Components = new(StringComparer.OrdinalIgnoreCase);
        public List<Dimension> Dimensions = new();
        public List<EntityObject> Notes = new();
        public List<string> SkippedSmall = new();
        public List<EntityObject> AllGeometry = new();
        public List<Callout> Callouts = new();
    }

    private sealed class Assigned
    {
        public Dictionary<string, List<Dimension>> Dims = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<EntityObject>> Notes = new(StringComparer.OrdinalIgnoreCase);
        public int Unmatched;
    }

    private sealed class PartSheet
    {
        public string Key = "";
        public string Name = "";
        public string DrgNo = "";
        public List<EntityObject> Items = new();
    }

    private sealed class Plan
    {
        public List<PartSheet> Sheets = new();
        public List<string> Skipped = new();
        public List<string> Warnings = new();
    }

    private struct Bounds
    {
        public double MinX, MinY, MaxX, MaxY;
        public bool Valid;

        public void Add(double x, double y)
        {
            if (!Valid) { MinX = MaxX = x; MinY = MaxY = y; Valid = true; return; }
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public void Add(Bounds b)
        {
            if (!b.Valid) return;
            Add(b.MinX, b.MinY);
            Add(b.MaxX, b.MaxY);
        }

        public double DistanceTo(double x, double y)
        {
            var dx = Math.Max(Math.Max(MinX - x, 0), x - MaxX);
            var dy = Math.Max(Math.Max(MinY - y, 0), y - MaxY);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    // --------------------------------------------------------------- analysis

    private static bool IsSupported(EntityObject e) =>
        e is Line || e is Polyline2D || e is Polyline3D || e is Circle || e is Arc ||
        e is Ellipse || e is Spline || e is Text || e is MText || e is netDxf.Entities.Point ||
        e is Hatch || e is netDxf.Entities.Solid;

    private static void Flatten(EntityObject e, List<EntityObject> output, int depth)
    {
        if (e is Insert ins)
        {
            if (depth > 10) return;
            List<EntityObject> sub;
            try { sub = ins.Explode(); } catch { return; }

            foreach (var s in sub)
            {
                if (s.Layer.Name == "0") s.Layer = ins.Layer;
                Flatten(s, output, depth + 1);
            }
        }
        else
        {
            output.Add(e);
        }
    }

    private static HashSet<string> DimensionLayerNames(DxfDocument doc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in doc.Layers)
        {
            var name = layer.Name;

            if (name.StartsWith("dim", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("dimension", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                set.Add(name);
            }
        }

        return set;
    }

    private static ComponentSettings BuildGenericSettings(List<EntityObject> flat)
    {
        var all = new Bounds();

        foreach (var e in flat)
        {
            if (e is Text || e is MText || e is netDxf.Entities.Point) continue;
            all.Add(BoxOf(e));
        }

        var diag = all.Valid
            ? Math.Sqrt(Math.Pow(all.MaxX - all.MinX, 2) + Math.Pow(all.MaxY - all.MinY, 2))
            : 1000.0;

        return new ComponentSettings
        {
            IgnoreLayers = new List<string> { "0", "Defpoints", "PAPER", "TXT", "TEXT", "CENTER", "CENTRE", "CEN", "TITLEBLOCK", "BORDER", "FRAME" },
            IgnoreLayerPatterns = new List<string> { "^dim", "^text", "^note", "^title", "^border", "^frame", "^sheet", "^center", "^centre", "^defpoints" },
            ViewSuffixes = new List<string>(),
            DimensionLayers = new List<string>(),
            Merge = new Dictionary<string, List<string>>(),
            MinEntities = 3,
            MatchTolerance = Math.Max(2.0, diag * 0.002),
            TextMaxDistance = Math.Max(50.0, diag * 0.08),
            CalloutRegex = new ComponentSettings().CalloutRegex,
            LabelRegex = new ComponentSettings().LabelRegex,
            MaxLabelLength = 40,
            LeaderTolerance = Math.Max(20.0, diag * 0.02),
            DefaultHalfSize = Math.Max(50.0, diag * 0.1),
            Callouts = new List<CalloutRule>()
        };
    }

    private static Analysis Analyze(List<EntityObject> flat, ComponentSettings s, bool generic, HashSet<string>? extraDimLayers)
    {
        var a = new Analysis();
        var leaders = new List<List<(double X, double Y)>>();
        var texts = new List<EntityObject>();

        foreach (var e in flat)
        {
            if (e is Leader ld) leaders.Add(ld.Vertexes.Select(v => ((double)v.X, (double)v.Y)).ToList());
            else if (e is Text || e is MText) texts.Add(e);
        }

        var ignore = new HashSet<string>(s.IgnoreLayers, StringComparer.OrdinalIgnoreCase);
        var ignoreRx = s.IgnoreLayerPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToList();
        var dimLayers = new HashSet<string>(s.DimensionLayers, StringComparer.OrdinalIgnoreCase);

        if (extraDimLayers != null)
        {
            foreach (var name in extraDimLayers) dimLayers.Add(name);
        }

        var mergeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in s.Merge)
        {
            foreach (var layer in m.Value) mergeMap[layer] = m.Key.ToUpperInvariant();
        }

        var suffix = s.ViewSuffixes.Count > 0
            ? new Regex("(" + string.Join("|", s.ViewSuffixes.Select(Regex.Escape)) + ")+$", RegexOptions.IgnoreCase)
            : null;

        foreach (var e in flat)
        {
            var layer = e.Layer.Name;

            if (e is Dimension d)
            {
                if (generic || dimLayers.Contains(layer)) a.Dimensions.Add(d);
                continue;
            }

            if ((e is Text || e is MText) && dimLayers.Contains(layer))
            {
                a.Notes.Add(e);
                continue;
            }

            if (!IsSupported(e)) continue;
            if (ignore.Contains(layer) || ignoreRx.Any(r => r.IsMatch(layer))) continue;

            a.AllGeometry.Add(e);

            string name;

            if (mergeMap.TryGetValue(layer, out var merged))
            {
                name = merged;
            }
            else
            {
                name = (suffix != null ? suffix.Replace(layer, "") : layer).ToUpperInvariant();
                if (name.Length == 0) name = layer.ToUpperInvariant();
            }

            if (!a.Components.TryGetValue(name, out var list)) a.Components[name] = list = new List<EntityObject>();
            list.Add(e);
        }

        foreach (var small in a.Components.Where(c => c.Value.Count < s.MinEntities).Select(c => c.Key).ToList())
        {
            a.Components.Remove(small);
            a.SkippedSmall.Add(small);
        }

        ParseCallouts(a, texts, leaders, s);
        return a;
    }

    private static Dictionary<string, List<EntityObject>> ClusterComponents(Analysis a, int minEntities)
    {
        var geometry = a.AllGeometry.Where(e => !(e is Text || e is MText)).ToList();
        var boxes = geometry.Select(BoxOf).ToList();
        var all = new Bounds();

        foreach (var b in boxes) all.Add(b);

        var result = new Dictionary<string, List<EntityObject>>(StringComparer.OrdinalIgnoreCase);

        if (!all.Valid || geometry.Count == 0) return result;

        var width = Math.Max(all.MaxX - all.MinX, 1.0);
        var height = Math.Max(all.MaxY - all.MinY, 1.0);
        var diag = Math.Sqrt(width * width + height * height);
        var gap = Math.Max(diag * 0.01, 1.0);
        var cell = gap * 4;

        var parent = new int[geometry.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y)
        {
            var rx = Find(x);
            var ry = Find(y);
            if (rx != ry) parent[ry] = rx;
        }

        var skip = new bool[geometry.Count];
        var grid = new Dictionary<(long, long), List<int>>();

        for (var i = 0; i < geometry.Count; i++)
        {
            var b = boxes[i];

            if (!b.Valid) { skip[i] = true; continue; }

            if ((b.MaxX - b.MinX) > width * 0.7 && (b.MaxY - b.MinY) > height * 0.7)
            {
                skip[i] = true;
                continue;
            }

            var x0 = (long)Math.Floor((b.MinX - gap) / cell);
            var x1 = (long)Math.Floor((b.MaxX + gap) / cell);
            var y0 = (long)Math.Floor((b.MinY - gap) / cell);
            var y1 = (long)Math.Floor((b.MaxY + gap) / cell);

            if ((x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerEntity)
            {
                skip[i] = true;
                continue;
            }

            for (var gx = x0; gx <= x1; gx++)
            {
                for (var gy = y0; gy <= y1; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var bucket))
                    {
                        grid[(gx, gy)] = bucket = new List<int>();
                    }

                    foreach (var other in bucket)
                    {
                        var ob = boxes[other];

                        if (b.MinX - gap <= ob.MaxX && b.MaxX + gap >= ob.MinX
                            && b.MinY - gap <= ob.MaxY && b.MaxY + gap >= ob.MinY)
                        {
                            Union(i, other);
                        }
                    }

                    bucket.Add(i);
                }
            }
        }

        var groups = new Dictionary<int, List<EntityObject>>();

        for (var i = 0; i < geometry.Count; i++)
        {
            if (skip[i]) continue;

            var root = Find(i);

            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<EntityObject>();
            list.Add(geometry[i]);
        }

        var ordered = groups.Values
            .Where(g => g.Count >= minEntities)
            .OrderByDescending(g => g.Count)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            result[$"PART_{i + 1:00}"] = ordered[i];
        }

        return result;
    }

    private static string? NearestComponent(Analysis a, double x, double y, double max)
    {
        string? best = null;
        var bestDist = max;

        foreach (var c in a.Components)
        {
            foreach (var e in c.Value)
            {
                var b = BoxOf(e);

                if (!b.Valid) continue;

                var d = b.DistanceTo(x, y);

                if (d < bestDist)
                {
                    bestDist = d;
                    best = c.Key;
                }
            }
        }

        return best;
    }

    // --------------------------------------------------------------- callouts

    private static string StripMText(string raw)
    {
        var t = raw.Replace("\\P", "\n").Replace("\\~", " ");
        t = Regex.Replace(t, @"\\[A-Za-z][^;\\{}]*;", "");
        t = Regex.Replace(t, @"\\[LlOoKk]", "");
        return t.Replace("{", "").Replace("}", "");
    }

    private static double SegDist(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - a.X) * dx + (py - a.Y) * dy) / len));
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static void ParseCallouts(Analysis a, List<EntityObject> texts, List<List<(double X, double Y)>> leaders, ComponentSettings s)
    {
        var calloutRx = new Regex(s.CalloutRegex, RegexOptions.IgnoreCase);
        var labelRx = new Regex(s.LabelRegex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seen = new HashSet<string>();

        foreach (var t in texts)
        {
            var raw = t is MText mt ? mt.Value : ((Text)t).Value;
            var pos = t is MText mt2 ? mt2.Position : ((Text)t).Position;
            var clean = StripMText(raw ?? "");
            var one = Regex.Replace(clean, @"\s+", " ").Trim();
            var m = calloutRx.Match(clean);
            var isLabel = !m.Success && one.Length > 0 && one.Length <= s.MaxLabelLength && labelRx.IsMatch(one);

            if (!m.Success && !isLabel) continue;

            List<(double X, double Y)>? bestLd = null;
            var bestD = double.MaxValue;

            foreach (var pts in leaders)
            {
                if (pts.Count < 2) continue;
                var d = SegDist(pos.X, pos.Y, pts[^2], pts[^1]);
                if (d < bestD) { bestD = d; bestLd = pts; }
            }

            double tipX = pos.X, tipY = pos.Y;
            var hasLeader = false;

            if (bestLd != null && bestD <= s.LeaderTolerance)
            {
                var chain = new List<(double X, double Y)>(bestLd);

                for (var pass = 0; pass < 3; pass++)
                {
                    foreach (var other in leaders)
                    {
                        if (ReferenceEquals(other, bestLd)) continue;
                        if (!other.Any(v => chain.Any(c => Math.Abs(c.X - v.X) <= 5 && Math.Abs(c.Y - v.Y) <= 5))) continue;
                        foreach (var v in other) if (!chain.Contains(v)) chain.Add(v);
                    }
                }

                var a0 = bestLd[^2];
                var b0 = bestLd[^1];
                var far = chain.OrderByDescending(v => SegDist(v.X, v.Y, a0, b0)).First();
                tipX = far.X;
                tipY = far.Y;
                hasLeader = true;
            }

            var lines = clean.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !calloutRx.IsMatch(l));
            var name = string.Join(" ", lines);

            if (name.Length == 0) continue;

            var drg = m.Success ? m.Groups[1].Value.Trim().TrimEnd('.') : "";

            if (!seen.Add(name + "|" + drg + "|" + Math.Round(tipX) + "|" + Math.Round(tipY))) continue;

            a.Callouts.Add(new Callout { Name = name, DrgNo = drg, X = tipX, Y = tipY, HasLeader = hasLeader });
        }
    }

    private static CalloutRule? RuleFor(string name, ComponentSettings s) =>
        s.Callouts.FirstOrDefault(r => Regex.IsMatch(name, r.Match, RegexOptions.IgnoreCase));

    private static void SnapToNearest(Analysis a, double x, double y, double max, out double cx, out double cy)
    {
        cx = x;
        cy = y;
        var best = max;

        foreach (var e in a.AllGeometry)
        {
            if (e is Text || e is MText) continue;

            var b = BoxOf(e);

            if (!b.Valid) continue;

            var d = b.DistanceTo(x, y);

            if (d < best)
            {
                best = d;
                cx = (b.MinX + b.MaxX) / 2;
                cy = (b.MinY + b.MaxY) / 2;
            }
        }
    }

    // --------------------------------------------------------------- cropping

    private static bool Inside(Bounds b, Bounds w) =>
        b.Valid && b.MinX >= w.MinX && b.MaxX <= w.MaxX && b.MinY >= w.MinY && b.MaxY <= w.MaxY;

    private static bool ClipLine(Line l, Bounds w, out EntityObject clipped)
    {
        clipped = l;
        double x0 = l.StartPoint.X, y0 = l.StartPoint.Y, x1 = l.EndPoint.X, y1 = l.EndPoint.Y;
        double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;

        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            var r = q / p;

            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }

            return true;
        }

        if (!Clip(-dx, x0 - w.MinX) || !Clip(dx, w.MaxX - x0) || !Clip(-dy, y0 - w.MinY) || !Clip(dy, w.MaxY - y0)) return false;
        if (t1 <= t0) return false;
        if (l.Clone() is not Line c) return false;

        c.StartPoint = new Vector3(x0 + t0 * dx, y0 + t0 * dy, l.StartPoint.Z);
        c.EndPoint = new Vector3(x0 + t1 * dx, y0 + t1 * dy, l.EndPoint.Z);
        clipped = c;
        return true;
    }

    private static List<EntityObject> CropEntities(Analysis a, Bounds win)
    {
        var list = new List<EntityObject>();
        var span = Math.Max(win.MaxX - win.MinX, win.MaxY - win.MinY);

        foreach (var e in a.AllGeometry)
        {
            if (e is Line l)
            {
                if (ClipLine(l, win, out var c)) list.Add(c);
                continue;
            }

            var b = BoxOf(e);

            if (!b.Valid) continue;

            if (e is Circle || e is Arc)
            {
                var cx = (b.MinX + b.MaxX) / 2;
                var cy = (b.MinY + b.MaxY) / 2;

                if (cx >= win.MinX && cx <= win.MaxX && cy >= win.MinY && cy <= win.MaxY && (b.MaxX - b.MinX) <= span)
                {
                    AddClone(list, e);
                }

                continue;
            }

            if (Inside(b, win)) AddClone(list, e);
        }

        foreach (var d in a.Dimensions)
        {
            if (d.Block != null && Inside(BoxOfDimension(d), win))
            {
                foreach (var be in d.Block.Entities) AddClone(list, be);
            }
        }

        foreach (var n in a.Notes)
        {
            if (Inside(BoxOf(n), win)) AddClone(list, n);
        }

        return list;
    }

    // --------------------------------------------------------------- geometry

    private static Bounds BoxOf(EntityObject e)
    {
        var b = new Bounds();

        switch (e)
        {
            case Line l: b.Add(l.StartPoint.X, l.StartPoint.Y); b.Add(l.EndPoint.X, l.EndPoint.Y); break;
            case Circle c: b.Add(c.Center.X - c.Radius, c.Center.Y - c.Radius); b.Add(c.Center.X + c.Radius, c.Center.Y + c.Radius); break;
            case Arc a: b.Add(a.Center.X - a.Radius, a.Center.Y - a.Radius); b.Add(a.Center.X + a.Radius, a.Center.Y + a.Radius); break;
            case Ellipse el:
                var r = Math.Max(el.MajorAxis, el.MinorAxis);
                b.Add(el.Center.X - r, el.Center.Y - r);
                b.Add(el.Center.X + r, el.Center.Y + r);
                break;
            case Polyline2D p2: foreach (var v in p2.Vertexes) b.Add(v.Position.X, v.Position.Y); break;
            case netDxf.Entities.Solid so:
                b.Add(so.FirstVertex.X, so.FirstVertex.Y);
                b.Add(so.SecondVertex.X, so.SecondVertex.Y);
                b.Add(so.ThirdVertex.X, so.ThirdVertex.Y);
                b.Add(so.FourthVertex.X, so.FourthVertex.Y);
                break;
            case Polyline3D p3: foreach (var v in p3.Vertexes) b.Add(v.X, v.Y); break;
            case Text t: b.Add(t.Position.X, t.Position.Y); break;
            case MText m: b.Add(m.Position.X, m.Position.Y); break;
            case netDxf.Entities.Point pt: b.Add(pt.Position.X, pt.Position.Y); break;
        }

        return b;
    }

    private static Bounds BoxOfDimension(Dimension d)
    {
        var b = new Bounds();

        if (d.Block != null)
        {
            foreach (var e in d.Block.Entities) b.Add(BoxOf(e));
        }

        return b;
    }

    private static List<(double X, double Y)> DimPoints(Dimension d)
    {
        var pts = new List<(double X, double Y)>();

        switch (d)
        {
            case LinearDimension ld:
                pts.Add((ld.FirstReferencePoint.X, ld.FirstReferencePoint.Y));
                pts.Add((ld.SecondReferencePoint.X, ld.SecondReferencePoint.Y));
                break;
            case AlignedDimension ad:
                pts.Add((ad.FirstReferencePoint.X, ad.FirstReferencePoint.Y));
                pts.Add((ad.SecondReferencePoint.X, ad.SecondReferencePoint.Y));
                break;
            default:
                var b = BoxOfDimension(d);
                if (b.Valid) pts.Add(((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2));
                break;
        }

        return pts;
    }

    private static Assigned AssignAnnotations(Analysis a, ComponentSettings s)
    {
        var res = new Assigned();
        var boxes = new Dictionary<string, List<Bounds>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in a.Components)
        {
            res.Dims[c.Key] = new List<Dimension>();
            res.Notes[c.Key] = new List<EntityObject>();
            boxes[c.Key] = c.Value.Select(BoxOf).Where(b => b.Valid).ToList();
        }

        foreach (var d in a.Dimensions)
        {
            var pts = DimPoints(d);
            string? best = null;
            var bestScore = 0;

            foreach (var c in a.Components)
            {
                var score = 0;

                foreach (var p in pts)
                {
                    if (boxes[c.Key].Any(b => b.DistanceTo(p.X, p.Y) <= s.MatchTolerance)) score++;
                }

                if (score > bestScore || (score == bestScore && score > 0 && best != null && c.Value.Count < a.Components[best].Count))
                {
                    best = c.Key;
                    bestScore = score;
                }
            }

            if (best == null) res.Unmatched++;
            else res.Dims[best].Add(d);
        }

        foreach (var n in a.Notes)
        {
            var nb = BoxOf(n);

            if (!nb.Valid) continue;

            string? best = null;
            var bestDist = double.MaxValue;

            foreach (var c in a.Components)
            {
                foreach (var b in boxes[c.Key])
                {
                    var dist = b.DistanceTo(nb.MinX, nb.MinY);
                    if (dist < bestDist) { bestDist = dist; best = c.Key; }
                }
            }

            if (best != null && bestDist <= s.TextMaxDistance) res.Notes[best].Add(n);
        }

        return res;
    }

    private static void AddClone(List<EntityObject> list, EntityObject e)
    {
        if (e.Clone() is EntityObject copy) list.Add(copy);
    }

    private static List<EntityObject> DropOutliers(List<EntityObject> items)
    {
        var cx = new List<double>();
        var cy = new List<double>();

        foreach (var e in items)
        {
            var b = BoxOf(e);

            if (!b.Valid) continue;

            cx.Add((b.MinX + b.MaxX) / 2);
            cy.Add((b.MinY + b.MaxY) / 2);
        }

        if (cx.Count < 10) return items;

        cx.Sort();
        cy.Sort();

        double P(List<double> v, double q) => v[(int)Math.Round((v.Count - 1) * q)];

        double x0 = P(cx, 0.1), x1 = P(cx, 0.9), y0 = P(cy, 0.1), y1 = P(cy, 0.9);
        double sx = Math.Max(x1 - x0, 1), sy = Math.Max(y1 - y0, 1);
        var res = new List<EntityObject>();

        foreach (var e in items)
        {
            var b = BoxOf(e);

            if (!b.Valid) { res.Add(e); continue; }

            var mx = (b.MinX + b.MaxX) / 2;
            var my = (b.MinY + b.MaxY) / 2;

            if (mx < x0 - sx || mx > x1 + sx || my < y0 - sy || my > y1 + sy) continue;

            res.Add(e);
        }

        return res.Count > 0 ? res : items;
    }

    // ------------------------------------------------------------------ sheet

    private static string SuggestScale(double extent)
    {
        var steps = new[] { 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0 };
        const double sheetLimit = 800.0;

        foreach (var step in steps)
        {
            if (extent / step <= sheetLimit)
            {
                return step == 1.0 ? "1:1" : "1:" + step.ToString("0");
            }
        }

        return "NTS";
    }

    private static int BuildSheet(DxfDocument doc, List<EntityObject> items, string partName, string drgNo, string gaFile)
    {
        items = DropOutliers(items);

        var bounds = new Bounds();

        foreach (var e in items)
        {
            if (e is not netDxf.Entities.Point) bounds.Add(BoxOf(e));
        }

        if (!bounds.Valid)
        {
            foreach (var e in items) bounds.Add(BoxOf(e));
        }

        if (!bounds.Valid)
        {
            bounds.Add(0, 0);
            bounds.Add(100, 100);
        }

        var w = bounds.MaxX - bounds.MinX;
        var h = bounds.MaxY - bounds.MinY;
        var sc = Math.Max(Math.Max(w, h), 1.0);
        var margin = sc * 0.04;
        var th = Math.Min(sc * 0.012, Math.Max(w, 1.0) / 40);
        var tbH = th * 1.8 * 6 + th;

        if (gaFile.Length > 38) gaFile = gaFile.Substring(0, 35) + "...";

        var shift = new Vector3(margin - bounds.MinX, tbH + margin - bounds.MinY, 0);

        foreach (var e in items)
        {
            e.TransformBy(Matrix3.Identity, shift);
            doc.Entities.Add(e);
        }

        var sheetW = w + 2 * margin;
        var sheetH = h + 2 * margin + tbH;
        var layer = new netDxf.Tables.Layer("TITLEBLOCK");

        void Rect(double x0, double y0, double x1, double y1)
        {
            var pl = new Polyline2D(new List<Vector2>
            {
                new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1)
            }, true);

            pl.Layer = layer;
            doc.Entities.Add(pl);
        }

        void Label(string text, double x, double y)
        {
            var t = new Text(text, new Vector2(x, y), th);
            t.Layer = layer;
            doc.Entities.Add(t);
        }

        Rect(0, 0, sheetW, sheetH);
        Rect(0, 0, sheetW, tbH);

        var tx = Math.Min(margin * 0.5, th);

        var rows = new[]
        {
            $"PART: {partName}",
            $"DRG NO: {drgNo}",
            $"GA FILE: {gaFile}",
            $"SCALE: NTS (SUGGESTED {SuggestScale(sc)})",
            "MATERIAL:",
            $"DATE: {DateTime.UtcNow:yyyy-MM-dd}"
        };

        for (var i = 0; i < rows.Length; i++)
        {
            Label(rows[i], tx, tbH - th * 0.5 - th * 1.8 * (i + 1) + th * 0.8);
        }

        return items.Count;
    }

    private static string Sanitize(string name)
    {
        var clean = Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_.\-]+", "_").Trim('_');
        return clean.Length == 0 ? "Part" : clean;
    }
}