using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.Repositories.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Web.Controllers;

public class ManufacturingIndexViewModel
{
    public List<ProjectHistoryItemDto> Projects { get; set; } = new();
    public int? SelectedProjectId { get; set; }
    public ManufacturingBreakdownDto? Breakdown { get; set; }
}

public class ManufacturingController : Controller
{
    private const string IndexViewPath = "~/Views/Manufacturing/Index.cshtml";
    private const long MaxUploadBytes = 52_428_800;
    private const long MaxBomBytes = 5_242_880;
    private readonly ParametricPartSheetService _parametricSheets;
    private readonly IProjectProcessingService _processingService;
    private readonly IBlockBreakdownService _breakdownService;
    private readonly ILogger<ManufacturingController> _logger;
    private readonly IPartManufacturingDxfService _manufacturingDxfService;
    private readonly GaManufacturingPipeline _gaPipeline;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorageService _storage;
    private readonly IEnumerable<IGeometryExtractor> _extractors;
    private readonly IComponentDecomposer _decomposer;

    public ManufacturingController(
    IProjectProcessingService processingService,
    IBlockBreakdownService breakdownService,
    ILogger<ManufacturingController> logger,
    IPartManufacturingDxfService manufacturingDxfService,
    ParametricPartSheetService parametricSheets,
    GaManufacturingPipeline gaPipeline,
    IUnitOfWork unitOfWork,
    IFileStorageService storage,
    IEnumerable<IGeometryExtractor> extractors,
    IComponentDecomposer decomposer)
    {
        _processingService = processingService;
        _breakdownService = breakdownService;
        _logger = logger;
        _manufacturingDxfService = manufacturingDxfService;
        _parametricSheets = parametricSheets;
        _gaPipeline = gaPipeline;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _extractors = extractors;
        _decomposer = decomposer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? id, CancellationToken cancellationToken)
    {
        var model = new ManufacturingIndexViewModel { SelectedProjectId = id };

        try
        {
            var history = await _processingService.GetHistoryAsync(null, cancellationToken);
            model.Projects = history
                .Where(p => p.SourceFileType == SourceFileType.Dxf && p.Status == ProjectStatus.Processed)
                .ToList();

            if (id.HasValue)
            {
                model.Breakdown = await _breakdownService.BuildBreakdownAsync(id.Value, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manufacturing page could not be loaded.");
            TempData["Error"] = "The manufacturing breakdown could not be loaded right now. Please try again.";
        }

        return View(IndexViewPath, model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Please choose a DXF file to upload.";
            return RedirectToAction(nameof(Index));
        }

        var fileName = Path.GetFileName(file.FileName);

        if (!string.Equals(Path.GetExtension(fileName), ".dxf", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Only DXF drawings contain blocks. Please upload a .dxf file.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _processingService.ProcessUploadAsync(stream, fileName, cancellationToken);

            if (result.Success && result.ProjectId.HasValue)
            {
                return RedirectToAction(nameof(Index), new { id = result.ProjectId.Value });
            }

            TempData["Error"] = result.ErrorMessage ?? "The drawing could not be processed.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manufacturing upload failed for file {FileName}.", fileName);
            TempData["Error"] = "The upload could not be processed.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadComponentDxf(
        int id,
        string nodeId,
        string? partName,
        string? material,
        decimal? thickness,
        int? quantity,
        string? projectCode,
        PartOriginMode origin = PartOriginMode.BottomLeft,
        CancellationToken cancellationToken = default)
    {
        var request = new PartManufacturingRequestDto
        {
            ProjectId = id,
            ProjectCode = projectCode,
            Origin = origin,
            Specs = new List<PartManufacturingSpecDto>
            {
                new()
                {
                    NodeId = nodeId,
                    PartName = partName,
                    Material = material,
                    Thickness = thickness,
                    Quantity = quantity
                }
            }
        };

        var file = await _manufacturingDxfService.BuildPartAsync(request, nodeId, cancellationToken);

        if (file is null)
        {
            TempData["Error"] = "The component DXF could not be generated.";
            return RedirectToAction(nameof(Index), new { id });
        }

        return File(file.Content, "application/dxf", file.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadManufacturingDxfPackage(
        int id,
        List<PartManufacturingSpecDto>? specs,
        string? projectCode,
        string? defaultMaterial,
        decimal? defaultThickness,
        bool includeReferenceGeometry = false,
        bool includeAllParts = false,
        string? cutLayers = null,
        PartOriginMode origin = PartOriginMode.BottomLeft,
        CancellationToken cancellationToken = default)
    {
        if (specs is null || specs.Count == 0)
        {
            var ga = await RunGaPipelineAsync(id, projectCode, defaultMaterial, (double?)defaultThickness, cancellationToken);

            if (ga.Error is not null)
            {
                _logger.LogWarning("GA pipeline error for project {ProjectId}: {Error}", id, ga.Error);
            }
            else if (ga.Result is not null)
            {
                foreach (var message in ga.Result.Messages)
                {
                    _logger.LogWarning("GA pipeline project {ProjectId}: {Message}", id, message);
                }

                if (ga.Result.HasFiles)
                {
                    var gaZip = _gaPipeline.BuildZip(ga.Result);

                    if (gaZip is not null)
                    {
                        return File(gaZip, "application/zip", "manufacturing-dxf-package-" + id + ".zip");
                    }
                }
            }
        }

        var request = new PartManufacturingRequestDto
        {
            ProjectId = id,
            ProjectCode = projectCode,
            Origin = origin,
            IncludeReferenceGeometry = includeReferenceGeometry,
            IncludeAllParts = includeAllParts,
            CutLayers = (cutLayers ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            DefaultMaterial = defaultMaterial,
            DefaultThickness = defaultThickness,
            Specs = specs ?? new List<PartManufacturingSpecDto>()
        };

        var package = await _manufacturingDxfService.BuildPackageAsync(request, cancellationToken);

        if (!package.Success || package.Files.Count == 0)
        {
            var reason = package.ErrorMessage ?? "The manufacturing DXF package could not be generated.";
            var detail = package.Warnings.Count > 0
                ? " " + string.Join(" | ", package.Warnings.Take(5))
                : string.Empty;

            TempData["Error"] = reason + detail;
            return RedirectToAction(nameof(Index), new { id });
        }

        byte[] zip;
        using (var output = new MemoryStream())
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var file in package.Files)
                {
                    var entry = archive.CreateEntry(file.FileName, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(file.Content, cancellationToken);
                }
            }

            zip = output.ToArray();
        }

        return File(zip, "application/zip", "manufacturing-dxf-package-" + id + ".zip");

    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GaReport(
        int id,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken = default)
    {
        var run = await RunGaPipelineAsync(id, projectCode, defaultMaterial, defaultThickness, cancellationToken);

        if (run.Error is not null)
        {
            return BadRequest(new { error = run.Error });
        }

        var result = run.Result!;
        var completeness = result.Completeness;

        return Json(new
        {
            calloutCount = result.Callouts.Parts.Count,
            dxfCount = result.Files.Count,
            generated = completeness.Generated,
            cutList = completeness.CutList,
            boughtOut = completeness.BoughtOut,
            unmatched = completeness.Unmatched,
            isComplete = completeness.IsComplete,
            hasErrors = result.HasErrors,
            calculatedFinishTotalKg = result.CalculatedFinishTotalKg,
            rows = completeness.Rows.Select(r => new
            {
                part = r.Part.Name,
                disposition = r.Disposition.ToString(),
                file = r.FileName,
                qty = r.Part.Qty,
                thickness = r.Part.ThicknessMm,
                weightKg = r.Part.WeightKg,
                reason = r.Reason
            }),
            unusedFiles = completeness.UnusedFiles,
            weightChecks = completeness.WeightChecks.Select(w => new
            {
                label = w.Label,
                drawingTotalKg = w.DrawingTotalKg,
                calloutSumKg = w.CalloutSumKg,
                differencePercent = w.DifferencePercent,
                matches = w.Matches
            }),
            issues = result.Report.Issues.Select(i => new
            {
                severity = i.Severity.ToString(),
                code = i.Code,
                part = i.PartName,
                message = i.Message
            })
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GaPackage(
        int id,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken = default)
    {
        var run = await RunGaPipelineAsync(id, projectCode, defaultMaterial, defaultThickness, cancellationToken);

        if (run.Error is not null)
        {
            TempData["Error"] = run.Error;
            return RedirectToAction(nameof(Index), new { id });
        }

        var zip = _gaPipeline.BuildZip(run.Result!);

        if (zip is null)
        {
            TempData["Error"] = "No manufacturing drawing could be generated from the callouts. "
                + string.Join(" | ", run.Result!.Messages.Take(5));
            return RedirectToAction(nameof(Index), new { id });
        }

        return File(zip, "application/zip", "ga-manufacturing-package-" + id + ".zip");
    }

    private async Task<(GaPipelineResult? Result, string? Error)> RunGaPipelineAsync(
        int id,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = await _unitOfWork.Projects.GetStoredFilePathAsync(id, cancellationToken);

            if (string.IsNullOrWhiteSpace(path))
            {
                return (null, "The stored drawing file is missing.");
            }

            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(SourceFileType.Dxf));

            if (extractor is null)
            {
                return (null, "No DXF geometry extractor is registered.");
            }

            await using var stream = await _storage.OpenReadAsync(path, cancellationToken)
                ?? throw new InvalidOperationException("The stored drawing file could not be opened.");

            var geometry = await extractor.ExtractAsync(stream, cancellationToken);
            var nodes = _decomposer.Decompose(geometry);

            var result = _gaPipeline.Run(new GaPipelineRequest
            {
                Geometry = geometry,
                Nodes = nodes,
                ProjectCode = projectCode,
                DefaultMaterial = defaultMaterial,
                DefaultThicknessMm = defaultThickness
            });

            return (result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GA manufacturing run failed for project {ProjectId}.", id);
            return (null, "The GA manufacturing run could not be completed.");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxBomBytes)]
    public async Task<IActionResult> BomSheetReport(
        IFormFile? bomFile,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildBomPackageAsync(bomFile, projectCode, defaultMaterial, defaultThickness, cancellationToken);

        if (result.Error is not null)
        {
            return BadRequest(new { error = result.Error });
        }

        var package = result.Package!;

        return Json(new
        {
            sheetCount = package.SheetCount,
            requestedCount = package.RequestedCount,
            hasErrors = package.HasErrors,
            issues = IssueRows(package)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxBomBytes)]
    public async Task<IActionResult> BomSheets(
        IFormFile? bomFile,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildBomPackageAsync(bomFile, projectCode, defaultMaterial, defaultThickness, cancellationToken);

        if (result.Error is not null)
        {
            return BadRequest(new { error = result.Error });
        }

        var package = result.Package!;

        if (package.Zip is null)
        {
            return UnprocessableEntity(new
            {
                error = "No sheet could be generated from the BOM.",
                issues = IssueRows(package)
            });
        }

        var code = string.IsNullOrWhiteSpace(projectCode) ? "bom" : SafeFileName(projectCode);
        return File(package.Zip, "application/zip", "part-sheets-" + code + ".zip");
    }

    private async Task<(ParametricSheetPackage? Package, string? Error)> BuildBomPackageAsync(
        IFormFile? bomFile,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThickness,
        CancellationToken cancellationToken)
    {
        if (bomFile is null || bomFile.Length == 0)
        {
            return (null, "Please choose a BOM file (.csv or .json).");
        }

        var extension = Path.GetExtension(bomFile.FileName);
        var isCsv = string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase);
        var isJson = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase);

        if (!isCsv && !isJson)
        {
            return (null, "The BOM must be a .csv or .json file.");
        }

        try
        {
            string text;
            using (var reader = new StreamReader(bomFile.OpenReadStream(), Encoding.UTF8, true))
            {
                text = await reader.ReadToEndAsync(cancellationToken);
            }

            var package = isCsv
                ? _parametricSheets.BuildFromCsv(text, projectCode, defaultMaterial, defaultThickness)
                : _parametricSheets.BuildFromJson(text, projectCode, defaultMaterial, defaultThickness);

            return (package, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BOM sheet generation failed for file {FileName}.", bomFile.FileName);
            return (null, "The BOM could not be processed.");
        }
    }

    private static IEnumerable<object> IssueRows(ParametricSheetPackage package)
    {
        return package.Issues.Select(i => (object)new
        {
            severity = i.Severity.ToString(),
            code = i.Code,
            part = i.PartName,
            message = i.Message
        });
    }
    [HttpGet]
    public async Task<IActionResult> Export(int id, CancellationToken cancellationToken)
    {
        var breakdown = await _breakdownService.BuildBreakdownAsync(id, cancellationToken);
        if (!breakdown.Success)
        {
            return NotFound();
        }

        var builder = new StringBuilder();
        builder.AppendLine("Item,Block name,Quantity,Width,Height,Layer,Lines,Arcs,Circles,Polylines,Labels");

        var item = 0;
        foreach (var part in breakdown.Parts)
        {
            item++;
            builder.AppendLine(string.Join(",",
                item.ToString(CultureInfo.InvariantCulture),
                Csv(part.BlockName),
                part.Quantity.ToString(CultureInfo.InvariantCulture),
                part.Width.ToString("0.###", CultureInfo.InvariantCulture),
                part.Height.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(part.Layer),
                part.LineCount.ToString(CultureInfo.InvariantCulture),
                part.ArcCount.ToString(CultureInfo.InvariantCulture),
                part.CircleCount.ToString(CultureInfo.InvariantCulture),
                part.PolylineCount.ToString(CultureInfo.InvariantCulture),
                Csv(string.Join("; ", part.Labels))));
        }

        var fileName = SafeFileName(breakdown.ProjectTitle) + "-manufacturing-breakdown.csv";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();

        return File(bytes, "text/csv", fileName);
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && "=+-@".Contains(text[0]))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static string SafeFileName(string? title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "drawing" : title.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}