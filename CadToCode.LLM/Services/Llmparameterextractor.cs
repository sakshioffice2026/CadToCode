using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Templates;
using CadToCode.LLM.Validation;
using CadToCode.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace CadToCode.LLM.Services;

public sealed class LlmExtractionResult
{
    public bool Success { get; set; }
    public Dictionary<string, double> Updates { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ComponentKeys { get; } = new();
    public bool FullAssembly { get; set; }
    public List<string> Rejected { get; } = new();

    public bool HasAnything => Updates.Count > 0 || ComponentKeys.Count > 0 || FullAssembly;
}

public interface ILlmParameterExtractor
{
    Task<LlmExtractionResult> ExtractAsync(
        string text,
        TemplateParameterSchema schema,
        CancellationToken cancellationToken = default);
}

public sealed class LlmParameterExtractor : ILlmParameterExtractor
{
    private const int MaxInputChars = 600;
    private const int TimeoutSeconds = 25;
    private const int ScratchProjectId = 0;

    private static readonly Regex NumberRegex = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    private readonly ILocalAiService _ai;
    private readonly ILogger<LlmParameterExtractor> _logger;

    public LlmParameterExtractor(ILocalAiService ai, ILogger<LlmParameterExtractor> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public async Task<LlmExtractionResult> ExtractAsync(
        string text,
        TemplateParameterSchema schema,
        CancellationToken cancellationToken = default)
    {
        var result = new LlmExtractionResult();

        if (string.IsNullOrWhiteSpace(text) || !_ai.IsAvailable)
        {
            return result;
        }

        var input = text.Trim();

        if (input.Length > MaxInputChars)
        {
            input = input[..MaxInputChars];
        }

        var scratchSession = _ai.EnsureSession(ScratchProjectId, null);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var response = await _ai.ChatAsync(
                new LocalAiGenerationRequest
                {
                    ProjectId = ScratchProjectId,
                    SessionId = scratchSession,
                    Prompt = BuildPrompt(input, schema),
                    GeometryContext = string.Empty
                },
                timeout.Token);

            if (!response.Success)
            {
                return result;
            }

            var raw = !string.IsNullOrWhiteSpace(response.Message?.Content)
                ? response.Message!.Content
                : response.Reply;

            Parse(raw, input, schema, result);
            result.Success = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("LLM parameter extraction timed out; falling back to deterministic parsing only.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LLM parameter extraction failed.");
        }
        finally
        {
            _ai.ResetSession(ScratchProjectId, scratchSession);
        }

        return result;
    }

    private static string BuildPrompt(string input, TemplateParameterSchema schema)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Task: extract CAD dimension edits from the user message. Reply with ONE JSON object only. No prose. No code.");
        builder.AppendLine("Schema: {\"parameters\":[{\"key\":string,\"value\":number,\"unit\":string}],\"components\":[string],\"full_assembly\":boolean}");
        builder.AppendLine("Rules: copy numbers exactly as the user wrote them. Never calculate, convert or invent numbers. Leave out anything the user did not state.");
        builder.AppendLine("Allowed keys:");

        foreach (var def in schema.Parameters)
        {
            var aliases = def.Aliases.Length > 0 ? " (also: " + string.Join(", ", def.Aliases) + ")" : string.Empty;
            builder.AppendLine("- " + def.Key + ": " + def.Label + ", unit " + def.Unit + aliases);
        }

        builder.AppendLine("Allowed components:");

        foreach (var component in schema.Components)
        {
            builder.AppendLine("- " + component.Key + ": " + component.Title);
        }

        builder.AppendLine("User message: " + input);

        return builder.ToString();
    }

    private static void Parse(string? raw, string input, TemplateParameterSchema schema, LlmExtractionResult result)
    {
        var json = JsonExtractor.Extract(raw);

        if (json is null)
        {
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, JsonExtractor.ParseOptions);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var statedNumbers = ReadStatedNumbers(input);

            if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in parameters.EnumerateArray())
                {
                    ReadParameter(item, schema, statedNumbers, result);
                }
            }

            if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in components.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var component = TemplateScriptComposer.ResolveComponent(schema, item.GetString());

                    if (component is not null && !result.ComponentKeys.Contains(component.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        result.ComponentKeys.Add(component.Key);
                    }
                }
            }

            if (root.TryGetProperty("full_assembly", out var full) && full.ValueKind == JsonValueKind.True)
            {
                result.FullAssembly = true;
            }
        }

        if (result.FullAssembly)
        {
            result.ComponentKeys.Clear();
        }
    }

    private static void ReadParameter(
        JsonElement item,
        TemplateParameterSchema schema,
        IReadOnlyList<double> statedNumbers,
        LlmExtractionResult result)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("value", out var valueElement))
        {
            return;
        }

        var keyText = keyElement.GetString() ?? string.Empty;
        var def = schema.FindParameter(keyText) ?? FindByAlias(schema, keyText);

        if (def is null)
        {
            result.Rejected.Add("Unknown parameter '" + keyText + "'.");
            return;
        }

        if (!TryReadNumber(valueElement, out var raw))
        {
            result.Rejected.Add(def.Label + ": the value was not a number.");
            return;
        }

        if (!statedNumbers.Any(n => Math.Abs(n - raw) < 1e-6))
        {
            result.Rejected.Add(def.Label + ": the value " + TemplateParameterCatalog.Format(raw) + " was not stated in your message.");
            return;
        }

        var unit = item.TryGetProperty("unit", out var unitElement) && unitElement.ValueKind == JsonValueKind.String
            ? (unitElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
            : string.Empty;

        result.Updates[def.Key] = Convert(def, raw, unit);
    }

    private static TemplateParameterDefinition? FindByAlias(TemplateParameterSchema schema, string text)
    {
        return schema.Parameters.FirstOrDefault(p =>
            p.Aliases.Contains(text, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(p.Label, text, StringComparison.OrdinalIgnoreCase));
    }

    private static double Convert(TemplateParameterDefinition def, double value, string unit)
    {
        if (!string.Equals(def.Unit, "mm", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return unit switch
        {
            "cm" => value * 10.0,
            "m" => value * 1000.0,
            "in" or "inch" or "inches" => value * 25.4,
            _ => value
        };
    }

    private static bool TryReadNumber(JsonElement element, out double value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = (element.GetString() ?? string.Empty).Replace(',', '.');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static List<double> ReadStatedNumbers(string input)
    {
        var numbers = new List<double>();

        foreach (Match match in NumberRegex.Matches(input))
        {
            var text = match.Value.Replace(',', '.');

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                numbers.Add(parsed);
            }
        }

        return numbers;
    }
}