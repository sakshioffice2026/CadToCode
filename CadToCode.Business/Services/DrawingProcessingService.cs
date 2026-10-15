using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public enum DrawingSourceKind
{
    Unsupported = 0,
    Dxf = 1,
    Image = 2
}

public enum DrawingProcessingMode
{
    None = 0,
    ExactGeometry = 1,
    TemplateInjection = 2,
    ParametersOnly = 3
}

public sealed record ParameterInjection(string Name, double OldValue, double NewValue, string Source);

public sealed class DrawingProcessingResult
{
    public bool Success { get; init; }

    public string? Error { get; init; }

    public string FileName { get; init; } = string.Empty;

    public DrawingSourceKind SourceKind { get; init; }

    public DrawingProcessingMode Mode { get; init; }

    public string ContextText { get; init; } = string.Empty;

    public string? ExactGeometryTable { get; init; }

    public string? ParameterTable { get; init; }

    public string? TemplateTitle { get; init; }

    public string? TemplateCategory { get; init; }

    public string? TemplateSource { get; init; }

    public double TemplateScore { get; init; }

    public string? TemplateScript { get; init; }

    public string? InjectedScript { get; init; }

    public IReadOnlyList<ParameterInjection> Injections { get; init; } = Array.Empty<ParameterInjection>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasUsableData => Success && Mode != DrawingProcessingMode.None && ContextText.Length > 0;
}

public interface IDrawingProcessingService
{
    bool IsSupported(string fileName);

    Task<DrawingProcessingResult> ProcessAsync(
        Stream fileStream,
        string fileName,
        string? userPrompt = null,
        int maxContextChars = 6000,
        CancellationToken cancellationToken = default,
        KeyDimensionsDto? keyDimensions = null);
}

public sealed class DrawingProcessingService : IDrawingProcessingService
{
    private const double MinTemplateScore = 0.2;
    private const double MinInjectionConfidence = 0.5;
    private const int MaxOcrTextForMatch = 1500;

