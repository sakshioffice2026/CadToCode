using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public enum PartDisposition
    {
        DxfGenerated = 0,
        CutList = 1,
        BoughtOut = 2,
        Unmatched = 3
    }

    public sealed class CompletenessRow
    {
        public CalloutPart Part { get; init; } = new();

        public PartDisposition Disposition { get; init; }

        public string? FileName { get; init; }

        public string Reason { get; init; } = string.Empty;
    }

    public sealed class WeightCheck
    {
        public string Label { get; init; } = string.Empty;

        public double DrawingTotalKg { get; init; }

        public double CalloutSumKg { get; init; }

        public double DifferencePercent { get; init; }

        public bool Matches { get; init; }
    }

    public sealed class CompletenessResult
    {
        public List<CompletenessRow> Rows { get; } = new();

        public List<WeightCheck> WeightChecks { get; } = new();

        public List<string> UnusedFiles { get; } = new();

        public int Generated => Rows.Count(r => r.Disposition == PartDisposition.DxfGenerated);

        public int CutList => Rows.Count(r => r.Disposition == PartDisposition.CutList);

        public int BoughtOut => Rows.Count(r => r.Disposition == PartDisposition.BoughtOut);

        public int Unmatched => Rows.Count(r => r.Disposition == PartDisposition.Unmatched);

        public bool IsComplete => Unmatched == 0 && UnusedFiles.Count == 0;

        /// <summary>Reasons the package must not be released. Weight differences are warnings, not blockers.</summary>
        public List<string> BlockingReasons(IReadOnlyCollection<string>? excluded = null)
        {
            var reasons = new List<string>();

            foreach (var row in Rows.Where(r => r.Disposition == PartDisposition.Unmatched))
            {
                if (excluded is not null && excluded.Contains(row.Part.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                reasons.Add("No usable drawing for " + row.Part.Name + ": " + row.Reason);
            }

            foreach (var file in UnusedFiles)
            {
                reasons.Add("Drawing " + file + " has no matching callout.");
            }

            return reasons;
        }

        public List<string> ToMessages()
        {
            var list = new List<string>
            {
                "Callout parts " + Rows.Count.ToString(CultureInfo.InvariantCulture)
                + ": DXF " + Generated.ToString(CultureInfo.InvariantCulture)
                + ", cut list " + CutList.ToString(CultureInfo.InvariantCulture)
                + ", bought-out " + BoughtOut.ToString(CultureInfo.InvariantCulture)
                + ", unmatched " + Unmatched.ToString(CultureInfo.InvariantCulture) + "."
            };

            foreach (var row in Rows.Where(r => r.Disposition == PartDisposition.Unmatched))
            {
                list.Add("UNMATCHED " + row.Part.Name + ": " + row.Reason);
            }

            foreach (var file in UnusedFiles)
            {
                list.Add("FILE WITHOUT CALLOUT " + file);
            }

            foreach (var check in WeightChecks)
            {
                list.Add((check.Matches ? "WEIGHT OK " : "WEIGHT DIFFERS ") + check.Label
                    + ": drawing " + check.DrawingTotalKg.ToString("0.##", CultureInfo.InvariantCulture)
                    + " kg, callouts " + check.CalloutSumKg.ToString("0.##", CultureInfo.InvariantCulture)
                    + " kg (" + check.DifferencePercent.ToString("0.#", CultureInfo.InvariantCulture) + " %).");
            }

            return list;
        }
    }

    public sealed class GaCompletenessChecker
    {
        private const double WeightTolerancePercent = 5.0;
        private const double MinMatchScore = 0.6;

        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "MS", "M", "S", "THK", "MM", "QTY", "NO", "NOS", "OF", "AND", "THE", "FOR", "AT", "WITH"
        };

        private static readonly Regex BoughtOutRx = new(
            @"bearing|motor|bellow|bolt|\bnut\b|hex|wire\s?mesh|rockwool|plummer|coupling|actuator|gasket|washer\s*(?:m\d+)|frp|silencer media",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CutListRx = new(
            @"\b(?:ismc|ismb|isa|islb)\b|\bflat\s*\d|\bbar\s*dia|\bpipe\b|\bchannel\b|\bangle\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public CompletenessResult Check(
            CalloutReadResult callouts,
            IReadOnlyList<PartManufacturingFileDto> files,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(callouts);
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(report);

            var result = new CompletenessResult();
            var fileUsed = new bool[files.Count];
            var assigned = new Dictionary<int, int>();

            var fabricated = new List<int>();

            for (var i = 0; i < callouts.Parts.Count; i++)
            {
                var part = callouts.Parts[i];
                var text = part.Name + " " + (part.Section ?? string.Empty);

                if (BoughtOutRx.IsMatch(part.Name) || GaPartNameRules.IsConsumable(part.Name))
                {
                    result.Rows.Add(new CompletenessRow
                    {
                        Part = part,
                        Disposition = PartDisposition.BoughtOut,
                        Reason = "Bought-out item; no DXF."
                    });
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(part.Section) && CutListRx.IsMatch(text))
                {
                    result.Rows.Add(new CompletenessRow
                    {
                        Part = part,
                        Disposition = PartDisposition.CutList,
                        Reason = "Rolled section " + part.Section + "; listed in cut list."
                    });
                    continue;
                }

                fabricated.Add(i);
            }

            var candidates = new List<(int Part, int File, double Score)>();

            foreach (var p in fabricated)
            {
                var part = callouts.Parts[p];

                for (var f = 0; f < files.Count; f++)
                {
                    var score = NameScore(part.Name, files[f].PartName);
                    if (score < MinMatchScore)
                    {
                        continue;
                    }

                    var fileThickness = files[f].Thickness.HasValue ? (double?)files[f].Thickness.Value : null;

                    if (part.ThicknessMm.HasValue && fileThickness.HasValue)
                    {
                        if (Math.Abs(part.ThicknessMm.Value - fileThickness.Value) > 0.01)
                        {
                            continue;
                        }

                        score += 0.1;
                    }

                    candidates.Add((p, f, score));
                }
            }

            foreach (var c in candidates.OrderByDescending(c => c.Score))
            {
                if (assigned.ContainsKey(c.Part) || fileUsed[c.File])
                {
                    continue;
                }

                assigned[c.Part] = c.File;
                fileUsed[c.File] = true;
            }

            foreach (var p in fabricated)
            {
                var part = callouts.Parts[p];

                if (assigned.TryGetValue(p, out var f) && files[f].Content is { Length: > 0 })
                {
                    var file = files[f];
                    var reason = "DXF generated.";

                    if (file.Quantity < part.Qty)
                    {
                        reason = "DXF generated; sheet quantity " + file.Quantity.ToString(CultureInfo.InvariantCulture)
                            + " is less than callout quantity " + part.Qty.ToString(CultureInfo.InvariantCulture) + ".";
                        report.Warn("COMPLETENESS_QTY", part.Name, reason);
                    }

                    result.Rows.Add(new CompletenessRow
                    {
                        Part = part,
                        Disposition = PartDisposition.DxfGenerated,
                        FileName = file.FileName,
                        Reason = reason
                    });
                    continue;
                }

                result.Rows.Add(new CompletenessRow
                {
                    Part = part,
                    Disposition = PartDisposition.Unmatched,
                    Reason = assigned.ContainsKey(p)
                        ? "The generated DXF sheet is empty."
                        : "No DXF sheet was generated for this callout."
                });

                report.Error(
                    "COMPLETENESS_UNMATCHED",
                    part.Name,
                    (assigned.ContainsKey(p) ? "The generated DXF sheet is empty (" : "No DXF sheet was generated for this callout (") + Brief(part) + ").");
            }

            for (var f = 0; f < files.Count; f++)
            {
                if (!fileUsed[f])
                {
                    result.UnusedFiles.Add(files[f].FileName);
                    report.Warn("COMPLETENESS_NO_CALLOUT", files[f].PartName, "File " + files[f].FileName + " has no matching callout.");
                }
            }

            var sum = callouts.Parts.Where(p => p.WeightKg.HasValue).Sum(p => p.WeightKg!.Value);

            foreach (var total in callouts.Totals)
            {
                if (total.Kg <= 0.0)
                {
                    continue;
                }

                var difference = Math.Abs(sum - total.Kg) / total.Kg * 100.0;
                var check = new WeightCheck
                {
                    Label = total.Label,
                    DrawingTotalKg = total.Kg,
                    CalloutSumKg = Math.Round(sum, 2),
                    DifferencePercent = Math.Round(difference, 1),
                    Matches = difference <= WeightTolerancePercent
                };

                result.WeightChecks.Add(check);

                if (!check.Matches)
                {
                    report.Warn(
                        "COMPLETENESS_WEIGHT",
                        total.Label,
                        "Drawing total " + total.Kg.ToString("0.##", CultureInfo.InvariantCulture)
                        + " kg, callout weights add to " + check.CalloutSumKg.ToString("0.##", CultureInfo.InvariantCulture)
                        + " kg. Some parts may be missing from the callouts.");
                }
            }

            report.Info(
                "COMPLETENESS_SUMMARY",
                "ALL PARTS",
                "Callout parts " + result.Rows.Count.ToString(CultureInfo.InvariantCulture)
                + ": DXF " + result.Generated.ToString(CultureInfo.InvariantCulture)
                + ", cut list " + result.CutList.ToString(CultureInfo.InvariantCulture)
                + ", bought-out " + result.BoughtOut.ToString(CultureInfo.InvariantCulture)
                + ", unmatched " + result.Unmatched.ToString(CultureInfo.InvariantCulture) + ".");

            return result;
        }

        private static double NameScore(string a, string b)
        {
            var ta = Tokens(a);
            var tb = Tokens(b);

            if (ta.Count == 0 || tb.Count == 0)
            {
                return 0.0;
            }

            var common = ta.Intersect(tb, StringComparer.Ordinal).Count();
            if (common == 0)
            {
                return 0.0;
            }

            return (double)common / Math.Min(ta.Count, tb.Count);
        }

        private static List<string> Tokens(string value)
        {
            var normal = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();

            return normal
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !StopWords.Contains(t) && !Regex.IsMatch(t, @"^\d+$"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string Brief(CalloutPart part)
        {
            var items = new List<string>
            {
                "THK " + (part.ThicknessMm.HasValue ? part.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-"),
                "QTY " + (part.QtyText ?? part.Qty.ToString(CultureInfo.InvariantCulture))
            };

            if (part.WeightKg.HasValue)
            {
                items.Add("WT " + part.WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) + " kg");
            }

            return string.Join(", ", items);
        }
    }
}
