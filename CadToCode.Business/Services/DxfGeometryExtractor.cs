using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using netDxf.Entities;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CadToCode.Business.Services;

public class DxfGeometryExtractor : IGeometryExtractor
{
    private const int MaxBlockDepth = 8;
    private const int MaxPrimitives = 250_000;
    private const int MaxInsertInstances = 2_000;
    private const double ArcStepDegrees = 5.0;
    private const int SplineSamples = 64;

    private static readonly Regex MTextCodes = new(
        @"\\[fFHhCcQqWwAaTtSs][^;]*;|\\[LlOoKk~]|\{|\}",
        RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public bool CanHandle(SourceFileType fileType) => fileType == SourceFileType.Dxf;

    public Task<ExtractedGeometry> ExtractAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Extract(fileStream, cancellationToken), cancellationToken);
    }

    private static ExtractedGeometry Extract(Stream fileStream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        fileStream.CopyTo(memory);

        if (LooksLikeDwg(memory))
        {
            throw new InvalidOperationException(
                "DWG files are not supported. Please export the drawing as DXF and upload that file.");
        }

        memory.Position = 0;

        DxfFile file;
        try
        {
            file = DxfFile.Load(memory);
        }
        catch (DxfReadException ex)
        {
            throw new InvalidOperationException("The file is not a valid DXF drawing. " + ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("The DXF file could not be read. " + ex.Message);
        }

        var result = new ExtractedGeometry
        {
            DetectedUnit = MapUnit(file.Header.DefaultDrawingUnits),
            SourceVersion = DescribeVersion(file.Header.Version)
        };

        var blocks = new Dictionary<string, DxfBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in file.Blocks)
        {
            if (!string.IsNullOrEmpty(block.Name))
            {
                blocks[block.Name] = block;
            }
        }

        var context = new ExtractionContext(result, blocks, cancellationToken);

        var modelEntities = file.Entities.Where(e => !e.IsInPaperSpace).ToList();
        IEnumerable<DxfEntity> source = modelEntities.Count > 0 ? modelEntities : file.Entities;

        ProcessEntities(source, Matrix2D.Identity, context, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (context.SplineApproximated)
        {
            result.Warnings.Add("Splines were approximated with straight segments.");
        }

        if (context.Truncated)
        {
            result.Warnings.Add("The drawing is very large; only part of it was read.");
        }

        if (context.Skipped.Count > 0)
        {
            var summary = string.Join(", ", context.Skipped
                .OrderByDescending(pair => pair.Value)
                .Take(5)
                .Select(pair => pair.Key + " x" + pair.Value));
            result.Warnings.Add("Unsupported entities were skipped: " + summary + ".");
        }

        if (result.Lines.Count == 0 && result.Arcs.Count == 0 && result.Circles.Count == 0)
        {
            result.Warnings.Add("The DXF file contains no drawable geometry.");
        }

        return result;
    }

    private static bool LooksLikeDwg(MemoryStream memory)
    {
        var buffer = memory.GetBuffer();
        var length = (int)memory.Length;

        return length >= 6
            && buffer[0] == (byte)'A'
            && buffer[1] == (byte)'C'
            && buffer[2] == (byte)'1'
            && buffer[3] >= (byte)'0'
            && buffer[3] <= (byte)'9';
    }

    private static void ProcessEntities(
        IEnumerable<DxfEntity> entities,
        Matrix2D matrix,
        ExtractionContext context,
        int depth,
        HashSet<string> blockStack)
    {
        foreach (var entity in entities)
        {
            context.Token.ThrowIfCancellationRequested();

            if (context.Truncated)
            {
                return;
            }

            if (!entity.IsVisible)
            {
                continue;
            }

            switch (entity)
            {
                case DxfLine line:
                    AddLine(context, matrix, line.P1.X, line.P1.Y, line.P2.X, line.P2.Y, entity);
                    break;

                case DxfArc arc:
                    AddArc(context, WithNormal(matrix, arc.Normal), arc.Center.X, arc.Center.Y, arc.Radius,
                        arc.StartAngle, arc.EndAngle, entity);
                    break;

                case DxfCircle circle:
                    AddCircle(context, WithNormal(matrix, circle.Normal), circle.Center.X, circle.Center.Y,
                        circle.Radius, entity);
                    break;

                case DxfLwPolyline lwPolyline:
                    if (lwPolyline.Vertices.Count >= 2)
                    {
                        ProcessEntities(lwPolyline.AsSimpleEntities(), WithNormal(matrix, lwPolyline.ExtrusionDirection),
                            context, depth, blockStack);
                    }
                    break;

                case DxfPolyline polyline:
                    if (polyline.IsPolyfaceMesh || polyline.Is3DPolygonMesh || polyline.Vertices.Count < 2)
                    {
                        context.Skip("POLYLINE");
                    }
                    else
                    {
                        ProcessEntities(polyline.AsSimpleEntities(), matrix, context, depth, blockStack);
                    }
                    break;

                case DxfEllipse ellipse:
                    AddEllipse(context, matrix, ellipse, entity);
                    break;

                case DxfSpline spline:
                    AddSpline(context, matrix, spline, entity);
                    break;

                case DxfText text:
                    AddText(context, matrix, text.Value, text.Location.X, text.Location.Y, text.TextHeight, entity);
                    break;

                case DxfMText mText:
                    AddText(context, matrix, GetMTextValue(mText), mText.InsertionPoint.X, mText.InsertionPoint.Y,
                        mText.InitialTextHeight, entity);
                    break;

                case DxfHatch hatch:
                    AddHatch(context, matrix, hatch, entity);
                    break;

                case DxfDimensionBase dimension:
                    AddDimension(context, matrix, dimension, entity);
                    break;

                case DxfInsert insert:
                    ProcessInsert(insert, matrix, context, depth, blockStack);
                    break;

                case DxfSeqend:
                case DxfVertex:
                case DxfAttribute:
                case DxfAttributeDefinition:
                    break;

                default:
                    context.Skip(entity.EntityTypeString);
                    break;
            }
        }
    }

    private static void ProcessInsert(
        DxfInsert insert,
        Matrix2D matrix,
        ExtractionContext context,
        int depth,
        HashSet<string> blockStack)
    {
        var name = insert.Name;
        if (string.IsNullOrEmpty(name) || !context.Blocks.TryGetValue(name, out var block))
        {
            context.Skip("INSERT");
            return;
        }

        if (depth >= MaxBlockDepth || blockStack.Contains(name))
        {
            context.Skip("INSERT");
            return;
        }

        var columns = Math.Max((int)insert.ColumnCount, 1);
        var rows = Math.Max((int)insert.RowCount, 1);
        if ((long)columns * rows > MaxInsertInstances)
        {
            context.Skip("INSERT");
            return;
        }

        var baseMatrix = WithNormal(matrix, insert.ExtrusionDirection);
        var angle = insert.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        blockStack.Add(name);

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var offsetX = column * insert.ColumnSpacing;
                var offsetY = row * insert.RowSpacing;
                var locationX = insert.Location.X + cos * offsetX - sin * offsetY;
                var locationY = insert.Location.Y + sin * offsetX + cos * offsetY;

                var a = cos * insert.XScaleFactor;
                var b = sin * insert.XScaleFactor;
                var c = -sin * insert.YScaleFactor;
                var d = cos * insert.YScaleFactor;
                var tx = locationX - (a * block.BasePoint.X + c * block.BasePoint.Y);
                var ty = locationY - (b * block.BasePoint.X + d * block.BasePoint.Y);

                var child = new Matrix2D(a, b, c, d, tx, ty).Then(baseMatrix);
                ProcessEntities(block.Entities, child, context, depth + 1, blockStack);

                if (context.Truncated)
                {
                    break;
                }
            }

            if (context.Truncated)
            {
                break;
            }
        }

        blockStack.Remove(name);
    }

    private static Matrix2D WithNormal(Matrix2D matrix, DxfVector normal)
    {
        return normal.Z < 0 ? Matrix2D.MirrorX.Then(matrix) : matrix;
    }

    private static void AddLine(
        ExtractionContext context,
        Matrix2D matrix,
        double x1,
        double y1,
        double x2,
        double y2,
        DxfEntity entity)
    {
        if (!context.TryCount())
        {
            return;
        }

        var (sx, sy) = matrix.Apply(x1, y1);
        var (ex, ey) = matrix.Apply(x2, y2);

        context.Result.Lines.Add(new GeometryLine
        {
            Start = new GeometryPoint(sx, sy),
            End = new GeometryPoint(ex, ey),
            Layer = entity.Layer,
            LineType = entity.LineTypeName
        });
    }

    private static void AddArc(
        ExtractionContext context,
        Matrix2D matrix,
        double cx,
        double cy,
        double radius,
        double startAngle,
        double endAngle,
        DxfEntity entity)
    {
        if (radius <= 0)
        {
            return;
        }

        if (matrix.TryGetSimilarity(out var scale, out var rotation))
        {
            if (!context.TryCount())
            {
                return;
            }

            var (x, y) = matrix.Apply(cx, cy);
            context.Result.Arcs.Add(new GeometryArc
            {
                Center = new GeometryPoint(x, y),
                Radius = radius * scale,
                StartAngle = NormalizeAngle(startAngle + rotation),
                EndAngle = NormalizeAngle(endAngle + rotation),
                Layer = entity.Layer,
                LineType = entity.LineTypeName
            });
            return;
        }

        var sweep = endAngle - startAngle;
        while (sweep <= 0)
        {
            sweep += 360.0;
        }

        TessellateArc(context, matrix, cx, cy, radius, startAngle, sweep, entity);
    }

    private static void AddCircle(
        ExtractionContext context,
        Matrix2D matrix,
        double cx,
        double cy,
        double radius,
        DxfEntity entity)
    {
        if (radius <= 0)
        {
            return;
        }

        if (matrix.TryGetSimilarity(out var scale, out _))
        {
            if (!context.TryCount())
            {
                return;
            }

            var (x, y) = matrix.Apply(cx, cy);
            context.Result.Circles.Add(new GeometryCircle
            {
                Center = new GeometryPoint(x, y),
                Radius = radius * scale,
                Layer = entity.Layer,
                LineType = entity.LineTypeName
            });
            return;
        }

        TessellateArc(context, matrix, cx, cy, radius, 0, 360.0, entity);
    }

    private static void TessellateArc(
        ExtractionContext context,
        Matrix2D matrix,
        double cx,
        double cy,
        double radius,
        double startAngle,
        double sweep,
        DxfEntity entity)
    {
        var steps = Math.Max(2, (int)Math.Ceiling(sweep / ArcStepDegrees));
        var previousX = 0.0;
        var previousY = 0.0;

        for (var i = 0; i <= steps; i++)
        {
            var angle = (startAngle + sweep * i / steps) * Math.PI / 180.0;
            var x = cx + radius * Math.Cos(angle);
            var y = cy + radius * Math.Sin(angle);

            if (i > 0)
            {
                AddLine(context, matrix, previousX, previousY, x, y, entity);
            }

            previousX = x;
            previousY = y;
        }
    }

    private static void AddEllipse(ExtractionContext context, Matrix2D matrix, DxfEllipse ellipse, DxfEntity entity)
    {
        var majorX = ellipse.MajorAxis.X;
        var majorY = ellipse.MajorAxis.Y;
        var direction = ellipse.Normal.Z < 0 ? -1.0 : 1.0;
        var minorX = -majorY * ellipse.MinorAxisRatio * direction;
        var minorY = majorX * ellipse.MinorAxisRatio * direction;

        var start = ellipse.StartParameter;
        var end = ellipse.EndParameter;
        while (end <= start)
        {
            end += 2 * Math.PI;
        }

        var steps = Math.Max(8, (int)Math.Ceiling((end - start) * 180.0 / Math.PI / ArcStepDegrees));
        var previousX = 0.0;
        var previousY = 0.0;

        for (var i = 0; i <= steps; i++)
        {
            var t = start + (end - start) * i / steps;
            var x = ellipse.Center.X + majorX * Math.Cos(t) + minorX * Math.Sin(t);
            var y = ellipse.Center.Y + majorY * Math.Cos(t) + minorY * Math.Sin(t);

            if (i > 0)
            {
                AddLine(context, matrix, previousX, previousY, x, y, entity);
            }

            previousX = x;
            previousY = y;
        }
    }

    private static void AddSpline(ExtractionContext context, Matrix2D matrix, DxfSpline spline, DxfEntity entity)
    {
        var points = new List<(double X, double Y)>();

        if (spline.FitPoints.Count >= 2)
        {
            foreach (var point in spline.FitPoints)
            {
                points.Add((point.X, point.Y));
            }
        }
        else if (spline.ControlPoints.Count >= 2)
        {
            points = EvaluateBSpline(spline.ControlPoints, spline.KnotValues, spline.DegreeOfCurve);
        }

        if (points.Count < 2)
        {
            context.Skip("SPLINE");
            return;
        }

        context.SplineApproximated = true;

        for (var i = 1; i < points.Count; i++)
        {
            AddLine(context, matrix, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y, entity);
        }
    }

    private static List<(double X, double Y)> EvaluateBSpline(
        IList<DxfControlPoint> controlPoints,
        IList<double> knots,
        int degree)
    {
        var count = controlPoints.Count;
        var polygon = controlPoints.Select(p => (p.Point.X, p.Point.Y)).ToList();

        if (degree < 1 || count <= degree || knots.Count != count + degree + 1)
        {
            return polygon;
        }

        var result = new List<(double X, double Y)>();
        var tStart = knots[degree];
        var tEnd = knots[count];

        if (tEnd <= tStart)
        {
            return polygon;
        }

        for (var i = 0; i <= SplineSamples; i++)
        {
            var t = tStart + (tEnd - tStart) * i / SplineSamples;

            var span = degree;
            while (span < count - 1 && t >= knots[span + 1])
            {
                span++;
            }

            var dx = new double[degree + 1];
            var dy = new double[degree + 1];
            for (var j = 0; j <= degree; j++)
            {
                var point = controlPoints[j + span - degree].Point;
                dx[j] = point.X;
                dy[j] = point.Y;
            }

            for (var r = 1; r <= degree; r++)
            {
                for (var j = degree; j >= r; j--)
                {
                    var left = knots[j + span - degree];
                    var denominator = knots[j + 1 + span - r] - left;
                    var alpha = denominator == 0 ? 0 : (t - left) / denominator;
                    dx[j] = (1 - alpha) * dx[j - 1] + alpha * dx[j];
                    dy[j] = (1 - alpha) * dy[j - 1] + alpha * dy[j];
                }
            }

            result.Add((dx[degree], dy[degree]));
        }

        return result;
    }

    private static void AddText(
        ExtractionContext context,
        Matrix2D matrix,
        string? rawText,
        double x,
        double y,
        double height,
        DxfEntity entity)
    {
        var text = CleanText(rawText);
        if (text.Length == 0)
        {
            return;
        }

        var (px, py) = matrix.Apply(x, y);
        context.Result.Texts.Add(new GeometryText
        {
            Text = text,
            Position = new GeometryPoint(px, py),
            Height = height,
            Layer = entity.Layer
        });
    }

    private static string GetMTextValue(DxfMText mText)
    {
        var builder = new StringBuilder();
        foreach (var chunk in mText.ExtendedText)
        {
            builder.Append(chunk);
        }

        builder.Append(mText.Text);
        return builder.ToString();
    }

    private static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw
            .Replace("\\P", " ")
            .Replace("\\\\", "\\");

        text = MTextCodes.Replace(text, string.Empty);
        text = text
            .Replace("%%c", "\u2300", StringComparison.OrdinalIgnoreCase)
            .Replace("%%d", "\u00B0", StringComparison.OrdinalIgnoreCase)
            .Replace("%%p", "\u00B1", StringComparison.OrdinalIgnoreCase);

        return Whitespace.Replace(text, " ").Trim();
    }

    private static void AddHatch(ExtractionContext context, Matrix2D matrix, DxfHatch hatch, DxfEntity entity)
    {
        var geometryHatch = new GeometryHatch
        {
            PatternName = hatch.PatternName,
            Layer = entity.Layer,
            LineType = entity.LineTypeName
        };

        foreach (var path in hatch.BoundaryPaths)
        {
            var points = new List<GeometryPoint>();

            switch (path)
            {
                case DxfHatch.PolylineBoundaryPath polylinePath:
                    foreach (var vertex in polylinePath.Vertices)
                    {
                        AddHatchPoint(points, matrix, vertex.Location.X, vertex.Location.Y);
                    }
                    break;

                case DxfHatch.NonPolylineBoundaryPath edgePath:
                    foreach (var edge in edgePath.Edges)
                    {
                        switch (edge)
                        {
                            case DxfHatch.LineBoundaryPathEdge lineEdge:
                                AddHatchPoint(points, matrix, lineEdge.StartPoint.X, lineEdge.StartPoint.Y);
                                AddHatchPoint(points, matrix, lineEdge.EndPoint.X, lineEdge.EndPoint.Y);
                                break;

                            case DxfHatch.CircularArcBoundaryPathEdge arcEdge:
                                AddArcEdgePoints(points, matrix, arcEdge);
                                break;
                        }
                    }
                    break;
            }

            if (points.Count > 0)
            {
                geometryHatch.BoundaryPaths.Add(points);
            }
        }

        context.Result.Hatches.Add(geometryHatch);
    }

    private static void AddArcEdgePoints(
        List<GeometryPoint> points,
        Matrix2D matrix,
        DxfHatch.CircularArcBoundaryPathEdge arcEdge)
    {
        var start = arcEdge.StartAngle;
        var end = arcEdge.EndAngle;

        if (!arcEdge.IsCounterClockwise)
        {
            (start, end) = (end, start);
        }

        while (end <= start)
        {
            end += 360.0;
        }

        for (var i = 0; i <= 2; i++)
        {
            var angle = (start + (end - start) * i / 2.0) * Math.PI / 180.0;
            AddHatchPoint(
                points,
                matrix,
                arcEdge.Center.X + arcEdge.Radius * Math.Cos(angle),
                arcEdge.Center.Y + arcEdge.Radius * Math.Sin(angle));
        }
    }

    private static void AddHatchPoint(List<GeometryPoint> points, Matrix2D matrix, double x, double y)
    {
        var (px, py) = matrix.Apply(x, y);
        points.Add(new GeometryPoint(px, py));
    }

    private static void AddDimension(
        ExtractionContext context,
        Matrix2D matrix,
        DxfDimensionBase dimension,
        DxfEntity entity)
    {
        var userText = dimension.Text;
        var hasUserText = !string.IsNullOrWhiteSpace(userText) && userText != "<>";
        var (x, y) = matrix.Apply(dimension.TextMidPoint.X, dimension.TextMidPoint.Y);

        context.Result.Dimensions.Add(new GeometryDimension
        {
            Text = hasUserText ? CleanText(userText) : null,
            Value = dimension.ActualMeasurement > 0 ? dimension.ActualMeasurement : null,
            Position = new GeometryPoint(x, y),
            Layer = entity.Layer
        });
    }

    private static double NormalizeAngle(double angle)
    {
        var value = angle % 360.0;
        return value < 0 ? value + 360.0 : value;
    }

    private static DrawingUnit MapUnit(DxfUnits unit)
    {
        return unit switch
        {
            DxfUnits.Centimeters => DrawingUnit.Centimeter,
            DxfUnits.Meters => DrawingUnit.Meter,
            DxfUnits.Inches => DrawingUnit.Inch,
            DxfUnits.Feet => DrawingUnit.Foot,
            _ => DrawingUnit.Millimeter
        };
    }

    private static string DescribeVersion(DxfAcadVersion version)
    {
        return version switch
        {
            DxfAcadVersion.R12 => "R12 (AC1009)",
            DxfAcadVersion.R13 => "R13 (AC1012)",
            DxfAcadVersion.R14 => "R14 (AC1014)",
            DxfAcadVersion.R2000 => "2000 (AC1015)",
            DxfAcadVersion.R2004 => "2004 (AC1018)",
            DxfAcadVersion.R2007 => "2007 (AC1021)",
            DxfAcadVersion.R2010 => "2010 (AC1024)",
            DxfAcadVersion.R2013 => "2013 (AC1027)",
            DxfAcadVersion.R2018 => "2018 (AC1032)",
            _ => version.ToString()
        };
    }

    private sealed class ExtractionContext
    {
        private int _primitiveCount;

        public ExtractionContext(
            ExtractedGeometry result,
            Dictionary<string, DxfBlock> blocks,
            CancellationToken token)
        {
            Result = result;
            Blocks = blocks;
            Token = token;
        }

        public ExtractedGeometry Result { get; }
        public Dictionary<string, DxfBlock> Blocks { get; }
        public CancellationToken Token { get; }
        public Dictionary<string, int> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Truncated { get; private set; }
        public bool SplineApproximated { get; set; }

        public bool TryCount()
        {
            if (_primitiveCount >= MaxPrimitives)
            {
                Truncated = true;
                return false;
            }

            _primitiveCount++;
            return true;
        }

        public void Skip(string? typeName)
        {
            var key = string.IsNullOrWhiteSpace(typeName) ? "UNKNOWN" : typeName;
            Skipped[key] = Skipped.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }

    private readonly record struct Matrix2D(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static Matrix2D Identity => new(1, 0, 0, 1, 0, 0);

        public static Matrix2D MirrorX => new(-1, 0, 0, 1, 0, 0);

        public (double X, double Y) Apply(double x, double y)
        {
            return (A * x + C * y + Tx, B * x + D * y + Ty);
        }

        public Matrix2D Then(Matrix2D outer)
        {
            return new Matrix2D(
                outer.A * A + outer.C * B,
                outer.B * A + outer.D * B,
                outer.A * C + outer.C * D,
                outer.B * C + outer.D * D,
                outer.A * Tx + outer.C * Ty + outer.Tx,
                outer.B * Tx + outer.D * Ty + outer.Ty);
        }

        public bool TryGetSimilarity(out double scale, out double rotationDegrees)
        {
            var determinant = A * D - B * C;
            var scaleX = Math.Sqrt(A * A + B * B);
            var scaleY = Math.Sqrt(C * C + D * D);
            var reference = Math.Max(1.0, scaleX);

            var isSimilarity = determinant > 0
                && Math.Abs(A * C + B * D) <= 1e-6 * reference * reference
                && Math.Abs(scaleX - scaleY) <= 1e-6 * reference;

            scale = isSimilarity ? scaleX : 1.0;
            rotationDegrees = isSimilarity ? Math.Atan2(B, A) * 180.0 / Math.PI : 0.0;
            return isSimilarity;
        }
    }
}