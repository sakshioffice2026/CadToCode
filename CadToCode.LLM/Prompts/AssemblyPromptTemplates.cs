using System.Globalization;
using System.Text;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Prompts;

public static class AssemblyPromptTemplates
{
    public const string AnalysisSystemPrompt =
        "You name parts of mechanical and architectural CAD assemblies and explain how they relate. "
        + "All measurements are computed by code and are final. You never change or invent numbers. "
        + "You reply with one JSON object and nothing else. No markdown, no commentary.";

    public const string CodeSystemPrompt =
        "You write short, clean C# data classes for CAD assemblies. "
        + "All measurements are computed by code and are final. You never change or invent numbers. "
        + "You reply with C# code only. No markdown fences, no commentary.";

    private const string AnalysisJsonShape =
        "{\"assemblyName\":\"<short name>\",\"summary\":\"<one short sentence>\",\"confidence\":0.0,"
        + "\"nodes\":[{\"nodeId\":\"<id from the list>\",\"name\":\"<short name>\",\"role\":\"<short role>\",\"confidence\":0.0}],"
        + "\"relationships\":[{\"fromId\":\"<id>\",\"toId\":\"<id>\",\"summary\":\"<one short sentence>\"}]}";

    private static readonly int[] DetailLevels = { 60, 30, 15, 8 };

    public static string BuildAnalysisPrompt(AssemblyAnalysisRequest request, int maxChars)
    {
        var user = string.Empty;

        foreach (var limit in DetailLevels)
        {
            user = BuildAnalysisBody(request, limit);
            if (Wrap(AnalysisSystemPrompt, user).Length <= maxChars)
            {
                return Wrap(AnalysisSystemPrompt, user);
            }
        }

        return WrapWithinLimit(AnalysisSystemPrompt, user, maxChars);
    }

    public static string BuildCodePrompt(CodeSynthesisRequest request, int maxChars)
    {
        var user = string.Empty;

        foreach (var limit in DetailLevels)
        {
            user = BuildCodeBody(request, limit);
            if (Wrap(CodeSystemPrompt, user).Length <= maxChars)
            {
                return Wrap(CodeSystemPrompt, user);
            }
        }

        return WrapWithinLimit(CodeSystemPrompt, user, maxChars);
    }

    public static Dictionary<string, string> BuildIdentifierMap(AssemblyTree tree, AssemblyAnalysisResult? analysis)
    {
        var semantic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (analysis != null)
        {
            foreach (var item in analysis.Nodes)
            {
                if (!string.IsNullOrWhiteSpace(item.NodeId) && !string.IsNullOrWhiteSpace(item.Name))
                {
                    semantic[item.NodeId] = item.Name;
                }
            }
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in tree.Nodes)
        {
            var source = semantic.TryGetValue(node.Id, out var name) ? name : node.Name;
            var identifier = ToIdentifier(source, "Part" + node.Id);
            var unique = identifier;
            var counter = 2;

            while (!used.Add(unique))
            {
                unique = identifier + counter.ToString(CultureInfo.InvariantCulture);
                counter++;
            }

            map[node.Id] = unique;
        }

        return map;
    }

