using System.Globalization;
using System.Text.RegularExpressions;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace CadToCode.Business.Services;

public enum PartShape
{
    Unknown = 0,
    Flange = 1,
    Plate = 2,
    Bar = 3
}

/// <summary>One part size read from a text label. Values that the label does not state stay null.</summary>
public sealed class PartSizeCandidate
{
    public string Name { get; set; } = "PART";

    public string Label { get; set; } = string.Empty;

    public PartShape Shape { get; set; } = PartShape.Unknown;

    public double? OuterDiameterMm { get; set; }

    public double? InnerDiameterMm { get; set; }

    public double? ThicknessMm { get; set; }

    public double? LengthMm { get; set; }

    public double? WidthMm { get; set; }

    public int? Qty { get; set; }

    public int? HoleCount { get; set; }

    public double? HoleDiameterMm { get; set; }

    public double? HolePcdMm { get; set; }

    public string? Material { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }
}

/// <summary>Reads sizes from label text such as "%%C400 X 10 THK", "300 X 200 X 8", "8 HOLES %%C14 ON PCD %%C300", "QTY. 2".</summary>
public static class PartSizeParser
{
    private const string Num = @"(\d+(?:\.\d+)?)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex HoleNote = new(
        @"(\d+)\s*(?:NOS?\.?\s*)?HOLES?\s*(?:OF\s*)?Ø\s*" + Num + @"\s*(?:ON\s*)?(?:P\.?C\.?D\.?)\s*(?:OF\s*)?Ø?\s*" + Num, Options);

    private static readonly Regex ThicknessAfter = new(Num + @"\s*(?:MM)?\s*(?:THK|THICK)", Options);
    private static readonly Regex ThicknessEquals = new(@"(?:THK|THICKNESS|\bT)\s*[=:]\s*" + Num, Options);
    private static readonly Regex QtyLabel = new(@"QTY\.?\s*[:=]?\s*(\d+)", Options);
    private static readonly Regex QtyNos = new(@"(\d+)\s*(?:NOS|PCS|OFF)\b", Options);
    private static readonly Regex Diameter = new(@"Ø\s*" + Num, Options);
    private static readonly Regex DiameterWord = new(Num + @"\s*DIA\b", Options);
    private static readonly Regex Sizes = new(@"(?<![\d.])" + Num + @"\s*[X*×]\s*" + Num + @"(?:\s*[X*×]\s*" + Num + ")?", Options);
    private static readonly Regex MaterialName = new(@"\b(SS\s?3(?:04|16)L?|IS\s?2062|EN\s?\d+|MS|AL)\b", Options);

    public static PartSizeCandidate? Parse(string? raw)
    {
        var text = Normalize(raw);
        if (text.Length == 0)
        {
            return null;
        }

        var candidate = new PartSizeCandidate { Label = raw!.Trim(), Name = GuessName(raw) };

        var hole = HoleNote.Match(text);
        if (hole.Success)
        {
            candidate.HoleCount = ToInt(hole.Groups[1].Value);
            candidate.HoleDiameterMm = ToDouble(hole.Groups[2].Value);
            candidate.HolePcdMm = ToDouble(hole.Groups[3].Value);
            text = text.Remove(hole.Index, hole.Length).Insert(hole.Index, " ");
        }

        var thickness = ThicknessAfter.Match(text);
        if (!thickness.Success)
        {
            thickness = ThicknessEquals.Match(text);
        }

        if (thickness.Success)
        {
            candidate.ThicknessMm = ToDouble(thickness.Groups[1].Value);
            text = text.Remove(thickness.Index, thickness.Length).Insert(thickness.Index, " ");
        }

        var qty = QtyLabel.Match(text);
        if (!qty.Success)
        {
            qty = QtyNos.Match(text);
        }

        if (qty.Success)
        {
            candidate.Qty = ToInt(qty.Groups[1].Value);
            text = text.Remove(qty.Index, qty.Length).Insert(qty.Index, " ");
        }

        var material = MaterialName.Match(text);
        if (material.Success)
        {
            candidate.Material = material.Value.ToUpperInvariant().Replace(" ", string.Empty);
        }

        var diameters = Diameter.Matches(text).Select(m => ToDouble(m.Groups[1].Value)).ToList();
        if (diameters.Count == 0)
        {
            diameters = DiameterWord.Matches(text).Select(m => ToDouble(m.Groups[1].Value)).ToList();
        }

        var sizes = Sizes.Match(text);
        var numbers = new List<double>();
        if (sizes.Success)
        {
            numbers.Add(ToDouble(sizes.Groups[1].Value));
            numbers.Add(ToDouble(sizes.Groups[2].Value));
            if (sizes.Groups[3].Success)
            {
                numbers.Add(ToDouble(sizes.Groups[3].Value));
            }
        }

        if (diameters.Count > 0)
        {
            candidate.OuterDiameterMm = diameters[0];
            candidate.InnerDiameterMm = diameters.Count > 1 ? diameters[1] : null;

            if (candidate.ThicknessMm is null && numbers.Count >= 2)
            {
                var second = numbers[1];
                if (second <= diameters[0] * 0.5)
                {
                    candidate.ThicknessMm = second;
                    candidate.Shape = PartShape.Flange;
                }
                else
                {
                    candidate.LengthMm = second;
                    candidate.Shape = PartShape.Bar;
                }
            }
            else if (candidate.ThicknessMm is not null)
            {
                candidate.Shape = PartShape.Flange;
            }
        }
        else if (numbers.Count >= 2)
        {
            candidate.LengthMm = Math.Max(numbers[0], numbers[1]);
            candidate.WidthMm = Math.Min(numbers[0], numbers[1]);
            if (candidate.ThicknessMm is null && numbers.Count == 3)
            {
                candidate.ThicknessMm = numbers[2];
            }

            if (candidate.ThicknessMm is not null)
            {
                candidate.Shape = PartShape.Plate;
            }
        }

        var hasSize = candidate.Shape != PartShape.Unknown;
        var holeOnly = candidate.HoleCount is not null;
        return hasSize || holeOnly ? candidate : null;
    }

    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw
            .Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
            .Replace("⌀", "Ø")
            .Replace("Φ", "Ø")
            .Replace("φ", "Ø")
            .Replace("ø", "Ø")
            .Replace("\\P", " ", StringComparison.Ordinal)
            .Replace("\r", " ")
            .Replace("\n", " ");

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string GuessName(string raw)
    {
        var first = raw.Replace("\\P", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0 && !line.Any(char.IsDigit));
        return string.IsNullOrWhiteSpace(first) ? "PART" : first.Trim().TrimEnd(':').ToUpperInvariant();
    }

    private static double ToDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static int ToInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}

