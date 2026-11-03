using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Core.Rules;

namespace CadToCode.Business.Services
{
    public enum GaLengthSource
    {
        None = 0,
        Callout = 1,
        GaView = 2
    }

    public sealed class GaCutListRow
    {
        public int No { get; init; }

        public CalloutPart Part { get; init; } = new();

        public GaPartType Type { get; init; }

        public string Name { get; init; } = string.Empty;

        public string Section { get; init; } = string.Empty;

        public string Material { get; init; } = string.Empty;

        public int Qty { get; init; }

        public double? LengthMm { get; set; }

        public GaLengthSource LengthSource { get; set; }

        public double? KgPerMetre { get; set; }

        public double? TotalWeightKg { get; set; }

        public double? CalloutWeightKg { get; init; }

        public bool WeightMatchesCallout { get; set; }

        public string Description { get; set; } = string.Empty;

        public List<string> Notes { get; } = new();

        public bool IsComplete => LengthMm.HasValue && KgPerMetre.HasValue;
    }

    public sealed class GaBoughtOutRow
    {
        public int No { get; init; }

        public CalloutPart Part { get; init; } = new();

        public string Name { get; init; } = string.Empty;

        public int Qty { get; init; }

        public string? Material { get; init; }

        public string Specification { get; init; } = string.Empty;

        public string Remark { get; init; } = "Bought-out item; no DXF.";
    }

    public sealed class GaCutListResult
    {
        public List<GaCutListRow> CutRows { get; } = new();

        public List<GaBoughtOutRow> BoughtOutRows { get; } = new();

        public double TotalWeightKg => Math.Round(CutRows.Where(r => r.TotalWeightKg.HasValue).Sum(r => r.TotalWeightKg!.Value), 2);

        public int Incomplete => CutRows.Count(r => !r.IsComplete);

        public GaCutListRow? Find(CalloutPart part)
        {
            return CutRows.FirstOrDefault(r => ReferenceEquals(r.Part, part));
        }

        public GaBoughtOutRow? FindBoughtOut(CalloutPart part)
        {
            return BoughtOutRows.FirstOrDefault(r => ReferenceEquals(r.Part, part));
        }
    }

    public sealed class GaCutListBuilder
    {
        private const double MaxViewLengthMm = 20000.0;
        private const double MinLinkConfidence = 0.5;
        private const double MinChannelLengthMm = 100.0;
        private const string DefaultMaterial = "MS";

        private static readonly Dictionary<string, double> IsmcKgPerMetre = new(StringComparer.Ordinal)
        {
            ["ISMC 75X40"] = 7.14,
            ["ISMC 100X50"] = 9.56,
            ["ISMC 125X65"] = 12.7,
            ["ISMC 150X75"] = 16.4,
            ["ISMC 175X75"] = 19.1,
            ["ISMC 200X75"] = 22.1,
            ["ISMC 225X80"] = 25.9,
            ["ISMC 250X80"] = 30.6,
            ["ISMC 300X90"] = 35.8,
            ["ISMC 350X100"] = 42.1,
            ["ISMC 400X100"] = 49.4
        };

        private static readonly Regex ChannelRx = new(
            @"\b(?<k>ismc|ismb|isa|islb)\s*(?<a>\d+(?:\.\d+)?)\s*[x×]\s*(?<b>\d+(?:\.\d+)?)(?:\s*[x×]\s*(?<c>\d+(?:\.\d+)?))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FlatRx = new(
            @"\bflat\s*(?<w>\d+(?:\.\d+)?)\s*[x×]\s*(?<t>\d+(?:\.\d+)?)(?:\s*[x×]\s*(?<l>\d+(?:\.\d+)?))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BarRx = new(
            @"\b(?:bar\s*)?dia\.?\s*(?<d>\d+(?:\.\d+)?)(?:\s*[x×]\s*(?<l>\d+(?:\.\d+)?))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LengthRx = new(
            @"(?<a>\d{2,5}(?:\.\d+)?)\s*(?:mm)?\s*(?:lg\.?|long|length)\b|\b(?:lg|len|length|l)\s*[:=\-]\s*(?<b>\d{2,5}(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public GaCutListResult Build(
            ManufacturingRules rules,
            GaClassificationResult classification,
            CalloutLinkResult links,
            double mmPerUnit,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(classification);
            ArgumentNullException.ThrowIfNull(links);
            ArgumentNullException.ThrowIfNull(report);

            var result = new GaCutListResult();
            var mm = mmPerUnit > 0.0 ? mmPerUnit : 1.0;
            var cutNo = 0;
            var boughtNo = 0;

            foreach (var item in classification.Parts)
            {
                if (item.Family == GaPartFamily.BoughtOut)
                {
                    boughtNo++;
                    result.BoughtOutRows.Add(BuildBoughtOut(item.Part, boughtNo, report));
                    continue;
                }

                if (item.Family != GaPartFamily.RolledSection)
                {
                    continue;
                }

                cutNo++;
                result.CutRows.Add(BuildCutRow(rules, item, links, mm, cutNo, report));
            }

            report.Info(
                "CUTLIST_SUMMARY",
                "CUT LIST",
                result.CutRows.Count.ToString(CultureInfo.InvariantCulture) + " cut-list row(s), "
                + result.Incomplete.ToString(CultureInfo.InvariantCulture) + " incomplete, total "
                + result.TotalWeightKg.ToString("0.##", CultureInfo.InvariantCulture) + " kg; "
                + result.BoughtOutRows.Count.ToString(CultureInfo.InvariantCulture) + " bought-out item(s).");

            return result;
        }

