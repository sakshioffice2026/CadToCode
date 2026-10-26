using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Web.Models;

namespace Web.Controllers;

public class RunCodeRequest
{
    public string? Code { get; set; }
}

public class ProjectController : Controller
{
    private const string UploadViewPath = "~/Views/Projects/Upload.cshtml";
    private const string DetailsViewPath = "~/Views/Projects/Details.cshtml";
    private const string HistoryViewPath = "~/Views/Projects/History.cshtml";

    private const int MaxFilesPerBatch = 20;
    private const long MaxBatchBytes = 524_288_000;

    private const int MaxCachedRuns = 50;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SandboxRunResult> RunCache = new();

    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;
    private readonly IDefinitionLearningService _learningService;
    private readonly IStepScriptRunner _stepScriptRunner;
    private readonly ILogger<ProjectController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ProjectController(
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService,
        IDefinitionLearningService learningService,
        IStepScriptRunner stepScriptRunner,
        ILogger<ProjectController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _processingService = processingService;
        _codeGenerationService = codeGenerationService;
        _learningService = learningService;
        _stepScriptRunner = stepScriptRunner;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    [HttpGet]
    public IActionResult Upload()
    {
        return View(UploadViewPath);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxBatchBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBatchBytes)]
    public async Task<IActionResult> Upload(List<IFormFile>? files, string? projectName, CancellationToken cancellationToken)
    {
        var selected = (files ?? new List<IFormFile>())
            .Where(f => f is not null && f.Length > 0)
            .ToList();

        if (selected.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Please choose at least one file to upload.");
            return View(UploadViewPath);
        }

        if (selected.Count > MaxFilesPerBatch)
        {
            ModelState.AddModelError(string.Empty, $"You can upload up to {MaxFilesPerBatch} files at a time.");
            return View(UploadViewPath);
        }

        var succeededIds = new List<int>();
        var failures = new List<string>();

        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file.FileName);

            try
            {
                await using var stream = file.OpenReadStream();
                var result = await _processingService.ProcessUploadAsync(stream, fileName, cancellationToken);

                if (result.ProjectId.HasValue)
                {
                    succeededIds.Add(result.ProjectId.Value);

                    if (selected.Count == 1 && !string.IsNullOrWhiteSpace(projectName))
                    {
                        await _processingService.RenameProjectAsync(result.ProjectId.Value, projectName, cancellationToken);
                    }
                }

                if (!result.Success)
                {
                    failures.Add($"{fileName}: {result.ErrorMessage ?? "The upload failed."}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                failures.Add($"{fileName}: The upload could not be processed.");
            }
        }

        if (selected.Count == 1)
        {
            if (succeededIds.Count == 1)
            {
                return RedirectToAction(nameof(Details), new { id = succeededIds[0] });
            }

            foreach (var failure in failures)
            {
                ModelState.AddModelError(string.Empty, failure);
            }

            return View(UploadViewPath);
        }

        if (succeededIds.Count == 0 && failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                ModelState.AddModelError(string.Empty, failure);
            }

            return View(UploadViewPath);
        }

        var analyzedCount = selected.Count - failures.Count;

        if (analyzedCount > 0)
        {
            TempData["Success"] = analyzedCount == 1
                ? "1 drawing uploaded and analyzed."
                : $"{analyzedCount} drawings uploaded and analyzed.";
        }

        if (failures.Count > 0)
        {
            TempData["Error"] = failures.Count == 1
                ? "1 drawing could not be analyzed. " + failures[0]
                : $"{failures.Count} drawings could not be analyzed. " + string.Join(" | ", failures);
        }

        return RedirectToAction(nameof(History));
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        try
        {
            var history = await _processingService.GetHistoryAsync(null, cancellationToken);
            return View(HistoryViewPath, history);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Project history could not be loaded.");
            TempData["Error"] = "History could not be loaded right now. Please try again.";
            return View(HistoryViewPath, new List<ProjectHistoryItemDto>());
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, KeyDimensionsDto? keyDimensions, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        if (HasNegativeValue(keyDimensions))
        {
            TempData["Error"] = "Dimensions cannot be negative.";
            return RedirectToAction(nameof(Details), new { id });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IProjectProcessingService>();
                await service.MarkOpenedAsync(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Project {ProjectId} could not be marked as opened.", id);
            }
        });

