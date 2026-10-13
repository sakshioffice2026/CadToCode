using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Web.Models;

namespace Web.Controllers;

public class ProjectController : Controller
{
    private const string UploadViewPath = "~/Views/Projects/Upload.cshtml";
    private const string DetailsViewPath = "~/Views/Projects/Details.cshtml";

    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;
    private readonly IDefinitionLearningService _learningService;

    public ProjectController(
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService,
        IDefinitionLearningService learningService)
    {
        _processingService = processingService;
        _codeGenerationService = codeGenerationService;
        _learningService = learningService;
    }

    [HttpGet]
    public IActionResult Upload()
    {
        return View(UploadViewPath);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please choose a file to upload.");
            return View(UploadViewPath);
        }

        await using var stream = file.OpenReadStream();
        var result = await _processingService.ProcessUploadAsync(stream, file.FileName, cancellationToken);

        if (result.ProjectId.HasValue)
        {
            return RedirectToAction(nameof(Details), new { id = result.ProjectId.Value });
        }

        ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "The upload failed.");
        return View(UploadViewPath);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var code = await _codeGenerationService.GenerateAsync(id, cancellationToken);

        var assembly = project.Status == ProjectStatus.Processed
            ? await _codeGenerationService.GenerateAssemblyAsync(id, false, cancellationToken)
            : new AssemblyGenerationResult { ProjectId = id };

        return View(DetailsViewPath, new ProjectResultViewModel
        {
            Project = project,
            GeneratedCode = code,
            Assembly = assembly
        });
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
    public async Task<IActionResult> RefineWithAi(int id, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var result = await _codeGenerationService.GenerateAssemblyAsync(id, true, cancellationToken);

        if (result.Success && result.Source == AssemblyCodeSource.Ai)
        {
            TempData["Success"] = "AI refinement complete.";
        }
        else
        {
            TempData["Error"] = "AI refinement was not used: " + (result.FallbackReason ?? result.Error ?? "unknown reason") + ".";
        }

        return RedirectToAction(nameof(Details), new { id });
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
            _ => "application/octet-stream"
        };

        return File(stream, contentType, project.OriginalFileName);
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