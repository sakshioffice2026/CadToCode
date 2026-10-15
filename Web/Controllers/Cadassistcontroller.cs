using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Prompts;
using CadToCode.LLM.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Web.Controllers;

public class CadAssistResetRequest
{
    public int ProjectId { get; set; }

    public string? SessionId { get; set; }
}

public class CadAssistController : Controller
{
    private const int MaxRequestBytes = 2_097_152;
    private const int MaxFileNameChars = 120;
    private const int MaxFieldChars = 400;
    private const int MaxTemplateChars = 3000;
    private const int MinGeometryChars = 1200;
    private const long MaxUploadBytes = 52_428_800;
    private const int MaxUploadFiles = 5;

    private readonly ILocalAiService _ai;
    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;
    private readonly IStepScriptRunner _stepScriptRunner;
    private readonly ICadTemplateMatcher _templateMatcher;
    private readonly IDrawingProcessingService _drawingProcessing;
    private readonly LocalAiOptions _options;
    private readonly ILogger<CadAssistController> _logger;

    public CadAssistController(
        ILocalAiService ai,
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService,
        IStepScriptRunner stepScriptRunner,
        ICadTemplateMatcher templateMatcher,
        IDrawingProcessingService drawingProcessing,
        IOptions<LocalAiOptions> options,
        ILogger<CadAssistController> logger)
    {
        _ai = ai;
        _processingService = processingService;
        _codeGenerationService = codeGenerationService;
        _stepScriptRunner = stepScriptRunner;
        _templateMatcher = templateMatcher;
        _drawingProcessing = drawingProcessing;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Status()
    {
        return Json(_ai.GetStatus());
    }

    [HttpGet]
    public IActionResult History(int projectId, string? sessionId)
    {
        if (projectId < 0 || string.IsNullOrWhiteSpace(sessionId))
        {
            return Json(new CadAssistHistoryResponse { SessionId = string.Empty });
        }

        return Json(new CadAssistHistoryResponse
        {
            SessionId = sessionId,
            Messages = _ai.GetHistory(projectId, sessionId).ToList()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public IActionResult Reset([FromBody] CadAssistResetRequest? request)
    {
        if (request is null || request.ProjectId < 0)
        {
            return BadRequest();
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            _ai.ResetSession(request.ProjectId, request.SessionId);
        }

        return Json(new { success = true, sessionId = _ai.EnsureSession(request.ProjectId, null) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> Chat([FromBody] CadAssistChatRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.ProjectId < 0)
        {
            return BadRequest(new CadAssistChatResponse { Error = "The request was not valid." });
        }

        var sessionId = _ai.EnsureSession(request.ProjectId, request.SessionId);

        if (!_ai.IsAvailable)
        {
            var status = _ai.GetStatus();

            return Json(new CadAssistChatResponse
            {
                Success = false,
                SessionId = sessionId,
                Model = status.Model,
                Error = status.Message ?? "The local model is not available."
            });
        }

        var questionOnly = CadAssistPromptTemplates.IsQuestionOnly(request.Prompt);
        var fewShotChars = ResolveFewShotChars(questionOnly);
        var geometryChars = Math.Max(MinGeometryChars, _options.MaxGeometryChars - fewShotChars);

        var attachments = CleanAttachments(request.Attachments);
        var projectId = 0;
        var geometryContext = GeometryContextBuilder.BuildDraftContext(attachments, request.DrawingType, geometryChars);

        if (request.ProjectId > 0)
        {
            var project = await _processingService.GetProjectAsync(request.ProjectId, cancellationToken);
            if (project is null)
            {
                return NotFound(new CadAssistChatResponse { SessionId = sessionId, Error = "That drawing no longer exists." });
            }

            AssemblyTree? tree = null;

            if (project.Status == ProjectStatus.Processed)
            {
                var assembly = await _codeGenerationService.GenerateAssemblyAsync(project.Id, false, cancellationToken);
                tree = assembly.Tree;
            }

            projectId = project.Id;

            CadTemplateMatch? match = null;

            if (fewShotChars > 0)
            {
                match = await _templateMatcher.MatchAsync(
                    project, tree, request.Prompt, request.DrawingType, attachments, cancellationToken);
            }

            geometryContext = GeometryContextBuilder.Build(
                project,
                tree,
                geometryChars,
                match,
                fewShotChars);
        }
        else if (fewShotChars > 0)
        {
            var match = await _templateMatcher.MatchAsync(
                null, null, request.Prompt, request.DrawingType, attachments, cancellationToken);

            var section = GeometryContextBuilder.BuildFewShotSection(match, fewShotChars);

            if (section.Length > 0)
            {
                geometryContext = geometryContext + Environment.NewLine + Environment.NewLine + section;
            }
        }

        var generation = new LocalAiGenerationRequest
        {
            ProjectId = projectId,
            SessionId = sessionId,
            Prompt = request.Prompt ?? string.Empty,
            DrawingType = Limit(request.DrawingType, MaxFieldChars),
            FilterInstructions = Limit(request.FilterInstructions, MaxFieldChars),
            CurrentCode = request.CurrentCode,
            GeometryContext = geometryContext,
            Attachments = attachments
        };

        var result = await _ai.ChatAsync(generation, cancellationToken);

        if (!result.Success || result.Message is null)
        {
            return Json(new CadAssistChatResponse
            {
                Success = false,
                SessionId = sessionId,
                Model = result.Model,
                ElapsedMs = result.ElapsedMs,
                Error = result.Error ?? "The assistant could not answer."
            });
        }

        if (_options.AutoVerifyCode)
        {
            result.Message.Status = await VerifyAsync(result.Message, cancellationToken);
        }

        return Json(new CadAssistChatResponse
        {
            Success = true,
            SessionId = sessionId,
            Message = result.Message,
            Model = result.Model,
            ElapsedMs = result.ElapsedMs
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> UploadAndGenerate(
        List<IFormFile>? files,
        int projectId,
        string? sessionId,
        string? prompt,
        string? drawingType,
        string? currentCode,
        KeyDimensionsDto? keyDimensions,
        CancellationToken cancellationToken)
    {
        if (projectId < 0)
        {
            return BadRequest(new CadAssistChatResponse { Error = "The request was not valid." });
        }

        var selected = (files ?? new List<IFormFile>())
            .Where(f => f is not null && f.Length > 0)
            .ToList();

        if (selected.Count == 0)
        {
            return BadRequest(new CadAssistChatResponse { Error = "Choose at least one DXF, PNG or JPG file." });
        }

        if (selected.Count > MaxUploadFiles)
        {
            return BadRequest(new CadAssistChatResponse { Error = "You can upload up to " + MaxUploadFiles + " files at a time." });
        }

        var session = _ai.EnsureSession(projectId, sessionId);

        if (!_ai.IsAvailable)
        {
            var status = _ai.GetStatus();

            return Json(new CadAssistChatResponse
            {
                Success = false,
                SessionId = session,
                Model = status.Model,
                Error = status.Message ?? "The local model is not available."
            });
        }

        var perFileChars = Math.Max(MinGeometryChars, _options.MaxGeometryChars) / selected.Count;

        var contexts = new List<string>();
        var attachments = new List<CadAssistAttachmentDto>();
        var warnings = new List<string>();
        var summaries = new List<object>();
        var failures = new List<string>();
        string? directScript = null;
        string? directFallback = null;
        string? directTemplate = null;

        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file.FileName ?? string.Empty);

            if (!_drawingProcessing.IsSupported(fileName))
            {
                failures.Add(fileName + ": unsupported file type. Use .dxf, .png, .jpg or .jpeg.");
                continue;
            }

            if (file.Length > _options.MaxAttachmentBytes)
            {
                failures.Add(fileName + ": the file is too large.");
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();

                var processed = await _drawingProcessing.ProcessAsync(
                    stream,
                    fileName,
                    prompt,
                    perFileChars,
                    cancellationToken,
                    keyDimensions);

                warnings.AddRange(processed.Warnings.Select(w => fileName + ": " + w));

                if (!processed.HasUsableData)
                {
                    failures.Add(fileName + ": " + (processed.Error ?? "no usable drawing data was found."));
                    continue;
                }

                contexts.Add(processed.ContextText);

                if (selected.Count == 1
                    && processed.Mode == DrawingProcessingMode.TemplateInjection
                    && !string.IsNullOrWhiteSpace(processed.InjectedScript ?? processed.TemplateScript))
                {
                    directScript = processed.InjectedScript ?? processed.TemplateScript;
                    directFallback = processed.TemplateScript;
                    directTemplate = processed.TemplateTitle;
                }

                attachments.Add(new CadAssistAttachmentDto
                {
                    FileName = Limit(fileName, MaxFileNameChars) ?? string.Empty,
                    ContentType = Limit(file.ContentType, 100) ?? string.Empty,
                    SizeBytes = file.Length,
                    TextContent = null
                });

                summaries.Add(new
                {
                    fileName,
                    source = processed.SourceKind.ToString(),
                    mode = processed.Mode.ToString(),
                    template = processed.TemplateTitle,
                    templateScore = processed.TemplateScore,
                    injected = processed.Injections.Select(i => new { i.Name, i.OldValue, i.NewValue, i.Source })
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Drawing processing failed for {File}.", fileName);
                failures.Add(fileName + ": the file could not be processed.");
            }
        }

        if (contexts.Count == 0)
        {
            return Json(new
            {
                success = false,
                sessionId = session,
                error = failures.Count > 0 ? string.Join(" ", failures) : "No usable drawing data was found.",
                warnings
            });
        }

        warnings.AddRange(failures);

        if (!string.IsNullOrWhiteSpace(directScript))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var script = directScript;
            var check = await _stepScriptRunner.RunAsync(script, cancellationToken);

            if (!string.IsNullOrWhiteSpace(directFallback)
                && !string.Equals(script, directFallback, StringComparison.Ordinal)
                && (!check.Success || !check.IsConnected))
            {
                warnings.Add(!check.Success
                    ? "The adjusted template did not build. The unmodified template is shown instead."
                    : "The adjusted template produced parts that float apart (" + string.Join(", ", check.IsolatedParts) + "). The unmodified template is shown instead.");

                script = directFallback;
                check = await _stepScriptRunner.RunAsync(script, cancellationToken);
            }

            var block = new CadAssistCodeBlockDto
            {
                Language = "csharp",
                Title = "STEP script",
                Code = script,
                LineCount = script.Split('\n').Length
            };

            var message = new CadAssistMessageDto
            {
                Role = CadAssistRole.Assistant,
                Content = "Template-based, approximate: " + (directTemplate ?? "verified template") + ". Built without the language model.",
                CodeBlocks = new List<CadAssistCodeBlockDto> { block },
                Actions = new List<CadAssistActionDto>
                {
                    new() { Kind = CadAssistActionKind.Run, Label = "Run", TargetCodeBlockId = block.Id },
                    new() { Kind = CadAssistActionKind.Apply, Label = "Apply to editor", TargetCodeBlockId = block.Id },
                    new() { Kind = CadAssistActionKind.DownloadStep, Label = "Download STEP", TargetCodeBlockId = block.Id },
                    new() { Kind = CadAssistActionKind.Copy, Label = "Copy", TargetCodeBlockId = block.Id },
                    new() { Kind = CadAssistActionKind.Regenerate, Label = "Regenerate" }
                },
                Model = "template",
                ElapsedMs = started.ElapsedMilliseconds,
                Status = check.Success
                    ? "Verified in sandbox: " + check.SolidCount + " solids, " + check.FaceCount + " faces, " + check.ElapsedMs + " ms"
                    : "Needs review: " + (check.Message ?? "the script did not run.")
            };

            return Json(new
            {
                success = true,
                sessionId = session,
                message,
                model = "template",
                elapsedMs = started.ElapsedMilliseconds,
                warnings,
                processing = summaries
            });
        }

        var generation = new LocalAiGenerationRequest
        {
            ProjectId = projectId,
            SessionId = session,
            Prompt = string.IsNullOrWhiteSpace(prompt)
                ? "Generate the STEP script for this drawing."
                : prompt.Trim(),
            DrawingType = Limit(drawingType, MaxFieldChars),
            FilterInstructions = null,
            CurrentCode = currentCode,
            GeometryContext = string.Join(Environment.NewLine + Environment.NewLine, contexts),
            Attachments = attachments
        };

        var result = await _ai.ChatAsync(generation, cancellationToken);

        if (!result.Success || result.Message is null)
        {
            return Json(new
            {
                success = false,
                sessionId = session,
                model = result.Model,
                elapsedMs = result.ElapsedMs,
                error = result.Error ?? "The assistant could not answer.",
                warnings,
                processing = summaries
            });
        }

        if (_options.AutoVerifyCode)
        {
            result.Message.Status = await VerifyAsync(result.Message, cancellationToken);
        }

        return Json(new
        {
            success = true,
            sessionId = session,
            message = result.Message,
            model = result.Model,
            elapsedMs = result.ElapsedMs,
            warnings,
            processing = summaries
        });
    }

    private int ResolveFewShotChars(bool questionOnly)
    {
        if (questionOnly)
        {
            return 0;
        }

        return Math.Min(MaxTemplateChars, Math.Max(0, _options.MaxGeometryChars / 2));
    }

    private async Task<string?> VerifyAsync(CadAssistMessageDto message, CancellationToken cancellationToken)
    {
        var block = message.CodeBlocks.FirstOrDefault(b => b.Language == "csharp");

        if (block is null)
        {
            return null;
        }

        try
        {
            var run = await _stepScriptRunner.RunAsync(block.Code, cancellationToken);

            if (run.Success)
            {
                return "Verified in sandbox: " + run.SolidCount + " solids, " + run.FaceCount + " faces, " + run.ElapsedMs + " ms";
            }

            var diagnostic = run.Diagnostics.FirstOrDefault();

            if (diagnostic is not null)
            {
                return "Needs review: line " + diagnostic.Line + ", " + diagnostic.Message;
            }

            return "Needs review: " + (run.Message ?? "the script did not run.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sandbox verification failed for an assistant script.");
            return "Could not verify the script.";
        }
    }

    private List<CadAssistAttachmentDto> CleanAttachments(List<CadAssistAttachmentDto>? attachments)
    {
        var cleaned = new List<CadAssistAttachmentDto>();

        if (attachments is null)
        {
            return cleaned;
        }

        foreach (var attachment in attachments.Where(a => a is not null).Take(Math.Max(0, _options.MaxAttachments)))
        {
            var name = Path.GetFileName(attachment.FileName ?? string.Empty);

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var readable = attachment.SizeBytes <= _options.MaxAttachmentBytes
                && !string.IsNullOrWhiteSpace(attachment.TextContent);

            cleaned.Add(new CadAssistAttachmentDto
            {
                FileName = Limit(name, MaxFileNameChars) ?? string.Empty,
                ContentType = Limit(attachment.ContentType, 100) ?? string.Empty,
                SizeBytes = attachment.SizeBytes,
                TextContent = readable ? Limit(attachment.TextContent, _options.MaxAttachmentTextChars) : null
            });
        }

        return cleaned;
    }

    private static string BuildDraftQuery(CadAssistChatRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            parts.Add("Request: " + request.Prompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.DrawingType))
        {
            parts.Add("Drawing type: " + request.DrawingType.Trim());
        }

        foreach (var attachment in request.Attachments.Where(a => a is not null).Take(5))
        {
            var name = Path.GetFileName(attachment.FileName ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(name))
            {
                parts.Add("Attached file: " + name);
            }

            if (!string.IsNullOrWhiteSpace(attachment.TextContent))
            {
                var text = attachment.TextContent.Trim();
                parts.Add(text.Length > 600 ? text[..600] : text);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string? Limit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        return text.Length > max ? text[..max] : text;
    }
}