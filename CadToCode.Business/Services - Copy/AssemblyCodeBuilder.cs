using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.LLM.Prompts;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Services;

public static class AssemblyCodeBuilder
{
    private const int ValuesPerRow = 4;
    private const double ThicknessRatio = 0.05;
    private const double MinimumLocalArea = 1e-6;
    private const double MinimumLocalRadius = 0.0005;

    private static readonly Regex EntityMarkerPattern = new(
        @"\s*//\s*id:(?<id>[A-Za-z0-9_\-]+)\s*$",
        RegexOptions.Compiled);

    private static readonly string[] ReservedNames =
    {
        "doc", "outputPath", "profile", "Thickness"
    };

    private sealed record Frame(double OriginX, double OriginY, double Scale)
    {
        public double MX(double value) => (value - OriginX) * Scale;

        public double MY(double value) => (value - OriginY) * Scale;
    }

    private sealed record ProfileEntry(
        string Name,
        string Layer,
        List<(double X, double Y)> Outline,
        List<List<(double X, double Y)>> Holes,
        bool IsCircle,
        double Cx,
        double Cy,
        double Radius);

    public static string ToEntityHtml(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return string.Empty;
        }

        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder();
        string? current = null;
        var blockIndent = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var text = line;
            string? id = null;

            var match = EntityMarkerPattern.Match(line);
            if (match.Success)
            {
                id = match.Groups["id"].Value;
                text = line[..match.Index];

                if (!text.TrimEnd().EndsWith(';'))
                {
                    current = id;
                    blockIndent = LeadingSpaces(text);
                }
            }
            else if (current != null)
            {
                id = current;

                var trimmed = text.Trim();
                if (LeadingSpaces(text) == blockIndent && (trimmed == "}" || trimmed == "};" || trimmed == "});"))
                {
                    current = null;
                }
            }

            var encoded = System.Net.WebUtility.HtmlEncode(text);

            if (id != null)
            {
                builder.Append("<span class=\"cc-entity-line\" data-entity-id=\"")
                    .Append(System.Net.WebUtility.HtmlEncode(id))
                    .Append("\">")
                    .Append(encoded)
                    .Append("</span>");
            }
            else
            {
                builder.Append(encoded);
            }

