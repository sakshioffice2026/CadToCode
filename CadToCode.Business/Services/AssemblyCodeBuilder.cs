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
    private const int CirclePolygonSides = 48;
    private const int MaxLoopsPerPart = 300;
    private const int MaxChainSegments = 4000;
    private const double ArcStepDegrees = 10.0;
    private const double ThicknessRatio = 0.05;

    private static readonly Regex EntityMarkerPattern = new(
        @"\s*//\s*id:(?<id>[A-Za-z0-9_\-]+)\s*$",
        RegexOptions.Compiled);

    private static readonly string[] ReservedNames =
    {
        "doc", "outputPath", "item", "Thickness"
    };

    private sealed record Frame(double OriginX, double OriginY, double Scale)
    {
        public double MX(double value) => (value - OriginX) * Scale;

        public double MY(double value) => (value - OriginY) * Scale;

        public string X(double value) => Num(MX(value));

        public string Y(double value) => Num(MY(value));
    }

    private sealed class Loop
    {
        public Loop(List<(double X, double Y)> points)
        {
            Points = points;
            MinX = points.Min(p => p.X);
            MaxX = points.Max(p => p.X);
            MinY = points.Min(p => p.Y);
            MaxY = points.Max(p => p.Y);
        }

        public List<(double X, double Y)> Points { get; }

        public bool IsCircle { get; init; }

        public double Cx { get; init; }

        public double Cy { get; init; }

        public double Radius { get; init; }

        public double Area { get; set; }

        public int Parent { get; set; } = -1;

        public int Depth { get; set; }

        public double MinX { get; }

        public double MaxX { get; }

        public double MinY { get; }

        public double MaxY { get; }
    }

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

        var scale = ScaleToMillimeter(tree.Unit);
        var frame = new Frame(tree.Bounds.MinX, tree.Bounds.MinY, scale);
        var span = Math.Max(tree.Bounds.Width, tree.Bounds.Height) * scale;
        var tolerance = ToleranceFor(span);
        var identifiers = AssemblyPromptTemplates.BuildIdentifierMap(tree, analysis);
        var nodes = tree.Nodes.Where(n => identifiers.ContainsKey(n.Id)).ToList();

        var header = new List<string>
        {
            "All dimensions in millimeters.",
            "Each closed outline is extruded along Z by Thickness; full circles become cylinders; nested outlines become holes.",
            "Origin (0, 0) is the lower-left corner of the drawing.",
            "Project: " + scriptName
        };

        if (!string.IsNullOrWhiteSpace(analysis?.Summary))
        {
            header.Add(analysis!.Summary!);
        }

        var builder = new StringBuilder();
        AppendScriptStart(builder, header, ThicknessFor(span), scriptName);

        var used = new HashSet<string>(ReservedNames, StringComparer.Ordinal);

        if (nodes.Count == 0)
        {
            builder.AppendLine("// No sub-components were detected. The drawing bounds are extruded as a single block.");
            AppendSolids(builder, new ExtractedGeometry(), "block", "Block", null, tree.Bounds, frame, tolerance, used);
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

                AppendSolids(builder, node.Geometry ?? new ExtractedGeometry(), baseName, identifier, node.Id, node.Bounds, frame, tolerance, used);
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
        var span = bounds is null ? 0 : Math.Max(bounds.Width, bounds.Height) * scale;
        var tolerance = ToleranceFor(span);

        var header = new List<string>
        {
            "All dimensions in millimeters.",
            "Each closed outline is extruded along Z by Thickness; full circles become cylinders; nested outlines become holes.",
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
        AppendScriptStart(builder, header, ThicknessFor(span), name);

        var used = new HashSet<string>(ReservedNames, StringComparer.Ordinal);

        BoundingBox2D? fallback = bounds is null
            ? null
            : new BoundingBox2D(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

        var count = AppendSolids(builder, geometry, "drawing", name, null, fallback, frame, tolerance, used);

        if (count == 0)
        {
            builder.AppendLine("// No closed outline or circle was found in the source drawing, so no solid was created.");
            builder.AppendLine();
        }

        AppendScriptEnd(builder, name);
        return builder.ToString();
    }

    private static double ToleranceFor(double span)
    {
        return Math.Max(1e-6, span * 1e-4);
    }

    private static double ThicknessFor(double span)
    {
        if (!double.IsFinite(span) || span <= 0)
        {
            return 10;
        }

        return Math.Max(1.0, Math.Round(span * ThicknessRatio, 1));
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
        BoundingBox2D? fallbackBounds,
        Frame frame,
        double tolerance,
        HashSet<string> used)
    {
        var marker = id == null ? string.Empty : " // id:" + id;
        var loops = ExtractLoops(geometry, frame, tolerance);

        if (loops.Count == 0 && fallbackBounds is not null)
        {
            var rectangle = RectangleLoop(fallbackBounds, frame, tolerance);
            if (rectangle is not null)
            {
                loops.Add(rectangle);
            }
        }

        if (loops.Count == 0)
        {
            return 0;
        }

        AssignNesting(loops, tolerance);

        var solids = Enumerable.Range(0, loops.Count).Where(i => loops[i].Depth % 2 == 0).ToList();
        var profiles = new List<(string Name, Loop Outline, List<Loop> Holes)>();
        var cylinders = new List<(string Name, Loop Circle)>();

        for (var n = 0; n < solids.Count; n++)
        {
            var index = solids[n];
            var loop = loops[index];
            var name = solids.Count > 1
                ? displayName + " " + (n + 1).ToString(CultureInfo.InvariantCulture)
                : displayName;

            var holes = Enumerable.Range(0, loops.Count)
                .Where(i => loops[i].Parent == index && loops[i].Depth % 2 == 1)
                .Select(i => loops[i])
                .ToList();

            if (loop.IsCircle && holes.Count == 0)
            {
                cylinders.Add((name, loop));
            }
            else
            {
                profiles.Add((name, loop, holes));
            }
        }

        if (profiles.Count > 0)
        {
            var variable = UniqueVariable(baseName + "Profiles", used);

            builder.AppendLine($"var {variable} = new (string Name, (double X, double Y)[] Outline, (double X, double Y)[][] Holes)[]{marker}");
            builder.AppendLine("{");

            foreach (var profile in profiles)
            {
                builder.AppendLine($"    ({Literal(profile.Name)},");
                builder.AppendLine("        new (double X, double Y)[]");
                builder.AppendLine("        {");
                AppendWrapped(builder, profile.Outline.Points.Select(p => $"({Num(p.X)}, {Num(p.Y)})"), "            ");
                builder.AppendLine("        },");
                builder.AppendLine("        new (double X, double Y)[][]");
                builder.AppendLine("        {");

                foreach (var hole in profile.Holes)
                {
                    builder.AppendLine("            new (double X, double Y)[]");
                    builder.AppendLine("            {");
                    AppendWrapped(builder, hole.Points.Select(p => $"({Num(p.X)}, {Num(p.Y)})"), "                ");
                    builder.AppendLine("            },");
                }

                builder.AppendLine("        }),");
            }

            builder.AppendLine("};");
            builder.AppendLine($"foreach (var item in {variable}){marker}");
            builder.AppendLine("{");
            builder.AppendLine("    doc.Add(Solid.Extrude(item.Outline, item.Holes, Thickness).Named(item.Name));");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        if (cylinders.Count > 0)
        {
            var variable = UniqueVariable(baseName + "Cylinders", used);

            builder.AppendLine($"var {variable} = new (string Name, double X, double Y, double Radius)[]{marker}");
            builder.AppendLine("{");

            foreach (var cylinder in cylinders)
            {
                builder.AppendLine(
                    $"    ({Literal(cylinder.Name)}, {Num(cylinder.Circle.Cx)}, {Num(cylinder.Circle.Cy)}, {Num(cylinder.Circle.Radius)}),");
            }

            builder.AppendLine("};");
            builder.AppendLine($"foreach (var item in {variable}){marker}");
            builder.AppendLine("{");
            builder.AppendLine("    doc.Add(Solid.Cylinder(item.X, item.Y, item.Radius, Thickness).Named(item.Name));");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return solids.Count;
    }

    private static List<Loop> ExtractLoops(ExtractedGeometry geometry, Frame frame, double tolerance)
    {
        var loops = new List<Loop>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<List<(double X, double Y)>>();

        foreach (var polyline in geometry.Polylines)
        {
            var points = FlattenPolyline(polyline, frame, out var closed);

            if (closed)
            {
                TryAddLoop(loops, seen, new Loop(Clean(points, tolerance)), tolerance);
            }
            else if (points.Count >= 2)
            {
                segments.Add(points);
            }
        }

        foreach (var circle in geometry.Circles)
        {
            var radius = circle.Radius * frame.Scale;
            if (!IsPositive(radius))
            {
                continue;
            }

            var cx = frame.MX(circle.Center.X);
            var cy = frame.MY(circle.Center.Y);
            var ring = new List<(double X, double Y)>(CirclePolygonSides);

            for (var k = 0; k < CirclePolygonSides; k++)
            {
                var angle = 2.0 * Math.PI * k / CirclePolygonSides;
                ring.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }

            var loop = new Loop(ring) { IsCircle = true, Cx = cx, Cy = cy, Radius = radius };
            loop.Area = Math.PI * radius * radius;
            TryAddLoop(loops, seen, loop, tolerance);
        }

        foreach (var line in geometry.Lines)
        {
            var start = (X: frame.MX(line.Start.X), Y: frame.MY(line.Start.Y));
            var end = (X: frame.MX(line.End.X), Y: frame.MY(line.End.Y));

            if (Dist(start, end) > tolerance)
            {
                segments.Add(new List<(double X, double Y)> { start, end });
            }
        }

        foreach (var arc in geometry.Arcs)
        {
            var points = FlattenArc(arc, frame);
            if (points.Count >= 2)
            {
                segments.Add(points);
            }
        }

        if (segments.Count > 0 && segments.Count <= MaxChainSegments)
        {
            foreach (var chain in ChainLoops(segments, tolerance))
            {
                TryAddLoop(loops, seen, new Loop(Clean(chain, tolerance)), tolerance);
            }
        }

        return loops
            .OrderByDescending(l => l.Area)
            .Take(MaxLoopsPerPart)
            .ToList();
    }

    private static void TryAddLoop(List<Loop> loops, HashSet<string> seen, Loop loop, double tolerance)
    {
        if (loop.Points.Count < 3)
        {
            return;
        }

        if (!loop.IsCircle)
        {
            loop.Area = Math.Abs(SignedArea(loop.Points));
        }

        if (!(loop.Area > tolerance * tolerance))
        {
            return;
        }

        var centerX = (loop.MinX + loop.MaxX) / 2.0;
        var centerY = (loop.MinY + loop.MaxY) / 2.0;
        var key = string.Join(
            "|",
            Math.Round(loop.Area, 2).ToString(CultureInfo.InvariantCulture),
            Math.Round(centerX, 1).ToString(CultureInfo.InvariantCulture),
            Math.Round(centerY, 1).ToString(CultureInfo.InvariantCulture));

        if (!seen.Add(key))
        {
            return;
        }

        loops.Add(loop);
    }

    private static Loop? RectangleLoop(BoundingBox2D bounds, Frame frame, double tolerance)
    {
        var points = new List<(double X, double Y)>
        {
            (frame.MX(bounds.MinX), frame.MY(bounds.MinY)),
            (frame.MX(bounds.MaxX), frame.MY(bounds.MinY)),
            (frame.MX(bounds.MaxX), frame.MY(bounds.MaxY)),
            (frame.MX(bounds.MinX), frame.MY(bounds.MaxY))
        };

        var loop = new Loop(points);
        loop.Area = Math.Abs(SignedArea(points));

        return loop.Area > tolerance * tolerance ? loop : null;
    }

    private static void AssignNesting(List<Loop> loops, double tolerance)
    {
        for (var i = 0; i < loops.Count; i++)
        {
            loops[i].Parent = -1;
            loops[i].Depth = 0;

            for (var j = i - 1; j >= 0; j--)
            {
                if (loops[j].Area > loops[i].Area && Contains(loops[j], loops[i], tolerance))
                {
                    loops[i].Parent = j;
                    loops[i].Depth = loops[j].Depth + 1;
                    break;
                }
            }
        }
    }

    private static bool Contains(Loop outer, Loop inner, double tolerance)
    {
        if (inner.MinX < outer.MinX - tolerance
            || inner.MaxX > outer.MaxX + tolerance
            || inner.MinY < outer.MinY - tolerance
            || inner.MaxY > outer.MaxY + tolerance)
        {
            return false;
        }

        var inside = inner.Points.Count(p => PointInPolygon(p, outer.Points));
        return inside * 10 >= inner.Points.Count * 6;
    }

    private static bool PointInPolygon((double X, double Y) point, List<(double X, double Y)> polygon)
    {
        var inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
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

    private static List<(double X, double Y)> Clean(List<(double X, double Y)> points, double tolerance)
    {
        var result = new List<(double X, double Y)>(points.Count);

        foreach (var point in points)
        {
            if (result.Count == 0 || Dist(result[^1], point) > tolerance)
            {
                result.Add(point);
            }
        }

        while (result.Count > 1 && Dist(result[0], result[^1]) <= tolerance)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static List<(double X, double Y)> FlattenPolyline(GeometryPolyline polyline, Frame frame, out bool closed)
    {
        var vertices = polyline.Vertices;
        var result = new List<(double X, double Y)>();
        closed = false;

        if (vertices.Count < 2)
        {
            return result;
        }

        var count = vertices.Count;
        var segmentCount = polyline.IsClosed ? count : count - 1;

        for (var i = 0; i < count; i++)
        {
            var current = vertices[i];
            var start = (X: frame.MX(current.X), Y: frame.MY(current.Y));
            result.Add(start);

            if (i >= segmentCount || Math.Abs(current.Bulge) <= 1e-9)
            {
                continue;
            }

            var next = vertices[(i + 1) % count];
            var end = (X: frame.MX(next.X), Y: frame.MY(next.Y));
            AppendBulgeArc(result, start, end, current.Bulge);
        }

        closed = polyline.IsClosed;

        if (!closed && result.Count >= 4 && Dist(result[0], result[^1]) <= 1e-6)
        {
            result.RemoveAt(result.Count - 1);
            closed = true;
        }

        return result;
    }

    private static void AppendBulgeArc(
        List<(double X, double Y)> points,
        (double X, double Y) start,
        (double X, double Y) end,
        double bulge)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);

        if (chord < 1e-9)
        {
            return;
        }

        var offset = chord / 2.0 * (1.0 - bulge * bulge) / (2.0 * bulge);
        var centerX = (start.X + end.X) / 2.0 + (-dy / chord) * offset;
        var centerY = (start.Y + end.Y) / 2.0 + (dx / chord) * offset;
        var radius = Math.Sqrt(offset * offset + chord * chord / 4.0);
        var sweep = 4.0 * Math.Atan(bulge);
        var first = Math.Atan2(start.Y - centerY, start.X - centerX);
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (ArcStepDegrees * Math.PI / 180.0)));

        for (var k = 1; k < steps; k++)
        {
            var angle = first + sweep * k / steps;
            points.Add((centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)));
        }
    }

    private static List<(double X, double Y)> FlattenArc(GeometryArc arc, Frame frame)
    {
        var points = new List<(double X, double Y)>();
        var radius = arc.Radius * frame.Scale;

        if (!IsPositive(radius))
        {
            return points;
        }

        var cx = frame.MX(arc.Center.X);
        var cy = frame.MY(arc.Center.Y);
        var sweep = arc.EndAngle - arc.StartAngle;

        while (sweep <= 0)
        {
            sweep += 360.0;
        }

        if (sweep > 360.0)
        {
            sweep %= 360.0;

            if (sweep <= 0)
            {
                sweep = 360.0;
            }
        }

        var steps = Math.Max(2, (int)Math.Ceiling(sweep / ArcStepDegrees));

        for (var k = 0; k <= steps; k++)
        {
            var angle = (arc.StartAngle + sweep * k / steps) * Math.PI / 180.0;
            points.Add((cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
        }

        return points;
    }

    private static List<List<(double X, double Y)>> ChainLoops(List<List<(double X, double Y)>> segments, double tolerance)
    {
        var loops = new List<List<(double X, double Y)>>();
        var used = new bool[segments.Count];

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            used[i] = true;
            var chain = new List<(double X, double Y)>(segments[i]);

            while (true)
            {
                if (chain.Count >= 3 && Dist(chain[0], chain[^1]) <= tolerance)
                {
                    chain.RemoveAt(chain.Count - 1);
                    loops.Add(chain);
                    break;
                }

                var tail = chain[^1];
                var extended = false;

                for (var j = 0; j < segments.Count && !extended; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var candidate = segments[j];

                    if (Dist(tail, candidate[0]) <= tolerance)
                    {
                        chain.AddRange(candidate.Skip(1));
                        used[j] = true;
                        extended = true;
                    }
                    else if (Dist(tail, candidate[^1]) <= tolerance)
                    {
                        for (var k = candidate.Count - 2; k >= 0; k--)
                        {
                            chain.Add(candidate[k]);
                        }

                        used[j] = true;
                        extended = true;
                    }
                }

                if (!extended)
                {
                    break;
                }
            }
        }

        return loops;
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

    private static bool IsPositive(double value)
    {
        return double.IsFinite(value) && Math.Round(value, 3) > 0;
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