/// <summary>Reads part sizes from the TEXT and MTEXT labels of a general arrangement DXF.</summary>
public sealed class GaTextPartSizeSource
{
    public IReadOnlyList<PartSizeCandidate> Read(DxfFile dxf)
    {
        ArgumentNullException.ThrowIfNull(dxf);

        var entities = dxf.Entities.Concat(dxf.Blocks.SelectMany(b => b.Entities));
        var sized = new List<PartSizeCandidate>();
        var holeOnly = new List<PartSizeCandidate>();

        foreach (var entity in entities)
        {
            string? text = null;
            double x = 0.0;
            double y = 0.0;

            switch (entity)
            {
                case DxfText t:
                    text = t.Value;
                    x = t.Location.X;
                    y = t.Location.Y;
                    break;
                case DxfMText m:
                    text = StripMText(m.Text);
                    x = m.InsertionPoint.X;
                    y = m.InsertionPoint.Y;
                    break;
            }

            var candidate = PartSizeParser.Parse(text);
            if (candidate is null)
            {
                continue;
            }

            candidate.X = x;
            candidate.Y = y;
            (candidate.Shape == PartShape.Unknown ? holeOnly : sized).Add(candidate);
        }

        foreach (var note in holeOnly)
        {
            var target = sized
                .Where(s => s.HoleCount is null && s.Shape is PartShape.Flange or PartShape.Plate)
                .OrderBy(s => Math.Pow((s.X ?? 0.0) - (note.X ?? 0.0), 2) + Math.Pow((s.Y ?? 0.0) - (note.Y ?? 0.0), 2))
                .FirstOrDefault();

            if (target is not null)
            {
                target.HoleCount = note.HoleCount;
                target.HoleDiameterMm = note.HoleDiameterMm;
                target.HolePcdMm = note.HolePcdMm;
            }
        }

        return sized;
    }