            if (i < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string Build(AssemblyTree tree, AssemblyAnalysisResult? analysis, string? className)
    {
        var scriptName = AssemblyPromptTemplates.ToIdentifier(
            string.IsNullOrWhiteSpace(className) ? analysis?.AssemblyName : className,
            "Assembly");

        var identifiers = AssemblyPromptTemplates.BuildIdentifierMap(tree, analysis);
        var nodes = tree.Nodes.Where(n => identifiers.ContainsKey(n.Id)).ToList();

        var bounds = EffectiveBounds(tree, nodes);
        var scale = ScaleToMillimeter(tree.Unit);
        var frame = new Frame(bounds.MinX, bounds.MinY, scale);
        var spanDrawing = Math.Max(bounds.Width, bounds.Height);
        var tolerance = ToleranceFor(spanDrawing);

        var header = new List<string>
        {
            "All dimensions in millimeters.",
            "Every closed contour found in the drawing becomes a CustomProfile that is extruded along Z by Thickness.",
            "Full circles become cylinders; nested contours become holes.",
            "Origin (0, 0) is the lower-left corner of the drawing.",
            "Project: " + scriptName
        };

        if (!string.IsNullOrWhiteSpace(analysis?.Summary))
        {
            header.Add(analysis!.Summary!);
        }

        var builder = new StringBuilder();
        AppendScriptStart(builder, header, ThicknessFor(spanDrawing * scale), scriptName);

        var used = new HashSet<string>(ReservedNames, StringComparer.Ordinal);

        if (nodes.Count == 0)
        {
            builder.AppendLine("// No sub-components were detected in the drawing, so no solid was created.");
            builder.AppendLine();
            AppendScriptEnd(builder, scriptName);
            return builder.ToString();
        }

        foreach (var group in GroupNodes(tree, analysis, nodes))
        {
            if (group.Name != null)
            {
                builder.AppendLine("// ==== Assembly: " + SingleLine(group.Name) + " ====");
                builder.AppendLine();
            }

            foreach (var node in group.Nodes)
            {
                var identifier = identifiers[node.Id];
                var baseName = char.ToLowerInvariant(identifier[0]) + identifier[1..];

                builder.AppendLine(
                    $"// ---- {identifier} ({node.Shape}, {Num(node.Bounds.Width * scale)} x {Num(node.Bounds.Height * scale)} mm) ----");

                var count = AppendSolids(builder, node.Geometry ?? new ExtractedGeometry(), baseName, identifier, node.Id, frame, tolerance, used);

                if (count == 0)
                {
                    builder.AppendLine("// No closed contour was found in this part, so no solid was created.");
                    builder.AppendLine();
                }
            }
        }

        AppendScriptEnd(builder, scriptName);
        return builder.ToString();
    }

    public static string BuildGeometryScript(
        ExtractedGeometry geometry,
        string? scriptName,
        IReadOnlyList<string>? notes)
    {
        var name = AssemblyPromptTemplates.ToIdentifier(scriptName, "Drawing");
        var bounds = geometry.Bounds;
        var scale = ScaleToMillimeter(geometry.DetectedUnit);
        var frame = new Frame(bounds?.MinX ?? 0, bounds?.MinY ?? 0, scale);
        var spanDrawing = bounds is null ? 0 : Math.Max(bounds.Width, bounds.Height);
        var tolerance = ToleranceFor(spanDrawing);

        var header = new List<string>
        {
            "All dimensions in millimeters.",
            "Every closed contour found in the drawing becomes a CustomProfile that is extruded along Z by Thickness.",
            "Full circles become cylinders; nested contours become holes.",
            bounds is null
                ? "Coordinates are kept as extracted from the drawing."
                : "Origin (0, 0) is the lower-left corner of the drawing.",
            "Project: " + name
        };

        if (notes is { Count: > 0 })
        {
            header.Add("Recognized components:");

            foreach (var note in notes)
            {
                header.Add("  " + note);
            }
        }

        var builder = new StringBuilder();
        AppendScriptStart(builder, header, ThicknessFor(spanDrawing * scale), name);

        var used = new HashSet<string>(ReservedNames, StringComparer.Ordinal);

        var count = AppendSolids(builder, geometry, "drawing", name, null, frame, tolerance, used);

        if (count == 0)
        {
            builder.AppendLine("// No closed contour was found in the source drawing, so no solid was created.");
            builder.AppendLine();
        }

        AppendScriptEnd(builder, name);
        return builder.ToString();
    }

    private static BoundingBox2D EffectiveBounds(AssemblyTree tree, List<AssemblyNode> nodes)
    {
        if (tree.Bounds.Width > 0 || tree.Bounds.Height > 0)
        {
            return tree.Bounds;
        }

        if (nodes.Count == 0)
        {
            return tree.Bounds;
        }

        var union = nodes[0].Bounds;
        foreach (var node in nodes)
        {
            union = union.Union(node.Bounds);
        }

        return union;
    }

    private static double ToleranceFor(double spanDrawing)
    {
        if (!double.IsFinite(spanDrawing) || spanDrawing <= 0)
        {
            return 1e-6;
        }

        return Math.Max(1e-9, spanDrawing * 1e-4);
    }

    private static double ThicknessFor(double spanMillimeters)
    {
        if (!double.IsFinite(spanMillimeters) || spanMillimeters <= 0)
        {
            return 10;
        }

        return Math.Max(1.0, Math.Round(spanMillimeters * ThicknessRatio, 1));
    }

    private static void AppendScriptStart(StringBuilder builder, IEnumerable<string> header, double thickness, string scriptName)
    {
        foreach (var line in header)
        {
            builder.AppendLine("// " + SingleLine(line));
        }

        builder.AppendLine("// Requires the CadToCode.Business library (namespace CadToCode.Step).");
        builder.AppendLine();
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.IO;");
        builder.AppendLine("using System.Linq;");
        builder.AppendLine("using CadToCode.Step;");
        builder.AppendLine();
        builder.AppendLine("// Extrusion depth along Z, in millimeters. Change it and press Run to rebuild the solids.");
        builder.AppendLine("const double Thickness = " + Num(thickness) + ";");
        builder.AppendLine();
        builder.AppendLine("var doc = new StepDocument(" + Literal(scriptName) + ");");
        builder.AppendLine();
    }

    private static void AppendScriptEnd(StringBuilder builder, string scriptName)
    {
        builder.AppendLine("var outputPath = Path.GetFullPath(" + Literal(scriptName + ".step") + ");");
        builder.AppendLine("doc.Save(outputPath);");
        builder.AppendLine("Console.WriteLine(\"STEP saved to \" + outputPath + \" (\" + doc.SolidCount + \" solids)\");");
    }

    private static int AppendSolids(
        StringBuilder builder,
        ExtractedGeometry geometry,
        string baseName,
        string displayName,
        string? id,
        Frame frame,
        double tolerance,
        HashSet<string> used)
    {
        var marker = id == null ? string.Empty : " // id:" + id;
        var contours = ContourMapper.Map(geometry, tolerance);

        if (contours.Count == 0)
        {
            return 0;
        }

        var solidIndexes = Enumerable.Range(0, contours.Count).Where(i => contours[i].IsSolid).ToList();
        var entries = new List<ProfileEntry>();

        foreach (var index in solidIndexes)
        {
            var contour = contours[index];
            var outline = ToLocal(contour, frame);

            if (outline is null)
            {
                continue;
            }

            var holes = new List<List<(double X, double Y)>>();

            for (var i = 0; i < contours.Count; i++)
            {
                if (contours[i].ParentIndex != index || contours[i].Depth % 2 != 1)
                {
                    continue;
                }

                var hole = ToLocal(contours[i], frame);
                if (hole is not null)
                {
                    holes.Add(hole);
                }
            }

            var radius = Math.Round(contour.Radius * frame.Scale, 3);
            var isCircle = contour.IsCircle && holes.Count == 0 && radius >= MinimumLocalRadius;

            entries.Add(new ProfileEntry(
                string.Empty,
                string.IsNullOrWhiteSpace(contour.Layer) ? "0" : contour.Layer!,
                outline,
                holes,
                isCircle,
                Math.Round(frame.MX(contour.CenterX), 3),
                Math.Round(frame.MY(contour.CenterY), 3),
                radius));
        }

        if (entries.Count == 0)
        {
            return 0;
        }

        var variable = UniqueVariable(baseName + "Profiles", used);

        builder.AppendLine($"var {variable} = new CustomProfile[]{marker}");
        builder.AppendLine("{");

        for (var n = 0; n < entries.Count; n++)
        {
            var entry = entries[n];
            var name = entries.Count > 1
                ? displayName + " " + (n + 1).ToString(CultureInfo.InvariantCulture)
                : displayName;

            if (entry.IsCircle)
            {
                builder.AppendLine(
                    $"    CustomProfile.Circle({Literal(name)}, {Literal(entry.Layer)}, {Num(entry.Cx)}, {Num(entry.Cy)}, {Num(entry.Radius)}),");
                continue;
            }

            builder.AppendLine($"    new CustomProfile({Literal(name)}, {Literal(entry.Layer)},");
            builder.AppendLine("        new (double X, double Y)[]");
            builder.AppendLine("        {");
            AppendWrapped(builder, entry.Outline.Select(p => $"({Num(p.X)}, {Num(p.Y)})"), "            ");

            if (entry.Holes.Count == 0)
            {
                builder.AppendLine("        }),");
                continue;
            }

            builder.AppendLine("        },");
            builder.AppendLine("        new (double X, double Y)[][]");
            builder.AppendLine("        {");

            foreach (var hole in entry.Holes)
            {
                builder.AppendLine("            new (double X, double Y)[]");
                builder.AppendLine("            {");
                AppendWrapped(builder, hole.Select(p => $"({Num(p.X)}, {Num(p.Y)})"), "                ");
                builder.AppendLine("            },");
            }

            builder.AppendLine("        }),");
        }

        builder.AppendLine("};");
        builder.AppendLine($"foreach (var profile in {variable}){marker}");
        builder.AppendLine("{");
        builder.AppendLine("    doc.Add.Extrude(profile, Thickness);");
        builder.AppendLine("}");
        builder.AppendLine();

        return entries.Count;
    }

    private static List<(double X, double Y)>? ToLocal(GeometryContour contour, Frame frame)
    {
        var points = new List<(double X, double Y)>(contour.Points.Count);

        foreach (var point in contour.Points)
        {
            var x = Math.Round(frame.MX(point.X), 3);
            var y = Math.Round(frame.MY(point.Y), 3);

            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return null;
            }

            if (points.Count == 0 || points[^1].X != x || points[^1].Y != y)
            {
                points.Add((x, y));
            }
        }

        while (points.Count > 1 && points[0].X == points[^1].X && points[0].Y == points[^1].Y)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3 || Math.Abs(SignedArea(points)) <= MinimumLocalArea)
        {
            return null;
        }

