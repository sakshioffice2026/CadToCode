using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Services;

public enum ImageCalloutKind
{
    Diameter = 1,
    Radius = 2,
    Linear = 3,
    Angle = 4,
    Scale = 5,
    Label = 6
}

/// <summary>A parsed text annotation in the source image's pixel coordinate space.</summary>
public sealed record ImageCallout(
    ImageCalloutKind Kind,
    string Text,
    double? Value,
    double X,
    double Y,
    double Width,
    double Height,
    double Confidence)
{
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}

public sealed class ImageAnnotationResult
{
    public List<ImageCallout> Callouts { get; } = new();
    /// <summary>Drawing scale multiplier for a scale written as a:b (b/a).</summary>
    public double? DrawingScale { get; set; }
    public DrawingUnit? DetectedUnit { get; set; }
}

/// <summary>
/// Groups OCR words by visual line and recognizes common mechanical-drawing callouts.
/// Parsing is intentionally conservative: text is never used as proof of a scale by itself;
/// scale calibration also requires geometric evidence in ImageGeometryExtractor.
/// </summary>
public static class ImageAnnotationParser
{
    private const double RowTolerance = 0.60;
    private const double MergeGapFactor = 1.35;
    private const string Number = @"\d+(?:[.,]\d+)?";

    private static readonly RegexOptions RxOptions =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex ScaleWord = new(
        @"\bscale\s*[:=]?\s*(?<a>\d+(?:[.,]\d+)?)\s*:\s*(?<b>\d+(?:[.,]\d+)?)\b", RxOptions);
    private static readonly Regex ScaleBare = new(
        @"^(?<a>\d+(?:[.,]\d+)?)\s*:\s*(?<b>\d+(?:[.,]\d+)?)$", RxOptions);
    private static readonly Regex Diameter = new(
        @"^(?:[Øø⌀ϕφΦ]|%%c|dia(?:meter)?\.?|diam\.?)\s*(?<v>" + Number + @")\s*(?:mm|cm|in|inch(?:es)?)?$", RxOptions);
    private static readonly Regex DiameterSuffix = new(
        @"^(?<v>" + Number + @")\s*(?:mm\s*)?(?:dia(?:meter)?\.?|diam\.?)$", RxOptions);
    private static readonly Regex Radius = new(
        @"^(?:sr|r)\s*(?<v>" + Number + @")\s*(?:mm|cm|in|inch(?:es)?)?$", RxOptions);
    private static readonly Regex Angle = new(
        @"^(?<v>" + Number + @")\s*[°º˚]$", RxOptions);
    private static readonly Regex LinearWithUnit = new(
        @"^\s*\(?(?<v>" + Number + @")\)?\s*(?<unit>mm|cm|in|inch(?:es)?)\s*(?:±\s*" + Number + @")?\s*$", RxOptions);
    private static readonly Regex LinearBare = new(
        @"^\s*\(?(?<v>" + Number + @")\)?\s*(?:±\s*" + Number + @")?\s*$", RxOptions);

    private static readonly Regex MillimeterUnit = new(@"\b(?:mm|millimet(?:er|re)s?)\b", RxOptions);
    private static readonly Regex CentimeterUnit = new(@"\b(?:cm|centimet(?:er|re)s?)\b", RxOptions);
    private static readonly Regex InchUnit = new("\\b(?:in|inch(?:es)?)\\b|\"", RxOptions);
    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumberToken = new(@"(?<![A-Za-z0-9.])\d+(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Phrase(string Text, double X, double Y, double Width, double Height, double Confidence);

    public static ImageAnnotationResult Parse(IReadOnlyList<RecognizedWord> words)
    {
        var result = new ImageAnnotationResult();
        if (words is null || words.Count == 0)
        {
            return result;
        }

        foreach (var phrase in BuildPhrases(words))
        {
            var text = phrase.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            result.DetectedUnit ??= DetectUnit(text);
            var callout = Classify(text, phrase, result);
            if (callout is not null)
            {
                result.Callouts.Add(callout);
            }
        }

        return result;
    }

    public static (List<string> Tokens, List<double> Numbers) BuildTokens(string text)
    {
        var tokens = new List<string>();
        var numbers = new List<double>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (tokens, numbers);
        }

        foreach (Match match in WordToken.Matches(text))
        {
            tokens.Add(match.Value.ToLowerInvariant());
        }

        foreach (Match match in NumberToken.Matches(text))
        {
            if (TryParseNumber(match.Value, out var value))
            {
                numbers.Add(value);
            }
        }

        return (tokens, numbers);
    }

