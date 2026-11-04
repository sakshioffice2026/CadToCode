using System.Globalization;
using System.Text;
using CadToCode.LLM.Prompts;
using CadToCode.Step;

namespace CadToCode.Business.Services;

public sealed class ScriptEmitterOptions
{
    public double Scale { get; init; } = 1.0;

    public double OriginX { get; init; }

    public double OriginY { get; init; }

    public IReadOnlyList<string>? Notes { get; init; }
}

public static class ScriptEmitter
{
    public static string Emit(FeatureRecognitionResult recognition, string? scriptName, ScriptEmitterOptions? options = null)
    {
        if (recognition is null)
        {
            throw new ArgumentNullException(nameof(recognition));
        }

        options ??= new ScriptEmitterOptions();

        if (!double.IsFinite(options.Scale) || options.Scale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The scale must be a number greater than zero.");
        }

        var name = AssemblyPromptTemplates.ToIdentifier(scriptName, "Drawing");

        return new Writer(options).Build(recognition, name);
    }

    private sealed class Writer
    {
        private const double TangentDot = 0.99996;

        private readonly double _scale;
        private readonly double _originX;
        private readonly double _originY;
        private readonly IReadOnlyList<string>? _notes;

        public Writer(ScriptEmitterOptions options)
        {
            _scale = options.Scale;
            _originX = options.OriginX;
            _originY = options.OriginY;
            _notes = options.Notes;
        }

        public string Build(FeatureRecognitionResult recognition, string name)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// All dimensions in millimeters.");
            sb.AppendLine("// Built from recognised drawing features; every size that matters is a named constant below.");

            if (Math.Abs(_scale - 1.0) > 1e-12)
            {
                sb.AppendLine("// The feature summary below quotes sizes in the drawing's own units.");
            }

            sb.AppendLine("// Project: " + name);

            if (recognition.Features.Count > 0)
            {
                sb.AppendLine("// Recognised features:");

                foreach (var feature in recognition.Features)
                {
                    sb.AppendLine("//   " + SingleLine(feature.Description));
                }
            }

            if (_notes is { Count: > 0 })
            {
                sb.AppendLine("// Recognised components:");

                foreach (var note in _notes)
                {
                    sb.AppendLine("//   " + SingleLine(note));
                }
            }

            foreach (var warning in recognition.Warnings)
            {
                sb.AppendLine("// Note: " + SingleLine(warning));
            }

            sb.AppendLine("// Requires the CadToCode.Business library (namespace CadToCode.Step).");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using CadToCode.Step;");
            sb.AppendLine();

            AppendParameters(sb, recognition);

            sb.AppendLine("var doc = new StepDocument(" + Literal(name) + ");");
            sb.AppendLine();

            if (recognition.Features.Count == 0)
            {
                sb.AppendLine("// No features were recognised in the source drawing, so no solid was created.");
                sb.AppendLine();
            }

            foreach (var feature in recognition.Features)
            {
                switch (feature.Kind)
                {
                    case RecognizedFeatureKind.Extrusion:
                        EmitExtrusion(sb, feature);
                        break;

                    case RecognizedFeatureKind.Cylinder:
                        EmitCylinder(sb, feature);
                        break;

                    case RecognizedFeatureKind.Revolve:
                        EmitRevolve(sb, feature);
                        break;

                    case RecognizedFeatureKind.Pipe:
                        EmitPipe(sb, feature);
                        break;
                }
            }

            sb.AppendLine("var outputPath = Path.GetFullPath(" + Literal(name + ".step") + ");");
            sb.AppendLine("doc.Save(outputPath);");
            sb.AppendLine("Console.WriteLine(\"STEP saved to \" + outputPath + \" (\" + doc.SolidCount + \" solids)\");");

            return sb.ToString();
        }

