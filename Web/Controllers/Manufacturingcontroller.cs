using System.Globalization;
using System.Text;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    private readonly IProjectProcessingService _processingService;
    private readonly IBlockBreakdownService _breakdownService;
    private readonly ILogger<ManufacturingController> _logger;
    private readonly IPartDxfExportService _partDxfService;

    public ManufacturingController(
        IProjectProcessingService processingService,
        IBlockBreakdownService breakdownService,
        ILogger<ManufacturingController> logger,
        IPartDxfExportService partDxfService)
    {
        _processingService = processingService;
        _breakdownService = breakdownService;
        _logger = logger;
        _partDxfService = partDxfService;
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
    public async Task<IActionResult> DownloadPart(
        int id,
        string blockName,
        string? material,
        decimal? thickness,
        CancellationToken cancellationToken)
    {
        var spec = new PartDxfSpecDto { BlockName = blockName, Material = material, Thickness = thickness };
        var file = await _partDxfService.BuildPartFileAsync(id, spec, cancellationToken);

        if (file is null)
        {
            TempData["Error"] = "The part drawing could not be generated.";
            return RedirectToAction(nameof(Index), new { id });
        }

        return File(file.Content, "application/dxf", file.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadPackage(
        int id,
        List<PartDxfSpecDto>? specs,
        CancellationToken cancellationToken)
    {
        var request = new PartDxfRequestDto
        {
            ProjectId = id,
            Specs = specs ?? new List<PartDxfSpecDto>()
        };

        var zip = await _partDxfService.BuildPackageZipAsync(request, cancellationToken);

        if (zip is null)
        {
            TempData["Error"] = "The manufacturing DXF package could not be generated.";
            return RedirectToAction(nameof(Index), new { id });
        }

        return File(zip, "application/zip", "manufacturing-dxf-package-" + id + ".zip");
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