    private static readonly string[] DxfExtensions = { ".dxf" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    private static readonly int[] TableDetailLevels = { 200, 100, 50, 25, 10, 5 };

    private static readonly Regex ConstDouble = new(
        @"(?<prefix>\bconst\s+double\s+(?<name>[A-Za-z_]\w*)\s*=\s*)(?<value>-?\d+(?:\.\d+)?)(?<suffix>\s*;)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (OcrDimensionKind Kind, string[] Keywords)[] KindKeywords =
    {
        (OcrDimensionKind.Diameter, new[] { "diameter", "dia", "outerdia", "innerdia", "bore" }),
        (OcrDimensionKind.Radius, new[] { "radius", "rad", "fillet" }),
        (OcrDimensionKind.Length, new[] { "length", "len" }),
        (OcrDimensionKind.Width, new[] { "width", "wid" }),
        (OcrDimensionKind.Height, new[] { "height", "hgt" }),
        (OcrDimensionKind.Thickness, new[] { "thickness", "thick", "thk", "wall" }),
        (OcrDimensionKind.Depth, new[] { "depth", "dep" })
    };

    private readonly IDxfGeometryParser _dxfParser;
    private readonly IBlueprintOcrService _ocr;
    private readonly ICadTemplateMatcher _qdrantService;
    private readonly ILogger<DrawingProcessingService> _logger;

    public DrawingProcessingService(
        IDxfGeometryParser dxfParser,
        IBlueprintOcrService ocr,
        ICadTemplateMatcher qdrantService,
        ILogger<DrawingProcessingService> logger)
    {
        _dxfParser = dxfParser;
        _ocr = ocr;
        _qdrantService = qdrantService;
        _logger = logger;
    }

    public bool IsSupported(string fileName)
    {
        var kind = Classify(fileName);
        return kind == DrawingSourceKind.Dxf || kind == DrawingSourceKind.Image;
    }

    public async Task<DrawingProcessingResult> ProcessAsync(
        Stream fileStream,
        string fileName,
        string? userPrompt = null,
        int maxContextChars = 6000,
        CancellationToken cancellationToken = default,
        KeyDimensionsDto? keyDimensions = null)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        var name = Path.GetFileName(fileName ?? string.Empty);
        var kind = Classify(name);
        var limit = Math.Max(1500, maxContextChars);

        switch (kind)
        {
            case DrawingSourceKind.Dxf:
                return await ProcessDxfAsync(fileStream, name, userPrompt, limit, cancellationToken).ConfigureAwait(false);

            case DrawingSourceKind.Image:
                return await ProcessImageAsync(fileStream, name, userPrompt, limit, keyDimensions, cancellationToken).ConfigureAwait(false);

            default:
                return new DrawingProcessingResult
                {
                    Success = false,
                    FileName = name,
                    SourceKind = DrawingSourceKind.Unsupported,
                    Error = string.Equals(Path.GetExtension(name), ".dwg", StringComparison.OrdinalIgnoreCase)
                        ? "DWG files are not supported. Export the drawing as DXF and upload that file."
                        : "Unsupported file type. Upload a .dxf, .png, .jpg or .jpeg file."
                };
        }
    }

    private async Task<DrawingProcessingResult> ProcessDxfAsync(
        Stream stream,
        string fileName,
        string? userPrompt,
        int limit,
        CancellationToken cancellationToken)
    {
        DxfParseResult parsed;

        try
        {
            parsed = await _dxfParser.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DXF parsing failed for {File}.", fileName);

            return new DrawingProcessingResult
            {
                Success = false,
                FileName = fileName,
                SourceKind = DrawingSourceKind.Dxf,
                Error = ex.Message
            };
        }

        var warnings = new List<string>(parsed.Warnings);

        if (parsed.IsEmpty)
        {
            warnings.Add("The DXF contains no Line, Circle or Arc entities.");

            return new DrawingProcessingResult
            {
                Success = false,
                FileName = fileName,
                SourceKind = DrawingSourceKind.Dxf,
                Mode = DrawingProcessingMode.None,
                Error = "The DXF file has no Line, Circle or Arc geometry to convert.",
                Warnings = warnings
            };
        }

        var header = new StringBuilder();
        header.AppendLine("MODE: EXACT GEOMETRY (DXF, deterministic)");
        header.AppendLine("File: " + fileName);
        header.AppendLine("Every coordinate, radius and angle below is exact and final. Build the solid only from these entities; do not invent, round, add or move geometry.");

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            header.AppendLine("User request: " + userPrompt.Trim());
        }

        header.AppendLine();

        string table = string.Empty;

        foreach (var level in TableDetailLevels)
        {
            table = parsed.ToCoordinateTable(level);

            if (header.Length + table.Length <= limit)
            {
                break;
            }
        }

        if (parsed.EntityCount > TableDetailLevels[^1] * 3 && header.Length + table.Length > limit)
        {
            warnings.Add("The drawing is large; only part of the entity list is shown to the model.");
        }

        return new DrawingProcessingResult
        {
            Success = true,
            FileName = fileName,
            SourceKind = DrawingSourceKind.Dxf,
            Mode = DrawingProcessingMode.ExactGeometry,
            ExactGeometryTable = table,
            ContextText = header + table,
            Warnings = warnings
        };
    }

