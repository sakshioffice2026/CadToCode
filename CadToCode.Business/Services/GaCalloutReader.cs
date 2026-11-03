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

        public bool FromTable { get; set; }

        public List<double> ThicknessValues { get; set; } = new();

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
            @"\b(?:m\.?\s?s\.?(?:\s?cr)?|s\.?s\.?[\s\-]?\d{3}[a-z]?|allu(?:m(?:inium)?)?\.?|en-?\d+|is\s?:?\s?2062|e250|grade\s?250)(?=\s*(?:[-:,;(.]|\d|$|flat\b|bar\b|ismc\b|dia\b|thk\b))",
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

            var entries = BuildEntries(geometry.Texts, report);

            foreach (var text in entries)
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

                var fromTable = string.Equals(text.EntityType, "TABLEROW", StringComparison.Ordinal);
                string? tableSpec = null;

                if (fromTable && !thickness.HasValue)
                {
                    var spec = TableSpecInTextRx.Match(joined);
                    if (spec.Success)
                    {
                        tableSpec = Regex.Replace(spec.Groups["s"].Value, @"\s+", string.Empty).ToUpperInvariant();
                    }
                }

                var part = new CalloutPart
                {
                    Name = name,
                    Material = material,
                    ThicknessMm = thickness,
                    Qty = qty,
                    QtyText = qtyText,
                    WeightKg = weight,
                    Section = section.Success ? Regex.Replace(section.Value.Trim(), @"\s+", " ").ToUpperInvariant() : tableSpec,
                    X = text.Position.X,
                    Y = text.Position.Y,
                    Layer = text.Layer,
                    Raw = joined,
                    FromTable = string.Equals(text.EntityType, "TABLEROW", StringComparison.Ordinal),
                    ThicknessValues = ReadThicknessValues(joined)
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

            ExpandCombinedParts(result.Parts, report);

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

        private sealed class RowCandidate
        {
            public List<GeometryText> Cells { get; init; } = new();

            public double SerialX { get; init; }
        }

        private static readonly Regex SerialCellRx = new(@"^\d{1,3}$", RegexOptions.Compiled);

        private static readonly Regex TableSpecRx = new(
            @"^(?:std|\d{1,3}\s*nb)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TableSpecInTextRx = new(
            @"\b(?<s>std|\d{1,3}\s*nb)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QtyCellRx = new(@"^\d{1,3}(?:\s*\+\s*\d{1,3})?$", RegexOptions.Compiled);

        private static readonly Regex MultiThicknessRx = new(
            @"(?<a>\d+(?:\.\d+)?)(?:\s*[,/]\s*\d+(?:\.\d+)?)+\s*(?:mm|m\.m\.)?\s*th(?:ic)?'?k\b\.?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HeaderCellRx = new(
            @"^(?:sr\.?\s*no\.?|s\.?\s*no\.?|item|description|material|qty\.?|quantity|remarks?|size|part\s*name)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static IReadOnlyList<GeometryText> BuildEntries(IReadOnlyList<GeometryText> texts, GenerationReport report)
        {
            var used = new HashSet<GeometryText>();
            var synthetic = new List<GeometryText>();

            var cells = texts
                .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !t.IsMultiLine)
                .Select(t => (Text: t, Clean: string.Join(" ", Clean(t.Text))))
                .Where(t => t.Clean.Length > 0 && t.Clean.Length <= 60)
                .OrderByDescending(t => t.Text.Position.Y)
                .ThenBy(t => t.Text.Position.X)
                .ToList();

            var rows = new List<List<(GeometryText Text, string Clean)>>();

            foreach (var cell in cells)
            {
                var tolerance = Math.Max(3.0, 0.35 * Math.Max(cell.Text.Height, 1.0));
                var current = rows.Count > 0 ? rows[^1] : null;

                if (current is not null && Math.Abs(current[0].Text.Position.Y - cell.Text.Position.Y) <= tolerance)
                {
                    current.Add(cell);
                }
                else
                {
                    rows.Add(new List<(GeometryText Text, string Clean)> { cell });
                }
            }

            var candidates = new List<(RowCandidate Row, List<(GeometryText Text, string Clean)> Cells)>();

            var segments = new List<List<(GeometryText Text, string Clean)>>();

            foreach (var row in rows)
            {
                var sorted = row.OrderBy(c => c.Text.Position.X).ToList();
                List<(GeometryText Text, string Clean)>? segment = null;

                foreach (var cell in sorted)
                {
                    if (segment is null || cell.Text.Position.X - segment[^1].Text.Position.X > 1200.0)
                    {
                        segment = new List<(GeometryText Text, string Clean)>();
                        segments.Add(segment);
                    }

                    segment.Add(cell);
                }
            }

            foreach (var row in segments)
            {
                if (row.Count < 4)
                {
                    continue;
                }

                var ordered = row.OrderBy(c => c.Text.Position.X).ToList();
                var first = ordered[0];
                var last = ordered[^1];

                if (!SerialCellRx.IsMatch(first.Clean) || !QtyCellRx.IsMatch(last.Clean))
                {
                    continue;
                }

                if (ordered[^1].Text.Position.X - ordered[0].Text.Position.X > 4000.0)
                {
                    continue;
                }

                var middle = ordered.Skip(1).Take(ordered.Count - 2).ToList();

                if (!middle.Any(c => c.Clean.Count(char.IsLetter) >= 3 && !HeaderCellRx.IsMatch(c.Clean)))
                {
                    continue;
                }

                candidates.Add((new RowCandidate { Cells = ordered.Select(c => c.Text).ToList(), SerialX = first.Text.Position.X }, ordered));
            }

            var tableRows = candidates
                .GroupBy(c => Math.Round(c.Row.SerialX / 10.0))
                .Where(g => g.Count() >= 3)
                .SelectMany(g => g)
                .ToList();

            foreach (var (row, ordered) in tableRows)
            {
                var lines = new List<string>();
                var middle = ordered.Skip(1).Take(ordered.Count - 2).ToList();

                foreach (var cell in middle)
                {
                    var text = cell.Clean;

                    if (HeaderCellRx.IsMatch(text))
                    {
                        continue;
                    }

                    if (TableSpecRx.IsMatch(text))
                    {
                        lines.Add(text);
                        continue;
                    }

                    if (ThicknessRx.IsMatch(text) || MultiThicknessRx.IsMatch(text))
                    {
                        lines.Add(text);
                        continue;
                    }

                    if (SectionRx.IsMatch(text) || MaterialRx.IsMatch(text) || text.Count(char.IsLetter) >= 3)
                    {
                        lines.Add(text);
                    }
                }

                lines.Add("QTY " + ordered[^1].Clean);

                var anchor = ordered[1].Text;

                synthetic.Add(new GeometryText
                {
                    Text = string.Join("\n", lines),
                    Position = anchor.Position,
                    Height = anchor.Height,
                    Layer = anchor.Layer,
                    EntityType = "TABLEROW",
                    IsMultiLine = true
                });

                foreach (var cell in ordered)
                {
                    used.Add(cell.Text);
                }
            }

            if (synthetic.Count > 0)
            {
                report.Info("CALLOUT_TABLE", "BOM", synthetic.Count.ToString(CultureInfo.InvariantCulture) + " BOM table row(s) were read from separate text cells.");
            }

            var entries = new List<GeometryText>(synthetic);
            entries.AddRange(texts.Where(t => !used.Contains(t)));
            return entries;
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

        private static readonly Regex ImpellerSetRx = new(
            @"impeller.*\bbp\s*/\s*bl\s*/\s*sh\b|\bbp\s*/\s*bl\s*/\s*sh\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InletOutletRx = new(
            @"\bi\s*/\s*l\s*&\s*o\s*/\s*l\b|inlet\s*&\s*outlet",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void ExpandCombinedParts(List<CalloutPart> parts, GenerationReport report)
        {
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                var part = parts[i];
                List<CalloutPart>? expanded = null;

                if (ImpellerSetRx.IsMatch(part.Raw))
                {
                    var names = new[] { "BACK PLATE", "BLADE", "SHROUD" };
                    expanded = new List<CalloutPart>();

                    for (var n = 0; n < names.Length; n++)
                    {
                        double? thickness = part.ThicknessValues.Count == names.Length
                            ? part.ThicknessValues[n]
                            : part.ThicknessMm;

                        expanded.Add(CloneFor(part, names[n], thickness, 1));
                    }
                }
                else if (InletOutletRx.IsMatch(part.Raw) && Regex.IsMatch(part.Raw, "flange", RegexOptions.IgnoreCase))
                {
                    var each = Math.Max(1, part.Qty / 2);

                    expanded = new List<CalloutPart>
                    {
                        CloneFor(part, "INLET FLANGE", part.ThicknessMm, each),
                        CloneFor(part, "OUTLET FLANGE", part.ThicknessMm, each)
                    };
                }

                if (expanded is null)
                {
                    continue;
                }

                parts.RemoveAt(i);
                parts.InsertRange(i, expanded);

                report.Info(
                    "CALLOUT_EXPANDED",
                    part.Name,
                    "One BOM row was split into " + string.Join(", ", expanded.Select(e => e.Name)) + ".");
            }
        }

        private static CalloutPart CloneFor(CalloutPart source, string name, double? thickness, int qty)
        {
            return new CalloutPart
            {
                Name = name,
                Material = source.Material,
                ThicknessMm = thickness,
                Qty = qty,
                QtyText = qty.ToString(CultureInfo.InvariantCulture),
                WeightKg = null,
                Section = source.Section,
                X = source.X,
                Y = source.Y,
                Layer = source.Layer,
                Raw = name + " | " + source.Raw,
                Duplicates = source.Duplicates,
                FromTable = source.FromTable,
                ThicknessValues = new List<double>()
            };
        }

        private static List<double> ReadThicknessValues(string text)
        {
            var values = new List<double>();
            var match = Regex.Match(
                text,
                @"(?<list>\d+(?:\.\d+)?(?:\s*[,/]\s*\d+(?:\.\d+)?)+)\s*(?:mm|m\.m\.)?\s*th(?:ic)?'?k\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return values;
            }

            foreach (var piece in Regex.Split(match.Groups["list"].Value, @"\s*[,/]\s*"))
            {
                if (ParseDouble(piece) is { } v && v > 0.0)
                {
                    values.Add(v);
                }
            }

            return values;
        }

        private static string StripFields(string line)
        {
            var s = MultiThicknessRx.Replace(line, " ");
            s = TableSpecInTextRx.Replace(s, " ");
            s = ThicknessRx.Replace(s, " ");
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
            var multi = MultiThicknessRx.Match(text);
            if (multi.Success)
            {
                var first = ParseDouble(multi.Groups["a"].Value);
                return first is > 0 ? first : null;
            }

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