        private static GaBoughtOutRow BuildBoughtOut(CalloutPart part, int no, GenerationReport report)
        {
            var row = new GaBoughtOutRow
            {
                No = no,
                Part = part,
                Name = part.Name.ToUpperInvariant(),
                Qty = Math.Max(1, part.Qty),
                Material = part.Material,
                Specification = part.Raw
            };

            report.Info(
                "BOUGHT_OUT_ROW",
                row.Name,
                "Listed as bought-out, QTY " + row.Qty.ToString(CultureInfo.InvariantCulture) + ". No DXF.");

            return row;
        }

        private static GaCutListRow BuildCutRow(
            ManufacturingRules rules,
            GaPartClassification classification,
            CalloutLinkResult links,
            double mm,
            int no,
            GenerationReport report)
        {
            var part = classification.Part;
            var qty = Math.Max(1, part.Qty);
            var section = (part.Section ?? string.Empty).Trim();
            var text = (section + " " + part.Name + " " + part.Raw).Trim();
            var material = string.IsNullOrWhiteSpace(part.Material) ? DefaultMaterial : part.Material.Trim();

            var row = new GaCutListRow
            {
                No = no,
                Part = part,
                Type = classification.Type,
                Name = part.Name.ToUpperInvariant(),
                Section = section.ToUpperInvariant(),
                Material = material,
                Qty = qty,
                CalloutWeightKg = part.WeightKg
            };

            if (string.IsNullOrWhiteSpace(part.Material))
            {
                row.Notes.Add("Material is not in the callout. " + DefaultMaterial + " was used.");
            }

            double? embeddedLength = null;
            string sectionKey;

            switch (classification.Type)
            {
                case GaPartType.Ismc:
                    sectionKey = ReadChannel(text, row, out embeddedLength);
                    break;

                case GaPartType.FlatBar:
                    sectionKey = ReadFlat(text, rules, row, out embeddedLength);
                    break;

                case GaPartType.RoundBar:
                    sectionKey = ReadBar(text, rules, row, out embeddedLength);
                    break;

                default:
                    sectionKey = section.ToUpperInvariant();
                    row.Notes.Add("No weight rule for " + classification.Type.ToString() + ".");
                    break;
            }

            if (embeddedLength.HasValue)
            {
                row.LengthMm = embeddedLength;
                row.LengthSource = GaLengthSource.Callout;
            }
            else
            {
                var hinted = ReadLength(part.Raw);

                if (hinted.HasValue)
                {
                    row.LengthMm = hinted;
                    row.LengthSource = GaLengthSource.Callout;
                }
                else
                {
                    var measured = MeasureFromView(part, links, mm);

                    if (measured.HasValue)
                    {
                        row.LengthMm = measured;
                        row.LengthSource = GaLengthSource.GaView;
                        row.Notes.Add("Length " + Fmt(measured.Value) + " mm was measured from the linked GA view. Please verify.");
                    }
                }
            }

            row.Description = (sectionKey.Length > 0 ? sectionKey : row.Name)
                + (row.LengthMm.HasValue ? " X " + Fmt(row.LengthMm.Value) + " LG" : string.Empty);

            if (!row.LengthMm.HasValue)
            {
                row.Notes.Add("Cut length is not in the callout and could not be measured from the GA.");
                report.Warn("CUTLIST_NO_LENGTH", row.Name, "Rolled section " + sectionKey + " has no cut length.");
            }

            if (!row.KgPerMetre.HasValue)
            {
                report.Warn("CUTLIST_NO_UNIT_WEIGHT", row.Name, "No weight per metre for " + (sectionKey.Length > 0 ? sectionKey : "this section") + ".");
            }

            if (row.IsComplete)
            {
                row.TotalWeightKg = Math.Round(row.KgPerMetre!.Value * row.LengthMm!.Value / 1000.0 * qty, 2);
                CompareWeight(rules, row, report);
            }

            report.Info(
                "CUTLIST_ROW",
                row.Name,
                row.Description + ", QTY " + qty.ToString(CultureInfo.InvariantCulture) + ", MAT " + material
                + (row.TotalWeightKg.HasValue ? ", " + Fmt(row.TotalWeightKg.Value) + " kg" : string.Empty) + ".");

            return row;
        }

