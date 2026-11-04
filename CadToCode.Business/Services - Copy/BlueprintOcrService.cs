using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Services;

public enum OcrDimensionKind
{
    Diameter = 1,
    Radius = 2,
    Length = 3,
    Width = 4,
    Height = 5,
    Thickness = 6,
    Depth = 7,
    Unlabeled = 8
}

public sealed record OcrDimension(
    OcrDimensionKind Kind,
    double ValueMm,
    string RawText,
    double Confidence,
    int Occurrences,
    string Name);

public sealed class BlueprintOcrResult
{
    public bool OcrAvailable { get; init; }

    public IReadOnlyList<OcrDimension> Dimensions { get; init; } = Array.Empty<OcrDimension>();

    public IReadOnlyList<string> RawLines { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool HasDimensions => Dimensions.Count > 0;

    public IEnumerable<OcrDimension> Of(OcrDimensionKind kind) => Dimensions.Where(d => d.Kind == kind);

    public string ToParameterTable()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Source: image OCR (labels read from the drawing, millimeters)");

        if (!OcrAvailable)
        {
            sb.AppendLine("OCR is not available on this machine, so no dimensions were read.");
        }
        else if (Dimensions.Count == 0)
        {
            sb.AppendLine("No dimension labels were recognized.");
        }
        else
        {
            foreach (var d in Dimensions)
            {
                sb.AppendLine(
                    d.Name + " = " + Format(d.ValueMm)
                    + "  // " + d.Kind + ", text \"" + d.RawText + "\""
                    + ", confidence " + d.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                    + ", seen " + d.Occurrences + "x");
            }
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("OCR warnings:");

            foreach (var warning in Warnings)
            {
                sb.AppendLine("- " + warning);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Format(double value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
    }
}

public interface IBlueprintOcrService
{
    bool IsAvailable { get; }

    Task<BlueprintOcrResult> AnalyzeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);

    Task<BlueprintOcrResult> AnalyzeAsync(Stream imageStream, CancellationToken cancellationToken = default);
}

public sealed class BlueprintOcrService : IBlueprintOcrService
{
    private const double MinMm = 0.01;
    private const double MaxMm = 100_000.0;
    private const double LineGroupFactor = 0.6;

    private const string Num = @"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:[.,]\d+)?";
    private const string Unit = @"(?:\s*(?<unit>mm|cm|m|in|"")(?![A-Za-z]))?";
    private const string DiameterSymbol = @"[\u00D8\u00F8\u03A6\u03C6\u0424\u2300\u2205\u03D5]";

    private static readonly Regex DiameterPrefix = new(
        @"(?:" + DiameterSymbol + @"|\bDIA(?:M(?:ETER)?)?\.?|\bPHI\b)\s*[:=]?\s*(?<num>" + Num + @")" + Unit,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiameterSuffix = new(
        @"(?<num>" + Num + @")" + Unit + @"\s*(?:\bDIA(?:M(?:ETER)?)?\b\.?|" + DiameterSymbol + @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Radius = new(
        @"(?<![A-Za-z])R(?:AD(?:IUS)?)?\s*[:=]?\s*(?<num>" + Num + @")" + Unit,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WordLabel = new(
        @"\b(?<label>LENGTH|LEN|WIDTH|WID|HEIGHT|HGT|HT|THICKNESS|THICK|THK|DEPTH|DEP)\b\.?\s*[:=]?\s*(?<num>" + Num + @")" + Unit,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LetterLabel = new(
        @"\b(?<label>[LWHTD])\s*[:=]\s*(?<num>" + Num + @")" + Unit,
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Product = new(
        @"(?<![\d.,])(?<a>" + Num + @")\s*[xX\u00D7]\s*(?<b>" + Num + @")(?:\s*[xX\u00D7]\s*(?<c>" + Num + @"))?(?![\d])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Bare = new(
        @"(?<![\w.,:/\u00B1+\-])(?<num>\d{2,5}(?:[.,]\d{1,3})?)" + Unit + @"(?![\w.,:/%\u00B0])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IImageTextRecognizer _recognizer;
    private readonly ILogger<BlueprintOcrService> _logger;

    public BlueprintOcrService(IImageTextRecognizer recognizer, ILogger<BlueprintOcrService> logger)
    {
        _recognizer = recognizer;
        _logger = logger;
    }

    public bool IsAvailable => _recognizer.IsAvailable;

    public async Task<BlueprintOcrResult> AnalyzeAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageStream);

        using var buffer = new MemoryStream();
        await imageStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return await AnalyzeAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<BlueprintOcrResult> AnalyzeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        var warnings = new List<string>();

        if (imageBytes.Length == 0)
        {
            warnings.Add("The image is empty.");
            return new BlueprintOcrResult { OcrAvailable = false, Warnings = warnings };
        }

        if (!_recognizer.IsAvailable)
        {
            warnings.Add("Tesseract or its eng.traineddata file is not available.");
            return new BlueprintOcrResult { OcrAvailable = false, Warnings = warnings };
        }

        IReadOnlyList<RecognizedWord> words;

        try
        {
            words = await _recognizer.RecognizeAsync(imageBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blueprint OCR failed.");
            warnings.Add("OCR failed: " + ex.Message);
            return new BlueprintOcrResult { OcrAvailable = false, Warnings = warnings };
        }

        if (words.Count == 0)
        {
            warnings.Add("No readable text was found in the image.");
            return new BlueprintOcrResult { OcrAvailable = true, Warnings = warnings };
        }

        var lines = GroupIntoLines(words);
        var found = new List<Candidate>();

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParseLine(line.Text, line.Confidence, found);
        }

        var dimensions = Merge(found);

        if (dimensions.Count == 0)
        {
            warnings.Add("Text was found but no dimension labels (for example Ø600, DIA 600, L=1200, R25) could be parsed.");
        }
        else if (dimensions.All(d => d.Kind == OcrDimensionKind.Unlabeled))
        {
            warnings.Add("Only unlabeled numbers were found; check which one is the diameter, length or height.");
        }

        if (dimensions.Any(d => d.Confidence < 0.75))
        {
            warnings.Add("Some values have low OCR confidence; verify them against the drawing.");
        }

        return new BlueprintOcrResult
        {
            OcrAvailable = true,
            Dimensions = dimensions,
            RawLines = lines.Select(l => l.Text).ToList(),
            Warnings = warnings
        };
    }

    private static void ParseLine(string text, double confidence, List<Candidate> found)
    {
        var consumed = new List<(int Start, int End)>();

        foreach (Match m in DiameterPrefix.Matches(text))
        {
            Add(found, consumed, m, OcrDimensionKind.Diameter, 1.0, confidence);
        }

        foreach (Match m in DiameterSuffix.Matches(text))
        {
            if (!Overlaps(consumed, m))
            {
                Add(found, consumed, m, OcrDimensionKind.Diameter, 1.0, confidence);
            }
        }

        foreach (Match m in WordLabel.Matches(text))
        {
            if (Overlaps(consumed, m))
            {
                continue;
            }

            Add(found, consumed, m, KindFromLabel(m.Groups["label"].Value), 1.0, confidence);
        }

        foreach (Match m in LetterLabel.Matches(text))
        {
            if (Overlaps(consumed, m))
            {
                continue;
            }

            Add(found, consumed, m, KindFromLabel(m.Groups["label"].Value), 1.0, confidence);
        }

        foreach (Match m in Radius.Matches(text))
        {
            if (!Overlaps(consumed, m))
            {
                Add(found, consumed, m, OcrDimensionKind.Radius, 1.0, confidence);
            }
        }

        foreach (Match m in Product.Matches(text))
        {
            if (Overlaps(consumed, m))
            {
                continue;
            }

            var kinds = new[] { OcrDimensionKind.Length, OcrDimensionKind.Width, OcrDimensionKind.Thickness };
            var groups = new[] { "a", "b", "c" };

            for (var i = 0; i < groups.Length; i++)
            {
                var g = m.Groups[groups[i]];

                if (!g.Success || !TryParseNumber(g.Value, out var value))
                {
                    continue;
                }

                AddValue(found, kinds[i], value, m.Value.Trim(), confidence);
            }

            consumed.Add((m.Index, m.Index + m.Length));
        }

        foreach (Match m in Bare.Matches(text))
        {
            if (Overlaps(consumed, m))
            {
                continue;
            }

            Add(found, consumed, m, OcrDimensionKind.Unlabeled, 1.0, confidence * 0.8);
        }
    }

    private static void Add(
        List<Candidate> found,
        List<(int Start, int End)> consumed,
        Match match,
        OcrDimensionKind kind,
        double factor,
        double confidence)
    {
        var numGroup = match.Groups["num"];

        if (!numGroup.Success || !TryParseNumber(numGroup.Value, out var value))
        {
            return;
        }

        var unit = match.Groups["unit"];

        if (unit.Success)
        {
            value *= UnitToMm(unit.Value);
        }

        consumed.Add((match.Index, match.Index + match.Length));
        AddValue(found, kind, value, match.Value.Trim(), confidence * factor);
    }

    private static void AddValue(List<Candidate> found, OcrDimensionKind kind, double valueMm, string raw, double confidence)
    {
        if (!double.IsFinite(valueMm) || valueMm < MinMm || valueMm > MaxMm)
        {
            return;
        }

        found.Add(new Candidate(kind, valueMm, raw, Math.Clamp(confidence, 0.0, 1.0)));
    }

    private static bool Overlaps(List<(int Start, int End)> consumed, Match match)
    {
        var start = match.Index;
        var end = match.Index + match.Length;

        foreach (var span in consumed)
        {
            if (start < span.End && end > span.Start)
            {
                return true;
            }
        }

        return false;
    }

    private static List<OcrDimension> Merge(List<Candidate> found)
    {
        var order = new[]
        {
            OcrDimensionKind.Diameter,
            OcrDimensionKind.Radius,
            OcrDimensionKind.Length,
            OcrDimensionKind.Width,
            OcrDimensionKind.Height,
            OcrDimensionKind.Thickness,
            OcrDimensionKind.Depth,
            OcrDimensionKind.Unlabeled
        };

        var result = new List<OcrDimension>();
        var labeledValues = found
            .Where(c => c.Kind != OcrDimensionKind.Unlabeled)
            .Select(c => Math.Round(c.ValueMm, 3))
            .ToHashSet();

        foreach (var kind in order)
        {
            var groups = found
                .Where(c => c.Kind == kind)
                .Where(c => kind != OcrDimensionKind.Unlabeled || !labeledValues.Contains(Math.Round(c.ValueMm, 3)))
                .GroupBy(c => Math.Round(c.ValueMm, 3))
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(c => c.Confidence))
                .ThenByDescending(g => g.Key)
                .ToList();

            var index = 0;

            foreach (var group in groups)
            {
                index++;
                var best = group.OrderByDescending(c => c.Confidence).First();

                result.Add(new OcrDimension(
                    kind,
                    group.Key,
                    best.Raw,
                    group.Max(c => c.Confidence),
                    group.Count(),
                    NameFor(kind, index)));
            }
        }

        return result;
    }

    private static string NameFor(OcrDimensionKind kind, int index)
    {
        var baseName = kind switch
        {
            OcrDimensionKind.Diameter => "Diameter",
            OcrDimensionKind.Radius => "Radius",
            OcrDimensionKind.Length => "Length",
            OcrDimensionKind.Width => "Width",
            OcrDimensionKind.Height => "Height",
            OcrDimensionKind.Thickness => "Thickness",
            OcrDimensionKind.Depth => "Depth",
            _ => "Value"
        };

        return baseName + index.ToString(CultureInfo.InvariantCulture);
    }

    private static OcrDimensionKind KindFromLabel(string label)
    {
        return label.Trim().ToUpperInvariant() switch
        {
            "LENGTH" or "LEN" or "L" => OcrDimensionKind.Length,
            "WIDTH" or "WID" or "W" => OcrDimensionKind.Width,
            "HEIGHT" or "HGT" or "HT" or "H" => OcrDimensionKind.Height,
            "THICKNESS" or "THICK" or "THK" or "T" => OcrDimensionKind.Thickness,
            "DEPTH" or "DEP" => OcrDimensionKind.Depth,
            "D" => OcrDimensionKind.Diameter,
            _ => OcrDimensionKind.Unlabeled
        };
    }

    private static double UnitToMm(string unit)
    {
        return unit.Trim().ToLowerInvariant() switch
        {
            "cm" => 10.0,
            "m" => 1000.0,
            "in" or "\"" => 25.4,
            _ => 1.0
        };
    }

    private static bool TryParseNumber(string text, out double value)
    {
        value = 0.0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();

        var commaCount = s.Count(ch => ch == ',');

        if (commaCount > 0)
        {
            var lastComma = s.LastIndexOf(',');
            var digitsAfter = s.Length - lastComma - 1;
            var hasDot = s.Contains('.');

            if (hasDot || commaCount > 1 || digitsAfter == 3)
            {
                s = s.Replace(",", string.Empty);
            }
            else
            {
                s = s.Replace(',', '.');
            }
        }

        return double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static List<OcrLine> GroupIntoLines(IReadOnlyList<RecognizedWord> words)
    {
        var ordered = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderBy(w => w.CenterY)
            .ThenBy(w => w.X)
            .ToList();

        var groups = new List<List<RecognizedWord>>();

        foreach (var word in ordered)
        {
            var placed = false;

            foreach (var group in groups)
            {
                var anchorY = group.Average(w => w.CenterY);
                var height = Math.Max(group.Max(w => w.Height), word.Height);

                if (Math.Abs(word.CenterY - anchorY) <= height * LineGroupFactor)
                {
                    group.Add(word);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                groups.Add(new List<RecognizedWord> { word });
            }
        }

        return groups
            .OrderBy(g => g.Average(w => w.CenterY))
            .Select(g =>
            {
                var sorted = g.OrderBy(w => w.X).ToList();
                return new OcrLine(string.Join(" ", sorted.Select(w => w.Text)), sorted.Average(w => w.Confidence));
            })
            .ToList();
    }

    private sealed record Candidate(OcrDimensionKind Kind, double ValueMm, string Raw, double Confidence);

    private sealed record OcrLine(string Text, double Confidence);
}