        private void AppendParameters(StringBuilder sb, FeatureRecognitionResult recognition)
        {
            var measured = new List<(string Name, double Value, string Description)>();
            var assumed = new List<(string Name, double Value, string Description)>();

            foreach (var feature in recognition.Features)
            {
                foreach (var parameter in feature.Parameters)
                {
                    (parameter.IsAssumed ? assumed : measured).Add((parameter.Name, parameter.Value * _scale, parameter.Description));
                }

                for (var i = 0; i < feature.Holes.Count; i++)
                {
                    measured.Add((
                        HoleConstant(feature, i + 1),
                        feature.Holes[i].Diameter * _scale,
                        "Diameter of " + feature.Holes[i].Name + " in " + feature.Name + " measured from the drawing."));
                }
            }

            if (measured.Count == 0 && assumed.Count == 0)
            {
                return;
            }

            sb.AppendLine("// ---- Parameters: change a value and press Run to rebuild the solids ----");

            if (measured.Count > 0)
            {
                sb.AppendLine("// Measured from the drawing:");

                foreach (var item in measured)
                {
                    sb.AppendLine("// " + SingleLine(item.Description));
                    sb.AppendLine("const double " + item.Name + " = " + Num(item.Value) + ";");
                }
            }

            if (assumed.Count > 0)
            {
                sb.AppendLine("// Assumed (not in the drawing, check these):");

                foreach (var item in assumed)
                {
                    sb.AppendLine("// " + SingleLine(item.Description));
                    sb.AppendLine("const double " + item.Name + " = " + Num(item.Value) + ";");
                }
            }

            sb.AppendLine();
        }

        private void EmitExtrusion(StringBuilder sb, RecognizedFeature feature)
        {
            var profile = feature.Profile!;
            var prefix = Lower(feature.Name);

            sb.AppendLine("// ---- " + feature.Name + " ----");

            EmitLoop(sb, prefix + "Outer", profile.Outer, Drawing, null);

            var holeNames = new List<string>();
            var circleIndex = 0;

            for (var i = 0; i < profile.Holes.Count; i++)
            {
                var hole = profile.Holes[i];
                var variable = prefix + "Hole" + (i + 1).ToString(CultureInfo.InvariantCulture);
                string? radius = null;

                if (hole.IsCircle)
                {
                    circleIndex++;
                    radius = HoleConstant(feature, circleIndex) + " / 2.0";
                }

                EmitLoop(sb, variable, hole, Drawing, radius);
                holeNames.Add(variable);
            }

            var profileVariable = prefix + "Profile";

            sb.AppendLine(holeNames.Count == 0
                ? "var " + profileVariable + " = Profile2D.Create(" + prefix + "Outer);"
                : "var " + profileVariable + " = Profile2D.Create(" + prefix + "Outer, new ProfileLoop[] { " + string.Join(", ", holeNames) + " });");

            sb.AppendLine("doc.Add.Extrude(" + Literal(feature.Name) + ", " + profileVariable + ", " + feature.DepthParameter!.Name + ");");
            sb.AppendLine();
        }

        private void EmitCylinder(StringBuilder sb, RecognizedFeature feature)
        {
            var arc = feature.Profile!.Outer.CircleArc!;
            var center = Drawing(arc.Center);
            var diameter = feature.Parameters.First(p => p.Name.EndsWith("Diameter", StringComparison.Ordinal)).Name;

            sb.AppendLine("// ---- " + feature.Name + " ----");
            sb.AppendLine(
                "doc.Add.Cylinder(" + Literal(feature.Name) + ", " + Num(center.X) + ", " + Num(center.Y) + ", "
                + diameter + " / 2.0, " + feature.DepthParameter!.Name + ");");
            sb.AppendLine();
        }

