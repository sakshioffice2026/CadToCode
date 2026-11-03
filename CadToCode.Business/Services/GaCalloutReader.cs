using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class CalloutPart
    {
        public string Name { get; set; } = string.Empty;

        public string? Material { get; set; }

        public double? ThicknessMm { get; set; }

        public int Qty { get; set; } = 1;

        public string? QtyText { get; set; }

        public double? WeightKg { get; set; }

        public string? Section { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public string? Layer { get; set; }

        public string Raw { get; set; } = string.Empty;

        public int Duplicates { get; set; }

        public PartBomItem ToBomItem()
        {
            var remarks = new List<string> { "Read from drawing callout." };

            if (WeightKg.HasValue)
            {
                remarks.Add("Drawing weight " + WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) + " kg.");
            }

            if (!string.IsNullOrWhiteSpace(Section))
            {
                remarks.Add("Section " + Section + ".");
            }

            return new PartBomItem
            {
                Name = Name.ToUpperInvariant(),
                Kind = BomPartKind.Unknown,
                Material = Material,
                ThicknessMm = ThicknessMm,
                Qty = Qty,
                Remarks = string.Join(" ", remarks)
            };
        }
    }

    public sealed class CalloutHoleNote
    {
        public int Count { get; set; }

        public double DiameterMm { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public string Raw { get; set; } = string.Empty;
    }

    public sealed class CalloutTotal
    {
        public string Label { get; set; } = string.Empty;

        public double Kg { get; set; }
    }

    public sealed class CalloutReadResult
    {
        public List<CalloutPart> Parts { get; } = new();

        public List<CalloutHoleNote> HoleNotes { get; } = new();

        public List<CalloutTotal> Totals { get; } = new();

        public int TextBlocksRead { get; set; }

        public int DuplicatesMerged { get; set; }
    }

    public sealed class GaCalloutReader
    {
        private const int MaxBlockLength = 220;

        private static readonly Regex ThicknessRx = new(
            @"(?<v>\d+(?:\.\d+)?)\s*(?:mm|m\.m\.)?\s*th(?:ic)?'?k\b\.?|\bthk\.?\s*[:=\-]?\s*(?<w>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QtyRx = new(
            @"\bqty\.?\s*[:=\-]?\s*(?<a>\d{1,3})(?:\s*\+\s*(?<b>\d{1,3}))?(?:\s*(?:nos?\.?|off))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QtyTrailingRx = new(
            @"\b(?<a>\d{1,3})\s*(?:nos?\.?|off)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WeightRx = new(
            @"\bw\.?\s?t\.?\s*[:=\-]?\s*(?<v>\d+(?:\.\d+)?)\s*(?<u>k\.?\s?g\.?|g\b)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MaterialRx = new(
            @"\b(?:m\.?\s?s\.?(?:\s?cr)?|s\.?s\.?\s?\d{3}[a-z]?|allu(?:m(?:inium)?)?\.?|en-?\d+|is\s?:?\s?2062|e250|grade\s?250)(?=\s*(?:[-:,;(.]|\d|$|flat\b|bar\b|ismc\b|dia\b|thk\b))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SectionRx = new(
            @"(?:\b(?:ismc|ismb|isa|islb)\s*\d+\s*[x×]\s*\d+(?:\s*[x×]\s*\d+)?|\b(?:m\.?\s?s\.?\s*)?flat\s*\d+\s*[x×]\s*\d+|\b(?:m\.?\s?s\.?\s*)?bar\s*dia\s*\d+(?:\.\d+)?|\bdia\s*\d+(?:\.\d+)?\s*[x×]\s*\d+(?:\.\d+)?\s*(?:thk|lg\.?|long)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HolesRx = new(
            @"(?<n>\d+)\s*holes?\s*[,:]?\s*(?:Ø|%%c|dia\.?)?\s*(?<d>\d+(?:\.\d+)?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TotalRx = new(
            @"total\s+(?<l>[a-z ]+?)\s+weight\s*[:\-]?\s*(?<v>\d+(?:\.\d+)?)\s*kg",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IgnoreNameRx = new(
            @"property of|permission|note\b|drawn|checked|approved|designed|revision|scale|material code|total|\bdate\b|projection|tolerance|deviation|do not|unspecified|sharp corner|made in|model|rev\. no|drg\.? no|job no",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumberingRx = new(@"^\s*\d{1,2}\s*\)\s*", RegexOptions.Compiled);

        public CalloutReadResult Read(ExtractedGeometry geometry, GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(report);

            var result = new CalloutReadResult();
            var byKey = new Dictionary<string, CalloutPart>(StringComparer.Ordinal);

            foreach (var text in geometry.Texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text))
                {
                    continue;
                }

                result.TextBlocksRead++;

                var lines = Clean(text.Text);
                if (lines.Count == 0)
                {
                    continue;
                }

                var joined = string.Join(" ", lines);
                if (joined.Length > MaxBlockLength)
                {
                    continue;
                }

                var total = TotalRx.Match(joined);
                if (total.Success)
                {
                    result.Totals.Add(new CalloutTotal
                    {
                        Label = total.Groups["l"].Value.Trim().ToUpperInvariant(),
                        Kg = ParseDouble(total.Groups["v"].Value) ?? 0.0
                    });
                    continue;
                }

                var thickness = ReadThickness(joined);
                var weight = ReadWeight(joined);
                var material = ReadMaterial(joined);
                var section = SectionRx.Match(joined);
                var qty = ReadQuantity(joined, out var qtyText);

                var name = ReadName(lines);

                var holes = HolesRx.Match(joined);
                if (holes.Success && holes.Groups["d"].Success && string.IsNullOrWhiteSpace(name) && !thickness.HasValue)
                {
                    result.HoleNotes.Add(new CalloutHoleNote
                    {
                        Count = int.TryParse(holes.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                        DiameterMm = ParseDouble(holes.Groups["d"].Value) ?? 0.0,
                        X = text.Position.X,
                        Y = text.Position.Y,
                        Raw = joined
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 80 || !name.Any(char.IsLetter))
                {
                    continue;
                }

                if (IgnoreNameRx.IsMatch(name))
                {
                    continue;
                }

                var hasData = thickness.HasValue || weight.HasValue || section.Success || qtyText is not null;
                if (!hasData)
                {
                    continue;
                }

                var part = new CalloutPart
                {
                    Name = name,
                    Material = material,
                    ThicknessMm = thickness,
                    Qty = qty,
                    QtyText = qtyText,
                    WeightKg = weight,
                    Section = section.Success ? Regex.Replace(section.Value.Trim(), @"\s+", " ").ToUpperInvariant() : null,
                    X = text.Position.X,
                    Y = text.Position.Y,
                    Layer = text.Layer,
                    Raw = joined
                };

                var key = string.Join("|",
                    part.Name.ToUpperInvariant(),
                    part.ThicknessMm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                    part.Qty.ToString(CultureInfo.InvariantCulture),
                    part.WeightKg?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-",
                    part.Section ?? "-");

                if (byKey.TryGetValue(key, out var existing))
                {
                    existing.Duplicates++;
                    result.DuplicatesMerged++;
                    continue;
                }

                byKey[key] = part;
                result.Parts.Add(part);
            }

            foreach (var part in result.Parts)
            {
                report.Info("CALLOUT_PART", part.Name, Describe(part));

                if (!part.ThicknessMm.HasValue && string.IsNullOrWhiteSpace(part.Section))
                {
                    report.Warn("CALLOUT_NO_THICKNESS", part.Name, "The callout gives no thickness and no section size.");
                }

                if (string.IsNullOrWhiteSpace(part.Material))
                {
                    report.Info("CALLOUT_NO_MATERIAL", part.Name, "The callout gives no material.");
                }
            }

            foreach (var total in result.Totals)
            {
                report.Info("CALLOUT_TOTAL", total.Label, "Drawing states total weight " + total.Kg.ToString("0.##", CultureInfo.InvariantCulture) + " kg.");
            }

            return result;
        }

        private static string ReadName(List<string> lines)
        {
            var names = new List<string>();

            foreach (var line in lines)
            {
                var rest = StripFields(line);
                rest = NumberingRx.Replace(rest, string.Empty);
                rest = Regex.Replace(rest, @"^[\s\-:,.;()+]+|[\s\-:,.;()+]+$", string.Empty);
                rest = Regex.Replace(rest, @"\s+", " ").Trim();

                if (rest.Length > 0 && rest.Any(char.IsLetter))
                {
                    names.Add(rest);
                }
            }

            return string.Join(" ", names).Trim();
        }

        private static string StripFields(string line)
        {
            var s = ThicknessRx.Replace(line, " ");
            s = QtyRx.Replace(s, " ");
            s = QtyTrailingRx.Replace(s, " ");
            s = WeightRx.Replace(s, " ");
            s = SectionRx.Replace(s, " ");
            s = MaterialRx.Replace(s, " ");
            s = Regex.Replace(s, @"\bmaterial\s*[:\-]*", " ", RegexOptions.IgnoreCase);
            return s;
        }

        private static double? ReadThickness(string text)
        {
            var match = ThicknessRx.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var raw = match.Groups["v"].Success ? match.Groups["v"].Value : match.Groups["w"].Value;
            var value = ParseDouble(raw);
            return value is > 0 ? value : null;
        }

        private static double? ReadWeight(string text)
        {
            var match = WeightRx.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var value = ParseDouble(match.Groups["v"].Value);
            if (value is null)
            {
                return null;
            }

            var unit = match.Groups["u"].Value.Replace(".", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            return unit == "g" ? value / 1000.0 : value;
        }

        private static string? ReadMaterial(string text)
        {
            var match = MaterialRx.Match(text);
            if (!match.Success)
            {
                return null;
            }

            return match.Value.Replace(".", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private static int ReadQuantity(string text, out string? qtyText)
        {
            qtyText = null;

            var match = QtyRx.Match(text);
            if (match.Success)
            {
                var a = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
                if (match.Groups["b"].Success)
                {
                    var b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
                    qtyText = a.ToString(CultureInfo.InvariantCulture) + "+" + b.ToString(CultureInfo.InvariantCulture);
                    return Math.Max(1, a + b);
                }

                qtyText = a.ToString(CultureInfo.InvariantCulture);
                return Math.Max(1, a);
            }

            var trailing = QtyTrailingRx.Match(text);
            if (trailing.Success)
            {
                var a = int.Parse(trailing.Groups["a"].Value, CultureInfo.InvariantCulture);
                qtyText = a.ToString(CultureInfo.InvariantCulture);
                return Math.Max(1, a);
            }

            return 1;
        }

        private static List<string> Clean(string raw)
        {
            var s = raw;

            s = Regex.Replace(s, @"\\[Ff][^;]*;", string.Empty);
            s = Regex.Replace(s, @"\\p[^;]*;", string.Empty);
            s = Regex.Replace(s, @"\\[AaCcHhQqTtWwSs][^;\\{}]*;", string.Empty);
            s = s.Replace("\\P", "\n").Replace("\\X", "\n").Replace("\\~", " ");
            s = Regex.Replace(s, @"\\[LlOoKk]", string.Empty);
            s = s.Replace("{", string.Empty).Replace("}", string.Empty);
            s = Regex.Replace(s, @"%%[cC]", "Ø");
            s = Regex.Replace(s, @"%%[pP]", "±");
            s = Regex.Replace(s, @"%%[dD]", "°");
            s = Regex.Replace(s, @"%%[uUoO]", string.Empty);
            s = s.Replace("∅", "Ø").Replace("\r", "\n");

            var lines = new List<string>();
            foreach (var part in s.Split('\n'))
            {
                var line = Regex.Replace(part, @"\s+", " ").Trim();
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        private static string Describe(CalloutPart part)
        {
            var items = new List<string>();

            items.Add("MAT " + (part.Material ?? "not stated"));
            items.Add("THK " + (part.ThicknessMm.HasValue ? part.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture) : "not stated"));
            items.Add("QTY " + (part.QtyText ?? part.Qty.ToString(CultureInfo.InvariantCulture)));

            if (part.WeightKg.HasValue)
            {
                items.Add("WT " + part.WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) + " kg");
            }

            if (!string.IsNullOrWhiteSpace(part.Section))
            {
                items.Add("SECTION " + part.Section);
            }

            if (part.Duplicates > 0)
            {
                items.Add(part.Duplicates.ToString(CultureInfo.InvariantCulture) + " duplicate callout(s) merged");
            }

            return string.Join(", ", items);
        }

        private static double? ParseDouble(string raw)
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
    }
}
