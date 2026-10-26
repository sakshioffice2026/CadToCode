// Repo path: Web/Controllers/Cadassistcontroller.cs
using System.Text;
using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.Core.Calculators;
using CadToCode.Core.Domain;
using CadToCode.Core.Knowledge;
using CadToCode.Core.Services;
using CadToCode.Core.Workflow;
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

public class DesignStartRequest
{
    public string? Equipment { get; set; }

    public string? Title { get; set; }
}

public class DesignRequirementsRequest
{
    public Guid SessionId { get; set; }

    public Dictionary<string, double>? Inputs { get; set; }
}

public class DesignIdRequest
{
    public Guid SessionId { get; set; }
}

public class DesignRejectRequest
{
    public Guid SessionId { get; set; }

    public string? Reason { get; set; }
}

public class DesignScriptRequest
{
    public Guid SessionId { get; set; }

    public double? BladeChordM { get; set; }

    public double? BladeThicknessM { get; set; }

    public double? BladePitchDeg { get; set; }
}

public class DesignExportRequest
{
    public Guid SessionId { get; set; }

    public string? Script { get; set; }
}

public class WebAdvisoryRequest
{
    public string? Query { get; set; }

    public string? Purpose { get; set; }

    public string? Equipment { get; set; }

    public int? MaxResults { get; set; }

    public List<string>? AllowedDomains { get; set; }
}

public class CadAssistController : Controller
{
    private const string OwnerCookieName = "cadtocode_owner";
    private const int MaxKnowledgeChars = 1200;
    private const int MaxQueryChars = 300;
    private const int MaxScriptChars = 200_000;

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
    private readonly IChatMemoryStore _memory;
    private readonly IProjectProcessingService _processingService;
    private readonly ICodeGenerationService _codeGenerationService;
    private readonly IStepScriptRunner _stepScriptRunner;
    private readonly ICadTemplateMatcher _templateMatcher;
    private readonly IDrawingProcessingService _drawingProcessing;
    private readonly ICadAssistConversationService _conversation;
    private readonly ICadAssistSessionStore _sessionStore;
    private readonly IDesignWorkflowEngine _workflow;
    private readonly IDesignSessionStore _designStore;
    private readonly IEngineeringKnowledgeService _knowledge;
    private readonly IWebSearchService _webSearch;
    private readonly LocalAiOptions _options;
    private readonly ILogger<CadAssistController> _logger;

    public CadAssistController(
        ILocalAiService ai,
        IChatMemoryStore memory,
        IProjectProcessingService processingService,
        ICodeGenerationService codeGenerationService,
        IStepScriptRunner stepScriptRunner,
        ICadTemplateMatcher templateMatcher,
        IDrawingProcessingService drawingProcessing,
        ICadAssistConversationService conversation,
        ICadAssistSessionStore sessionStore,
        IDesignWorkflowEngine workflow,
        IDesignSessionStore designStore,
        IEngineeringKnowledgeService knowledge,
        IWebSearchService webSearch,
        IOptions<LocalAiOptions> options,
        ILogger<CadAssistController> logger)
    {
        _workflow = workflow;
        _designStore = designStore;
        _knowledge = knowledge;
        _webSearch = webSearch;
        _ai = ai;
        _memory = memory;
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
    public async Task<IActionResult> History(int projectId, string? sessionId, CancellationToken cancellationToken)
    {
        if (projectId < 0)
        {
            return Json(new CadAssistHistoryResponse { SessionId = string.Empty });
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (projectId == 0)
            {
                return Json(new CadAssistHistoryResponse { SessionId = string.Empty });
            }

            sessionId = _ai.EnsureSession(projectId, null);
        }

        var messages = _ai.GetHistory(projectId, sessionId).ToList();
        string? editorCode = null;

        if (projectId > 0)
        {
            var json = await _processingService.LoadChatSnapshotAsync(projectId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var snapshot = ReadSnapshot(json);

                    if (snapshot is not null)
                    {
                        editorCode = snapshot.EditorCode;

                        if (messages.Count == 0 && snapshot.Messages.Count > 0)
                        {
                            for (var i = 0; i < snapshot.Messages.Count && i < snapshot.ModelContents.Count; i++)
                            {
                                snapshot.Messages[i].ModelContent = snapshot.ModelContents[i];
                            }

                            _memory.Restore(projectId, sessionId, snapshot.Messages);
                            messages = snapshot.Messages;
                        }
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex, "Saved chat for project {ProjectId} could not be read.", projectId);
                }
            }
        }

        return Json(new CadAssistHistoryResponse
        {
            SessionId = sessionId,
            Messages = messages,
            EditorCode = editorCode
        });
    }

