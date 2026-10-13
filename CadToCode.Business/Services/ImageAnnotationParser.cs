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
    public double? DrawingScale { get; set; }
    public DrawingUnit? DetectedUnit { get; set; }
}

public static class ImageAnnotationParser
{
    private const double RowTolerance = 0.6;
    private const double MergeGapFactor = 1.2;

    private const string Number = @"\d+(?:[.,]\d+)?";

    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex ScaleWord = new(
        @"scale\s*[:=]?\s*(?<a>\d+(?:\.\d+)?)\s*:\s*(?<b>\d+(?:\.\d+)?)", Options);

    private static readonly Regex ScaleBare = new(
        @"^(?<a>\d+(?:\.\d+)?)\s*:\s*(?<b>\d+(?:\.\d+)?)$", Options);

    private static readonly Regex DiameterSymbol = new(
        @"^(?:[Øø⌀ϕφΦ@]|%%c)\s*(?<v>" + Number + @")\s*(?:mm)?$", Options);

    private static readonly Regex DiameterWordFirst = new(
        @"^(?:dia\.?|diameter)\s*(?<v>" + Number + @")\s*(?:mm)?$", Options);

    private static readonly Regex DiameterWordLast = new(
        @"^(?<v>" + Number + @")\s*(?:mm\s*)?(?:dia\.?|diameter)$", Options);

    private static readonly Regex DiameterLookalike = new(
        @"^[0O]\s*(?<v>[1-9]\d*(?:[.,]\d+)?)$", Options);

    private static readonly Regex Radius = new(
        @"^(?:S?R)\s*(?<v>" + Number + @")\s*(?:mm)?$", Options);

    private static readonly Regex Angle = new(
        @"^(?<v>" + Number + @")\s*[°º˚]$", Options);

    private static readonly Regex Linear = new(
        @"^\(?(?<v>" + Number + @")\)?\s*(?:mm|cm)?\s*(?:±\s*" + Number + @")?$", Options);

    private static readonly Regex MillimeterUnit = new(@"\b(?:mm|millimet(?:er|re)s?)\b", Options);
    private static readonly Regex CentimeterUnit = new(@"\b(?:cm|centimet(?:er|re)s?)\b", Options);
    private static readonly Regex InchUnit = new(@"\binch(?:es)?\b", Options);

    private static readonly Regex WordToken = new(@"[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);
    private static readonly Regex NumberToken = new(@"(?<![A-Za-z0-9.])\d+(?:\.\d+)?", RegexOptions.Compiled);

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
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
        }

        return (tokens, numbers);
    }

    private static ImageCallout? Classify(string text, Phrase phrase, ImageAnnotationResult result)
    {
        var scale = ScaleWord.Match(text);
        if (!scale.Success)
        {
            scale = ScaleBare.Match(text);
        }

        if (scale.Success
            && double.TryParse(scale.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(scale.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && a > 0
            && b > 0)
        {
            result.DrawingScale ??= b / a;
            return Create(ImageCalloutKind.Scale, "SCALE " + Format(a) + ":" + Format(b), b / a, phrase);
        }

        foreach (var pattern in new[] { DiameterSymbol, DiameterWordFirst, DiameterWordLast, DiameterLookalike })
        {
            var match = pattern.Match(text);
            if (match.Success && TryParse(match.Groups["v"].Value, out var diameter))
            {
                return Create(ImageCalloutKind.Diameter, "Ø" + Format(diameter), diameter, phrase);
            }
        }

        var radius = Radius.Match(text);
        if (radius.Success && TryParse(radius.Groups["v"].Value, out var radiusValue))
        {
            return Create(ImageCalloutKind.Radius, "R" + Format(radiusValue), radiusValue, phrase);
        }

        var angle = Angle.Match(text);
        if (angle.Success && TryParse(angle.Groups["v"].Value, out var angleValue))
        {
            return Create(ImageCalloutKind.Angle, Format(angleValue) + "°", angleValue, phrase);
        }

        var linear = Linear.Match(text);
        if (linear.Success && TryParse(linear.Groups["v"].Value, out var linearValue))
        {
            return Create(ImageCalloutKind.Linear, Format(linearValue), linearValue, phrase);
        }

        if (text.Any(char.IsLetter))
        {
            return Create(ImageCalloutKind.Label, text, null, phrase);
        }

        return null;
    }

    private static ImageCallout Create(ImageCalloutKind kind, string text, double? value, Phrase phrase)
    {
        return new ImageCallout(kind, text, value, phrase.X, phrase.Y, phrase.Width, phrase.Height, phrase.Confidence);
    }

    private static List<Phrase> BuildPhrases(IReadOnlyList<RecognizedWord> words)
    {
        var phrases = new List<Phrase>();

        var usable = words
            .Where(IsUsable)
            .OrderBy(w => w.CenterY)
            .ThenBy(w => w.X)
            .ToList();

        var rows = new List<List<RecognizedWord>>();

        foreach (var word in usable)
        {
            List<RecognizedWord>? target = null;

            foreach (var row in rows)
            {
                var rowCenter = row.Average(w => w.CenterY);
                var rowHeight = row.Max(w => (double)w.Height);

                if (Math.Abs(word.CenterY - rowCenter) <= RowTolerance * Math.Max(rowHeight, word.Height))
                {
                    target = row;
                    break;
                }
            }

            if (target is null)
            {
                rows.Add(new List<RecognizedWord> { word });
            }
            else
            {
                target.Add(word);
            }
        }

        foreach (var row in rows)
        {
            var ordered = row.OrderBy(w => w.X).ToList();
            var current = new List<RecognizedWord>();

            foreach (var word in ordered)
            {
                if (current.Count > 0)
                {
                    var gap = word.X - current[^1].Right;
                    var height = Math.Max(current.Average(w => (double)w.Height), word.Height);

                    if (gap > MergeGapFactor * height)
                    {
                        phrases.Add(Flush(current));
                        current = new List<RecognizedWord>();
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

    private static Phrase Flush(List<RecognizedWord> words)
    {
        var left = words.Min(w => w.X);
        var top = words.Min(w => w.Y);
        var right = words.Max(w => w.Right);
        var bottom = words.Max(w => w.Bottom);

        return new Phrase(
            string.Join(" ", words.Select(w => w.Text)),
            left,
            top,
            right - left,
            bottom - top,
            words.Average(w => w.Confidence));
    }

    private static bool IsUsable(RecognizedWord word)
    {
        if (string.IsNullOrWhiteSpace(word.Text) || word.Width <= 0 || word.Height <= 0)
        {
            return false;
        }

        return word.Text.Any(c => char.IsLetterOrDigit(c) || c is '@' or '⌀' or '°' or 'º' or '˚' or '±');
    }

    private static DrawingUnit? DetectUnit(string text)
    {
        if (MillimeterUnit.IsMatch(text))
        {
            return DrawingUnit.Millimeter;
        }

        if (CentimeterUnit.IsMatch(text))
        {
            return DrawingUnit.Centimeter;
        }

        if (InchUnit.IsMatch(text))
        {
            return DrawingUnit.Inch;
        }

        return null;
    }

    private static bool TryParse(string raw, out double value)
    {
        var ok = double.TryParse(
            raw.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

        return ok && value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string Format(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}