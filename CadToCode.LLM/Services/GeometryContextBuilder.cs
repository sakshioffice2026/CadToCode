using System.Globalization;
using System.Text;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Services;

public static class GeometryContextBuilder
{
    private static readonly int[] DetailLevels = { 60, 30, 15, 8, 4 };

    public static string Build(ProjectResultDto project, AssemblyTree? tree, int maxChars)
    {
        var limit = Math.Max(400, maxChars);
        var text = string.Empty;

        foreach (var detail in DetailLevels)
        {
            text = BuildCore(project, tree, detail);
            if (text.Length <= limit)
            {
                return text;
            }
        }

        return text.Length > limit ? text[..limit] : text;
    }

    private static string BuildCore(ProjectResultDto project, AssemblyTree? tree, int detail)
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