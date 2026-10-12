using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using Microsoft.AspNetCore.Mvc;
using Web.Models;

namespace Web.Controllers;

public class ProjectsController : Controller
{
    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;

    public ProjectsController(
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService)
    {
        _processingService = processingService;
        _codeGenerationService = codeGenerationService;
    }

    [HttpGet]
    public IActionResult Upload()
    {
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please choose a file to upload.");
            return View();
        }

        await using var stream = file.OpenReadStream();
        var result = await _processingService.ProcessUploadAsync(stream, file.FileName, cancellationToken);

        if (result.ProjectId.HasValue)
        {
            return RedirectToAction(nameof(Result), new { id = result.ProjectId.Value });
        }

        ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "The upload failed.");
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Result(int id, CancellationToken cancellationToken)
    {
        var project = await _processingService.GetProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return NotFound();
        }

        var code = await _codeGenerationService.GenerateAsync(id, cancellationToken);

        return View(new ProjectResultViewModel
        {
            Project = project,
            GeneratedCode = code
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateComponent(ComponentDto component, CancellationToken cancellationToken)
    {
        if (HasNegativeValue(component))
        {
            TempData["Error"] = "Dimensions and angle cannot be negative.";
            return RedirectToAction(nameof(Result), new { id = component.ProjectId });
        }

        var updated = await _processingService.UpdateComponentAsync(component, cancellationToken);
        if (!updated)
        {
            return NotFound();
        }

        TempData["Success"] = "Component updated.";
        return RedirectToAction(nameof(Result), new { id = component.ProjectId });
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