    public static string ToIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder();
        var startOfWord = true;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                builder.Append(startOfWord ? char.ToUpperInvariant(ch) : ch);
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }

        if (builder.Length == 0)
        {
            return fallback;
        }

        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, "Part");
        }

        return builder.Length > 48 ? builder.ToString(0, 48) : builder.ToString();
    }

    public static string ToPropertyName(string key)
    {
        return ToIdentifier(key, "Value");
    }

    private static string BuildAnalysisBody(AssemblyAnalysisRequest request, int limit)
    {
        var tree = request.Tree;
        var nodes = SelectNodes(tree, limit);
        var ids = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        var user = new StringBuilder();

        user.AppendLine("Analyze this CAD assembly. Name each node by its function, describe how nodes relate, and give the assembly a short name.");
        user.AppendLine("Use only node ids from the list. Do not change any measurement.");
        user.AppendLine();
        user.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Assembly size: {0} x {1}, {2} nodes.",
            Num(tree.Bounds.Width),
            Num(tree.Bounds.Height),
            tree.Nodes.Count));

        AppendNodes(user, nodes, null);
        AppendArrays(user, tree, ids, null);
        AppendRelations(user, tree, ids, limit * 2, null);
        AppendPatterns(user, request.SimilarPatterns);

        user.AppendLine();
        user.AppendLine("Reply with exactly this JSON shape:");
        user.AppendLine(AnalysisJsonShape);

        return user.ToString().TrimEnd();
    }

    private static string BuildCodeBody(CodeSynthesisRequest request, int limit)
    {
        var tree = request.Tree;
        var identifiers = BuildIdentifierMap(tree, request.Analysis);
        var className = ToIdentifier(request.ClassName, "Assembly");
        var nodes = SelectNodes(tree, limit);
        var ids = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        var user = new StringBuilder();

        user.AppendLine("Write C# for this CAD assembly.");
        user.AppendLine("Rules:");
        user.AppendLine("- Output only C# code. No markdown fences, no explanation.");
        user.AppendLine($"- Write one public class named {className}. No namespace. No using directives.");
        user.AppendLine("- Inside it, write one nested public class per node, using the class name given for that node.");
        user.AppendLine("- Each nested class has one public double property per parameter, set to the exact given value. Property names are the parameter names in PascalCase.");
        user.AppendLine("- When an offset is given for a node, add public double OffsetX and OffsetY set to the exact values.");
        user.AppendLine("- For each array add public int <Name>Count and public double <Name>Spacing. Polar arrays also get <Name>Radius and <Name>AngleStep.");
        user.AppendLine("- Use only the numbers listed below. Never invent numbers.");
        user.AppendLine("- No file, network, reflection, process or unsafe code.");

        if (request.Analysis != null && !string.IsNullOrWhiteSpace(request.Analysis.Summary))
        {
            user.AppendLine();
            user.AppendLine("Assembly summary: " + Truncate(request.Analysis.Summary, 200));
        }

        AppendNodes(user, nodes, identifiers);
        AppendArrays(user, tree, ids, identifiers);
        AppendRelations(user, tree, ids, limit * 2, identifiers);
        AppendPatterns(user, request.SimilarPatterns);

        user.AppendLine();
        user.AppendLine("Write the C# code now.");

        return user.ToString().TrimEnd();
    }

    private static List<AssemblyNode> SelectNodes(AssemblyTree tree, int limit)
    {
        return tree.Nodes
            .OrderByDescending(n => n.Bounds.Area)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void AppendNodes(StringBuilder user, List<AssemblyNode> nodes, Dictionary<string, string>? identifiers)
    {
        user.AppendLine();
        user.AppendLine("Nodes:");

        foreach (var node in nodes)
        {
            var line = new StringBuilder();
            line.Append("- ").Append(node.Id);

            if (identifiers != null && identifiers.TryGetValue(node.Id, out var identifier))
            {
                line.Append(" | class ").Append(identifier);
            }

            line.Append(" | ").Append(node.Shape);

            if (node.Parameters.Count > 0)
            {
                var parameters = node.Parameters
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => (identifiers != null ? ToPropertyName(p.Key) : p.Key) + "=" + Num(p.Value));
                line.Append(" | ").Append(string.Join(" ", parameters));
            }

            line.Append(string.Format(
                CultureInfo.InvariantCulture,
                " | lines {0}, arcs {1}, circles {2}, polylines {3}, hatches {4}",
                node.LineCount,
                node.ArcCount,
                node.CircleCount,
                node.PolylineCount,
                node.HatchCount));

            var labels = node.Labels
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => Truncate(l.Trim(), 40))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            if (labels.Count > 0)
            {
                line.Append(" | labels: ").Append(string.Join("; ", labels));
            }

            if (node.ParentId != null)
            {
                line.Append(" | inside ").Append(node.ParentId);
            }

            user.AppendLine(line.ToString());
        }
    }

    private static void AppendArrays(
        StringBuilder user,
        AssemblyTree tree,
        HashSet<string> ids,
        Dictionary<string, string>? identifiers)
    {
        var arrays = tree.Arrays
            .Where(a => a.MemberIds.Any(ids.Contains))
            .Take(10)
            .ToList();

        if (arrays.Count == 0)
        {
            return;
        }

        user.AppendLine();
        user.AppendLine("Arrays:");

        foreach (var array in arrays)
        {
            var name = identifiers != null && array.MemberIds.Count > 0 && identifiers.TryGetValue(array.MemberIds[0], out var id)
                ? id + "Array"
                : array.Id;

            var line = new StringBuilder();
            line.Append("- ").Append(array.Id);

            if (identifiers != null)
            {
                line.Append(" | name ").Append(name);
            }

            line.Append(" | ").Append(array.Kind);
            line.Append(" | members ").Append(string.Join(",", array.MemberIds.Take(12)));
            line.Append(" | count=").Append(array.Count.ToString(CultureInfo.InvariantCulture));
            line.Append(" spacing=").Append(Num(array.Spacing));

            if (array.Kind == AssemblyArrayKind.Polar)
            {
                line.Append(" radius=").Append(Num(array.Radius));
                line.Append(" angleStep=").Append(Num(array.AngleStepDegrees));
            }
            else
            {
                line.Append(" direction=").Append(Num(array.DirectionDegrees));
            }

            user.AppendLine(line.ToString());
        }
    }

    private static void AppendRelations(
        StringBuilder user,
        AssemblyTree tree,
        HashSet<string> ids,
        int limit,
        Dictionary<string, string>? identifiers)
    {
        var relations = tree.Relations
            .Where(r => ids.Contains(r.FromId) && ids.Contains(r.ToId))
            .Take(Math.Max(1, limit))
            .ToList();

        if (relations.Count == 0)
        {
            return;
        }

        user.AppendLine();
        user.AppendLine("Relations:");

        foreach (var relation in relations)
        {
            var from = identifiers != null && identifiers.TryGetValue(relation.FromId, out var f) ? f : relation.FromId;
            var to = identifiers != null && identifiers.TryGetValue(relation.ToId, out var t) ? t : relation.ToId;

            user.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- {0} -> {1} | {2} | {3} to {4} | gap={5} offsetX={6} offsetY={7}",
                from,
                to,
                relation.Type,
                relation.FromAnchor ?? "-",
                relation.ToAnchor ?? "-",
                Num(relation.Gap),
                Num(relation.OffsetX),
                Num(relation.OffsetY)));
        }
    }

    private static void AppendPatterns(StringBuilder user, List<ClassificationPatternHint> patterns)
    {
        var hints = patterns.Take(3).ToList();
        if (hints.Count == 0)
        {
            return;
        }

        user.AppendLine();
        user.AppendLine("Similar known patterns (hints only):");

        foreach (var pattern in hints)
        {
            user.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "- {0} (similarity {1:0.00}): {2}",
                pattern.ComponentType,
                pattern.Similarity,
                Truncate(pattern.Description, 160)));
        }
    }

    private static string Wrap(string system, string user)
    {
        return "<|system|>\n" + system + "<|end|>\n"
            + "<|user|>\n" + user + "<|end|>\n"
            + "<|assistant|>\n";
    }

    private static string WrapWithinLimit(string system, string user, int maxChars)
    {
        var overhead = Wrap(system, string.Empty).Length;
        var allowed = Math.Max(200, maxChars - overhead);

        if (user.Length > allowed)
        {
            user = user[..allowed];
        }

        return Wrap(system, user);
    }

    private static string Num(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }
}