    private static CadAssistSnapshotDto? ReadSnapshot(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var legacy = System.Text.Json.JsonSerializer.Deserialize<List<CadAssistMessageDto>>(json);

            return legacy is null ? null : new CadAssistSnapshotDto { Messages = legacy };
        }

        return System.Text.Json.JsonSerializer.Deserialize<CadAssistSnapshotDto>(json);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> SaveAsProject(
        List<IFormFile>? files,
        int projectId,
        string? sessionId,
        string? title,
        string? editorCode,
        CancellationToken cancellationToken)
    {
        if (projectId < 0 || string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { success = false, error = "There is no chat to save yet." });
        }

        var messages = _ai.GetHistory(projectId, sessionId).ToList();

        if (messages.Count == 0)
        {
            return BadRequest(new { success = false, error = "There is no chat to save yet." });
        }

        var snapshot = System.Text.Json.JsonSerializer.Serialize(new CadAssistSnapshotDto
        {
            Messages = messages,
            ModelContents = messages.Select(m => m.ModelContent).ToList(),
            EditorCode = editorCode
        });

        if (projectId > 0)
        {
            var updated = await _processingService.SaveChatSnapshotAsync(projectId, snapshot, cancellationToken);

            return updated
                ? Json(new { success = true, projectId, sessionId, created = false })
                : NotFound(new { success = false, error = "That project no longer exists." });
        }

        var file = (files ?? new List<IFormFile>()).FirstOrDefault(f => f is not null && f.Length > 0);

        if (file is null)
        {
            return BadRequest(new
            {
                success = false,
                error = "Attach a DXF, PNG or JPG drawing in this chat first. A project needs a drawing to be saved."
            });
        }

        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        if (!_drawingProcessing.IsSupported(fileName))
        {
            return BadRequest(new { success = false, error = fileName + ": unsupported file type. Use .dxf, .png, .jpg or .jpeg." });
        }