        var code = await _codeGenerationService.GenerateAsync(id, cancellationToken, keyDimensions);

        var assembly = project.Status == ProjectStatus.Processed
            ? await _codeGenerationService.GenerateAssemblyAsync(id, false, cancellationToken, keyDimensions)
            : new AssemblyGenerationResult { ProjectId = id };

        return View(DetailsViewPath, new ProjectResultViewModel
        {
            Project = project,
            GeneratedCode = code,
            Assembly = assembly,
            KeyDimensions = keyDimensions ?? new KeyDimensionsDto()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(int id, string? title, CancellationToken cancellationToken)
    {
        var clean = (title ?? string.Empty).Trim();

        if (clean.Length == 0)
        {
            return BadRequest(new { success = false, error = "The name cannot be empty." });
        }

        var renamed = await _processingService.RenameProjectAsync(id, clean, cancellationToken);

        if (!renamed)
        {
            return NotFound(new { success = false, error = "That drawing no longer exists." });
        }

        return Json(new { success = true, id, title = clean.Length <= 200 ? clean : clean[..200] });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _processingService.DeleteProjectAsync(id, cancellationToken);

        if (deleted)
        {
            TempData["Success"] = "Drawing removed from history.";
        }
        else
        {
            TempData["Error"] = "That drawing no longer exists.";
        }

        return RedirectToAction(nameof(History));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(262_144)]
    public async Task<IActionResult> RunCode([FromBody] RunCodeRequest? request, CancellationToken cancellationToken)
    {
        var code = request?.Code;
        string? cacheKey = null;

        if (!string.IsNullOrWhiteSpace(code))
        {
            cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

            if (RunCache.TryGetValue(cacheKey, out var cachedRun))
            {
                return Json(cachedRun);
            }
        }

        var result = await _stepScriptRunner.RunAsync(code, cancellationToken);

        if (cacheKey is not null && result is not null && result.Success)
        {
            if (RunCache.Count >= MaxCachedRuns)
            {
                RunCache.Clear();
            }

            RunCache[cacheKey] = result;
        }

        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateComponent(ComponentDto component, CancellationToken cancellationToken)
    {
        if (HasNegativeValue(component))
        {
            TempData["Error"] = "Dimensions and angle cannot be negative.";
            return RedirectToAction(nameof(Details), new { id = component.ProjectId });
        }

        var updated = await _processingService.UpdateComponentAsync(component, cancellationToken);
        if (!updated)
        {
            return NotFound();
        }

        TempData["Success"] = "Component updated.";
        return RedirectToAction(nameof(Details), new { id = component.ProjectId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmDefinition(int id, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var learned = await _learningService.ConfirmAsync(id, cancellationToken);

        if (learned > 0)
        {
            TempData["Success"] = learned == 1
                ? "Definition saved. Similar drawings will now be recognized faster."
                : $"{learned} definitions saved. Similar drawings will now be recognized faster.";
        }
        else
        {
            TempData["Error"] = "Nothing could be saved for this drawing.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var stream = await _processingService.OpenSourceFileAsync(id, cancellationToken);
        if (stream is null)
        {
            return NotFound();
        }

        var contentType = Path.GetExtension(project.OriginalFileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".dxf" => "application/dxf",
            ".step" or ".stp" => "model/step",
            _ => "application/octet-stream"
        };

        return File(stream, contentType, project.OriginalFileName);
    }

    private static bool HasNegativeValue(KeyDimensionsDto? keys)
    {
        return keys is not null
            && (keys.VesselDiameter < 0
                || keys.VesselHeight < 0
                || keys.InletPipeDiameter < 0
                || keys.OutletPipeDiameter < 0
                || keys.TankLength < 0);
    }

    private static bool HasNegativeValue(ComponentDto component)
    {
        return component.Height < 0
            || component.Width < 0
            || component.Diameter < 0
            || component.TopDiameter < 0
            || component.BottomDiameter < 0
            || component.Angle < 0;
    }
}