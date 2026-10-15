using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Web.Controllers;

public class LibraryController : Controller
{
    private const string IndexViewPath = "~/Views/Library/Index.cshtml";

    private const int MaxFilesPerBatch = 20;
    private const long MaxBatchBytes = 104_857_600;

    private const string MessageKey = "LibraryMessage";
    private const string ErrorKey = "LibraryError";

    private readonly ICadLibraryService _library;
    private readonly ILogger<LibraryController> _logger;

    public LibraryController(ICadLibraryService library, ILogger<LibraryController> logger)
    {
        _library = library;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? search,
        string? category,
        string? fileType,
        CancellationToken cancellationToken)
    {
        var model = await _library.ListAsync(search, category, fileType, cancellationToken);
        return View(IndexViewPath, model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxBatchBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxBatchBytes)]
    public async Task<IActionResult> Upload(
        List<IFormFile>? files,
        string? title,
        string? category,
        string? tags,
        string? description,
        CancellationToken cancellationToken)
    {
        var selected = (files ?? new List<IFormFile>())
            .Where(f => f is not null && f.Length > 0)
            .ToList();

        if (selected.Count == 0)
        {
            TempData[ErrorKey] = "Please choose at least one .dxf, .step or .cs file.";
            return RedirectToAction(nameof(Index));
        }

        if (selected.Count > MaxFilesPerBatch)
        {
            TempData[ErrorKey] = $"You can add up to {MaxFilesPerBatch} files at a time.";
            return RedirectToAction(nameof(Index));
        }

        var added = 0;
        var warnings = new List<string>();

        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file.FileName);

            try
            {
                await using var stream = file.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);

                var result = await _library.AddAsync(
                    new CadLibraryUploadRequestDto
                    {
                        Title = selected.Count == 1 ? (title ?? string.Empty) : string.Empty,
                        Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                        Tags = tags,
                        Description = description,
                        FileName = fileName,
                        Content = buffer.ToArray()
                    },
                    cancellationToken);

                if (!result.Success)
                {
                    warnings.Add(fileName + ": " + result.ErrorMessage);
                    continue;
                }

                added++;

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    warnings.Add(fileName + ": " + result.ErrorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library upload failed for {FileName}.", fileName);
                warnings.Add(fileName + ": the file could not be added.");
            }
        }

        if (added > 0)
        {
            TempData[MessageKey] = added == 1
                ? "1 item added to the CAD library."
                : added + " items added to the CAD library.";
        }

        if (warnings.Count > 0)
        {
            TempData[ErrorKey] = string.Join(" ", warnings);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(CadLibraryUpdateRequestDto request, CancellationToken cancellationToken)
    {
        var result = await _library.UpdateAsync(request, cancellationToken);
        SetResult(result, "Library item updated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(int id, CancellationToken cancellationToken)
    {
        var result = await _library.SyncAsync(id, cancellationToken);
        SetResult(result, "Item synced to the cloud vector library.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncAll(bool pendingOnly, CancellationToken cancellationToken)
    {
        var result = await _library.SyncAllAsync(pendingOnly, cancellationToken);

        if (result.Total == 0)
        {
            TempData[MessageKey] = "Nothing to sync.";
        }
        else
        {
            TempData[MessageKey] = $"Synced {result.Synced} of {result.Total} items.";
        }

        if (result.Failed > 0)
        {
            TempData[ErrorKey] = string.Join(" ", result.Errors.Take(5));
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await _library.DeleteAsync(id, cancellationToken);
        SetResult(result, "Library item deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id, CancellationToken cancellationToken)
    {
        var file = await _library.GetFileAsync(id, cancellationToken);

        if (file is null)
        {
            return NotFound();
        }

        return File(file.Value.Content, "application/octet-stream", file.Value.FileName);
    }

    private void SetResult(CadLibraryResultDto result, string successMessage)
    {
        if (!result.Success)
        {
            TempData[ErrorKey] = result.ErrorMessage ?? "The request could not be completed.";
            return;
        }

        TempData[MessageKey] = successMessage;
    }
}