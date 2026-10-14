using System.Globalization;
using System.Text;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Services;

public sealed class GeometryFeatureContext
{
    public IReadOnlyList<string> Profiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> KindCounts { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Profiles.Count == 0 && Features.Count == 0 && Parameters.Count == 0;
}

public static class GeometryContextBuilder
{
    private static readonly int[] DetailLevels = { 24, 14, 8, 4, 2 };

    public static string Build(
        ProjectResultDto project,
        AssemblyTree? tree,
        int maxChars,
        IReadOnlyList<VectorSearchHit>? templates,
        int maxTemplateChars,
        GeometryFeatureContext? features = null)
    {
        var geometry = Build(project, tree, maxChars, features);
        var section = BuildTemplatesSection(templates, maxTemplateChars);

        return section.Length == 0
            ? geometry
            : geometry + Environment.NewLine + section;
    }

    public static string BuildRetrievalQuery(
        ProjectResultDto? project,
        AssemblyTree? tree,
        string? prompt,
        GeometryFeatureContext? features = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            sb.AppendLine("Request: " + Clean(prompt, 800));
        }

        if (features is { KindCounts.Count: > 0 })
        {
            sb.AppendLine("Features: " + string.Join(", ", features.KindCounts.Take(6).Select(k => Clean(k, 40))));
        }

        if (project is not null)
        {
            sb.AppendLine("Drawing: " + Clean(project.Title, 80));
            sb.AppendLine("Source: " + project.SourceFileType);

            var kinds = project.Components
                .GroupBy(c => c.ComponentType.ToString())
                .OrderByDescending(g => g.Count())
                .Take(8)
                .Select(g => g.Key + " x" + g.Count())
                .ToList();

            if (kinds.Count > 0)
            {
                sb.AppendLine("Components: " + string.Join(", ", kinds));
            }
        }

        if (tree is not null && tree.Nodes.Count > 0)
        {
            var shapes = tree.Nodes
                .GroupBy(n => n.Shape.ToString())
                .OrderByDescending(g => g.Count())
                .Take(6)
                .Select(g => g.Key + " x" + g.Count())
                .ToList();

            if (shapes.Count > 0)
            {
                sb.AppendLine("Shapes: " + string.Join(", ", shapes));
            }

            var assemblies = tree.Assemblies
                .Select(a => Clean(a.AssemblyName, 40) + " (" + a.Category + ")")
                .Take(6)
                .ToList();

            if (assemblies.Count > 0)
            {
                sb.AppendLine("Assemblies: " + string.Join(", ", assemblies));
            }

            var labels = tree.Nodes
                .SelectMany(n => n.Labels)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => Clean(l, 40))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            if (labels.Count > 0)
            {
                sb.AppendLine("Texts: " + string.Join(" | ", labels));
            }
        }