        return points;
    }

    private static double SignedArea(List<(double X, double Y)> points)
    {
        var sum = 0.0;

        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            sum += points[i].X * next.Y - next.X * points[i].Y;
        }

        return sum / 2.0;
    }

    private static void AppendWrapped(StringBuilder builder, IEnumerable<string> items, string indent)
    {
        var row = new List<string>();

        foreach (var item in items)
        {
            row.Add(item);

            if (row.Count == ValuesPerRow)
            {
                builder.AppendLine(indent + string.Join(", ", row) + ",");
                row.Clear();
            }
        }

        if (row.Count > 0)
        {
            builder.AppendLine(indent + string.Join(", ", row) + ",");
        }
    }

    private static List<(string? Name, List<AssemblyNode> Nodes)> GroupNodes(
        AssemblyTree tree,
        AssemblyAnalysisResult? analysis,
        List<AssemblyNode> nodes)
    {
        var groups = new List<(string? Name, List<AssemblyNode> Nodes)>();

        if (tree.Assemblies.Count == 0)
        {
            groups.Add((null, nodes));
            return groups;
        }

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in tree.Assemblies)
        {
            var members = assembly.NodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = nodes.Where(n => members.Contains(n.Id) && placed.Add(n.Id)).ToList();

            if (list.Count == 0)
            {
                continue;
            }

            var name = tree.Assemblies.Count == 1 && !string.IsNullOrWhiteSpace(analysis?.AssemblyName)
                ? analysis!.AssemblyName
                : assembly.AssemblyName;

            groups.Add((name, list));
        }

        var rest = nodes.Where(n => placed.Add(n.Id)).ToList();
        if (rest.Count > 0)
        {
            groups.Add(("Unassigned", rest));
        }

        return groups;
    }

    private static int LeadingSpaces(string text)
    {
        var count = 0;
        while (count < text.Length && text[count] == ' ')
        {
            count++;
        }

        return count;
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

    private static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var rounded = Math.Round(value, 3);

        return rounded == 0 ? "0" : rounded.ToString("0.###", CultureInfo.InvariantCulture);
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