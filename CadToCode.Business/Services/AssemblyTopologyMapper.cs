using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services;

public class AssemblyTopologyMapper : IAssemblyTopologyMapper
{
    private const double ProximityFraction = 0.005;
    private const double AdjacentFraction = 0.05;
    private const double AlignFraction = 0.25;
    private const double SizeMatchFraction = 0.03;
    private const int MinArrayMembers = 3;
    private const int MaxRelations = 4000;
    private const int MaxSymmetryGroup = 200;
    private const double IslandMinGapFraction = 0.03;
    private const double IslandJumpRatio = 2.5;
    private const double IslandPairSizeFactor = 0.5;
    private const int MaxIslandNodes = 600;

    private static readonly (string[] Keywords, string Category)[] CategoryRules =
    {
        (new[] { "door" }, "Door"),
        (new[] { "window", "glazing", "sash" }, "Window"),
        (new[] { "fan", "impeller", "rotor", "blade" }, "Fan"),
        (new[] { "flange", "gasket", "bolt" }, "Flange"),
        (new[] { "cyclone" }, "Cyclone"),
        (new[] { "duct" }, "Duct"),
        (new[] { "bracket" }, "Bracket"),
        (new[] { "panel", "glass" }, "Panel")
    };

    public AssemblyTree Map(ExtractedGeometry geometry, IReadOnlyList<AssemblyNode> nodes)
    {
        var tree = new AssemblyTree();

        if (geometry != null)
        {
            tree.Unit = geometry.DetectedUnit;
        }

        if (nodes == null || nodes.Count == 0)
        {
            return tree;
        }

        var list = nodes.ToList();

        var bounds = list[0].Bounds;
        foreach (var node in list)
        {
            bounds = bounds.Union(node.Bounds);
        }

        tree.Bounds = bounds;
        tree.Nodes = list;

        var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        if (diagonal <= 0)
        {
            diagonal = 1.0;
        }

        var tolerance = diagonal * ProximityFraction;
        var adjacentReach = diagonal * AdjacentFraction;
        var alignReach = diagonal * AlignFraction;

        ResetLinks(list);
        BuildHierarchy(list, tolerance);
        tree.RootIds = list.Where(n => n.ParentId == null).Select(n => n.Id).ToList();
        tree.Assemblies = DetectAssemblies(list, diagonal, tolerance);
        tree.SharedProfiles = BuildSharedProfiles(list, tree.Unit);
        tree.Arrays = DetectArrays(list, tolerance);
        BuildRelations(tree, list, tolerance, adjacentReach, alignReach);
        AddSymmetry(tree, list, tolerance);

        return tree;
    }

    private static void ResetLinks(List<AssemblyNode> list)
    {
        foreach (var node in list)
        {
            node.ParentId = null;
            node.ChildIds = new List<string>();
            node.ArrayGroupId = null;
            node.AssemblyId = null;
            node.SharedProfileId = null;
        }
    }

    private static void BuildHierarchy(List<AssemblyNode> list, double tolerance)
    {
        var areaEpsilon = tolerance * tolerance;

        for (var i = 0; i < list.Count; i++)
        {
            var node = list[i];
            var best = -1;
            var bestArea = 0.0;

            for (var j = 0; j < list.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var candidate = list[j];
                if (!candidate.Bounds.Contains(node.Bounds, tolerance))
                {
                    continue;
                }

                if (candidate.Bounds.Area <= node.Bounds.Area + areaEpsilon)
                {
                    continue;
                }

                if (best < 0 || candidate.Bounds.Area < bestArea)
                {
                    best = j;
                    bestArea = candidate.Bounds.Area;
                }
            }

            if (best >= 0)
            {
                node.ParentId = list[best].Id;
                list[best].ChildIds.Add(node.Id);
            }
        }
    }