        return sb.ToString().Trim();
    }

    public static string BuildTemplatesSection(IReadOnlyList<VectorSearchHit>? templates, int maxChars)
    {
        if (templates is null || templates.Count == 0 || maxChars < 200)
        {
            return string.Empty;
        }

        var perTemplate = Math.Max(150, maxChars / templates.Count);
        var sb = new StringBuilder();

        sb.AppendLine("Reference templates from the CAD library (most similar first, adapt rather than copy):");

        var index = 1;

        foreach (var t in templates)
        {
            sb.AppendLine();
            sb.AppendLine("[" + index + "] " + Clean(t.Title, 60) + " (" + Clean(t.Category, 30) + ", " + t.FileType
                + ", similarity " + Num(t.Score) + ")");

            var budget = perTemplate;

            if (!string.IsNullOrWhiteSpace(t.MetadataSummary))
            {
                var summary = Clean(t.MetadataSummary, Math.Min(240, budget / 3));
                sb.AppendLine("Summary: " + summary);
                budget -= summary.Length;
            }

            if (!string.IsNullOrWhiteSpace(t.CodeSnippet))
            {
                var code = t.CodeSnippet.Replace("\r", string.Empty).Trim();

                if (code.Length > budget)
                {
                    code = code[..Math.Max(80, budget)];
                    var lastBreak = code.LastIndexOf('\n');

                    if (lastBreak > 40)
                    {
                        code = code[..lastBreak];
                    }

                    code += "\n// ... truncated";
                }

                sb.AppendLine("```csharp");
                sb.AppendLine(code);
                sb.AppendLine("```");
            }

            index++;
        }

        var text = sb.ToString();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    public static string Build(ProjectResultDto project, AssemblyTree? tree, int maxChars, GeometryFeatureContext? features = null)
    {
        var limit = Math.Max(400, maxChars);
        var text = string.Empty;

        foreach (var detail in DetailLevels)
        {
            text = BuildCore(project, tree, detail, features);
            if (text.Length <= limit)
            {
                return text;
            }
        }

        return text.Length > limit ? text[..limit] : text;
    }

    private static string BuildCore(ProjectResultDto project, AssemblyTree? tree, int detail, GeometryFeatureContext? features)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Drawing: " + Clean(project.Title, 80));
        sb.AppendLine("Source: " + project.SourceFileType + ", file " + Clean(project.OriginalFileName, 80));
        sb.AppendLine("Unit: " + project.Unit + (project.DetectedUnit.HasValue ? " (detected " + project.DetectedUnit.Value + ")" : string.Empty));

        if (project.ScaleMmPerPixel.HasValue)
        {
            sb.AppendLine("Scale: " + Num((double)project.ScaleMmPerPixel.Value) + " mm per pixel");
        }

        var components = project.Components
            .OrderBy(c => c.SequenceIndex)
            .Take(detail)
            .ToList();

        if (components.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recognized components:");

            foreach (var c in components)
            {
                var parts = new List<string>();
                Add(parts, "width", c.Width);
                Add(parts, "height", c.Height);
                Add(parts, "diameter", c.Diameter);
                Add(parts, "topDiameter", c.TopDiameter);
                Add(parts, "bottomDiameter", c.BottomDiameter);
                Add(parts, "angle", c.Angle);

                if (c.Profile is not null)
                {
                    parts.Add("boundingWidth " + Num(c.Profile.BoundingWidth));
                    parts.Add("boundingHeight " + Num(c.Profile.BoundingHeight));
                }

                if (c.HasHatch)
                {
                    parts.Add("hatched");
                }

                sb.AppendLine("- " + c.ComponentType + (string.IsNullOrWhiteSpace(c.Name) ? string.Empty : " \"" + Clean(c.Name, 40) + "\"")
                    + (parts.Count > 0 ? ": " + string.Join(", ", parts) : string.Empty));
            }
        }

        AppendFeatures(sb, features, detail);

        if (tree is null || tree.Nodes.Count == 0)
        {
            return sb.ToString();
        }

        var b = tree.Bounds;
        sb.AppendLine();
        sb.AppendLine("Drawing bounds: width " + Num(b.Width) + ", height " + Num(b.Height) + " (origin " + Num(b.MinX) + ", " + Num(b.MinY) + ")");
        sb.AppendLine("Sub-components: " + tree.Nodes.Count + ", assemblies: " + tree.Assemblies.Count);

        if (tree.Assemblies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Assemblies:");

            foreach (var a in tree.Assemblies.Take(Math.Max(3, detail / 3)))
            {
                sb.AppendLine("- " + Clean(a.AssemblyName, 40) + " [" + a.Id + "] " + a.Category
                    + ", box " + Num(a.Bounds.Width) + " x " + Num(a.Bounds.Height)
                    + ", parts " + a.NodeIds.Count);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Parts (id, shape, box at x,y size w x h, entities):");

        foreach (var n in tree.Nodes.Take(detail))
        {
            var entities = new List<string>();
            AddCount(entities, "lines", n.LineCount);
            AddCount(entities, "arcs", n.ArcCount);
            AddCount(entities, "circles", n.CircleCount);
            AddCount(entities, "polylines", n.PolylineCount);
            AddCount(entities, "hatches", n.HatchCount);

            var line = "- " + n.Id + " " + Clean(n.Name, 32) + ", " + n.Shape
                + ", at " + Num(n.Bounds.MinX) + "," + Num(n.Bounds.MinY)
                + " size " + Num(n.Bounds.Width) + " x " + Num(n.Bounds.Height);

            if (entities.Count > 0)
            {
                line += ", " + string.Join(" ", entities);
            }

            if (n.HasClosedProfile)
            {
                line += ", closed";
            }

            if (n.IsFilled)
            {
                line += ", filled";
            }

            if (!string.IsNullOrWhiteSpace(n.Layer))
            {
                line += ", layer " + Clean(n.Layer, 24);
            }

            if (n.Parameters.Count > 0)
            {
                line += ", " + string.Join(" ", n.Parameters.Take(4).Select(p => Clean(p.Key, 16) + "=" + Num(p.Value)));
            }

            sb.AppendLine(line);
        }

        if (tree.Nodes.Count > detail)
        {
            sb.AppendLine("- ... " + (tree.Nodes.Count - detail) + " more parts not listed");
        }

        var labels = tree.Nodes
            .SelectMany(n => n.Labels)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Clean(l, 40))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(4, detail / 2))
            .ToList();

        if (labels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Texts in drawing: " + string.Join(" | ", labels));
        }

        if (tree.Arrays.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Repeated patterns:");

            foreach (var arr in tree.Arrays.Take(Math.Max(2, detail / 6)))
            {
                sb.AppendLine("- " + arr.Kind + " x" + arr.Count
                    + (arr.Kind == AssemblyArrayKind.Linear
                        ? ", spacing " + Num(arr.Spacing)
                        : ", radius " + Num(arr.Radius) + ", step " + Num(arr.AngleStepDegrees) + " deg"));
            }
        }

        return sb.ToString();
    }

    private static void AppendFeatures(StringBuilder sb, GeometryFeatureContext? features, int detail)
    {
        if (features is null || features.IsEmpty)
        {
            return;
        }

        if (features.Profiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("2D profiles found in the drawing (closed loops, millimeters):");

            foreach (var line in features.Profiles.Take(Math.Max(2, detail / 4)))
            {
                sb.AppendLine("- " + Clean(line, 200));
            }

            if (features.Profiles.Count > Math.Max(2, detail / 4))
            {
                sb.AppendLine("- ... " + (features.Profiles.Count - Math.Max(2, detail / 4)) + " more profiles not listed");
            }
        }

        if (features.Features.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Detected features (each one becomes a solid in the script):");

            foreach (var line in features.Features.Take(Math.Max(3, detail / 2)))
            {
                sb.AppendLine("- " + Clean(line, 240));
            }

            if (features.Features.Count > Math.Max(3, detail / 2))
            {
                sb.AppendLine("- ... " + (features.Features.Count - Math.Max(3, detail / 2)) + " more features not listed");
            }
        }

        if (features.Parameters.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Named parameters (declare each as const double and reuse the name; assumed values are not in the drawing):");

            foreach (var line in features.Parameters.Take(Math.Max(4, detail)))
            {
                sb.AppendLine("- " + Clean(line, 160));
            }
        }
    }

    private static void Add(List<string> parts, string name, decimal? value)
    {
        if (value.HasValue && value.Value != 0m)
        {
            parts.Add(name + " " + Num((double)value.Value));
        }
    }

    private static void AddCount(List<string> parts, string name, int count)
    {
        if (count > 0)
        {
            parts.Add(count.ToString(CultureInfo.InvariantCulture) + " " + name);
        }
    }

    private static string Num(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > max ? text[..max] : text;
    }
}