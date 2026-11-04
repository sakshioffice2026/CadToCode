using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
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
    private readonly ReferenceCalloutCatalog _referenceCatalog;
    private readonly IConfiguration _configuration;
    private readonly IComponentDecomposer _decomposer;
    private readonly IDxfGeometryParser _dxfParser;
    private readonly IWebHostEnvironment _environment;
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
            IComponentDecomposer decomposer,
            ReferenceCalloutCatalog referenceCatalog,
            IConfiguration configuration,
            IDxfGeometryParser dxfParser,
            IWebHostEnvironment environment)
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
        _referenceCatalog = referenceCatalog;
        _configuration = configuration;
        _dxfParser = dxfParser;
        _environment = environment;
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
        var isAjax = string.Equals(
            Request.Headers["X-Requested-With"].ToString(),
            "XMLHttpRequest",
            StringComparison.OrdinalIgnoreCase);

        if (file is null || file.Length == 0)
        {
            return UploadFailure(isAjax, "Please choose a DXF file to upload.");
        }

        var fileName = Path.GetFileName(file.FileName);

        if (!string.Equals(Path.GetExtension(fileName), ".dxf", StringComparison.OrdinalIgnoreCase))
        {
            return UploadFailure(isAjax, "Only DXF drawings contain blocks. Please upload a .dxf file.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _processingService.ProcessUploadAsync(stream, fileName, cancellationToken);

            if (result.Success && result.ProjectId.HasValue)
            {
                if (!isAjax)
                {
                    return RedirectToAction(nameof(Index), new { id = result.ProjectId.Value });
                }

                int? partCount = null;

                try
                {
                    var breakdown = await _breakdownService.BuildBreakdownAsync(result.ProjectId.Value, cancellationToken);
                    partCount = breakdown?.Parts?.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Part count could not be read for project {ProjectId}.", result.ProjectId.Value);
                }

                return Json(new
                {
                    success = true,
                    projectId = result.ProjectId.Value,
                    partCount
                });
            }

            return UploadFailure(isAjax, result.ErrorMessage ?? "The drawing could not be processed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manufacturing upload failed for file {FileName}.", fileName);
            return UploadFailure(isAjax, "The upload could not be processed.");
        }
    }

    private IActionResult UploadFailure(bool isAjax, string message)
    {
        if (isAjax)
        {
            return Json(new { success = false, error = message });
        }

        TempData["Error"] = message;
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
        bool useLegacyFallback = true,
        CancellationToken cancellationToken = default)
    {
        if (specs is null || specs.Count == 0)
        {
            var ga = await RunGaPipelineAsync(id, projectCode, defaultMaterial, (double?)defaultThickness, cancellationToken);

            if (ga.Error is not null)
            {
                _logger.LogWarning("GA pipeline error for project {ProjectId}: {Error}", id, ga.Error);

                if (!useLegacyFallback)
                {
                    TempData["Error"] = ga.Error;
                    return RedirectToAction(nameof(Index), new { id });
                }
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

                if (!useLegacyFallback)
                {
                    var gaErrors = ga.Result.Messages.Where(m => m.StartsWith("[ERROR]", StringComparison.Ordinal)).ToList();
                    var detail = ga.Result.Messages.Count > 0
                        ? " " + string.Join(" | ", (gaErrors.Count > 0 ? gaErrors : ga.Result.Messages).Take(20))
                        : string.Empty;

                    TempData["Error"] = "No manufacturing drawing could be generated from the GA callouts." + detail;
                    return RedirectToAction(nameof(Index), new { id });
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
    public async Task<IActionResult> GenerateManufacturingDrawings(
    int id,
    string? projectCode = null,
    string? material = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var storedPath = await _unitOfWork.Projects.GetStoredFilePathAsync(id, cancellationToken);

            if (string.IsNullOrWhiteSpace(storedPath))
            {
                return NotFound(new { error = "The stored drawing file is missing." });
            }

            var breakdown = await _breakdownService.BuildBreakdownAsync(id, cancellationToken);

            if (breakdown is null || !breakdown.Success)
            {
                return BadRequest(new { error = breakdown?.ErrorMessage ?? "The drawing could not be analysed." });
            }

            var outputFolder = Path.Combine(
                _environment.ContentRootPath,
                "App_Data",
                "manufacturing",
                id.ToString(CultureInfo.InvariantCulture));

            var exported = await TryBuildPartPackageAsync(id, projectCode, material, outputFolder, cancellationToken);

            if (exported is not null)
            {
                await _unitOfWork.Projects.TouchLastOpenedAsync(id, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                return Json(new
                {
                    projectId = id,
                    count = exported.Count,
                    pdfCount = 0,
                    files = exported
                });
            }

            var requests = (breakdown.Parts ?? new List<BlockPartDto>())
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Layer))
                .GroupBy(p => p.Layer!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DxfPartRequest(
                    string.IsNullOrWhiteSpace(g.First().BlockName) ? g.Key : g.First().BlockName,
                    g.Key))
                .ToList();

            if (requests.Count == 0)
            {
                return UnprocessableEntity(new { error = "No component layers were found in the drawing." });
            }

            await using var stream = await _storage.OpenReadAsync(storedPath, cancellationToken);

            if (stream is null)
            {
                return NotFound(new { error = "The stored drawing file could not be opened." });
            }

            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            if (Directory.Exists(outputFolder))
            {
                foreach (var oldFile in Directory.GetFiles(outputFolder, "*.dxf")
                    .Concat(Directory.GetFiles(outputFolder, "*.pdf")))
                {
                    System.IO.File.Delete(oldFile);
                }
            }

            var sheetOptions = new DxfSheetOptions(
                breakdown.ProjectTitle,
                material,
                string.IsNullOrWhiteSpace(projectCode)
                    ? "PRJ-" + id.ToString("D5", CultureInfo.InvariantCulture)
                    : projectCode.Trim());

            var generated = await Task.Run(
                () => _dxfParser.SplitByLayers(buffer, requests, outputFolder, cancellationToken, sheetOptions),
                cancellationToken);

            if (generated.Count == 0)
            {
                return UnprocessableEntity(new { error = "No component drawing could be generated from the layers." });
            }

            await _unitOfWork.Projects.TouchLastOpenedAsync(id, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var files = generated.Select(g =>
            {
                var dxfName = Path.GetFileName(g.FilePath);
                var pdfName = string.IsNullOrWhiteSpace(g.PdfFilePath) ? null : Path.GetFileName(g.PdfFilePath);

                return new
                {
                    part = g.PartName,
                    layer = g.Layer,
                    entityCount = g.EntityCount,
                    dxfFileName = dxfName,
                    dxfDownloadUrl = Url.Action(
                        nameof(DownloadGeneratedFile),
                        "Manufacturing",
                        new { id, fileName = dxfName }),
                    pdfFileName = pdfName,
                    pdfDownloadUrl = pdfName is null
                        ? null
                        : Url.Action(
                            nameof(DownloadGeneratedFile),
                            "Manufacturing",
                            new { id, fileName = pdfName })
                };
            }).ToList();

            return Json(new
            {
                projectId = id,
                count = files.Count,
                pdfCount = files.Count(f => f.pdfFileName is not null),
                files
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manufacturing drawing generation failed for project {ProjectId}.", id);
            return StatusCode(500, new { error = "The manufacturing drawings could not be generated." });
        }
    }

    private sealed record GeneratedFileView(
        string Part,
        string Layer,
        int EntityCount,
        string DxfFileName,
        string? DxfDownloadUrl,
        string? PdfFileName,
        string? PdfDownloadUrl);

    private async Task<List<GeneratedFileView>?> TryBuildPartPackageAsync(
        int id,
        string? projectCode,
        string? material,
        string outputFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new PartManufacturingRequestDto
            {
                ProjectId = id,
                ProjectCode = string.IsNullOrWhiteSpace(projectCode) ? null : projectCode.Trim(),
                Origin = PartOriginMode.BottomLeft,
                DefaultMaterial = material
            };

            var package = await _manufacturingDxfService.BuildPackageAsync(request, cancellationToken);

            if (package is null || !package.Success || package.Files.Count == 0)
            {
                _logger.LogWarning(
                    "Part exporter returned no files for project {ProjectId}: {Reason}",
                    id,
                    package?.ErrorMessage ?? "no files");
                return null;
            }

            Directory.CreateDirectory(outputFolder);

            foreach (var oldFile in Directory.GetFiles(outputFolder, "*.dxf")
                .Concat(Directory.GetFiles(outputFolder, "*.pdf")))
            {
                System.IO.File.Delete(oldFile);
            }

            var invalid = Path.GetInvalidFileNameChars();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<GeneratedFileView>();

            foreach (var file in package.Files)
            {
                if (file.Content is null || file.Content.Length == 0)
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(file.FileName ?? string.Empty));

                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = "part";
                }

                baseName = new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

                var uniqueName = baseName;
                var counter = 2;

                while (!used.Add(uniqueName))
                {
                    uniqueName = baseName + "_" + counter.ToString(CultureInfo.InvariantCulture);
                    counter++;
                }

                var dxfName = uniqueName + ".dxf";

                await System.IO.File.WriteAllBytesAsync(
                    Path.Combine(outputFolder, dxfName),
                    file.Content,
                    cancellationToken);

                files.Add(new GeneratedFileView(
                    string.IsNullOrWhiteSpace(file.PartName) ? baseName : file.PartName,
                    file.NodeId ?? string.Empty,
                    file.EntityCount,
                    dxfName,
                    Url.Action(nameof(DownloadGeneratedFile), "Manufacturing", new { id, fileName = dxfName }),
                    null,
                    null));
            }

            return files.Count == 0 ? null : files;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Part exporter failed for project {ProjectId}. Falling back to layer split.", id);
            return null;
        }
    }

    [HttpGet]
    public IActionResult DownloadGeneratedFile(int id, string fileName)
    {
        var safeName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(safeName);

        var isDxf = string.Equals(extension, ".dxf", StringComparison.OrdinalIgnoreCase);
        var isPdf = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(safeName) || (!isDxf && !isPdf))
        {
            return BadRequest();
        }

        var folder = Path.GetFullPath(Path.Combine(
            _environment.ContentRootPath,
            "App_Data",
            "manufacturing",
            id.ToString(CultureInfo.InvariantCulture)));

        var fullPath = Path.GetFullPath(Path.Combine(folder, safeName));

        if (!fullPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, isPdf ? "application/pdf" : "application/dxf", safeName);
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
            var gaErrors = run.Result!.Messages.Where(m => m.StartsWith("[ERROR]", StringComparison.Ordinal)).ToList();
            TempData["Error"] = "No manufacturing drawing could be generated from the callouts. "
                + string.Join(" | ", (gaErrors.Count > 0 ? gaErrors : run.Result.Messages).Take(20));
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

            var referenceFiles = new List<string>();
            var referenceFolder = _configuration["Manufacturing:ReferenceDxfFolder"];
            if (!string.IsNullOrWhiteSpace(referenceFolder) && Directory.Exists(referenceFolder))
            {
                referenceFiles.AddRange(Directory.GetFiles(referenceFolder, "*.dxf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }

            var referenceReport = new GenerationReport();
            var referenceCallouts = await _referenceCatalog.LoadAsync(referenceFiles, referenceReport, cancellationToken);

            var result = _gaPipeline.Run(new GaPipelineRequest
            {
                Geometry = geometry,
                Nodes = nodes,
                ProjectCode = projectCode,
                DefaultMaterial = defaultMaterial,
                DefaultThicknessMm = defaultThickness,
                ReferenceCallouts = referenceCallouts
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