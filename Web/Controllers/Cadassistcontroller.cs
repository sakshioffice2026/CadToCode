using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
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
    private const int MaxCatalogChars = 1200;
    private const int MaxVisionChars = 1200;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const int MinGeometryChars = 1200;
    private const int MaxMatchTextChars = 2000;
    private const long MaxUploadBytes = 52_428_800;
    private const int MaxUploadFiles = 5;

    private readonly ILocalAiService _ai;
    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;
    private readonly IStepScriptRunner _stepScriptRunner;
    private readonly ICadTemplateMatcher _templateMatcher;
    private readonly IDrawingProcessingService _drawingProcessing;
    private readonly ICadAssistConversationService _conversation;
    private readonly ICadAssistSessionStore _sessionStore;
    private readonly LocalAiOptions _options;
    private readonly ILogger<CadAssistController> _logger;

    public CadAssistController(
        ILocalAiService ai,
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService,
        IStepScriptRunner stepScriptRunner,
        ICadTemplateMatcher templateMatcher,
        IDrawingProcessingService drawingProcessing,
        ICadAssistConversationService conversation,
        ICadAssistSessionStore sessionStore,
        IOptions<LocalAiOptions> options,
        ILogger<CadAssistController> logger)
    {
        _ai = ai;
        _processingService = processingService;
        _codeGenerationService = codeGenerationService;
        _stepScriptRunner = stepScriptRunner;
        _templateMatcher = templateMatcher;
        _drawingProcessing = drawingProcessing;
        _conversation = conversation;
        _sessionStore = sessionStore;
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
            _sessionStore.Reset(request.ProjectId, request.SessionId);
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

        if (ShouldUseConversation(request.ProjectId, sessionId, request.Prompt))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            var reply = await _conversation.HandleMessageAsync(
                request.ProjectId,
                sessionId,
                request.Prompt,
                cancellationToken);

            reply.Status = null;
            reply.Model = "template";

            if (_options.AutoVerifyCode)
            {
                reply.Status = await VerifyAsync(reply, cancellationToken);
            }

            reply.ElapsedMs = started.ElapsedMilliseconds;
            Decorate(reply, request.ProjectId, sessionId);

            return Json(new CadAssistChatResponse
            {
                Success = true,
                SessionId = sessionId,
                Message = reply,
                Model = "template",
                Stage = reply.Stage,
                ElapsedMs = reply.ElapsedMs
            });
        }

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

            if (questionOnly)
            {
                var catalogMatch = await _templateMatcher.MatchAsync(
                    project, tree, request.Prompt, request.DrawingType, attachments, cancellationToken);

                var catalog = GeometryContextBuilder.BuildParameterCatalogSection(catalogMatch, MaxCatalogChars);

                if (catalog.Length > 0)
                {
                    geometryContext = geometryContext + Environment.NewLine + Environment.NewLine + catalog;
                }
            }
        }
        else if (questionOnly)
        {
            var catalogMatch = await _templateMatcher.MatchAsync(
                null, null, request.Prompt, request.DrawingType, attachments, cancellationToken);

            var catalog = GeometryContextBuilder.BuildParameterCatalogSection(catalogMatch, MaxCatalogChars);

            if (catalog.Length > 0)
            {
                geometryContext = geometryContext + Environment.NewLine + Environment.NewLine + catalog;
            }
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

        var visionSection = GeometryContextBuilder.BuildVisionSection(
            _sessionStore.Get(request.ProjectId, sessionId), MaxVisionChars);

        if (visionSection.Length > 0)
        {
            geometryContext = geometryContext + Environment.NewLine + Environment.NewLine + visionSection;
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

        var perFileChars = Math.Max(MinGeometryChars, _options.MaxGeometryChars) / selected.Count;

        var attachments = new List<CadAssistAttachmentDto>();
        var warnings = new List<string>();
        var summaries = new List<object>();
        var failures = new List<string>();
        DrawingProcessingResult? primary = null;
        string? primaryName = null;

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

            var extension = Path.GetExtension(fileName).ToLowerInvariant();

            if (extension is ".png" or ".jpg" or ".jpeg")
            {
                var header = new byte[8];
                int headerRead;

                await using (var headerStream = file.OpenReadStream())
                {
                    headerRead = await headerStream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
                }

                if (!HasImageSignature(header.AsSpan(0, headerRead)))
                {
                    failures.Add(fileName + ": the file content is not a real PNG or JPEG image.");
                    continue;
                }
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

                if (primary is null)
                {
                    primary = processed;
                    primaryName = fileName;
                }

                attachments.Add(new CadAssistAttachmentDto
                {
                    FileName = Limit(fileName, MaxFileNameChars) ?? string.Empty,
                    ContentType = Limit(file.ContentType, 100) ?? string.Empty,
                    SizeBytes = file.Length,
                    TextContent = Limit(processed.ContextText, MaxMatchTextChars)
                });

                summaries.Add(new
                {
                    fileName,
                    source = processed.SourceKind.ToString(),
                    mode = processed.Mode.ToString(),
                    template = processed.TemplateTitle,
                    templateScore = processed.TemplateScore
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

        if (primary is null || primaryName is null)
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

        if (attachments.Count > 1)
        {
            warnings.Add("Only the first usable drawing (" + primaryName + ") is analysed per conversation.");
        }

        ProjectResultDto? project = null;
        AssemblyTree? tree = null;

        if (projectId > 0)
        {
            project = await _processingService.GetProjectAsync(projectId, cancellationToken);

            if (project is not null && project.Status == ProjectStatus.Processed)
            {
                var assembly = await _codeGenerationService.GenerateAssemblyAsync(project.Id, false, cancellationToken);
                tree = assembly.Tree;
            }
        }

        var isImage = primary.SourceKind == DrawingSourceKind.Image;

        IReadOnlyDictionary<string, double>? dxfValues = null;

        if (primary.SourceKind == DrawingSourceKind.Dxf && primary.Injections.Count > 0)
        {
            dxfValues = primary.Injections
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().NewValue, StringComparer.OrdinalIgnoreCase);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();

        var message = await _conversation.HandleUploadAsync(
            new CadAssistUploadInput
            {
                ProjectId = projectId,
                SessionId = session,
                FileName = primaryName,
                IsImage = isImage,
                Prompt = prompt,
                DrawingType = Limit(drawingType, MaxFieldChars),
                Project = project,
                Tree = tree,
                Attachments = attachments,
                DxfVerifiedValues = dxfValues,
                Vision = isImage ? primary.Vision : null,
                OcrText = isImage ? primary.OcrText : null
            },
            cancellationToken);

        message.Status = null;
        message.Model = "template";
        message.ElapsedMs = started.ElapsedMilliseconds;
        Decorate(message, projectId, session);

        var unverified = isImage && primary.Vision is not null;

        return Json(new
        {
            success = true,
            sessionId = session,
            message,
            model = "template",
            stage = message.Stage,
            elapsedMs = started.ElapsedMilliseconds,
            warnings,
            unverified,
            processing = summaries
        });
    }

    private bool ShouldUseConversation(int projectId, string sessionId, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var state = _sessionStore.Get(projectId, sessionId);

        if (!state.HasTemplate && state.Stage != CadAssistStage.Uploaded)
        {
            return false;
        }

        if (CadAssistIntentParser.Parse(prompt, null).ShowParameters)
        {
            return true;
        }

        return !CadAssistPromptTemplates.IsQuestionOnly(prompt);
    }

    private void Decorate(CadAssistMessageDto message, int projectId, string sessionId)
    {
        var state = _sessionStore.Get(projectId, sessionId);

        message.Stage = state.Stage.ToString();

        message.Views = state.VisionViews
            .Select(v => new CadAssistViewDto { Name = v.Name, Description = v.Description })
            .ToList();

        message.Uncertainties = state.VisionUncertainties.ToList();

        var schema = state.Schema;

        if (!state.HasTemplate || schema is null)
        {
            return;
        }

        message.TemplateTitle = state.TemplateTitle;

        message.Breakdown = state.DetectedComponents
            .Select(c => new CadAssistComponentDto
            {
                Label = c.Label,
                MappedComponent = schema.FindComponent(c.MappedComponentKey ?? string.Empty)?.Title
            })
            .ToList();

        message.Parameters = schema.Parameters
            .Select(p =>
            {
                var has = state.Values.TryGetValue(p.Key, out var stored);

                return new CadAssistParameterDto
                {
                    Key = p.Key,
                    Label = p.Label,
                    Unit = p.Unit,
                    Default = p.Default,
                    Min = p.Min,
                    Max = p.Max,
                    Value = has ? stored!.Value : null,
                    Source = has ? stored!.Source.ToString() : "Default",
                    Evidence = state.Evidence.TryGetValue(p.Key, out var evidence) ? evidence.Text : null,
                    Verified = state.IsVerified(p.Key)
                };
            })
            .ToList();
    }

    private static bool HasImageSignature(ReadOnlySpan<byte> header)
    {
        if (header.Length >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return true;
        }

        return header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
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