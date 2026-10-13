using System.Globalization;
using System.Text;
using CadToCode.LLM.Prompts;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Services;

public static class AssemblyCodeBuilder
{
    public static string Build(AssemblyTree tree, AssemblyAnalysisResult? analysis, string? className)
    {
        var assemblyName = AssemblyPromptTemplates.ToIdentifier(
            string.IsNullOrWhiteSpace(className) ? analysis?.AssemblyName : className,
            "Assembly");

        var scale = ScaleToMillimeter(tree.Unit);
        var identifiers = AssemblyPromptTemplates.BuildIdentifierMap(tree, analysis);
        var nodes = tree.Nodes.Where(n => identifiers.ContainsKey(n.Id)).ToList();

        var builder = new StringBuilder();
        builder.AppendLine("// All dimensions in millimeters. Positions are relative to the assembly origin (lower-left corner).");

        if (!string.IsNullOrWhiteSpace(analysis?.Summary))
        {
            builder.AppendLine("// " + SingleLine(analysis!.Summary!));
        }

        builder.AppendLine();

        if (nodes.Count == 0)
        {
            builder.AppendLine($"public class {assemblyName}");
            builder.AppendLine("{");
            builder.AppendLine("    public double Width { get; set; } = " + Num(tree.Bounds.Width * scale) + ";");
            builder.AppendLine("    public double Height { get; set; } = " + Num(tree.Bounds.Height * scale) + ";");
            builder.AppendLine("}");
            return builder.ToString();
        }

        foreach (var node in nodes)
        {
            AppendPartClass(builder, node, identifiers[node.Id], tree, scale);
            builder.AppendLine();
        }

        AppendAssemblyClass(builder, assemblyName, tree, nodes, identifiers, analysis, scale);

        return builder.ToString();
    }

    private static void AppendPartClass(
        StringBuilder builder,
        AssemblyNode node,
        string identifier,
        AssemblyTree tree,
        double scale)
    {
        var offsetX = (node.Bounds.MinX - tree.Bounds.MinX) * scale;
        var offsetY = (node.Bounds.MinY - tree.Bounds.MinY) * scale;

        builder.AppendLine($"public class {identifier}");
        builder.AppendLine("{");
        AppendProperty(builder, "Width", node.Bounds.Width * scale);
        AppendProperty(builder, "Height", node.Bounds.Height * scale);
        AppendProperty(builder, "OffsetX", offsetX);
        AppendProperty(builder, "OffsetY", offsetY);

        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "Width", "Height", "OffsetX", "OffsetY", "Shape", "HasHatch"
        };

        foreach (var parameter in node.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var name = AssemblyPromptTemplates.ToPropertyName(parameter.Key);
            if (!reserved.Add(name))
            {
                continue;
            }

            AppendProperty(builder, name, parameter.Value * scale);
        }

        builder.AppendLine($"    public string Shape {{ get; set; }} = \"{node.Shape}\";");
        builder.AppendLine($"    public bool HasHatch {{ get; set; }} = {(node.HatchCount > 0 || node.IsFilled ? "true" : "false")};");
        builder.AppendLine("}");
    }

    private static void AppendAssemblyClass(
        StringBuilder builder,
        string assemblyName,
        AssemblyTree tree,
        List<AssemblyNode> nodes,
        Dictionary<string, string> identifiers,
        AssemblyAnalysisResult? analysis,
        double scale)
    {
        builder.AppendLine($"public class {assemblyName}");
        builder.AppendLine("{");
        AppendProperty(builder, "Width", tree.Bounds.Width * scale);
        AppendProperty(builder, "Height", tree.Bounds.Height * scale);
        builder.AppendLine();

        foreach (var node in nodes)
        {
            var identifier = identifiers[node.Id];
            builder.AppendLine($"    public {identifier} {identifier} {{ get; set; }} = new {identifier}();");
        }

        var relations = tree.Relations
            .Where(r => identifiers.ContainsKey(r.FromId) && identifiers.ContainsKey(r.ToId))
            .ToList();

        if (relations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("    // Relationships");

            foreach (var relation in relations)
            {
                builder.AppendLine("    // " + DescribeRelation(relation, identifiers, scale));
            }
        }

        var arrays = tree.Arrays
            .Where(a => a.MemberIds.Any(identifiers.ContainsKey))
            .ToList();

        if (arrays.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("    // Repeated groups");

            foreach (var group in arrays)
            {
                builder.AppendLine("    // " + DescribeArray(group, identifiers, scale));
            }
        }

        var insights = analysis?.Relationships
            .Where(r => !string.IsNullOrWhiteSpace(r.Summary))
            .ToList();

        if (insights is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("    // Notes");

            foreach (var insight in insights)
            {
                builder.AppendLine("    // " + SingleLine(insight.Summary));
            }
        }

        builder.AppendLine("}");
    }

    private static string DescribeRelation(
        AssemblyRelation relation,
        Dictionary<string, string> identifiers,
        double scale)
    {
        var text = $"{identifiers[relation.FromId]} -> {identifiers[relation.ToId]}: {relation.Type}";

        if (Math.Abs(relation.Gap) > 1e-9)
        {
            text += $", gap {Num(relation.Gap * scale)}";
        }

        if (Math.Abs(relation.OffsetX) > 1e-9 || Math.Abs(relation.OffsetY) > 1e-9)
        {
            text += $", offset ({Num(relation.OffsetX * scale)}, {Num(relation.OffsetY * scale)})";
        }

        return text;
    }

    private static string DescribeArray(
        AssemblyArrayGroup group,
        Dictionary<string, string> identifiers,
        double scale)
    {
        var members = string.Join(", ", group.MemberIds
            .Where(identifiers.ContainsKey)
            .Select(id => identifiers[id]));

        return group.Kind == AssemblyArrayKind.Polar
            ? $"Polar array of {group.Count}: {members}; radius {Num(group.Radius * scale)}, step {Num(group.AngleStepDegrees)} deg"
            : $"Linear array of {group.Count}: {members}; spacing {Num(group.Spacing * scale)}, direction {Num(group.DirectionDegrees)} deg";
    }

    private static void AppendProperty(StringBuilder builder, string name, double value)
    {
        builder.AppendLine($"    public double {name} {{ get; set; }} = {Num(value)};");
    }

    private static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var rounded = Math.Round(value, 3);
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string SingleLine(string value)
    {
        return value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static double ScaleToMillimeter(DrawingUnit unit)
    {
        return unit switch
        {
            DrawingUnit.Centimeter => 10.0,
            DrawingUnit.Meter => 1000.0,
            DrawingUnit.Inch => 25.4,
            DrawingUnit.Foot => 304.8,
            _ => 1.0
        };
    }
}