        if (file.Length > _options.MaxAttachmentBytes)
        {
            return BadRequest(new { success = false, error = fileName + ": the file is too large." });
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
                return BadRequest(new { success = false, error = fileName + ": the file content is not a real PNG or JPEG image." });
            }
        }

        try
        {
            await using var stream = file.OpenReadStream();

            var upload = await _processingService.ProcessUploadAsync(stream, fileName, CancellationToken.None);

            if (!upload.Success || !upload.ProjectId.HasValue)
            {
                return UnprocessableEntity(new
                {
                    success = false,
                    error = upload.ErrorMessage ?? "The drawing could not be saved as a project."
                });
            }

            var newId = upload.ProjectId.Value;

            var chosenTitle = Limit(title, MaxFieldChars);

            if (!string.IsNullOrWhiteSpace(chosenTitle))
            {
                await _processingService.RenameProjectAsync(newId, chosenTitle, CancellationToken.None);
            }

            await _processingService.SaveChatSnapshotAsync(newId, snapshot, CancellationToken.None);

            _memory.Rebind(projectId, newId, sessionId);

            return Json(new
            {
                success = true,
                projectId = newId,
                sessionId,
                created = true,
                title = Limit(title, MaxFieldChars)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the chat as a project failed.");
            return StatusCode(500, new { success = false, error = "The chat could not be saved as a project." });
        }
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

            var equipmentReply = IsEquipmentDesignReply(reply.Status);

            reply.Status = null;
            reply.Model = "template";

            if (_options.AutoVerifyCode)
            {
                reply.Status = await VerifyAsync(reply, cancellationToken);
            }

            reply.ElapsedMs = started.ElapsedMilliseconds;

            if (equipmentReply)
            {
                reply.Stage = _sessionStore.Get(request.ProjectId, sessionId).Stage.ToString();
            }
            else
            {
                Decorate(reply, request.ProjectId, sessionId);
            }

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

    [HttpGet]
    public IActionResult DesignEquipment()
    {
        var items = EquipmentRegistry.All.Select(m => new
        {
            code = m.Code,
            name = m.DisplayName,
            description = m.Description,
            calculatorStatus = m.CalculatorStatus.ToString(),
            canDriveCadGeneration = m.CanDriveCadGeneration,
            requiredInputs = m.Inputs.Where(i => i.IsRequired).Select(DescribeParameter),
            optionalInputs = m.Inputs.Where(i => !i.IsRequired).Select(DescribeParameter)
        });

        return Json(new { equipment = items });
    }

    [HttpGet]
    public async Task<IActionResult> DesignGet(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await LoadOwnedAsync(sessionId, cancellationToken);

        if (session is null)
        {
            return NotFound(new { success = false, error = "The design session was not found." });
        }

        return Json(new { success = true, design = BuildDesignSummary(session) });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignStart([FromBody] DesignStartRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { success = false, error = "The request was not valid." });
        }

        var type = EquipmentRegistry.Resolve(request.Equipment);

        if (type == EquipmentType.Unknown)
        {
            return BadRequest(new
            {
                success = false,
                clarificationNeeded = true,
                error = "The equipment type was not recognised. Choose one of: "
                    + string.Join(", ", EquipmentRegistry.All.Select(m => m.DisplayName)) + "."
            });
        }

        var metadata = EquipmentRegistry.Get(type);

        if (!metadata.CanDriveCadGeneration)
        {
            return UnprocessableEntity(new
            {
                success = false,
                equipment = metadata.Code,
                error = metadata.DisplayName + " does not have a deterministic calculator yet, so a design cannot be started."
            });
        }

        try
        {
            var session = await _workflow.StartAsync(GetOwnerId(), type, cancellationToken);
            var title = Limit(request.Title, MaxFieldChars);

            if (title is not null)
            {
                session.Rename(title);
                await _designStore.UpdateAsync(session, cancellationToken);
            }

            return Json(new { success = true, design = BuildDesignSummary(session) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignRequirements([FromBody] DesignRequirementsRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.Inputs is null || request.Inputs.Count == 0)
        {
            return BadRequest(new { success = false, error = "Provide the design inputs in SI units." });
        }

        var session = await LoadOwnedAsync(request.SessionId, cancellationToken);

        if (session is null)
        {
            return NotFound(new { success = false, error = "The design session was not found." });
        }

        if (!EquipmentRegistry.TryGet(session.EquipmentType, out var metadata))
        {
            return Conflict(new { success = false, error = "The design session has no equipment type." });
        }

        var supplied = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in request.Inputs)
        {
            var key = pair.Key?.Trim();

            if (string.IsNullOrEmpty(key))
            {
                return BadRequest(new { success = false, error = "An input key was empty." });
            }

            if (!double.IsFinite(pair.Value))
            {
                return BadRequest(new { success = false, error = "Input '" + key + "' must be a finite number." });
            }

            supplied[key] = pair.Value;
        }

        var unknown = supplied.Keys
            .Where(k => !metadata.Inputs.Any(i => string.Equals(i.Key, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unknown.Count > 0)
        {
            return BadRequest(new
            {
                success = false,
                error = "Unknown input for " + metadata.DisplayName + ": " + string.Join(", ", unknown) + "."
            });
        }

        var missing = metadata.Inputs
            .Where(i => i.IsRequired && !supplied.ContainsKey(i.Key))
            .Select(DescribeParameter)
            .ToList();

        if (missing.Count > 0)
        {
            return UnprocessableEntity(new
            {
                success = false,
                clarificationNeeded = true,
                missing,
                error = "Required inputs are missing: " + string.Join(", ", missing.Select(m => m.Key)) + "."
            });
        }

        try
        {
            var updated = await _workflow.SubmitRequirementsAsync(request.SessionId, supplied, cancellationToken);
            return Json(new { success = true, design = BuildDesignSummary(updated) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignValidate([FromBody] DesignIdRequest? request, CancellationToken cancellationToken)
    {
        return await RunTransitionAsync(request?.SessionId, (id, ct) => _workflow.ValidateFormulasAsync(id, ct), cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignApprove([FromBody] DesignIdRequest? request, CancellationToken cancellationToken)
    {
        return await RunTransitionAsync(request?.SessionId, (id, ct) => _workflow.ApproveDesignReviewAsync(id, ct), cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignReject([FromBody] DesignRejectRequest? request, CancellationToken cancellationToken)
    {
        var reason = Limit(request?.Reason, MaxFieldChars);

        if (reason is null)
        {
            return BadRequest(new { success = false, error = "A rejection reason is required." });
        }

        return await RunTransitionAsync(request?.SessionId, (id, ct) => _workflow.RejectDesignReviewAsync(id, reason, ct), cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignScript([FromBody] DesignScriptRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { success = false, error = "The request was not valid." });
        }

        var session = await LoadOwnedAsync(request.SessionId, cancellationToken);

        if (session is null)
        {
            return NotFound(new { success = false, error = "The design session was not found." });
        }

        if (session.EquipmentType != EquipmentType.AxialFan && session.EquipmentType != EquipmentType.CentrifugalFan)
        {
            return UnprocessableEntity(new
            {
                success = false,
                error = "CAD script generation is not available for " + session.EquipmentType + " yet."
            });
        }

        if (session.State != DesignWorkflowState.ScriptGeneration)
        {
            return Conflict(new
            {
                success = false,
                error = "The design is in '" + DesignWorkflowStates.DisplayName(session.State) + "'. Approve the design review first."
            });
        }

        var latest = session.LatestRevision;

        if (latest is null || !session.HasCadReadyCalculation || session.IsCalculationStale)
        {
            return Conflict(new { success = false, error = "There is no current, CAD-ready calculation to build from." });
        }

        if (session.EquipmentType == EquipmentType.CentrifugalFan)
        {
            return await ComposeCentrifugalFanScriptAsync(request, latest.Calculation.Values, cancellationToken);
        }

        var missingInputs = new List<string>();

        if (request.BladeChordM is null) missingInputs.Add(AxialFanScriptComposer.BladeChordM);
        if (request.BladeThicknessM is null) missingInputs.Add(AxialFanScriptComposer.BladeThicknessM);
        if (request.BladePitchDeg is null) missingInputs.Add(AxialFanScriptComposer.BladePitchDeg);

        if (missingInputs.Count > 0)
        {
            return UnprocessableEntity(new
            {
                success = false,
                clarificationNeeded = true,
                missing = missingInputs,
                error = "Blade geometry is not produced by the calculator and must be provided: " + string.Join(", ", missingInputs) + "."
            });
        }

        var calculated = latest.Calculation.Values;
        var composerInputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var missingCalculated = new List<string>();

        var mapping = new (string ComposerKey, string CalculatedKey)[]
        {
            (AxialFanScriptComposer.TipDiameterM, "tip_diameter_m"),
            (AxialFanScriptComposer.HubDiameterM, "hub_diameter_m"),
            (AxialFanScriptComposer.TipClearanceM, "tip_clearance_m"),
            (AxialFanScriptComposer.CasingWallM, "casing_wall_thickness_m"),
            (AxialFanScriptComposer.CasingLengthM, "casing_length_m"),
            (AxialFanScriptComposer.BladeCount, "blade_count")
        };

        foreach (var (composerKey, calculatedKey) in mapping)
        {
            if (calculated.TryGetValue(calculatedKey, out var value))
            {
                composerInputs[composerKey] = value;
            }
            else
            {
                missingCalculated.Add(calculatedKey);
            }
        }

        if (missingCalculated.Count > 0)
        {
            return Conflict(new
            {
                success = false,
                error = "The calculation is missing: " + string.Join(", ", missingCalculated) + "."
            });
        }

        composerInputs[AxialFanScriptComposer.BladeChordM] = request.BladeChordM!.Value;
        composerInputs[AxialFanScriptComposer.BladeThicknessM] = request.BladeThicknessM!.Value;
        composerInputs[AxialFanScriptComposer.BladePitchDeg] = request.BladePitchDeg!.Value;

        var composed = AxialFanScriptComposer.Compose(composerInputs);

        if (!composed.Success)
        {
            return UnprocessableEntity(new
            {
                success = false,
                error = "The script could not be generated: " + string.Join(" ", composed.Errors),
                errors = composed.Errors
            });
        }

        try
        {
            var updated = await _workflow.MarkScriptGeneratedAsync(request.SessionId, cancellationToken);

            return Json(new
            {
                success = true,
                design = BuildDesignSummary(updated),
                script = composed.Script,
                fileBaseName = composed.FileBaseName,
                appliedValues = composed.AppliedValues
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    private async Task<IActionResult> ComposeCentrifugalFanScriptAsync(
        DesignScriptRequest request,
        IReadOnlyDictionary<string, double> calculated,
        CancellationToken cancellationToken)
    {
        var composerInputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var missingCalculated = new List<string>();

        var mapping = new (string ComposerKey, string CalculatedKey)[]
        {
            (CentrifugalFanScriptComposer.ImpellerOuterDiameterM, "impeller_outer_diameter_m"),
            (CentrifugalFanScriptComposer.ImpellerInletDiameterM, "impeller_inlet_diameter_m"),
            (CentrifugalFanScriptComposer.BladeWidthM, "impeller_width_m"),
            (CentrifugalFanScriptComposer.BladeInletWidthM, "blade_inlet_width_m"),
            (CentrifugalFanScriptComposer.HubDiameterM, "hub_diameter_m"),
            (CentrifugalFanScriptComposer.BladeCount, "blade_count"),
            (CentrifugalFanScriptComposer.BladeInletAngleDeg, "blade_inlet_angle_deg"),
            (CentrifugalFanScriptComposer.BladeOutletAngleDeg, "blade_outlet_angle_deg"),
            (CentrifugalFanScriptComposer.BladeThicknessM, "blade_thickness_m"),
            (CentrifugalFanScriptComposer.BladeProfileCode, "blade_profile_code"),
            (CentrifugalFanScriptComposer.PlateThicknessM, "plate_thickness_m"),
            (CentrifugalFanScriptComposer.CasingBaseDiameterM, "casing_base_diameter_m"),
            (CentrifugalFanScriptComposer.CasingWidthM, "casing_width_m"),
            (CentrifugalFanScriptComposer.CasingWallM, "casing_wall_thickness_m"),
            (CentrifugalFanScriptComposer.OutletHeightM, "outlet_height_m"),
            (CentrifugalFanScriptComposer.CasingInletDiameterM, "inlet_eye_diameter_m")
        };

        foreach (var (composerKey, calculatedKey) in mapping)
        {
            if (calculated.TryGetValue(calculatedKey, out var value))
            {
                composerInputs[composerKey] = value;
            }
            else
            {
                missingCalculated.Add(calculatedKey);
            }
        }

        if (missingCalculated.Count > 0)
        {
            return Conflict(new
            {
                success = false,
                error = "The calculation is missing: " + string.Join(", ", missingCalculated) + "."
            });
        }

        if (request.BladeThicknessM is not null)
        {
            composerInputs[CentrifugalFanScriptComposer.BladeThicknessM] = request.BladeThicknessM.Value;
        }

        var composed = CentrifugalFanScriptComposer.Compose(composerInputs);

        if (!composed.Success)
        {
            return UnprocessableEntity(new
            {
                success = false,
                error = "The script could not be generated: " + string.Join(" ", composed.Errors),
                errors = composed.Errors
            });
        }

        try
        {
            var updated = await _workflow.MarkScriptGeneratedAsync(request.SessionId, cancellationToken);

            return Json(new
            {
                success = true,
                design = BuildDesignSummary(updated),
                script = composed.Script,
                fileBaseName = composed.FileBaseName,
                appliedValues = composed.AppliedValues
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignExport([FromBody] DesignExportRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Script))
        {
            return BadRequest(new { success = false, error = "The generated script is required." });
        }

        if (request.Script.Length > MaxScriptChars)
        {
            return BadRequest(new { success = false, error = "The script is too large." });
        }

        var session = await LoadOwnedAsync(request.SessionId, cancellationToken);

        if (session is null)
        {
            return NotFound(new { success = false, error = "The design session was not found." });
        }

        if (session.State != DesignWorkflowState.StepExport)
        {
            return Conflict(new
            {
                success = false,
                error = "The design is in '" + DesignWorkflowStates.DisplayName(session.State) + "'. Generate the script first."
            });
        }

        try
        {
            var run = await _stepScriptRunner.RunAsync(request.Script, cancellationToken);

            if (!run.Success || string.IsNullOrWhiteSpace(run.StepContent))
            {
                return UnprocessableEntity(new
                {
                    success = false,
                    error = run.Message ?? "The script did not produce a STEP file.",
                    diagnostics = run.Diagnostics
                        .Take(10)
                        .Select(d => new { line = d.Line, column = d.Column, message = d.Message })
                });
            }

            var updated = await _workflow.MarkStepExportedAsync(request.SessionId, cancellationToken);

            return Json(new
            {
                success = true,
                design = BuildDesignSummary(updated),
                fileName = SafeStepFileName(run.FileName, updated.EquipmentType),
                stepContent = run.StepContent,
                solidCount = run.SolidCount,
                faceCount = run.FaceCount,
                elapsedMs = run.ElapsedMs
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    [HttpGet]
    public async Task<IActionResult> DesignKnowledge(string? query, string? equipment, int topK = 5, CancellationToken cancellationToken = default)
    {
        var text = Limit(query, MaxQueryChars);

        if (text is null)
        {
            return BadRequest(new { success = false, error = "A search query is required." });
        }

        EquipmentType? type = null;

        if (!string.IsNullOrWhiteSpace(equipment))
        {
            var resolved = EquipmentRegistry.Resolve(equipment);

            if (resolved == EquipmentType.Unknown)
            {
                return BadRequest(new { success = false, error = "The equipment type was not recognised." });
            }

            type = resolved;
        }

        try
        {
            var results = await _knowledge.SearchAsync(text, type, Math.Clamp(topK, 1, 10), cancellationToken);

            return Json(new
            {
                success = true,
                notice = "Reference material only. Numeric design values come from the deterministic calculators.",
                results = results.Select(r => new
                {
                    id = r.Chunk.Id,
                    equipment = r.Chunk.EquipmentType.ToString(),
                    source = r.Chunk.Source,
                    title = r.Chunk.Title,
                    content = Limit(r.Chunk.Content, MaxKnowledgeChars),
                    tags = r.Chunk.Tags,
                    score = r.Score
                })
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Engineering knowledge search failed.");
            return StatusCode(500, new { success = false, error = "The engineering knowledge search failed." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRequestBytes)]
    public async Task<IActionResult> DesignWebAdvisory([FromBody] WebAdvisoryRequest? request, CancellationToken cancellationToken)
    {
        var text = Limit(request?.Query, MaxQueryChars);

        if (request is null || text is null)
        {
            return BadRequest(new { success = false, error = "A search query is required." });
        }

        var purpose = WebSearchPurpose.GeneralReference;

        if (!string.IsNullOrWhiteSpace(request.Purpose)
            && !Enum.TryParse(request.Purpose.Trim(), true, out purpose))
        {
            return BadRequest(new
            {
                success = false,
                error = "The search purpose must be one of: " + string.Join(", ", Enum.GetNames<WebSearchPurpose>()) + "."
            });
        }

        var equipmentType = EquipmentType.Unknown;

        if (!string.IsNullOrWhiteSpace(request.Equipment))
        {
            equipmentType = EquipmentRegistry.Resolve(request.Equipment);

            if (equipmentType == EquipmentType.Unknown)
            {
                return BadRequest(new { success = false, error = "The equipment type was not recognised." });
            }
        }

        if (!_webSearch.IsAvailable)
        {
            return StatusCode(503, new
            {
                success = false,
                advisoryOnly = true,
                provider = _webSearch.ProviderName,
                error = "Web search is not configured. Local engineering knowledge is still available."
            });
        }

        WebSearchRequest searchRequest;

        try
        {
            searchRequest = new WebSearchRequest(
                text,
                purpose,
                equipmentType,
                Math.Clamp(request.MaxResults ?? WebSearchRequest.DefaultMaxResults, 1, WebSearchRequest.MaxAllowedResults),
                WebSearchRequest.DefaultMaxSnippetCharacters,
                request.AllowedDomains?.Take(10));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }

        var result = await _webSearch.SearchAsync(searchRequest, cancellationToken);

        if (!result.Succeeded)
        {
            return StatusCode(502, new
            {
                success = false,
                advisoryOnly = true,
                provider = result.Provider,
                error = result.Error ?? "The web search failed."
            });
        }

        return Json(new
        {
            success = true,
            advisoryOnly = true,
            notice = WebSearchResult.AdvisoryNotice,
            provider = result.Provider,
            retrievedAtUtc = result.RetrievedAtUtc,
            snippets = result.Snippets.Select(s => new
            {
                title = s.Title,
                url = s.Url,
                domain = s.Domain,
                snippet = s.Snippet,
                publishedUtc = s.PublishedUtc,
                relevance = s.Relevance
            })
        });
    }

    private async Task<IActionResult> RunTransitionAsync(
        Guid? sessionId,
        Func<Guid, CancellationToken, Task<DesignSession>> transition,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            return BadRequest(new { success = false, error = "The request was not valid." });
        }

        var session = await LoadOwnedAsync(sessionId.Value, cancellationToken);

        if (session is null)
        {
            return NotFound(new { success = false, error = "The design session was not found." });
        }

        try
        {
            var updated = await transition(sessionId.Value, cancellationToken);
            return Json(new { success = true, design = BuildDesignSummary(updated) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignError(ex);
        }
    }

    private IActionResult DesignError(Exception ex)
    {
        switch (ex)
        {
            case KeyNotFoundException:
                return NotFound(new { success = false, error = ex.Message });

            case NotSupportedException:
                return UnprocessableEntity(new { success = false, error = ex.Message });

            case ArgumentException:
                return BadRequest(new { success = false, error = ex.Message });

            case InvalidOperationException:
                return Conflict(new { success = false, error = ex.Message });

            default:
                _logger.LogError(ex, "A design workflow request failed.");
                return StatusCode(500, new { success = false, error = "The design request could not be completed." });
        }
    }

    private string GetOwnerId()
    {
        var name = User.Identity?.Name;

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (Request.Cookies.TryGetValue(OwnerCookieName, out var existing) && Guid.TryParse(existing, out var parsed))
        {
            return parsed.ToString("N");
        }

        var created = Guid.NewGuid().ToString("N");

        Response.Cookies.Append(OwnerCookieName, created, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });

        return created;
    }

    private async Task<DesignSession?> LoadOwnedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
        {
            return null;
        }

        var session = await _designStore.GetAsync(sessionId, cancellationToken);

        return session is not null && string.Equals(session.OwnerId, GetOwnerId(), StringComparison.Ordinal)
            ? session
            : null;
    }

    private sealed record ParameterInfo(string Key, string Name, string Unit, string Description);

    private static ParameterInfo DescribeParameter(ParameterDescriptor p)
    {
        return new ParameterInfo(p.Key, p.DisplayName, p.SiUnit, p.Description);
    }

    private static string SafeStepFileName(string? name, EquipmentType equipment)
    {
        var clean = Path.GetFileName(name ?? string.Empty);

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(invalid.ToString(), string.Empty);
        }

        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = equipment == EquipmentType.CentrifugalFan ? "CentrifugalFan" : "AxialFan";
        }

        if (clean.Length > MaxFileNameChars)
        {
            clean = clean[..MaxFileNameChars];
        }

        var extension = Path.GetExtension(clean);

        return extension.Equals(".step", StringComparison.OrdinalIgnoreCase) || extension.Equals(".stp", StringComparison.OrdinalIgnoreCase)
            ? clean
            : clean + ".step";
    }

    private static object BuildDesignSummary(DesignSession session)
    {
        EquipmentMetadata? metadata = EquipmentRegistry.TryGet(session.EquipmentType, out var found) ? found : null;
        var latest = session.LatestRevision;

        var missing = metadata is null
            ? new List<string>()
            : metadata.Inputs.Where(i => i.IsRequired && !session.Inputs.ContainsKey(i.Key)).Select(i => i.Key).ToList();

        return new
        {
            sessionId = session.Id,
            title = session.Title,
            equipment = metadata?.Code,
            equipmentName = metadata?.DisplayName,
            state = session.State.ToString(),
            stateName = DesignWorkflowStates.DisplayName(session.State),
            stateDescription = DesignWorkflowStates.Description(session.State),
            allowedNext = DesignWorkflowStates.AllowedNext(session.State).Select(s => s.ToString()),
            inputs = session.Inputs,
            missingRequiredInputs = missing,
            isCalculationStale = session.IsCalculationStale,
            hasCadReadyCalculation = session.HasCadReadyCalculation,
            revisionCount = session.Revisions.Count,
            approvals = session.Approvals.Select(a => new
            {
                stage = a.State.ToString(),
                revision = a.RevisionNumber,
                approvedUtc = a.ApprovedUtc,
                approvedBy = a.ApprovedBy
            }),
            latest = latest is null
                ? null
                : new
                {
                    revision = latest.Number,
                    changeNote = latest.ChangeNote,
                    calculatedAtUtc = latest.Calculation.CalculatedAtUtc,
                    calculatorVersion = latest.Calculation.CalculatorVersion,
                    knowledgeVersion = latest.Calculation.KnowledgeVersion,
                    isCadReady = latest.Calculation.IsCadReady,
                    values = latest.Calculation.Values,
                    formulaIds = latest.Calculation.FormulaIdsUsed,
                    warnings = latest.Calculation.Warnings.Select(w => new
                    {
                        code = w.Code,
                        message = w.Message,
                        severity = w.Severity.ToString(),
                        key = w.RelatedKey,
                        formulaId = w.FormulaId
                    }),
                    errors = latest.Calculation.Errors
                }
        };
    }

    private bool ShouldUseConversation(int projectId, string sessionId, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var state = _sessionStore.Get(projectId, sessionId);
        var designActive = _conversation.IsDesignActive(projectId, sessionId);

        if (!state.HasTemplate && state.Stage != CadAssistStage.Uploaded && !designActive)
        {
            return !CadAssistPromptTemplates.IsQuestionOnly(prompt)
                   && _conversation.CanRouteToDesign(projectId, sessionId, prompt);
        }

        if (CadAssistIntentParser.Parse(prompt, null).ShowParameters)
        {
            return true;
        }

        return !CadAssistPromptTemplates.IsQuestionOnly(prompt);
    }

    private static bool IsEquipmentDesignReply(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.StartsWith("design-", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "equipment-parts", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "equipment-identified", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "equipment-ambiguous", StringComparison.OrdinalIgnoreCase);
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
        var blocks = message.CodeBlocks.Where(b => b.Language == "csharp").ToList();

        if (blocks.Count == 0)
        {
            return null;
        }

        if (blocks.Count == 1)
        {
            return (await VerifyBlockAsync(blocks[0], cancellationToken)).Text;
        }

        var failures = new List<string>();
        var passed = 0;
        long solids = 0;
        long faces = 0;
        long elapsed = 0;

        // Scripts are verified in parallel. The sandbox runner limits how many run at once, so this only removes idle waiting.
        var results = await Task.WhenAll(blocks.Select(item => VerifyBlockAsync(item, cancellationToken)));

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var result = results[index];

            if (result.Verified)
            {
                passed++;
                solids += result.Solids;
                faces += result.Faces;
                elapsed += result.ElapsedMs;
            }
            else
            {
                failures.Add(block.Title + ": " + result.Text.Replace("Needs review: ", string.Empty));
            }
        }

        if (failures.Count == 0)
        {
            return "Verified in sandbox: " + passed + " of " + blocks.Count + " scripts, "
                   + solids + " solids, " + faces + " faces, " + elapsed + " ms";
        }

        return "Needs review: " + passed + " of " + blocks.Count + " scripts verified. "
               + string.Join(" | ", failures);
    }

    private readonly record struct BlockVerification(bool Verified, string Text, long Solids, long Faces, long ElapsedMs);

    private async Task<BlockVerification> VerifyBlockAsync(CadAssistCodeBlockDto block, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _stepScriptRunner.RunAsync(block.Code, cancellationToken);

            if (run.Success)
            {
                return new BlockVerification(
                    true,
                    "Verified in sandbox: " + run.SolidCount + " solids, " + run.FaceCount + " faces, " + run.ElapsedMs + " ms",
                    run.SolidCount,
                    run.FaceCount,
                    run.ElapsedMs);
            }

            var diagnostic = run.Diagnostics.FirstOrDefault();

            if (diagnostic is not null)
            {
                return new BlockVerification(false, "Needs review: line " + diagnostic.Line + ", " + diagnostic.Message, 0, 0, 0);
            }

            return new BlockVerification(false, "Needs review: " + (run.Message ?? "the script did not run."), 0, 0, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sandbox verification failed for an assistant script.");
            return new BlockVerification(false, "Could not verify the script.", 0, 0, 0);
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