    private static ImageCallout? Classify(string text, Phrase phrase, ImageAnnotationResult result)
    {
        // OCR can separate the scale label and ratio into adjacent tokens; row grouping
        // normally joins them, while this normalization removes harmless internal spaces.
        var compact = Regex.Replace(text, @"\s+", string.Empty);
        var scale = ScaleWord.Match(text);
        if (!scale.Success)
        {
            scale = ScaleBare.Match(compact);
        }

        if (scale.Success
            && TryParseNumber(scale.Groups["a"].Value, out var a)
            && TryParseNumber(scale.Groups["b"].Value, out var b)
            && a > 0 && b > 0)
        {
            var multiplier = b / a;
            if (double.IsFinite(multiplier) && multiplier > 0)
            {
                result.DrawingScale ??= multiplier;
                return Create(ImageCalloutKind.Scale,
                    "SCALE " + Format(a) + ":" + Format(b), multiplier, phrase);
            }
        }

        // Symbols such as Ø can be returned as a separate word by OCR, so try both the
        // original phrase and a whitespace-free form. Do not turn arbitrary labels into diameters.
        foreach (var candidateText in new[] { text, compact })
        {
            var diameter = Diameter.Match(candidateText);
            if (!diameter.Success)
            {
                diameter = DiameterSuffix.Match(candidateText);
            }

            if (diameter.Success && TryParseNumber(diameter.Groups["v"].Value, out var diameterValue)
                && diameterValue > 0)
            {
                return Create(ImageCalloutKind.Diameter, "Ø" + Format(diameterValue), diameterValue, phrase);
            }

            var radius = Radius.Match(candidateText);
            if (radius.Success && TryParseNumber(radius.Groups["v"].Value, out var radiusValue) && radiusValue > 0)
            {
                return Create(ImageCalloutKind.Radius, "R" + Format(radiusValue), radiusValue, phrase);
            }

            var angle = Angle.Match(candidateText);
            if (angle.Success && TryParseNumber(angle.Groups["v"].Value, out var angleValue) && angleValue > 0)
            {
                return Create(ImageCalloutKind.Angle, Format(angleValue) + "°", angleValue, phrase);
            }

            var linearWithUnit = LinearWithUnit.Match(candidateText);
            if (linearWithUnit.Success && TryParseNumber(linearWithUnit.Groups["v"].Value, out var unitValue) && unitValue > 0)
            {
                return Create(ImageCalloutKind.Linear, Format(unitValue), unitValue, phrase);
            }

            var linearBare = LinearBare.Match(candidateText);
            if (linearBare.Success && TryParseNumber(linearBare.Groups["v"].Value, out var linearValue) && linearValue > 0)
            {
                return Create(ImageCalloutKind.Linear, Format(linearValue), linearValue, phrase);
            }
        }

        return text.Any(char.IsLetter)
            ? Create(ImageCalloutKind.Label, text, null, phrase)
            : null;
    }

    private static List<Phrase> BuildPhrases(IReadOnlyList<RecognizedWord> words)
    {
        var usable = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Width > 0 && w.Height > 0
                        && double.IsFinite(w.Confidence) && w.Confidence >= 0)
            .OrderBy(w => w.CenterY)
            .ThenBy(w => w.X)
            .ToList();

        var rows = new List<List<RecognizedWord>>();
        foreach (var word in usable)
        {
            List<RecognizedWord>? bestRow = null;
            var bestDistance = double.MaxValue;
            foreach (var row in rows)
            {
                var rowCenter = row.Average(w => w.CenterY);
                var typicalHeight = Math.Max(row.Average(w => (double)w.Height), word.Height);
                var distance = Math.Abs(word.CenterY - rowCenter);
                if (distance <= RowTolerance * typicalHeight && distance < bestDistance)
                {
                    bestRow = row;
                    bestDistance = distance;
                }
            }

            if (bestRow is null)
            {
                rows.Add(new List<RecognizedWord> { word });
            }
            else
            {
                bestRow.Add(word);
            }
        }

        var phrases = new List<Phrase>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(w => w.X).ToList();
            var current = new List<RecognizedWord>();
            foreach (var word in ordered)
            {
                if (current.Count > 0)
                {
                    var gap = word.X - current[^1].Right;
                    var typicalHeight = Math.Max(current.Average(w => (double)w.Height), word.Height);
                    if (gap > MergeGapFactor * typicalHeight)
                    {
                        phrases.Add(Flush(current));
                        current.Clear();
                    }
                }

                current.Add(word);
            }

            if (current.Count > 0)
            {
                phrases.Add(Flush(current));
            }
        }

        return phrases;
    }

    private static Phrase Flush(IReadOnlyList<RecognizedWord> words)
    {
        var left = words.Min(w => w.X);
        var top = words.Min(w => w.Y);
        var right = words.Max(w => w.Right);
        var bottom = words.Max(w => w.Bottom);
        return new Phrase(
            string.Join(" ", words.OrderBy(w => w.X).Select(w => w.Text.Trim())),
            left,
            top,
            right - left,
            bottom - top,
            words.Average(w => Math.Clamp(w.Confidence, 0.0, 1.0)));
    }

    private static ImageCallout Create(ImageCalloutKind kind, string text, double? value, Phrase phrase) =>
        new(kind, text, value, phrase.X, phrase.Y, phrase.Width, phrase.Height, phrase.Confidence);

    private static DrawingUnit? DetectUnit(string text)
    {
        if (MillimeterUnit.IsMatch(text)) return DrawingUnit.Millimeter;
        if (CentimeterUnit.IsMatch(text)) return DrawingUnit.Centimeter;
        if (InchUnit.IsMatch(text)) return DrawingUnit.Inch;
        return null;
    }

    private static bool TryParseNumber(string raw, out double value)
    {
        // Decimal comma is common in technical drawings. A comma followed by three digits
        // can be ambiguous (decimal vs thousands separator), so this parser assumes decimal.
        var normalized = raw.Replace(',', '.');
        var ok = double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return ok && double.IsFinite(value) && value > 0;
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