    private static List<List<AssemblyNode>> GroupSimilar(List<AssemblyNode> list, double tolerance)
    {
        var groups = new List<List<AssemblyNode>>();

        foreach (var node in list)
        {
            var placed = false;

            foreach (var group in groups)
            {
                var head = group[0];
                if ((head.ParentId ?? string.Empty) == (node.ParentId ?? string.Empty)
                    && IsSimilar(head, node, tolerance))
                {
                    group.Add(node);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                groups.Add(new List<AssemblyNode> { node });
            }
        }

        return groups;
    }

    private static bool IsSimilar(AssemblyNode a, AssemblyNode b, double tolerance)
    {
        if (a.Shape != b.Shape
            || a.LineCount != b.LineCount
            || a.ArcCount != b.ArcCount
            || a.CircleCount != b.CircleCount
            || a.PolylineCount != b.PolylineCount
            || a.HatchCount != b.HatchCount)
        {
            return false;
        }

        var straight = SizeClose(a.Bounds.Width, b.Bounds.Width, tolerance)
            && SizeClose(a.Bounds.Height, b.Bounds.Height, tolerance);

        var swapped = SizeClose(a.Bounds.Width, b.Bounds.Height, tolerance)
            && SizeClose(a.Bounds.Height, b.Bounds.Width, tolerance);

        return straight || swapped;
    }

    private static bool SizeClose(double x, double y, double tolerance)
    {
        return Math.Abs(x - y) <= Math.Max(tolerance, SizeMatchFraction * Math.Max(Math.Abs(x), Math.Abs(y)));
    }

    private static List<AssemblyArrayGroup> DetectArrays(List<AssemblyNode> list, double tolerance)
    {
        var arrays = new List<AssemblyArrayGroup>();

        foreach (var group in GroupSimilar(list, tolerance))
        {
            if (group.Count < MinArrayMembers)
            {
                continue;
            }

            var found = new List<AssemblyArrayGroup>();
            var polar = TryPolar(group, tolerance);

            if (polar != null)
            {
                var hub = list.FirstOrDefault(n =>
                    n.ParentId == group[0].ParentId
                    && !polar.MemberIds.Contains(n.Id)
                    && Distance(n.Bounds.CenterX, n.Bounds.CenterY, polar.CenterX, polar.CenterY)
                        <= Math.Max(tolerance * 2.0, polar.Radius * 0.1));

                polar.CenterNodeId = hub?.Id;
                found.Add(polar);
            }
            else
            {
                var linear = TryLinear(group, tolerance);
                if (linear != null)
                {
                    found.Add(linear);
                }
                else
                {
                    found.AddRange(TryGrid(group, tolerance));
                }
            }

            foreach (var array in found)
            {
                array.Id = $"A{arrays.Count + 1}";

                foreach (var member in group)
                {
                    if (array.MemberIds.Contains(member.Id))
                    {
                        member.ArrayGroupId = array.Id;
                    }
                }

                arrays.Add(array);
            }
        }

        return arrays;
    }

    private static AssemblyArrayGroup? TryPolar(List<AssemblyNode> group, double tolerance)
    {
        if (group.Count < MinArrayMembers)
        {
            return null;
        }

        var points = group.Select(n => (X: n.Bounds.CenterX, Y: n.Bounds.CenterY)).ToList();
        var fit = FitCircle(points);
        if (fit == null)
        {
            return null;
        }

        var (cx, cy, radius) = fit.Value;
        if (radius < tolerance * 2.0)
        {
            return null;
        }

        var radialTolerance = Math.Max(tolerance, radius * 0.02);
        foreach (var point in points)
        {
            if (Math.Abs(Distance(point.X, point.Y, cx, cy) - radius) > radialTolerance)
            {
                return null;
            }
        }

        var ordered = group
            .Select(n => new
            {
                Node = n,
                Angle = NormalizeDegrees(Math.Atan2(n.Bounds.CenterY - cy, n.Bounds.CenterX - cx) * 180.0 / Math.PI)
            })
            .OrderBy(a => a.Angle)
            .ToList();

        var count = ordered.Count;
        var gaps = new double[count];
        for (var i = 0; i < count; i++)
        {
            var next = ordered[(i + 1) % count].Angle;
            gaps[i] = (next - ordered[i].Angle + 360.0) % 360.0;
        }

        var maxGap = gaps.Max();
        var minGap = gaps.Min();
        var fullStep = 360.0 / count;
        var fullCircle = (maxGap - minGap) <= Math.Max(1.0, fullStep * 0.1);

        int start;
        double step;

        if (fullCircle)
        {
            start = 0;
            step = fullStep;
        }
        else
        {
            var maxIndex = Array.IndexOf(gaps, maxGap);
            start = (maxIndex + 1) % count;

            var diffs = new List<double>();
            for (var t = 0; t < count - 1; t++)
            {
                diffs.Add(gaps[(start + t) % count]);
            }

            step = diffs.Average();
            if (step <= 0 || diffs.Any(d => Math.Abs(d - step) > Math.Max(1.0, step * 0.1)))
            {
                return null;
            }
        }

        var sequence = Enumerable.Range(0, count).Select(t => ordered[(start + t) % count]).ToList();

        return new AssemblyArrayGroup
        {
            Kind = AssemblyArrayKind.Polar,
            MemberIds = sequence.Select(s => s.Node.Id).ToList(),
            Count = count,
            Spacing = Round(radius * step * Math.PI / 180.0),
            DirectionDegrees = 0,
            CenterX = Round(cx),
            CenterY = Round(cy),
            Radius = Round(radius),
            StartAngleDegrees = Round(sequence[0].Angle),
            AngleStepDegrees = Round(step)
        };
    }

    private static AssemblyArrayGroup? TryLinear(List<AssemblyNode> group, double tolerance)
    {
        if (group.Count < MinArrayMembers)
        {
            return null;
        }

        var centers = group
            .Select(n => (Node: n, X: n.Bounds.CenterX, Y: n.Bounds.CenterY))
            .ToList();

        var xRange = centers.Max(c => c.X) - centers.Min(c => c.X);
        var yRange = centers.Max(c => c.Y) - centers.Min(c => c.Y);

        var sorted = xRange >= yRange
            ? centers.OrderBy(c => c.X).ThenBy(c => c.Y).ToList()
            : centers.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();

        var first = sorted[0];
        var last = sorted[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length <= tolerance)
        {
            return null;
        }

        var ux = dx / length;
        var uy = dy / length;
        var lineTolerance = Math.Max(tolerance, length * 0.01);

        foreach (var c in sorted)
        {
            var perpendicular = Math.Abs((c.X - first.X) * uy - (c.Y - first.Y) * ux);
            if (perpendicular > lineTolerance)
            {
                return null;
            }
        }

        var projections = sorted
            .Select(c => (c.X - first.X) * ux + (c.Y - first.Y) * uy)
            .ToList();

        var diffs = new List<double>();
        for (var i = 1; i < projections.Count; i++)
        {
            diffs.Add(projections[i] - projections[i - 1]);
        }

        var spacing = diffs.Average();
        if (spacing <= tolerance)
        {
            return null;
        }

        var spacingTolerance = Math.Max(tolerance, spacing * 0.05);
        if (diffs.Any(d => Math.Abs(d - spacing) > spacingTolerance))
        {
            return null;
        }

        return new AssemblyArrayGroup
        {
            Kind = AssemblyArrayKind.Linear,
            MemberIds = sorted.Select(c => c.Node.Id).ToList(),
            Count = sorted.Count,
            Spacing = Round(spacing),
            DirectionDegrees = Round(NormalizeDegrees(Math.Atan2(dy, dx) * 180.0 / Math.PI)),
            CenterX = Round(centers.Average(c => c.X)),
            CenterY = Round(centers.Average(c => c.Y))
        };
    }

    private static List<AssemblyArrayGroup> TryGrid(List<AssemblyNode> group, double tolerance)
    {
        var result = new List<AssemblyArrayGroup>();
        var used = new HashSet<AssemblyNode>();

        foreach (var row in ClusterBy(group, n => n.Bounds.CenterY, tolerance))
        {
            if (row.Count < MinArrayMembers)
            {
                continue;
            }

            var array = TryLinear(row, tolerance);
            if (array == null)
            {
                continue;
            }

            result.Add(array);
            foreach (var node in row)
            {
                used.Add(node);
            }
        }

        var remaining = group.Where(n => !used.Contains(n)).ToList();

        foreach (var column in ClusterBy(remaining, n => n.Bounds.CenterX, tolerance))
        {
            if (column.Count < MinArrayMembers)
            {
                continue;
            }

            var array = TryLinear(column, tolerance);
            if (array != null)
            {
                result.Add(array);
            }
        }

        return result;
    }

    private static List<List<AssemblyNode>> ClusterBy(
        List<AssemblyNode> items,
        Func<AssemblyNode, double> key,
        double tolerance)
    {
        var result = new List<List<AssemblyNode>>();
        var sorted = items.OrderBy(key).ToList();

        List<AssemblyNode>? current = null;
        var previous = 0.0;

        foreach (var item in sorted)
        {
            var value = key(item);
            if (current == null || value - previous > tolerance)
            {
                current = new List<AssemblyNode>();
                result.Add(current);
            }

            current.Add(item);
            previous = value;
        }

        return result;
    }

    private static (double Cx, double Cy, double R)? FitCircle(List<(double X, double Y)> points)
    {
        var n = points.Count;
        if (n < 3)
        {
            return null;
        }

        var mx = points.Average(p => p.X);
        var my = points.Average(p => p.Y);

        double suu = 0, svv = 0, suv = 0, a = 0, b = 0;

        foreach (var p in points)
        {
            var u = p.X - mx;
            var v = p.Y - my;
            var q = u * u + v * v;
            suu += u * u;
            svv += v * v;
            suv += u * v;
            a += u * q;
            b += v * q;
        }

        var scale = suu + svv;
        if (scale <= 0)
        {
            return null;
        }

        var det = suu * svv - suv * suv;
        if (Math.Abs(det) <= 1e-9 * scale * scale)
        {
            return null;
        }

        var d = (-a * svv + suv * b) / det;
        var e = (-suu * b + suv * a) / det;

        var cu = -d / 2.0;
        var cv = -e / 2.0;
        var radiusSquared = (d * d + e * e) / 4.0 + scale / n;

        if (radiusSquared <= 0)
        {
            return null;
        }

        return (mx + cu, my + cv, Math.Sqrt(radiusSquared));
    }

    private static void BuildRelations(
        AssemblyTree tree,
        List<AssemblyNode> list,
        double tolerance,
        double adjacentReach,
        double alignReach)
    {
        var order = Enumerable.Range(0, list.Count)
            .OrderBy(i => list[i].Bounds.MinX)
            .ThenBy(i => list[i].Bounds.MinY)
            .ToList();

        var capped = false;

        for (var oi = 0; oi < order.Count && !capped; oi++)
        {
            var a = list[order[oi]];

            for (var oj = oi + 1; oj < order.Count; oj++)
            {
                var b = list[order[oj]];

                if (b.Bounds.MinX > a.Bounds.MaxX + alignReach)
                {
                    break;
                }

                if (a.ArrayGroupId != null && a.ArrayGroupId == b.ArrayGroupId)
                {
                    continue;
                }

                var relation = Classify(a, b, tolerance, adjacentReach, alignReach);
                if (relation == null)
                {
                    continue;
                }

                if (tree.Relations.Count >= MaxRelations)
                {
                    tree.Warnings.Add($"Relation limit of {MaxRelations} reached; remaining relations were skipped.");
                    capped = true;
                    break;
                }

                tree.Relations.Add(relation);
            }
        }
    }

    private static AssemblyRelation? Classify(
        AssemblyNode a,
        AssemblyNode b,
        double tolerance,
        double adjacentReach,
        double alignReach)
    {
        AssemblyNode? outer = null;
        AssemblyNode? inner = null;

        if (IsContainer(a, b, tolerance))
        {
            outer = a;
            inner = b;
        }
        else if (IsContainer(b, a, tolerance))
        {
            outer = b;
            inner = a;
        }

        if (outer != null && inner != null)
        {
            if (inner.ParentId != outer.Id)
            {
                return null;
            }

            var concentric = Math.Abs(outer.Bounds.CenterX - inner.Bounds.CenterX) <= tolerance
                && Math.Abs(outer.Bounds.CenterY - inner.Bounds.CenterY) <= tolerance;

            var clearance = Math.Min(
                Math.Min(inner.Bounds.MinX - outer.Bounds.MinX, outer.Bounds.MaxX - inner.Bounds.MaxX),
                Math.Min(inner.Bounds.MinY - outer.Bounds.MinY, outer.Bounds.MaxY - inner.Bounds.MaxY));

            return new AssemblyRelation
            {
                FromId = outer.Id,
                ToId = inner.Id,
                Type = concentric ? AssemblyRelationType.Concentric : AssemblyRelationType.Contained,
                FromAnchor = concentric ? "center" : "interior",
                ToAnchor = "center",
                OffsetX = Round(inner.Bounds.CenterX - outer.Bounds.CenterX),
                OffsetY = Round(inner.Bounds.CenterY - outer.Bounds.CenterY),
                Gap = Round(Math.Max(0, clearance)),
                Description = concentric
                    ? $"{inner.Name} is concentric with {outer.Name}"
                    : $"{inner.Name} sits inside {outer.Name}"
            };
        }

        var overlapX = Math.Min(a.Bounds.MaxX, b.Bounds.MaxX) - Math.Max(a.Bounds.MinX, b.Bounds.MinX);
        var overlapY = Math.Min(a.Bounds.MaxY, b.Bounds.MaxY) - Math.Max(a.Bounds.MinY, b.Bounds.MinY);
        var gap = a.Bounds.GapTo(b.Bounds);
        var offsetX = Round(b.Bounds.CenterX - a.Bounds.CenterX);
        var offsetY = Round(b.Bounds.CenterY - a.Bounds.CenterY);

        if (overlapX > tolerance && overlapY > tolerance)
        {
            return new AssemblyRelation
            {
                FromId = a.Id,
                ToId = b.Id,
                Type = AssemblyRelationType.Overlapping,
                FromAnchor = "center",
                ToAnchor = "center",
                OffsetX = offsetX,
                OffsetY = offsetY,
                Gap = 0,
                OverlapX = Round(overlapX),
                OverlapY = Round(overlapY),
                Description = $"{a.Name} overlaps {b.Name}"
            };
        }

        if (gap <= tolerance)
        {
            var (fromAnchor, toAnchor) = AnchorsFor(a, b);
            return new AssemblyRelation
            {
                FromId = a.Id,
                ToId = b.Id,
                Type = AssemblyRelationType.Connected,
                FromAnchor = fromAnchor,
                ToAnchor = toAnchor,
                OffsetX = offsetX,
                OffsetY = offsetY,
                Gap = 0,
                Description = $"{a.Name} touches {b.Name} ({fromAnchor} to {toAnchor})"
            };
        }

        if (gap <= adjacentReach)
        {
            var (fromAnchor, toAnchor) = AnchorsFor(a, b);
            return new AssemblyRelation
            {
                FromId = a.Id,
                ToId = b.Id,
                Type = AssemblyRelationType.Adjacent,
                FromAnchor = fromAnchor,
                ToAnchor = toAnchor,
                OffsetX = offsetX,
                OffsetY = offsetY,
                Gap = Round(gap),
                Description = $"{a.Name} is {Round(gap)} away from {b.Name} ({fromAnchor} to {toAnchor})"
            };
        }

        if (gap <= alignReach)
        {
            var alignment = AlignmentOf(a, b, tolerance);
            if (alignment != null)
            {
                return new AssemblyRelation
                {
                    FromId = a.Id,
                    ToId = b.Id,
                    Type = AssemblyRelationType.Aligned,
                    FromAnchor = alignment,
                    ToAnchor = alignment,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    Gap = Round(gap),
                    Description = $"{a.Name} and {b.Name} are aligned on {alignment}"
                };
            }
        }

        return null;
    }

    private static bool IsContainer(AssemblyNode outer, AssemblyNode inner, double tolerance)
    {
        return outer.Bounds.Contains(inner.Bounds, tolerance)
            && outer.Bounds.Area > inner.Bounds.Area + tolerance * tolerance;
    }

    private static (string From, string To) AnchorsFor(AssemblyNode a, AssemblyNode b)
    {
        var right = b.Bounds.MinX - a.Bounds.MaxX;
        var left = a.Bounds.MinX - b.Bounds.MaxX;
        var up = b.Bounds.MinY - a.Bounds.MaxY;
        var down = a.Bounds.MinY - b.Bounds.MaxY;

        var best = Math.Max(Math.Max(right, left), Math.Max(up, down));
        if (best <= 0)
        {
            return ("center", "center");
        }

        if (best == right)
        {
            return ("right", "left");
        }

        if (best == left)
        {
            return ("left", "right");
        }

        if (best == up)
        {
            return ("top", "bottom");
        }

        return ("bottom", "top");
    }

    private static string? AlignmentOf(AssemblyNode a, AssemblyNode b, double tolerance)
    {
        if (Math.Abs(a.Bounds.CenterX - b.Bounds.CenterX) <= tolerance)
        {
            return "center-x";
        }

        if (Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY) <= tolerance)
        {
            return "center-y";
        }

        if (Math.Abs(a.Bounds.MinX - b.Bounds.MinX) <= tolerance)
        {
            return "left";
        }

        if (Math.Abs(a.Bounds.MaxX - b.Bounds.MaxX) <= tolerance)
        {
            return "right";
        }

        if (Math.Abs(a.Bounds.MinY - b.Bounds.MinY) <= tolerance)
        {
            return "bottom";
        }

        if (Math.Abs(a.Bounds.MaxY - b.Bounds.MaxY) <= tolerance)
        {
            return "top";
        }

        return null;
    }

