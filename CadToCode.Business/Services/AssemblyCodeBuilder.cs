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

        if (tree.Assemblies.Count > 0)
        {
            return BuildUniversal(tree, analysis, assemblyName, identifiers, scale);
        }

        foreach (var node in nodes)
        {
            AppendPartClass(builder, node, identifiers[node.Id], tree, scale);
            builder.AppendLine();
        }

        AppendAssemblyClass(builder, assemblyName, tree, nodes, identifiers, analysis, scale);

        return builder.ToString();
    }

    private const int MaxRelationsPerAssembly = 200;

    private static string BuildUniversal(
        AssemblyTree tree,
        AssemblyAnalysisResult? analysis,
        string projectName,
        Dictionary<string, string> identifiers,
        double scale)
    {
        var builder = new StringBuilder();
        var nodeById = tree.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        builder.AppendLine("// All dimensions in millimeters, all angles in degrees.");
        builder.AppendLine("// Assembly offsets are relative to the drawing origin. Part offsets are relative to their assembly.");
        builder.AppendLine("// Project: " + SingleLine(projectName));

        if (!string.IsNullOrWhiteSpace(analysis?.Summary))
        {
            builder.AppendLine("// " + SingleLine(analysis!.Summary!));
        }

        builder.AppendLine();
        AppendUniversalTypes(builder);
        builder.AppendLine();

        builder.AppendLine("public class UniversalProjectLayout");
        builder.AppendLine("{");
        builder.AppendLine("    public List<ComponentAssembly> Assemblies { get; set; } = new();");
        builder.AppendLine("    public CommonProfileCatalog SharedProfiles { get; set; } = new();");
        builder.AppendLine();
        builder.AppendLine("    public static UniversalProjectLayout Create()");
        builder.AppendLine("    {");
        builder.AppendLine("        var layout = new UniversalProjectLayout();");

        var usedVariables = new HashSet<string>(StringComparer.Ordinal);
        var profileVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in tree.SharedProfiles)
        {
            var head = group.NodeIds
                .Select(id => nodeById.TryGetValue(id, out var n) ? n : null)
                .FirstOrDefault(n => n != null);

            if (head == null)
            {
                continue;
            }

            var baseName = identifiers.TryGetValue(head.Id, out var identifier)
                ? identifier
                : AssemblyPromptTemplates.ToIdentifier(group.Name, "Shared" + group.Id);

            var variable = UniqueVariable(char.ToLowerInvariant(baseName[0]) + baseName[1..] + "Profile", usedVariables);
            profileVariables[group.Id] = variable;

            var (orientation, axis) = Orient(head);

            builder.AppendLine();
            builder.AppendLine($"        var {variable} = new SharedProfile // id:{group.Id}");
            builder.AppendLine("        {");
            builder.AppendLine($"            Name = {Literal(baseName)},");
            builder.AppendLine($"            Shape = {Literal(head.Shape.ToString())},");
            builder.AppendLine($"            Width = {Num(head.Bounds.Width * scale)},");
            builder.AppendLine($"            Height = {Num(head.Bounds.Height * scale)},");
            builder.AppendLine($"            Orientation = PartOrientation.{orientation},");
            builder.AppendLine($"            Axis = PartAxis.{axis},");
            builder.AppendLine($"            Angle = {Num(DominantAngle(head))},");
            builder.AppendLine($"            IsHatched = {Bool(head.HatchCount > 0 || head.IsFilled)},");
            builder.AppendLine($"            UsageCount = {group.NodeIds.Count.ToString(CultureInfo.InvariantCulture)},");
            builder.AppendLine("        };");
            builder.AppendLine($"        layout.SharedProfiles.Profiles.Add({variable});");
        }

        for (var index = 0; index < tree.Assemblies.Count; index++)
        {
            var assembly = tree.Assemblies[index];
            var variable = "assembly" + (index + 1).ToString(CultureInfo.InvariantCulture);
            usedVariables.Add(variable);

            var members = assembly.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assemblyNodes = tree.Nodes.Where(n => members.Contains(n.Id)).ToList();

            var name = tree.Assemblies.Count == 1 && !string.IsNullOrWhiteSpace(analysis?.AssemblyName)
                ? analysis!.AssemblyName
                : assembly.AssemblyName;

            builder.AppendLine();
            builder.AppendLine($"        var {variable} = new ComponentAssembly // id:{assembly.Id}");
            builder.AppendLine("        {");
            builder.AppendLine($"            AssemblyName = {Literal(name)},");
            builder.AppendLine($"            Category = {Literal(assembly.Category)},");
            builder.AppendLine($"            Width = {Num(assembly.Bounds.Width * scale)},");
            builder.AppendLine($"            Height = {Num(assembly.Bounds.Height * scale)},");
            builder.AppendLine($"            OffsetX = {Num((assembly.Bounds.MinX - tree.Bounds.MinX) * scale)},");
            builder.AppendLine($"            OffsetY = {Num((assembly.Bounds.MinY - tree.Bounds.MinY) * scale)},");
            builder.AppendLine("        };");

            foreach (var node in assemblyNodes)
            {
                AppendUniversalNode(builder, variable, node, assembly, identifiers, profileVariables, scale);
            }

            var relationCount = 0;
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var relation in tree.Relations)
            {
                if (!members.Contains(relation.FromId) || !members.Contains(relation.ToId)
                    || !identifiers.ContainsKey(relation.FromId) || !identifiers.ContainsKey(relation.ToId))
                {
                    continue;
                }

                if (relationCount >= MaxRelationsPerAssembly)
                {
                    break;
                }

                var key = identifiers[relation.FromId] + "->" + identifiers[relation.ToId];
                var unique = key;
                var counter = 2;
                while (!usedKeys.Add(unique))
                {
                    unique = key + "#" + counter.ToString(CultureInfo.InvariantCulture);
                    counter++;
                }

                builder.AppendLine($"        {variable}.SpatialRelations[{Literal(unique)}] = {Literal(DescribeRelationValue(relation, scale))};");
                relationCount++;
            }

            builder.AppendLine($"        layout.Assemblies.Add({variable});");
        }

        builder.AppendLine();
        builder.AppendLine("        return layout;");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendUniversalNode(
        StringBuilder builder,
        string assemblyVariable,
        AssemblyNode node,
        ComponentAssembly assembly,
        Dictionary<string, string> identifiers,
        Dictionary<string, string> profileVariables,
        double scale)
    {
        var identifier = identifiers.TryGetValue(node.Id, out var value) ? value : "Part" + node.Id;
        var (orientation, axis) = Orient(node);
        var shared = node.SharedProfileId != null && profileVariables.TryGetValue(node.SharedProfileId, out var profileVariable)
            ? profileVariable
            : null;

        builder.AppendLine();
        builder.AppendLine($"        {assemblyVariable}.Nodes.Add(new SubComponentNode // id:{node.Id}");
        builder.AppendLine("        {");
        builder.AppendLine($"            Id = {Literal(node.Id)},");
        builder.AppendLine($"            Name = {Literal(identifier)},");
        builder.AppendLine($"            Shape = {Literal(node.Shape.ToString())},");
        builder.AppendLine($"            Orientation = PartOrientation.{orientation},");
        builder.AppendLine($"            Axis = PartAxis.{axis},");
        builder.AppendLine($"            Angle = {Num(DominantAngle(node))},");
        builder.AppendLine($"            IsHatched = {Bool(node.HatchCount > 0 || node.IsFilled)},");
        builder.AppendLine($"            OffsetX = {Num((node.Bounds.MinX - assembly.Bounds.MinX) * scale)},");
        builder.AppendLine($"            OffsetY = {Num((node.Bounds.MinY - assembly.Bounds.MinY) * scale)},");

        if (shared != null)
        {
            builder.AppendLine($"            Profile = {shared},");
        }
        else
        {
            builder.AppendLine($"            Width = {Num(node.Bounds.Width * scale)},");
            builder.AppendLine($"            Height = {Num(node.Bounds.Height * scale)},");

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "width", "height", "centerX", "centerY" };
            var parameters = node.Parameters
                .Where(p => !reserved.Contains(p.Key))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToList();

            if (parameters.Count > 0)
            {
                builder.AppendLine("            Parameters =");
                builder.AppendLine("            {");

                foreach (var parameter in parameters)
                {
                    builder.AppendLine($"                [{Literal(parameter.Key)}] = {Num(parameter.Value * scale)},");
                }

                builder.AppendLine("            },");
            }
        }

        builder.AppendLine("        });");
    }

    private static void AppendUniversalTypes(StringBuilder builder)
    {
        var lines = new[]
        {
            "public enum PartOrientation { Vertical, Horizontal, Neutral }",
            "",
            "public enum PartAxis { X, Y, None }",
            "",
            "public class SharedProfile",
            "{",
            "    public string Name { get; set; } = string.Empty;",
            "    public string Shape { get; set; } = string.Empty;",
            "    public double Width { get; set; }",
            "    public double Height { get; set; }",
            "    public PartOrientation Orientation { get; set; }",
            "    public PartAxis Axis { get; set; }",
            "    public double Angle { get; set; }",
            "    public bool IsHatched { get; set; }",
            "    public int UsageCount { get; set; }",
            "}",
            "",
            "public class CommonProfileCatalog",
            "{",
            "    public List<SharedProfile> Profiles { get; set; } = new();",
            "}",
            "",
            "public class SubComponentNode",
            "{",
            "    public string Id { get; set; } = string.Empty;",
            "    public string Name { get; set; } = string.Empty;",
            "    public string Shape { get; set; } = string.Empty;",
            "    public PartOrientation Orientation { get; set; }",
            "    public PartAxis Axis { get; set; }",
            "    public double Angle { get; set; }",
            "    public bool IsHatched { get; set; }",
            "    public double OffsetX { get; set; }",
            "    public double OffsetY { get; set; }",
            "    public double Width { get; set; }",
            "    public double Height { get; set; }",
            "    public SharedProfile? Profile { get; set; }",
            "    public Dictionary<string, double> Parameters { get; set; } = new();",
            "}",
            "",
            "public class ComponentAssembly",
            "{",
            "    public string AssemblyName { get; set; } = string.Empty;",
            "    public string Category { get; set; } = string.Empty;",
            "    public double Width { get; set; }",
            "    public double Height { get; set; }",
            "    public double OffsetX { get; set; }",
            "    public double OffsetY { get; set; }",
            "    public List<SubComponentNode> Nodes { get; set; } = new();",
            "    public Dictionary<string, string> SpatialRelations { get; set; } = new();",
            "}"
        };

        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }
    }

    private static (string Orientation, string Axis) Orient(AssemblyNode node)
    {
        if (node.Shape is PartShape.Circular or PartShape.Annular)
        {
            return ("Neutral", "None");
        }

        var width = node.Bounds.Width;
        var height = node.Bounds.Height;

        if (height > width * 1.2)
        {
            return ("Vertical", "Y");
        }

        if (width > height * 1.2)
        {
            return ("Horizontal", "X");
        }

        return ("Neutral", "None");
    }

    private static double DominantAngle(AssemblyNode node)
    {
        var lines = node.Geometry?.Lines;
        if (lines == null || lines.Count == 0)
        {
            return 0;
        }

        var longest = lines.OrderByDescending(l => l.Length).First();
        var dx = longest.End.X - longest.Start.X;
        var dy = longest.End.Y - longest.Start.Y;

        if (double.IsNaN(dx) || double.IsNaN(dy) || (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12))
        {
            return 0;
        }

        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI % 180.0;
        if (angle < 0)
        {
            angle += 180.0;
        }

        if (angle < 0.5 || angle > 179.5)
        {
            return 0;
        }

        if (Math.Abs(angle - 90.0) < 0.5)
        {
            return 90;
        }

        return Math.Round(angle, 2);
    }

    private static string DescribeRelationValue(AssemblyRelation relation, double scale)
    {
        var text = relation.Type.ToString();

        if (Math.Abs(relation.Gap) > 1e-9)
        {
            text += ", gap " + Num(relation.Gap * scale);
        }

        if (Math.Abs(relation.OffsetX) > 1e-9 || Math.Abs(relation.OffsetY) > 1e-9)
        {
            text += ", offset (" + Num(relation.OffsetX * scale) + ", " + Num(relation.OffsetY * scale) + ")";
        }

        return text;
    }

    private static string UniqueVariable(string candidate, HashSet<string> used)
    {
        var unique = candidate;
        var counter = 2;

        while (!used.Add(unique))
        {
            unique = candidate + counter.ToString(CultureInfo.InvariantCulture);
            counter++;
        }

        return unique;
    }

    private static string Literal(string? value)
    {
        var text = SingleLine(value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return "\"" + text + "\"";
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
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