using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public enum CalloutLinkMethod
    {
        LabelOnNode = 0,
        LeaderTip = 1,
        TextInside = 2,
        TextNearest = 3
    }

    public sealed class CalloutLink
    {
        public CalloutPart Part { get; init; } = new();

        public AssemblyNode Node { get; init; } = new();

        public CalloutLinkMethod Method { get; init; }

        public double Distance { get; init; }

        public double Confidence { get; init; }

        public GeometryPoint Tip { get; init; } = new(0, 0);

        public bool LayerHint { get; init; }
    }

    public sealed class CalloutLinkResult
    {
        public List<CalloutLink> Links { get; } = new();

        public List<CalloutPart> Unlinked { get; } = new();
    }

    public sealed class GaCalloutGeometryLinker
    {
        private const double TextRadiusFraction = 0.012;
        private const double ChainToleranceFraction = 0.0006;
        private const double MaxLeaderLengthFraction = 0.2;
        private const double MaxReachFraction = 0.15;
        private const double MaxNodeAreaFraction = 0.6;
        private const int MaxChainHops = 6;
        private const double LowConfidence = 0.5;

        private static readonly string[] LeaderLayerMarkers =
        {
            "dim", "note", "leader", "annot", "defpoints", "text"
        };

        private static readonly string[] ExcludedNodeLayerMarkers =
        {
            "title", "border", "paper", "viewport", "table", "defpoints", "dim", "text", "note"
        };

        private static readonly (Regex Name, string[] Layers)[] LayerHints =
        {
            (new Regex(@"shroud", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "shroud" }),
            (new Regex(@"back\s?plate", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "backplate" }),
            (new Regex(@"blade", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "blade" }),
            (new Regex(@"\bhub\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "hub" }),
            (new Regex(@"spark", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "ris" }),
            (new Regex(@"drain", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "drain" }),
            (new Regex(@"pedestal", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "ped" }),
            (new Regex(@"base\s?(frame|plate)", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "base" }),
            (new Regex(@"casing|scroll", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "casing" }),
            (new Regex(@"inlet", RegexOptions.IgnoreCase | RegexOptions.Compiled), new[] { "inlet" })
        };

        public CalloutLinkResult Link(
            CalloutReadResult callouts,
            ExtractedGeometry geometry,
            IReadOnlyList<AssemblyNode> nodes,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(callouts);
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(report);

            var result = new CalloutLinkResult();

            var diag = DrawingDiagonal(geometry, nodes);
            if (diag <= 0.0)
            {
                foreach (var part in callouts.Parts)
                {
                    result.Unlinked.Add(part);
                    report.Error("CALLOUT_UNLINKED", part.Name, "The drawing has no measurable geometry.");
                }

                return result;
            }

            var drawingArea = DrawingArea(geometry, nodes);
            var valid = nodes.Where(n => IsValidNode(n, drawingArea)).ToList();
            var leaderLines = geometry.Lines.Where(l => IsLeaderLayer(l.Layer) && l.Length <= diag * MaxLeaderLengthFraction).ToList();

            foreach (var part in callouts.Parts)
            {
                var link = LinkOne(part, valid, leaderLines, diag);

                if (link is null)
                {
                    result.Unlinked.Add(part);
                    report.Error("CALLOUT_UNLINKED", part.Name, "No part outline was found near this callout.");
                    continue;
                }

                result.Links.Add(link);

                report.Info(
                    "CALLOUT_LINK",
                    part.Name,
                    "Linked to " + link.Node.Id + " (" + (link.Node.Layer ?? "-") + ") by " + link.Method
                    + ", distance " + link.Distance.ToString("0.#", CultureInfo.InvariantCulture)
                    + ", confidence " + link.Confidence.ToString("0.00", CultureInfo.InvariantCulture) + ".");

                if (link.Confidence < LowConfidence)
                {
                    report.Warn("CALLOUT_LOW_CONFIDENCE", part.Name, "The link to " + link.Node.Id + " is weak. Please verify.");
                }
            }

            foreach (var group in result.Links.GroupBy(l => l.Node.Id).Where(g => g.Count() > 1))
            {
                report.Info(
                    "CALLOUT_SHARED_NODE",
                    group.Key,
                    "Callouts sharing one outline: " + string.Join(", ", group.Select(g => g.Part.Name)) + ".");
            }

            return result;
        }

        private CalloutLink? LinkOne(CalloutPart part, List<AssemblyNode> nodes, List<GeometryLine> leaderLines, double diag)
        {
            var labelNode = FindLabelNode(part, nodes);
            if (labelNode is not null)
            {
                var anchor = new GeometryPoint(labelNode.Bounds.CenterX, labelNode.Bounds.CenterY);
                return new CalloutLink
                {
                    Part = part,
                    Node = labelNode,
                    Method = CalloutLinkMethod.LabelOnNode,
                    Distance = 0.0,
                    Confidence = 0.95,
                    Tip = anchor,
                    LayerHint = HasLayerHint(part, labelNode)
                };
            }

            var tip = FindLeaderTip(part, leaderLines, diag, out var viaLeader);
            var maxReach = diag * MaxReachFraction;

            AssemblyNode? best = null;
            var bestCost = double.MaxValue;
            var bestDistance = 0.0;
            var bestInside = false;
            var bestHint = false;

            foreach (var node in nodes)
            {
                var d = DistanceToBounds(tip, node.Bounds);
                if (d > maxReach)
                {
                    continue;
                }

                var nodeDiag = Math.Sqrt(node.Bounds.Width * node.Bounds.Width + node.Bounds.Height * node.Bounds.Height);
                var inside = d <= 1e-9;
                var cost = inside ? 0.02 * nodeDiag : d + 0.02 * nodeDiag;
                var hint = HasLayerHint(part, node);

                if (hint)
                {
                    cost *= 0.3;
                }

                if (cost < bestCost)
                {
                    best = node;
                    bestCost = cost;
                    bestDistance = d;
                    bestInside = inside;
                    bestHint = hint;
                }
            }

            if (best is null)
            {
                return null;
            }

            double confidence;
            CalloutLinkMethod method;

            if (viaLeader)
            {
                method = CalloutLinkMethod.LeaderTip;
                confidence = bestInside ? 0.9 : 0.7;
            }
            else if (bestInside)
            {
                method = CalloutLinkMethod.TextInside;
                confidence = 0.6;
            }
            else
            {
                method = CalloutLinkMethod.TextNearest;
                confidence = 0.4;
            }

            if (bestHint)
            {
                confidence = Math.Min(1.0, confidence + 0.1);
            }

            return new CalloutLink
            {
                Part = part,
                Node = best,
                Method = method,
                Distance = bestDistance,
                Confidence = confidence,
                Tip = tip,
                LayerHint = bestHint
            };
        }

        private static AssemblyNode? FindLabelNode(CalloutPart part, List<AssemblyNode> nodes)
        {
            if (string.IsNullOrWhiteSpace(part.Name))
            {
                return null;
            }

            AssemblyNode? best = null;

            foreach (var node in nodes)
            {
                if (node.Labels is null || node.Labels.Count == 0)
                {
                    continue;
                }

                var matches = node.Labels.Any(l =>
                    !string.IsNullOrWhiteSpace(l)
                    && (part.Raw.Contains(l, StringComparison.OrdinalIgnoreCase)
                        || l.Contains(part.Name, StringComparison.OrdinalIgnoreCase)));

                if (!matches)
                {
                    continue;
                }

                if (best is null || node.Bounds.Area < best.Bounds.Area)
                {
                    best = node;
                }
            }

            return best;
        }

        private static GeometryPoint FindLeaderTip(CalloutPart part, List<GeometryLine> leaderLines, double diag, out bool viaLeader)
        {
            viaLeader = false;

            var anchor = new GeometryPoint(part.X, part.Y);
            var radius = diag * TextRadiusFraction;
            var tolerance = diag * ChainToleranceFraction;

            GeometryLine? first = null;
            var firstNearEnd = false;
            var bestDistance = double.MaxValue;

            foreach (var line in leaderLines)
            {
                var ds = Distance(anchor, line.Start);
                var de = Distance(anchor, line.End);
                var d = Math.Min(ds, de);

                if (d <= radius && d < bestDistance)
                {
                    first = line;
                    firstNearEnd = de < ds;
                    bestDistance = d;
                }
            }

            if (first is null)
            {
                return anchor;
            }

            var current = firstNearEnd ? first.Start : first.End;
            var used = new HashSet<GeometryLine> { first };

            for (var hop = 0; hop < MaxChainHops; hop++)
            {
                GeometryLine? next = null;
                var nextFar = current;

                foreach (var line in leaderLines)
                {
                    if (used.Contains(line))
                    {
                        continue;
                    }

                    if (Distance(current, line.Start) <= tolerance)
                    {
                        next = line;
                        nextFar = line.End;
                        break;
                    }

                    if (Distance(current, line.End) <= tolerance)
                    {
                        next = line;
                        nextFar = line.Start;
                        break;
                    }
                }

                if (next is null)
                {
                    break;
                }

                used.Add(next);
                current = nextFar;
            }

            viaLeader = true;
            return current;
        }

        private static bool IsValidNode(AssemblyNode node, double drawingArea)
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
            if (ExcludedNodeLayerMarkers.Any(m => layer.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return node.LineCount + node.ArcCount + node.CircleCount + node.PolylineCount > 0;
        }

        private static bool IsLeaderLayer(string? layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return LeaderLayerMarkers.Any(m => layer.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasLayerHint(CalloutPart part, AssemblyNode node)
        {
            var layer = node.Layer ?? string.Empty;
            if (layer.Length == 0)
            {
                return false;
            }

            foreach (var (name, layers) in LayerHints)
            {
                if (name.IsMatch(part.Name) && layers.Any(l => layer.Contains(l, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static double DrawingDiagonal(ExtractedGeometry geometry, IReadOnlyList<AssemblyNode> nodes)
        {
            if (geometry.Bounds is not null)
            {
                var w = geometry.Bounds.Width;
                var h = geometry.Bounds.Height;
                return Math.Sqrt(w * w + h * h);
            }

            if (nodes.Count == 0)
            {
                return 0.0;
            }

            var minX = nodes.Min(n => n.Bounds.MinX);
            var minY = nodes.Min(n => n.Bounds.MinY);
            var maxX = nodes.Max(n => n.Bounds.MaxX);
            var maxY = nodes.Max(n => n.Bounds.MaxY);

            return Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        }

        private static double DrawingArea(ExtractedGeometry geometry, IReadOnlyList<AssemblyNode> nodes)
        {
            if (geometry.Bounds is not null)
            {
                return geometry.Bounds.Width * geometry.Bounds.Height;
            }

            if (nodes.Count == 0)
            {
                return 0.0;
            }

            var minX = nodes.Min(n => n.Bounds.MinX);
            var minY = nodes.Min(n => n.Bounds.MinY);
            var maxX = nodes.Max(n => n.Bounds.MaxX);
            var maxY = nodes.Max(n => n.Bounds.MaxY);

            return (maxX - minX) * (maxY - minY);
        }

        private static double DistanceToBounds(GeometryPoint p, BoundingBox2D b)
        {
            var dx = Math.Max(Math.Max(b.MinX - p.X, 0.0), p.X - b.MaxX);
            var dy = Math.Max(Math.Max(b.MinY - p.Y, 0.0), p.Y - b.MaxY);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(GeometryPoint a, GeometryPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
