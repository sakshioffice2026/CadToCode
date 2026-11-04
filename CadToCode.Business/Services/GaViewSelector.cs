using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class ViewSelection
    {
        public CalloutPart Part { get; init; } = new();

        public AssemblyNode Node { get; init; } = new();

        public double Score { get; init; }

        public string Reason { get; init; } = string.Empty;

        public int CandidateCount { get; init; }

        public bool EdgeOnOnly { get; init; }

        public bool IsEdgeOn { get; init; }

        public bool IsWholeView { get; init; }

        public double WidthMm { get; init; }

        public double HeightMm { get; init; }
    }

    public sealed class ViewSelectionResult
    {
        public List<ViewSelection> Selections { get; } = new();

        public List<CalloutPart> Failed { get; } = new();

        /// <summary>Every scored view for each part, best first. The chosen view is still in Selections.</summary>
        public Dictionary<string, List<ViewSelection>> ViewsByPart { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class GaViewSelector
    {
        private const double DensityKgPerMm3 = 7.85e-6;
        private const double EdgeOnThicknessFactor = 2.5;
        private const double EdgeOnAspect = 8.0;
        private const int WholeViewEntityLimit = 400;
        private const double WholeViewAreaFraction = 0.25;
        private const double MaxNodeAreaFraction = 0.6;

        private static readonly Regex BoughtOutRx = new(
            @"bearing|motor|bellow|bolt|\bnut\b|hex|wire\s?mesh|rockwool|plummer|coupling|actuator|gasket",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RoundNameRx = new(
            @"ring|flange|disc|disk|hub|washer|shroud|back\s?plate|cone|cap",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FlatNameRx = new(
            @"plate|stiffener|stiffner|base|door|bracket|strip|rib|cutoff|spacer|hook|extractor",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ExcludedLayerMarkers =
        {
            "title", "border", "paper", "viewport", "table", "defpoints", "dim", "text", "note"
        };

        public ViewSelectionResult Select(
            CalloutLinkResult links,
            IReadOnlyList<AssemblyNode> nodes,
            ExtractedGeometry geometry,
            double mmPerUnit,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(links);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(report);

            var result = new ViewSelectionResult();
            var mm = mmPerUnit > 0.0 ? mmPerUnit : 1.0;
            var drawingArea = geometry.Bounds is null ? 0.0 : geometry.Bounds.Width * geometry.Bounds.Height;

            foreach (var link in links.Links)
            {
                var part = link.Part;

                if (BoughtOutRx.IsMatch(part.Name) || GaPartNameRules.IsConsumable(part.Name) || !string.IsNullOrWhiteSpace(part.Section))
                {
                    continue;
                }

                var candidates = GatherCandidates(link, nodes, drawingArea);
                if (candidates.Count == 0)
                {
                    result.Failed.Add(part);
                    report.Error("VIEW_NONE", part.Name, "No usable view was found for this part.");
                    continue;
                }

                var scoredAll = new List<ViewSelection>();
                var allEdgeOn = true;

                foreach (var node in candidates)
                {
                    var scored = Score(part, link, node, mm, drawingArea, candidates.Count, out var edgeOn);
                    scoredAll.Add(scored);

                    if (!edgeOn)
                    {
                        allEdgeOn = false;
                    }
                }

                result.ViewsByPart[part.Name] = scoredAll.OrderByDescending(s => s.Score).ToList();

                var preferred = scoredAll.Where(s => !s.IsEdgeOn && !s.IsWholeView).ToList();

                if (preferred.Count == 0)
                {
                    preferred = scoredAll.Where(s => !s.IsEdgeOn).ToList();
                }

                if (preferred.Count == 0)
                {
                    preferred = scoredAll;
                }

                var best = preferred.OrderByDescending(s => s.Score).FirstOrDefault();

                if (best is null)
                {
                    result.Failed.Add(part);
                    report.Error("VIEW_NONE", part.Name, "No usable view was found for this part.");
                    continue;
                }

                var final = new ViewSelection
                {
                    Part = best.Part,
                    Node = best.Node,
                    Score = best.Score,
                    Reason = best.Reason,
                    CandidateCount = candidates.Count,
                    EdgeOnOnly = allEdgeOn,
                    WidthMm = best.WidthMm,
                    HeightMm = best.HeightMm,
                    IsEdgeOn = best.IsEdgeOn,
                    IsWholeView = best.IsWholeView
                };

                if (allEdgeOn)
                {
                    report.Warn(
                        "VIEW_EDGE_ON_ONLY",
                        part.Name,
                        "Only edge-on views were found (best " + final.WidthMm.ToString("0.#", CultureInfo.InvariantCulture)
                        + " x " + final.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm). The true shape must come from the BOM or a flat pattern.");
                }

                if (final.IsWholeView)
                {
                    report.Warn(
                        "VIEW_WHOLE_VIEW_ONLY",
                        part.Name,
                        "Only a whole-view capture was found (" + final.WidthMm.ToString("0.#", CultureInfo.InvariantCulture)
                        + " x " + final.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm). Lines of other parts may be included.");
                }

                if (final.Score < 0.0)
                {
                    result.Failed.Add(part);
                    report.Error("VIEW_NONE", part.Name, "No view passed the shape checks. Best score " + final.Score.ToString("0.00", CultureInfo.InvariantCulture) + ".");
                    continue;
                }

                result.Selections.Add(final);

                report.Info(
                    "VIEW_SELECTED",
                    part.Name,
                    "View " + final.Node.Id + " (" + (final.Node.Layer ?? "-") + ") " + final.WidthMm.ToString("0.#", CultureInfo.InvariantCulture)
                    + " x " + final.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm, score "
                    + final.Score.ToString("0.00", CultureInfo.InvariantCulture) + ", from " + candidates.Count.ToString(CultureInfo.InvariantCulture)
                    + " candidate(s). " + final.Reason);
            }

            return result;
        }

        private static List<AssemblyNode> GatherCandidates(CalloutLink link, IReadOnlyList<AssemblyNode> nodes, double drawingArea)
        {
            var list = new List<AssemblyNode>();
            var layer = link.Node.Layer ?? string.Empty;

            foreach (var node in nodes)
            {
                if (!IsUsable(node, drawingArea))
                {
                    continue;
                }

                var sameNode = string.Equals(node.Id, link.Node.Id, StringComparison.Ordinal);
                var sameLayer = layer.Length > 0 && string.Equals(node.Layer, layer, StringComparison.OrdinalIgnoreCase);
                var sameLabel = node.Labels.Any(l =>
                    !string.IsNullOrWhiteSpace(l)
                    && l.Contains(link.Part.Name, StringComparison.OrdinalIgnoreCase));

                if (sameNode || sameLayer || sameLabel)
                {
                    list.Add(node);
                }
            }

            if (!list.Any(n => string.Equals(n.Id, link.Node.Id, StringComparison.Ordinal)) && IsUsable(link.Node, drawingArea))
            {
                list.Add(link.Node);
            }

            return list;
        }

        private static bool IsUsable(AssemblyNode node, double drawingArea)
        {
            if (node.Bounds.Area <= 0.0)
            {
                return false;
            }

            if (drawingArea > 0.0 && node.Bounds.Area > drawingArea * MaxNodeAreaFraction)
            {
                return false;
            }

            var layer = node.Layer ?? string.Empty;
            if (ExcludedLayerMarkers.Any(m => layer.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return node.LineCount + node.ArcCount + node.CircleCount + node.PolylineCount > 0;
        }

        private static ViewSelection Score(
            CalloutPart part,
            CalloutLink link,
            AssemblyNode node,
            double mm,
            double drawingArea,
            int candidateCount,
            out bool edgeOn)
        {
            var w = node.Bounds.Width * mm;
            var h = node.Bounds.Height * mm;
            var minDim = Math.Min(w, h);
            var maxDim = Math.Max(w, h);
            var thickness = part.ThicknessMm ?? 0.0;
            var reasons = new List<string>();
            var score = 0.0;

            edgeOn = false;

            if (thickness > 0.0 && minDim <= thickness * EdgeOnThicknessFactor + 1.0 && maxDim > minDim * EdgeOnAspect)
            {
                edgeOn = true;
                score -= 1.0;
                reasons.Add("edge-on");
            }
            else if (minDim > 0.0 && maxDim / minDim > 40.0 && thickness <= 0.0)
            {
                edgeOn = true;
                score -= 0.8;
                reasons.Add("sliver");
            }

            var entities = node.LineCount + node.ArcCount + node.CircleCount + node.PolylineCount;
            var wholeView = entities > WholeViewEntityLimit
                || (drawingArea > 0.0 && node.Bounds.Area > drawingArea * WholeViewAreaFraction);

            if (wholeView)
            {
                score -= 0.6;
                reasons.Add("whole-view capture");
            }

            if (node.HasClosedProfile)
            {
                score += 0.3;
                reasons.Add("closed outline");
            }

            var round = RoundNameRx.IsMatch(part.Name);
            var flat = FlatNameRx.IsMatch(part.Name);

            if (round && (node.Shape == PartShape.Circular || node.Shape == PartShape.Annular))
            {
                score += 0.3;
                reasons.Add("round shape");
            }
            else if (round && node.CircleCount + node.ArcCount > 0)
            {
                score += 0.15;
                reasons.Add("has arcs or circles");
            }

            if (flat && node.Shape == PartShape.Rectangular)
            {
                score += 0.3;
                reasons.Add("rectangular shape");
            }

            if (part.WeightKg is > 0.0 && thickness > 0.0)
            {
                var expectedTotal = part.WeightKg.Value / (DensityKgPerMm3 * thickness);
                var expectedEach = expectedTotal / Math.Max(1, part.Qty);
                var envelope = node.Shape == PartShape.Circular || node.Shape == PartShape.Annular
                    ? Math.PI / 4.0 * w * h
                    : w * h;

                var r1 = envelope / expectedTotal;
                var r2 = envelope / expectedEach;

                if ((r1 >= 0.9 && r1 <= 12.0) || (r2 >= 0.9 && r2 <= 12.0))
                {
                    score += 0.4;
                    reasons.Add("size fits drawing weight");
                }
                else
                {
                    score -= 0.2;
                    reasons.Add("size does not fit drawing weight");
                }
            }

            if (string.Equals(node.Id, link.Node.Id, StringComparison.Ordinal))
            {
                score += 0.2;
                reasons.Add("linked node");
            }

            return new ViewSelection
            {
                Part = part,
                Node = node,
                Score = Math.Round(score, 3),
                Reason = string.Join(", ", reasons) + ".",
                CandidateCount = candidateCount,
                WidthMm = Math.Round(w, 2),
                HeightMm = Math.Round(h, 2),
                IsEdgeOn = edgeOn,
                IsWholeView = wholeView
            };
        }
    }
}