    private async Task<DrawingProcessingResult> ProcessImageAsync(
        Stream stream,
        string fileName,
        string? userPrompt,
        int limit,
        KeyDimensionsDto? keyDimensions,
        CancellationToken cancellationToken)
    {
        BlueprintOcrResult ocr;

        try
        {
            ocr = await _ocr.AnalyzeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image OCR failed for {File}.", fileName);

            return new DrawingProcessingResult
            {
                Success = false,
                FileName = fileName,
                SourceKind = DrawingSourceKind.Image,
                Error = "The image could not be read: " + ex.Message
            };
        }

        var warnings = new List<string>(ocr.Warnings);

        CadTemplateMatch? match = null;

        try
        {
            match = await _qdrantService
                .MatchAsync(null, null, BuildMatchPrompt(userPrompt, ocr), null, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Template lookup failed for {File}.", fileName);
            warnings.Add("Template lookup failed: " + ex.Message);
        }

        if (match is not null && (match.Score < MinTemplateScore || string.IsNullOrWhiteSpace(match.Script)))
        {
            match = null;
        }

        var injections = new List<ParameterInjection>();
        string? injected = null;

        if (match is not null)
        {
            injected = InjectParameters(match.Script, ocr, injections);
            injected = ApplyKeyDimensions(injected, keyDimensions, injections, warnings);

            if (injections.Count == 0 && ocr.HasDimensions)
            {
                warnings.Add("No OCR dimension could be mapped to a named const in the template; the template values are unchanged.");
            }
        }
        else
        {
            warnings.Add("No verified template matched this drawing; the model must not invent geometry.");
        }

        if (match is null && !ocr.HasDimensions)
        {
            return new DrawingProcessingResult
            {
                Success = false,
                FileName = fileName,
                SourceKind = DrawingSourceKind.Image,
                Mode = DrawingProcessingMode.None,
                Error = "No dimensions could be read and no matching template was found. Upload a DXF file or a clearer image.",
                Warnings = warnings
            };
        }

        var mode = match is not null ? DrawingProcessingMode.TemplateInjection : DrawingProcessingMode.ParametersOnly;
        var parameterTable = ocr.ToParameterTable();

        var context = BuildImageContext(fileName, userPrompt, mode, parameterTable, match, injected, injections, limit, warnings);

        return new DrawingProcessingResult
        {
            Success = true,
            FileName = fileName,
            SourceKind = DrawingSourceKind.Image,
            Mode = mode,
            ContextText = context,
            ParameterTable = parameterTable,
            TemplateTitle = match?.Title,
            TemplateCategory = match?.Category,
            TemplateSource = match?.Source,
            TemplateScore = match?.Score ?? 0.0,
            TemplateScript = match?.Script,
            InjectedScript = injected,
            Injections = injections,
            Warnings = warnings
        };
    }

    private static string BuildImageContext(
        string fileName,
        string? userPrompt,
        DrawingProcessingMode mode,
        string parameterTable,
        CadTemplateMatch? match,
        string? injectedScript,
        IReadOnlyList<ParameterInjection> injections,
        int limit,
        List<string> warnings)
    {
        var sb = new StringBuilder();

        if (mode == DrawingProcessingMode.TemplateInjection)
        {
            sb.AppendLine("MODE: TEMPLATE PARAMETER INJECTION (image, OCR values)");
        }
        else
        {
            sb.AppendLine("MODE: PARAMETERS ONLY (image, OCR values, no template)");
        }

        sb.AppendLine("File: " + fileName);

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            sb.AppendLine("User request: " + userPrompt.Trim());
        }

        sb.AppendLine();
        sb.AppendLine(parameterTable);

        if (match is null)
        {
            return Fit(sb.ToString().TrimEnd(), limit, warnings);
        }

        sb.AppendLine();
        sb.AppendLine("Verified working template: " + match.Title + " (category " + match.Category
            + ", source " + match.Source + ", match score " + match.Score.ToString("0.00", CultureInfo.InvariantCulture) + ")");

        if (injections.Count > 0)
        {
            sb.AppendLine("Parameters already injected into the template from the OCR values:");

            foreach (var i in injections)
            {
                sb.AppendLine("- " + i.Name + ": " + Format(i.OldValue) + " -> " + Format(i.NewValue) + " (" + i.Source + ")");
            }
        }

        sb.AppendLine("Keep the template's structure exactly; change only the named const double values, part names and feature counts.");
        sb.AppendLine("```csharp");
        sb.AppendLine((injectedScript ?? match.Script).Trim());
        sb.AppendLine("```");

        return Fit(sb.ToString().TrimEnd(), limit, warnings);
    }

    private static string Fit(string text, int limit, List<string> warnings)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        warnings.Add("The drawing context was truncated to fit the model's context window.");