        private static string ReadChannel(string text, GaCutListRow row, out double? embeddedLength)
        {
            embeddedLength = null;
            var match = ChannelRx.Match(text);

            if (!match.Success)
            {
                row.Notes.Add("Section size could not be read.");
                return string.Empty;
            }

            var kind = match.Groups["k"].Value.ToUpperInvariant();
            var key = kind + " " + Number(match.Groups["a"].Value) + "X" + Number(match.Groups["b"].Value);

            if (match.Groups["c"].Success && ParseDouble(match.Groups["c"].Value) is double third && third >= MinChannelLengthMm)
            {
                embeddedLength = third;
            }

            if (IsmcKgPerMetre.TryGetValue(key, out var kg))
            {
                row.KgPerMetre = kg;
                row.Notes.Add("Weight per metre " + Fmt(kg) + " kg is a nominal table value. Please verify.");
            }

            return key;
        }

        private static string ReadFlat(string text, ManufacturingRules rules, GaCutListRow row, out double? embeddedLength)
        {
            embeddedLength = null;
            var match = FlatRx.Match(text);

            if (!match.Success)
            {
                row.Notes.Add("Flat size could not be read.");
                return string.Empty;
            }

            var width = ParseDouble(match.Groups["w"].Value);
            var thickness = ParseDouble(match.Groups["t"].Value);

            if (match.Groups["l"].Success)
            {
                embeddedLength = ParseDouble(match.Groups["l"].Value);
            }

            if (width is > 0.0 && thickness is > 0.0)
            {
                row.KgPerMetre = Math.Round(width.Value * thickness.Value * rules.Weight.DensityKgPerM3 / 1e6, 3);
            }

            return "FLAT " + Number(match.Groups["w"].Value) + "X" + Number(match.Groups["t"].Value);
        }

        private static string ReadBar(string text, ManufacturingRules rules, GaCutListRow row, out double? embeddedLength)
        {
            embeddedLength = null;
            var match = BarRx.Match(text);

            if (!match.Success)
            {
                row.Notes.Add("Bar diameter could not be read.");
                return string.Empty;
            }

            var diameter = ParseDouble(match.Groups["d"].Value);

            if (match.Groups["l"].Success)
            {
                embeddedLength = ParseDouble(match.Groups["l"].Value);
            }

            if (diameter is > 0.0)
            {
                row.KgPerMetre = Math.Round(Math.PI / 4.0 * diameter.Value * diameter.Value * rules.Weight.DensityKgPerM3 / 1e6, 3);
            }

            return "BAR %%C" + Number(match.Groups["d"].Value);
        }

        private static double? ReadLength(string raw)
        {
            var match = LengthRx.Match(raw ?? string.Empty);

            if (!match.Success)
            {
                return null;
            }

            var value = ParseDouble(match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value);
            return value is > 0.0 ? value : null;
        }

        private static double? MeasureFromView(CalloutPart part, CalloutLinkResult links, double mm)
        {
            var link = links.Links.FirstOrDefault(l => ReferenceEquals(l.Part, part));

            if (link is null || link.Confidence < MinLinkConfidence)
            {
                return null;
            }

            var length = Math.Max(link.Node.Bounds.Width, link.Node.Bounds.Height) * mm;

            if (length <= 0.0 || length > MaxViewLengthMm)
            {
                return null;
            }

            return Math.Round(length, 1);
        }

        private static void CompareWeight(ManufacturingRules rules, GaCutListRow row, GenerationReport report)
        {
            if (!row.CalloutWeightKg.HasValue || row.CalloutWeightKg.Value <= 0.0 || !row.TotalWeightKg.HasValue)
            {
                return;
            }

            var reference = row.CalloutWeightKg.Value;
            var total = row.TotalWeightKg.Value;
            var each = qtyEach(total, row.Qty);

            if (rules.IsWithinReference(total, reference) || rules.IsWithinReference(each, reference))
            {
                row.WeightMatchesCallout = true;
                return;
            }

            row.WeightMatchesCallout = false;

            report.Warn(
                "CUTLIST_WEIGHT_DIFFERS",
                row.Name,
                "Calculated " + Fmt(total) + " kg (total) differs from callout weight " + Fmt(reference) + " kg.");
        }

        private static double qtyEach(double total, int qty)
        {
            return qty > 0 ? total / qty : total;
        }

        private static string Number(string raw)
        {
            var value = ParseDouble(raw);
            return value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : raw;
        }

        private static double? ParseDouble(string raw)
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