        private void EmitRevolve(StringBuilder sb, RecognizedFeature feature)
        {
            var profile = feature.Profile!;
            var prefix = Lower(feature.Name);

            sb.AppendLine("// ---- " + feature.Name + " ----");

            EmitLoop(sb, prefix + "Outer", profile.Outer, Local, null);

            var holeNames = new List<string>();

            for (var i = 0; i < profile.Holes.Count; i++)
            {
                var variable = prefix + "Hole" + (i + 1).ToString(CultureInfo.InvariantCulture);

                EmitLoop(sb, variable, profile.Holes[i], Local, null);
                holeNames.Add(variable);
            }

            var profileVariable = prefix + "Profile";
            var frameVariable = prefix + "Frame";
            var origin = Drawing(feature.AxisOrigin);

            sb.AppendLine(holeNames.Count == 0
                ? "var " + profileVariable + " = Profile2D.Create(" + prefix + "Outer);"
                : "var " + profileVariable + " = Profile2D.Create(" + prefix + "Outer, new ProfileLoop[] { " + string.Join(", ", holeNames) + " });");

            sb.AppendLine(
                "var " + frameVariable + " = Frame3D.FromAxes(new Vec3(" + Num(origin.X) + ", " + Num(origin.Y) + ", 0), new Vec3("
                + Dir(feature.RadialDirection.X) + ", " + Dir(feature.RadialDirection.Y) + ", 0), new Vec3("
                + Dir(feature.AxisDirection.X) + ", " + Dir(feature.AxisDirection.Y) + ", 0));");

            sb.AppendLine("doc.Add.Revolve(" + Literal(feature.Name) + ", " + profileVariable + ", " + frameVariable + ");");
            sb.AppendLine();
        }