    private static void AddSymmetry(AssemblyTree tree, List<AssemblyNode> list, double tolerance)
    {
        var byId = new Dictionary<string, AssemblyNode>();
        foreach (var node in list)
        {
            byId[node.Id] = node;
        }

        foreach (var group in GroupSimilar(list, tolerance))
        {
            if (group.Count < 2 || group.Count > MaxSymmetryGroup)
            {
                continue;
            }

            var axisBounds = group[0].ParentId != null && byId.TryGetValue(group[0].ParentId!, out var parent)
                ? parent.Bounds
                : tree.Bounds;

            for (var i = 0; i < group.Count; i++)
            {
                for (var j = i + 1; j < group.Count; j++)
                {
                    var a = group[i];
                    var b = group[j];

                    if (a.ArrayGroupId != null && a.ArrayGroupId == b.ArrayGroupId)
                    {
                        continue;
                    }

                    var dx = Math.Abs(a.Bounds.CenterX - b.Bounds.CenterX);
                    var dy = Math.Abs(a.Bounds.CenterY - b.Bounds.CenterY);
                    var midX = (a.Bounds.CenterX + b.Bounds.CenterX) / 2.0;
                    var midY = (a.Bounds.CenterY + b.Bounds.CenterY) / 2.0;

                    string? description = null;

                    if (Math.Abs(midX - axisBounds.CenterX) <= tolerance && dy <= tolerance && dx > tolerance * 2.0)
                    {
                        description = $"{a.Name} and {b.Name} are mirror-symmetric about the vertical axis x={Round(axisBounds.CenterX)}";
                    }
                    else if (Math.Abs(midY - axisBounds.CenterY) <= tolerance && dx <= tolerance && dy > tolerance * 2.0)
                    {
                        description = $"{a.Name} and {b.Name} are mirror-symmetric about the horizontal axis y={Round(axisBounds.CenterY)}";
                    }

                    if (description == null)
                    {
                        continue;
                    }

                    if (tree.Relations.Count >= MaxRelations)
                    {
                        if (!tree.Warnings.Any(w => w.StartsWith("Relation limit", StringComparison.Ordinal)))
                        {
                            tree.Warnings.Add($"Relation limit of {MaxRelations} reached; remaining relations were skipped.");
                        }

                        return;
                    }

                    tree.Relations.Add(new AssemblyRelation
                    {
                        FromId = a.Id,
                        ToId = b.Id,
                        Type = AssemblyRelationType.Aligned,
                        FromAnchor = "mirror",
                        ToAnchor = "mirror",
                        OffsetX = Round(b.Bounds.CenterX - a.Bounds.CenterX),
                        OffsetY = Round(b.Bounds.CenterY - a.Bounds.CenterY),
                        Gap = Round(a.Bounds.GapTo(b.Bounds)),
                        Description = description
                    });
                }
            }
        }
    }

