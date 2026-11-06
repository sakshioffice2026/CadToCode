using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services;

/// <summary>
/// Detects "generate GA drawing" style requests. Manufacturing requests ("manufacturing drawings for all parts")
/// are left to CadAssistManufacturingIntentResolver, which expects an uploaded GA drawing.
/// </summary>
public static class CadAssistGaDrawingIntentResolver
{
    private static readonly Regex GaRequest = new(
        @"\b(generate|create|make|build|produce|prepare|give|get|export|draw|need|want)\b.{0,40}\b(ga|general\s+arrangement)\b.{0,20}\b(drawings?|drgs?|dxf)\b"
        + @"|^\s*(ga|general\s+arrangement)\s+(drawings?|drgs?|dxf)\s*[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ManufacturingWords = new(
        @"\b(manufacturing|fabrication|shop|workshop|production|detail|detailed|per[\s-]?part)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    public static bool IsGaDrawingRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            return GaRequest.IsMatch(text) && !ManufacturingWords.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>Holds generated GA DXF files in memory until the user downloads them.</summary>
public interface ICadAssistGaDrawingStore
{
    Guid Save(string fileName, byte[] content);
    bool TryGet(Guid id, out string fileName, out byte[] content);
}

public sealed class CadAssistGaDrawingStore : ICadAssistGaDrawingStore
{
    private readonly ConcurrentDictionary<Guid, (string FileName, byte[] Content, DateTime Utc)> _items = new();

    public Guid Save(string fileName, byte[] content)
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);

        foreach (var pair in _items.Where(p => p.Value.Utc < cutoff).ToList())
            _items.TryRemove(pair.Key, out _);

        var id = Guid.NewGuid();
        _items[id] = (fileName, content, DateTime.UtcNow);
        return id;
    }

    public bool TryGet(Guid id, out string fileName, out byte[] content)
    {
        if (_items.TryGetValue(id, out var item))
        {
            fileName = item.FileName;
            content = item.Content;
            return true;
        }

        fileName = string.Empty;
        content = Array.Empty<byte>();
        return false;
    }
}

public sealed class CadAssistGaDrawingHandler
{
    private readonly ICadAssistGaDrawingStore _store;

    public CadAssistGaDrawingHandler(ICadAssistGaDrawingStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Builds the reply for a GA drawing request. calculatedValues are the validated SI design values of the
    /// active centrifugal fan (null or empty when no design has been calculated yet).
    /// downloadUrlFactory turns the stored file id into the download URL of the hosting controller action.
    /// </summary>
    public CadAssistMessageDto Handle(
        IReadOnlyDictionary<string, double>? calculatedValues,
        Func<Guid, string> downloadUrlFactory)
    {
        if (calculatedValues is null || calculatedValues.Count == 0)
        {
            return Reply(
                "To generate a GA drawing I need a calculated fan design. Design the centrifugal fan first " +
                "(airflow, pressure and speed), or say `use defaults`, then send the request again.",
                "design-need-inputs");
        }

        var built = CentrifugalFanGaDrawingBuilder.Build(calculatedValues, "Centrifugal fan");

        if (!built.Success)
        {
            return Reply(
                "The GA drawing could not be built: " + string.Join(" ", built.Errors),
                "design-need-inputs");
        }

        var id = _store.Save(built.FileName, built.Content);
        var url = downloadUrlFactory(id);

        var text = new StringBuilder();
        text.AppendLine("Generated the GA drawing for the centrifugal fan:");
        text.AppendLine();
        text.AppendLine("- File: " + built.FileName + " (use the Download GA DXF button below)");
        text.AppendLine("- Views: front view and Section A-A");
        text.AppendLine("- Layers: " + string.Join(", ", built.Layers));
        text.AppendLine();
        text.Append("Attach this DXF and ask for `generate manufacturing drawings for all parts of the attached GA DXF` to get one drawing per part.");

        return Reply(text.ToString(), "design-generated");
    }

    private static CadAssistMessageDto Reply(string content, string status) => new()
    {
        Role = CadAssistRole.Assistant,
        Content = content,
        Status = status
    };
}

// ------------------------------------------------------------------------------------------------
// Wiring (apply in the repo yourself; nothing here changes the repo):
//
// 1. Program.cs
//      builder.Services.AddSingleton<ICadAssistGaDrawingStore, CadAssistGaDrawingStore>();
//      builder.Services.AddScoped<CadAssistGaDrawingHandler>();
//
// 2. Cadassistcontroller.Chat(), directly after the IsManufacturingRequest block:
//      if (CadAssistGaDrawingIntentResolver.IsGaDrawingRequest(request.Prompt))
//      {
//          var values = _conversation.GetCalculatedValues(request.ProjectId, sessionId); // validated fan values
//          var message = _gaDrawing.Handle(values, id => Url.Action("GaDrawingDownload", "CadAssist", new { id })!);
//          message.Model = "template";
//          return Json(new CadAssistChatResponse { Success = true, SessionId = sessionId, Message = message, Model = "template" });
//      }
//
// 3. Cadassistcontroller, new download action:
//      [HttpGet]
//      public IActionResult GaDrawingDownload(Guid id, [FromServices] ICadAssistGaDrawingStore store)
//          => store.TryGet(id, out var name, out var bytes)
//              ? File(bytes, "application/dxf", name)
//              : NotFound();
//
// 4. ICadAssistConversationService / CadAssistConversationService, new method returning the active
//    fan's values only when state.CalculationValidated is true:
//      public IReadOnlyDictionary<string, double>? GetCalculatedValues(int projectId, string sessionId)
//      {
//          var s = _equipment.Get(projectId, sessionId).Active;
//          return s is { CalculationValidated: true } ? s.Values : null;
//      }
//    The values must be the mapped script values (the same dictionary passed to
//    CentrifugalFanScriptComposer.Compose), not the raw calculator keys.
// ------------------------------------------------------------------------------------------------