        return text[..limit];
    }

    private static string BuildMatchPrompt(string? userPrompt, BlueprintOcrResult ocr)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            sb.AppendLine(userPrompt.Trim());
        }

        foreach (var kind in ocr.Dimensions.Select(d => d.Kind).Where(k => k != OcrDimensionKind.Unlabeled).Distinct())
        {
            sb.Append(kind.ToString().ToLowerInvariant()).Append(' ');
        }

        sb.AppendLine();

        foreach (var line in ocr.RawLines)
        {
            if (sb.Length >= MaxOcrTextForMatch)
            {
                break;
            }

            sb.AppendLine(line);
        }

        var text = sb.ToString();

        return text.Length > MaxOcrTextForMatch ? text[..MaxOcrTextForMatch] : text;
    }

    private static string InjectParameters(string script, BlueprintOcrResult ocr, List<ParameterInjection> injections)
    {
        if (string.IsNullOrWhiteSpace(script) || !ocr.HasDimensions)
        {
            return script;
        }

        var queues = new Dictionary<OcrDimensionKind, Queue<OcrDimension>>();

        foreach (var (kind, _) in KindKeywords)
        {
            var items = ocr.Of(kind)
                .Where(d => d.Confidence >= MinInjectionConfidence)
                .OrderByDescending(d => d.Occurrences)
                .ThenByDescending(d => d.Confidence)
                .ToList();

            if (items.Count > 0)
            {
                queues[kind] = new Queue<OcrDimension>(items);
            }
        }

        if (queues.Count == 0)
        {
            return script;
        }

        return ConstDouble.Replace(script, match =>
        {
            var constName = match.Groups["n"].Value;
            var normalized = constName.ToLowerInvariant();

            foreach (var (kind, keywords) in KindKeywords)
            {
                if (!queues.TryGetValue(kind, out var queue) || queue.Count == 0)
                {
                    continue;
                }

                if (!keywords.Any(k => NameMatches(normalized, k)))
                {
                    continue;
                }

                if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oldValue))
                {
                    return match.Value;
                }

                var dimension = queue.Dequeue();

                injections.Add(new ParameterInjection(constName, oldValue, dimension.ValueMm, "OCR \"" + dimension.RawText + "\""));

                return match.Groups["prefix"].Value
                    + dimension.ValueMm.ToString("0.####", CultureInfo.InvariantCulture)
                    + match.Groups["suffix"].Value;
            }

            return match.Value;
        });
    }

    private static readonly Dictionary<string, (Func<KeyDimensionsDto, double?> Select, double Scale)> KeyDimensionTargets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Radius"] = (k => k.VesselDiameter, 0.5),
            ["VesselRadius"] = (k => k.VesselDiameter, 0.5),
            ["VesselDiameter"] = (k => k.VesselDiameter, 1.0),
            ["ShellDiameter"] = (k => k.VesselDiameter, 1.0),
            ["ShellHeight"] = (k => k.VesselHeight, 1.0),
            ["VesselHeight"] = (k => k.VesselHeight, 1.0),
            ["BodyHeight"] = (k => k.VesselHeight, 1.0),
            ["InletDiameter"] = (k => k.InletPipeDiameter, 1.0),
            ["StackDiameter"] = (k => k.OutletPipeDiameter, 1.0),
            ["ExitDiameter"] = (k => k.OutletPipeDiameter, 1.0),
            ["OutletDiameter"] = (k => k.OutletPipeDiameter, 1.0),
            ["TankLength"] = (k => k.TankLength, 1.0)
        };

    private static string ApplyKeyDimensions(
        string script,
        KeyDimensionsDto? keys,
        List<ParameterInjection> injections,
        List<string> warnings)
    {
        if (keys is null || !keys.HasAny || string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        var applied = 0;

        var result = ConstDouble.Replace(script, match =>
        {
            var constName = match.Groups["n"].Value;

            if (!KeyDimensionTargets.TryGetValue(constName, out var target))
            {
                return match.Value;
            }

            var supplied = target.Select(keys);

            if (supplied is not > 0)
            {
                return match.Value;
            }

            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oldValue))
            {
                return match.Value;
            }

            var newValue = supplied.Value * target.Scale;

            injections.RemoveAll(i => string.Equals(i.Name, constName, StringComparison.Ordinal));
            injections.Add(new ParameterInjection(constName, oldValue, newValue, "User input"));
            applied++;

            return match.Groups["prefix"].Value
                + newValue.ToString("0.####", CultureInfo.InvariantCulture)
                + match.Groups["suffix"].Value;
        });

        if (applied == 0)
        {
            warnings.Add("None of the supplied key dimensions could be mapped to a named const in the template.");
        }

        return result;
    }

    private static bool NameMatches(string normalizedName, string keyword)
    {
        return normalizedName.Contains(keyword, StringComparison.Ordinal);
    }

    private static DrawingSourceKind Classify(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        if (DxfExtensions.Contains(extension))
        {
            return DrawingSourceKind.Dxf;
        }

        if (ImageExtensions.Contains(extension))
        {
            return DrawingSourceKind.Image;
        }

        return DrawingSourceKind.Unsupported;
    }

    private static string Format(double value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
    }
}