    private static List<ComponentAssembly> DetectAssemblies(List<AssemblyNode> list, double diagonal, double tolerance)
    {
        var assemblies = new List<ComponentAssembly>();

        var roots = list.Where(n => n.ParentId == null).ToList();
        if (roots.Count == 0)
        {
            return assemblies;
        }

        var byId = list.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var groups = ClusterIslands(roots, diagonal, tolerance);

        var ordered = groups
            .Select(g => new { Roots = g, Bounds = UnionBounds(g) })
            .OrderBy(g => g.Bounds.MinX)
            .ThenBy(g => g.Bounds.MinY)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var members = new List<AssemblyNode>();
            foreach (var root in ordered[i].Roots)
            {
                CollectWithDescendants(root, byId, members);
            }

            var id = "A" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (category, confidence) = ClassifyAssembly(members);

            var labels = members
                .SelectMany(n => n.Labels)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Any(char.IsLetter))
                .Select(l => l.Trim())
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .Take(8)
                .ToList();

            foreach (var member in members)
            {
                member.AssemblyId = id;
            }

            assemblies.Add(new ComponentAssembly
            {
                Id = id,
                AssemblyName = labels.Count > 0 && category == "Assembly"
                    ? labels[0]
                    : category + " " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Category = category,
                Bounds = ordered[i].Bounds,
                NodeIds = members.Select(n => n.Id).ToList(),
                Labels = labels,
                Confidence = confidence
            });
        }

        return assemblies;
    }

    private static List<SharedProfileGroup> BuildSharedProfiles(List<AssemblyNode> list, CadToCode.Models.Enums.DrawingUnit unit)
    {
        var scale = unit switch
        {
            CadToCode.Models.Enums.DrawingUnit.Centimeter => 10.0,
            CadToCode.Models.Enums.DrawingUnit.Meter => 1000.0,
            CadToCode.Models.Enums.DrawingUnit.Inch => 25.4,
            CadToCode.Models.Enums.DrawingUnit.Foot => 304.8,
            _ => 1.0
        };

        var shared = StructuralProfileExtractor.CatalogShared(list, scale);
        var byId = list.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var group in shared)
        {
            foreach (var nodeId in group.NodeIds)
            {
                if (byId.TryGetValue(nodeId, out var node))
                {
                    node.SharedProfileId = group.Id;
                }
            }
        }

        return shared;
    }

    private static BoundingBox2D UnionBounds(List<AssemblyNode> nodes)
    {
        var bounds = nodes[0].Bounds;
        foreach (var node in nodes)
        {
            bounds = bounds.Union(node.Bounds);
        }

        return bounds;
    }

    private static void CollectWithDescendants(
        AssemblyNode root,
        Dictionary<string, AssemblyNode> byId,
        List<AssemblyNode> output)
    {
        var stack = new Stack<AssemblyNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            output.Add(node);

            foreach (var childId in node.ChildIds)
            {
                if (byId.TryGetValue(childId, out var child))
                {
                    stack.Push(child);
                }
            }
        }
    }

    private static List<List<AssemblyNode>> ClusterIslands(List<AssemblyNode> roots, double diagonal, double tolerance)
    {
        var single = new List<List<AssemblyNode>> { roots };

        if (roots.Count < 2 || roots.Count > MaxIslandNodes)
        {
            return single;
        }

        var edges = new List<(double Gap, int A, int B)>();
        for (var i = 0; i < roots.Count; i++)
        {
            for (var j = i + 1; j < roots.Count; j++)
            {
                edges.Add((roots[i].Bounds.GapTo(roots[j].Bounds), i, j));
            }
        }

        edges.Sort((x, y) => x.Gap.CompareTo(y.Gap));

        var parent = Enumerable.Range(0, roots.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        var mst = new List<(double Gap, int A, int B)>();
        foreach (var edge in edges)
        {
            var ra = Find(edge.A);
            var rb = Find(edge.B);
            if (ra == rb)
            {
                continue;
            }

            parent[rb] = ra;
            mst.Add(edge);
        }

        var minGap = diagonal * IslandMinGapFraction;
        var cutAbove = double.PositiveInfinity;

        if (mst.Count == 1)
        {
            var gap = mst[0].Gap;
            var smaller = Math.Min(
                Math.Max(roots[0].Bounds.Width, roots[0].Bounds.Height),
                Math.Max(roots[1].Bounds.Width, roots[1].Bounds.Height));

            if (gap >= minGap && gap >= smaller * IslandPairSizeFactor)
            {
                cutAbove = 0;
            }
        }
        else
        {
            var bestRatio = 0.0;
            for (var k = 0; k + 1 < mst.Count; k++)
            {
                var next = mst[k + 1].Gap;
                if (next < minGap)
                {
                    continue;
                }

                var ratio = next / Math.Max(mst[k].Gap, tolerance);
                if (ratio >= IslandJumpRatio && ratio > bestRatio)
                {
                    bestRatio = ratio;
                    cutAbove = mst[k].Gap;
                }
            }
        }

        if (double.IsPositiveInfinity(cutAbove))
        {
            return single;
        }

        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }

        foreach (var edge in mst)
        {
            if (edge.Gap > cutAbove)
            {
                continue;
            }

            var ra = Find(edge.A);
            var rb = Find(edge.B);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        return Enumerable.Range(0, roots.Count)
            .GroupBy(Find)
            .Select(g => g.Select(index => roots[index]).ToList())
            .ToList();
    }

    private static (string Category, decimal Confidence) ClassifyAssembly(List<AssemblyNode> members)
    {
        var text = string.Join(
            " ",
            members.SelectMany(n => n.Labels.Concat(n.Tokens)).Select(t => t.ToLowerInvariant()));

        foreach (var (keywords, category) in CategoryRules)
        {
            if (keywords.Any(k => text.Contains(k, StringComparison.Ordinal)))
            {
                return (category, 0.9m);
            }
        }

        var circular = members.Count(n => n.Shape is PartShape.Circular or PartShape.Annular);
        if (circular > 0 && circular * 2 >= members.Count)
        {
            return ("RadialAssembly", 0.6m);
        }

        var boxy = members.Count(n => n.Shape is PartShape.Rectangular or PartShape.Polygonal or PartShape.Linear);
        if (members.Count >= 3 && boxy * 2 >= members.Count)
        {
            return ("FramedAssembly", 0.5m);
        }

        return ("Assembly", 0.4m);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NormalizeDegrees(double degrees)
    {
        var value = degrees % 360.0;
        return value < 0 ? value + 360.0 : value;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 4);
    }
}