    private static string StripMText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = Regex.Replace(value, @"\\[A-Za-z][^;\\]*;", string.Empty);
        return text.Replace("{", string.Empty).Replace("}", string.Empty);
    }
}

/// <summary>Reads part sizes from design flow output lines such as "Back plate: %%C420 X 8 THK QTY 1".</summary>
public static class DesignFlowPartSizeSource
{
    public static IReadOnlyList<PartSizeCandidate> FromLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new List<PartSizeCandidate>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : null;
            var body = colon > 0 ? line[(colon + 1)..] : line;

            var candidate = PartSizeParser.Parse(body);
            if (candidate is null || candidate.Shape == PartShape.Unknown)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                candidate.Name = name.ToUpperInvariant();
            }

            candidate.Label = line.Trim();
            result.Add(candidate);
        }

        return result;
    }
}

/// <summary>Turns a parsed label into a request for the matching part sheet builder.</summary>
public static class PartSizeRequestFactory
{
    public static FlangePartRequest? ToFlange(PartSizeCandidate candidate, SheetFrameRequest frame, int itemNo)
    {
        if (candidate.Shape != PartShape.Flange || candidate.OuterDiameterMm is not > 0.0 || candidate.ThicknessMm is not > 0.0)
        {
            return null;
        }

        return new FlangePartRequest
        {
            Name = candidate.Name,
            Material = candidate.Material ?? "MS",
            ItemNo = itemNo,
            Qty = candidate.Qty ?? 1,
            OuterDiameterMm = candidate.OuterDiameterMm.Value,
            InnerDiameterMm = candidate.InnerDiameterMm ?? 0.0,
            ThicknessMm = candidate.ThicknessMm.Value,
            HoleCount = candidate.HoleCount ?? 0,
            HoleDiameterMm = candidate.HoleDiameterMm ?? 0.0,
            HolePcdMm = candidate.HolePcdMm ?? 0.0,
            Frame = frame
        };
    }

    public static RectangularPlateRequest? ToPlate(PartSizeCandidate candidate, SheetFrameRequest frame, int itemNo)
    {
        if (candidate.Shape != PartShape.Plate || candidate.LengthMm is not > 0.0 || candidate.WidthMm is not > 0.0 || candidate.ThicknessMm is not > 0.0)
        {
            return null;
        }

        return new RectangularPlateRequest
        {
            Name = candidate.Name,
            Material = candidate.Material ?? "MS",
            ItemNo = itemNo,
            Qty = candidate.Qty ?? 1,
            LengthMm = candidate.LengthMm.Value,
            WidthMm = candidate.WidthMm.Value,
            ThicknessMm = candidate.ThicknessMm.Value,
            Frame = frame
        };
    }

    public static RoundBarRequest? ToBar(PartSizeCandidate candidate, SheetFrameRequest frame, int itemNo)
    {
        if (candidate.Shape != PartShape.Bar || candidate.OuterDiameterMm is not > 0.0 || candidate.LengthMm is not > 0.0)
        {
            return null;
        }

        return new RoundBarRequest
        {
            Name = candidate.Name,
            Material = candidate.Material ?? "MS",
            ItemNo = itemNo,
            Qty = candidate.Qty ?? 1,
            DiameterMm = candidate.OuterDiameterMm.Value,
            LengthMm = candidate.LengthMm.Value,
            Frame = frame
        };
    }
}