        private void EmitPipe(StringBuilder sb, RecognizedFeature feature)
        {
            var runs = SplitAtCorners(feature.Centerline);
            var prefix = Lower(feature.Name);
            var outer = feature.OuterDiameterParameter!.Name;
            var wall = feature.WallParameter!.Name;

            sb.AppendLine("// ---- " + feature.Name + " ----");

            if (runs.Count > 1)
            {
                sb.AppendLine("// The centreline has sharp corners, so it is built as " + runs.Count + " tangent-continuous runs.");
            }

            for (var r = 0; r < runs.Count; r++)
            {
                var run = runs[r];
                var suffix = runs.Count > 1 ? (r + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
                var route = prefix + "Route" + suffix;
                var solidName = runs.Count > 1 ? feature.Name + "_" + (r + 1).ToString(CultureInfo.InvariantCulture) : feature.Name;

                var first = run[0];

                if (first is LineSegment2D line)
                {
                    var s = Drawing(line.Start);
                    var e = Drawing(line.End);

                    sb.AppendLine(
                        "var " + route + " = SweepRoute.From(" + Num(s.X) + ", " + Num(s.Y) + ", 0).LineTo(" + Num(e.X) + ", " + Num(e.Y) + ", 0);");
                }
                else
                {
                    var arc = (ArcSegment2D)first;
                    var s = Drawing(arc.Start);
                    var c = Drawing(arc.Center);

                    sb.AppendLine(
                        "var " + route + " = SweepRoute.From(" + Num(s.X) + ", " + Num(s.Y) + ", 0).Arc(new Vec3("
                        + Num(c.X) + ", " + Num(c.Y) + ", 0), Vec3.UnitZ, " + Num(arc.SweepAngle * 180.0 / Math.PI) + ");");
                }

                for (var k = 1; k < run.Count; k++)
                {
                    if (run[k] is LineSegment2D next)
                    {
                        sb.AppendLine(route + " = " + route + ".Straight(" + Num(next.Length * _scale) + ");");
                    }
                    else
                    {
                        var bend = (ArcSegment2D)run[k];

                        sb.AppendLine(
                            route + " = " + route + ".Bend(" + Num(bend.Radius * _scale) + ", " + Num(bend.SweepAngle * 180.0 / Math.PI)
                            + ", Vec3.UnitZ);");
                    }
                }

                sb.AppendLine("doc.Add.Pipe(" + Literal(solidName) + ", " + route + ", " + outer + ", " + wall + ");");
            }

            sb.AppendLine();
        }

        private void EmitLoop(
            StringBuilder sb,
            string variable,
            ProfileLoop loop,
            Func<(double X, double Y), (double X, double Y)> map,
            string? radiusExpression)
        {
            if (loop.IsCircle)
            {
                var arc = loop.CircleArc!;
                var center = map(arc.Center);
                var radius = radiusExpression ?? Num(arc.Radius * _scale);

                sb.AppendLine("var " + variable + " = ProfileLoop.Circle(" + Num(center.X) + ", " + Num(center.Y) + ", " + radius + ");");
                return;
            }

            var segments = loop.Segments;
            var count = segments.Count;
            var points = new string[count];

            for (var k = 0; k < count; k++)
            {
                points[k] = Point(map(segments[k].Start));
            }

            if (!loop.HasArcs)
            {
                sb.AppendLine("var " + variable + " = ProfileLoop.Polygon(new (double X, double Y)[]");
                sb.AppendLine("{");

                foreach (var point in points)
                {
                    sb.AppendLine("    " + point + ",");
                }

                sb.AppendLine("});");
                return;
            }

            sb.AppendLine("var " + variable + " = ProfileLoop.Create(new ProfileSegment[]");
            sb.AppendLine("{");

            for (var k = 0; k < count; k++)
            {
                var start = points[k];
                var end = points[(k + 1) % count];

                if (segments[k] is ArcSegment2D arc)
                {
                    var mid = Point(map(arc.PointAt(0.5)));
                    sb.AppendLine("    ArcSegment2D.FromThreePoints(" + start + ", " + mid + ", " + end + "),");
                }
                else
                {
                    sb.AppendLine("    new LineSegment2D(" + start + ", " + end + "),");
                }
            }

            sb.AppendLine("});");
        }

        private (double X, double Y) Drawing((double X, double Y) p)
        {
            return ((p.X - _originX) * _scale, (p.Y - _originY) * _scale);
        }

        private (double X, double Y) Local((double X, double Y) p)
        {
            return (p.X * _scale, p.Y * _scale);
        }

        private static List<List<ProfileSegment>> SplitAtCorners(IReadOnlyList<ProfileSegment> segments)
        {
            var runs = new List<List<ProfileSegment>>();
            var current = new List<ProfileSegment>();

            foreach (var segment in segments)
            {
                if (current.Count > 0 && !IsTangent(current[^1], segment))
                {
                    runs.Add(current);
                    current = new List<ProfileSegment>();
                }

                current.Add(segment);
            }

            if (current.Count > 0)
            {
                runs.Add(current);
            }

            return runs;
        }

        private static bool IsTangent(ProfileSegment a, ProfileSegment b)
        {
            var ta = EndTangent(a);
            var tb = StartTangent(b);

            return ta.X * tb.X + ta.Y * tb.Y >= TangentDot;
        }

        private static (double X, double Y) StartTangent(ProfileSegment segment)
        {
            if (segment is ArcSegment2D arc)
            {
                return ArcTangent(arc, arc.StartAngle);
            }

            return LineDirection(segment);
        }

        private static (double X, double Y) EndTangent(ProfileSegment segment)
        {
            if (segment is ArcSegment2D arc)
            {
                return ArcTangent(arc, arc.StartAngle + arc.SweepAngle);
            }

            return LineDirection(segment);
        }

        private static (double X, double Y) ArcTangent(ArcSegment2D arc, double angle)
        {
            var sign = arc.SweepAngle > 0 ? 1.0 : -1.0;

            return (-sign * Math.Sin(angle), sign * Math.Cos(angle));
        }

        private static (double X, double Y) LineDirection(ProfileSegment segment)
        {
            var dx = segment.End.X - segment.Start.X;
            var dy = segment.End.Y - segment.Start.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);

            return length <= 1e-12 ? (1.0, 0.0) : (dx / length, dy / length);
        }

        private static string HoleConstant(RecognizedFeature feature, int number)
        {
            return Lower(feature.Name) + "Hole" + number.ToString(CultureInfo.InvariantCulture) + "Diameter";
        }

        private static string Lower(string name)
        {
            return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
        }

        private static string Point((double X, double Y) p)
        {
            return "(" + Num(p.X) + ", " + Num(p.Y) + ")";
        }

        private static string Num(double value)
        {
            if (!double.IsFinite(value))
            {
                return "0";
            }

            var rounded = Math.Round(value, 4);

            return rounded == 0 ? "0" : rounded.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string Dir(double value)
        {
            if (!double.IsFinite(value))
            {
                return "0";
            }

            var rounded = Math.Round(value, 8);

            return rounded == 0 ? "0" : rounded.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string Literal(string? value)
        {
            var text = SingleLine(value ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

            return "\"" + text + "\"";
        }

        private static string SingleLine(string value)
        {
            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
