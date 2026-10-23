using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Web.Controllers;

public class ZooController : Controller
{
    private const string IndexViewPath = "~/Views/Zoo/Index.cshtml";

    private readonly IZooTextToCadService _zoo;
    private readonly ZooOptions _options;

    public ZooController(IZooTextToCadService zoo, IOptions<ZooOptions> options)
    {
        _zoo = zoo;
        _options = options.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new ZooIndexViewModel
        {
            IsConfigured = _zoo.IsConfigured,
            Formats = _options.OutputFormats,
            DefaultFormat = _options.DefaultOutputFormat,
            MaxPromptChars = _options.MaxPromptChars,
            History = await _zoo.ListAsync(cancellationToken)
        };

        return View(IndexViewPath, model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(
        [FromBody] ZooGenerateRequestDto? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Json(ZooActionResultDto.Fail("Describe the part you want to generate."));
        }

        var result = await _zoo.SubmitAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> Status(string id, CancellationToken cancellationToken)
    {
        var result = await _zoo.GetStatusAsync(id, cancellationToken);
        return Json(result);
    }

    [HttpGet]
    public async Task<IActionResult> Download(string id, string name, CancellationToken cancellationToken)
    {
        var file = await _zoo.GetFileAsync(id, name, cancellationToken);

        if (file is null)
        {
            return NotFound();
        }

        return File(file.Value.Content, ContentTypeFor(file.Value.FileName), file.Value.FileName);
    }

    private static string ContentTypeFor(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".step" or ".stp" => "model/step",
            ".gltf" => "model/gltf+json",
            ".glb" => "model/gltf-binary",
            ".stl" => "model/stl",
            ".obj" => "model/obj",
            _ => "application/octet-stream"
        };
    }
}
