using System.Globalization;
using System.Text;

namespace CadToCode.Step;

public sealed class StepExportOptions
{
    public static StepExportOptions Default { get; } = new();

    /// <summary>Manufacturing part number. Defaults to the document name.</summary>
    public string? PartNumber { get; init; }

    public string? PartDescription { get; init; }

    public string? Revision { get; init; }

    public string Author { get; init; } = "CadToCode";

    public string Organization { get; init; } = "CadToCode";

    /// <summary>Equipment manifest the part belongs to.</summary>
    public string? ManifestId { get; init; }

    /// <summary>Manifest part key this file represents.</summary>
    public string? PartKey { get; init; }

    public int Quantity { get; init; } = 1;

    /// <summary>When true the document must contain exactly one body, as a manufacturable part.</summary>
    public bool RequireSingleBody { get; init; }

    /// <summary>When true the file is labelled as a generic, non-engineered model.</summary>
    public bool IsGeneric { get; init; }

    /// <summary>Extra FILE_DESCRIPTION lines.</summary>
    public IReadOnlyList<string> ExtraDescriptions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<IStepExportHook> Hooks { get; init; } = Array.Empty<IStepExportHook>();
}

public sealed class StepExportContext
{
    internal StepExportContext(StepDocument document, StepExportOptions options, string fileName, string timestamp)
    {
        Document = document;
        Options = options;
        FileName = fileName;
        Timestamp = timestamp;
    }

    public StepDocument Document { get; }

    public StepExportOptions Options { get; }

    public string FileName { get; }

    public string Timestamp { get; }

    public int SolidCount => Document.SolidCount;

    /// <summary>FILE_DESCRIPTION lines. Hooks may add to or replace them in BeforeWrite.</summary>
    public List<string> Descriptions { get; } = new();
}

public interface IStepExportHook
{
    /// <summary>Runs before anything is written. Throw to abort the export.</summary>
    void BeforeWrite(StepExportContext context);

    /// <summary>Runs on the finished STEP text. Return the text to write, or null to keep it unchanged.</summary>
    string? AfterWrite(StepExportContext context, string step);
}

internal static class StepWriter
{
    private const double AxisTolerance = 1e-9;
    private const double SurfaceTolerance = 1e-7;

    public static string Write(StepDocument document)
    {
        return Write(document, null);
    }

    public static string Write(StepDocument document, StepExportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);

        options ??= StepExportOptions.Default;

        if (options.RequireSingleBody && document.Solids.Count != 1)
        {
            throw new InvalidOperationException(
                "Part '" + (options.PartKey ?? document.Name) + "' must contain exactly one body, but has "
                + document.Solids.Count.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var emitter = new Emitter();
        var name = document.Name;
        var fileName = name + ".step";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var exportContext = new StepExportContext(document, options, fileName, timestamp);

        exportContext.Descriptions.Add("CadToCode STEP export");

        if (options.IsGeneric)
        {
            exportContext.Descriptions.Add("GENERIC NON-ENGINEERED MODEL");
        }

        if (!string.IsNullOrWhiteSpace(options.ManifestId))
        {
            exportContext.Descriptions.Add("EQUIPMENT=" + options.ManifestId);
        }

        if (!string.IsNullOrWhiteSpace(options.PartKey))
        {
            exportContext.Descriptions.Add("PART=" + options.PartKey);
            exportContext.Descriptions.Add("QUANTITY=" + Math.Max(1, options.Quantity).ToString(CultureInfo.InvariantCulture));
        }

        exportContext.Descriptions.AddRange(options.ExtraDescriptions.Where(d => !string.IsNullOrWhiteSpace(d)));

        foreach (var hook in options.Hooks)
        {
            hook.BeforeWrite(exportContext);
        }

        var partNumber = string.IsNullOrWhiteSpace(options.PartNumber) ? name : options.PartNumber.Trim();
        var partDescription = options.PartDescription ?? string.Empty;
        var revision = options.Revision ?? string.Empty;

        var appContext = emitter.Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
        emitter.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,{R(appContext)})");
        var productContext = emitter.Add($"PRODUCT_CONTEXT('',{R(appContext)},'mechanical')");
        var product = emitter.Add($"PRODUCT({Str(partNumber)},{Str(name)},{Str(partDescription)},({R(productContext)}))");
        emitter.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,({R(product)}))");
        var formation = emitter.Add($"PRODUCT_DEFINITION_FORMATION({Str(revision)},'',{R(product)})");
        var definitionContext = emitter.Add($"PRODUCT_DEFINITION_CONTEXT('part definition',{R(appContext)},'design')");
        var definition = emitter.Add($"PRODUCT_DEFINITION('design','',{R(formation)},{R(definitionContext)})");
        var shape = emitter.Add($"PRODUCT_DEFINITION_SHAPE('','',{R(definition)})");

        var lengthUnit = emitter.Add("(LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.))");
        var angleUnit = emitter.Add("(NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.))");
        var solidAngleUnit = emitter.Add("(NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT())");
        var uncertainty = emitter.Add(
            $"UNCERTAINTY_MEASURE_WITH_UNIT(LENGTH_MEASURE(1.E-07),{R(lengthUnit)},'distance_accuracy_value','confusion accuracy')");
        var context = emitter.Add(
            $"(GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT(({R(uncertainty)})) GLOBAL_UNIT_ASSIGNED_CONTEXT(({R(lengthUnit)},{R(angleUnit)},{R(solidAngleUnit)})) REPRESENTATION_CONTEXT('Context3D','3D Context with UNIT and UNCERTAINTY'))");

        var items = new List<int> { Placement(emitter, 0, 0, 0, 0, 0, 1, 1, 0, 0) };

        foreach (var solid in document.Solids)
        {
            switch (solid)
            {
                case PrismSolid prism:
                    items.Add(WritePrism(emitter, prism));
                    break;

                case CylinderSolid cylinder:
                    items.Add(WriteCylinder(emitter, cylinder));
                    break;

                case ExtrudedProfileSolid extrusion:
                    items.Add(WriteExtrusion(emitter, extrusion));
                    break;

                case RevolveSolid revolve:
                    items.Add(WriteRevolution(emitter, revolve.Name, revolve.Profile, revolve.WorldFrame));
                    break;

                case SweepSolid sweep:
                    items.Add(WriteSweepSolid(emitter, sweep));
                    break;

                case ConeSolid cone:
                    items.Add(WriteCone(emitter, cone));
                    break;

                case SphereSolid sphere:
                    items.Add(WriteSphere(emitter, sphere));
                    break;

                case TorusSolid torus:
                    items.Add(WriteTorus(emitter, torus));
                    break;
            }
        }

        var representation = emitter.Add(
            $"ADVANCED_BREP_SHAPE_REPRESENTATION({Str(name)},({string.Join(",", items.Select(R))}),{R(context)})");
        emitter.Add($"SHAPE_DEFINITION_REPRESENTATION({R(shape)},{R(representation)})");

        var builder = new StringBuilder();
        builder.Append("ISO-10303-21;\n");
        builder.Append("HEADER;\n");
        builder.Append("FILE_DESCRIPTION((")
            .Append(string.Join(",", exportContext.Descriptions.Select(Str)))
            .Append("),'2;1');\n");
        builder.Append("FILE_NAME(").Append(Str(exportContext.FileName)).Append(',').Append(Str(exportContext.Timestamp))
            .Append(",(").Append(Str(options.Author)).Append("),(").Append(Str(options.Organization))
            .Append("),'CadToCode STEP writer','CadToCode','');\n");
        builder.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\n");
        builder.Append("ENDSEC;\n");
        builder.Append("DATA;\n");
        builder.Append(emitter.Data);
        builder.Append("ENDSEC;\n");
        builder.Append("END-ISO-10303-21;\n");

        var step = builder.ToString();

        foreach (var hook in options.Hooks)
        {
            step = hook.AfterWrite(exportContext, step) ?? step;
        }

        return step;
    }

    private static int WritePrism(Emitter e, PrismSolid solid)
    {
        var z0 = solid.Z;
        var z1 = solid.Z + solid.Depth;

        var loops = new List<List<(double X, double Y)>> { solid.Outline };
        loops.AddRange(solid.Holes);

        var bottomEdges = new List<int[]>();
        var topEdges = new List<int[]>();
        var verticalEdges = new List<int[]>();

        foreach (var loop in loops)
        {
            var count = loop.Count;
            var bottomVertices = new int[count];
            var topVertices = new int[count];

            for (var i = 0; i < count; i++)
            {
                bottomVertices[i] = Vertex(e, loop[i].X, loop[i].Y, z0);
                topVertices[i] = Vertex(e, loop[i].X, loop[i].Y, z1);
            }

            var bottom = new int[count];
            var top = new int[count];
            var vertical = new int[count];

            for (var i = 0; i < count; i++)
            {
                var j = (i + 1) % count;

                bottom[i] = LineEdge(
                    e, bottomVertices[i], bottomVertices[j],
                    (loop[i].X, loop[i].Y, z0), (loop[j].X, loop[j].Y, z0));

                top[i] = LineEdge(
                    e, topVertices[i], topVertices[j],
                    (loop[i].X, loop[i].Y, z1), (loop[j].X, loop[j].Y, z1));

                vertical[i] = LineEdge(
                    e, bottomVertices[i], topVertices[i],
                    (loop[i].X, loop[i].Y, z0), (loop[i].X, loop[i].Y, z1));
            }

            bottomEdges.Add(bottom);
            topEdges.Add(top);
            verticalEdges.Add(vertical);
        }

        var faces = new List<int>();

        var topBounds = new List<int>();
        var bottomBounds = new List<int>();

        for (var k = 0; k < loops.Count; k++)
        {
            var count = loops[k].Count;

            var forward = Enumerable.Range(0, count).Select(i => (topEdges[k][i], true));
            var reverse = Enumerable.Range(0, count).Select(i => (bottomEdges[k][count - 1 - i], false));

            var topLoop = EdgeLoop(e, forward);
            var bottomLoop = EdgeLoop(e, reverse);

            topBounds.Add(k == 0 ? e.Add($"FACE_OUTER_BOUND('',{R(topLoop)},.T.)") : e.Add($"FACE_BOUND('',{R(topLoop)},.T.)"));
            bottomBounds.Add(k == 0 ? e.Add($"FACE_OUTER_BOUND('',{R(bottomLoop)},.T.)") : e.Add($"FACE_BOUND('',{R(bottomLoop)},.T.)"));
        }

        var first = loops[0][0];

        var topPlane = Plane(e, first.X, first.Y, z1, 0, 0, 1, 1, 0, 0);
        faces.Add(Face(e, topBounds, topPlane));

        var bottomPlane = Plane(e, first.X, first.Y, z0, 0, 0, -1, 1, 0, 0);
        faces.Add(Face(e, bottomBounds, bottomPlane));

        for (var k = 0; k < loops.Count; k++)
        {
            var loop = loops[k];
            var count = loop.Count;

            for (var i = 0; i < count; i++)
            {
                var j = (i + 1) % count;
                var dx = loop[j].X - loop[i].X;
                var dy = loop[j].Y - loop[i].Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                var ux = dx / length;
                var uy = dy / length;

                var sideLoop = EdgeLoop(e, new[]
                {
                    (bottomEdges[k][i], true),
                    (verticalEdges[k][j], true),
                    (topEdges[k][i], false),
                    (verticalEdges[k][i], false)
                });

                var bound = e.Add($"FACE_OUTER_BOUND('',{R(sideLoop)},.T.)");
                var plane = Plane(e, loop[i].X, loop[i].Y, z0, uy, -ux, 0, ux, uy, 0);
                faces.Add(Face(e, new List<int> { bound }, plane));
            }
        }

        return Brep(e, solid.Name, faces);
    }

    private static int WriteCylinder(Emitter e, CylinderSolid solid)
    {
        var z0 = solid.Z;
        var z1 = solid.Z + solid.Height;
        var cx = solid.Cx;
        var cy = solid.Cy;
        var radius = solid.Radius;

        var bottomVertex = Vertex(e, cx + radius, cy, z0);
        var topVertex = Vertex(e, cx + radius, cy, z1);

        var bottomAxis = Placement(e, cx, cy, z0, 0, 0, 1, 1, 0, 0);
        var topAxis = Placement(e, cx, cy, z1, 0, 0, 1, 1, 0, 0);

        var bottomCircle = e.Add($"CIRCLE('',{R(bottomAxis)},{Real(radius)})");
        var topCircle = e.Add($"CIRCLE('',{R(topAxis)},{Real(radius)})");

        var bottomEdge = e.Add($"EDGE_CURVE('',{R(bottomVertex)},{R(bottomVertex)},{R(bottomCircle)},.T.)");
        var topEdge = e.Add($"EDGE_CURVE('',{R(topVertex)},{R(topVertex)},{R(topCircle)},.T.)");

        var seam = LineEdge(
            e, bottomVertex, topVertex,
            (cx + radius, cy, z0), (cx + radius, cy, z1));

        var faces = new List<int>();

        var lateralLoop = EdgeLoop(e, new[]
        {
            (bottomEdge, true),
            (seam, true),
            (topEdge, false),
            (seam, false)
        });
        var lateralBound = e.Add($"FACE_OUTER_BOUND('',{R(lateralLoop)},.T.)");
        var surface = e.Add($"CYLINDRICAL_SURFACE('',{R(bottomAxis)},{Real(radius)})");
        faces.Add(Face(e, new List<int> { lateralBound }, surface));

        var topLoop = EdgeLoop(e, new[] { (topEdge, true) });
        var topBound = e.Add($"FACE_OUTER_BOUND('',{R(topLoop)},.T.)");
        var topPlane = Plane(e, cx, cy, z1, 0, 0, 1, 1, 0, 0);
        faces.Add(Face(e, new List<int> { topBound }, topPlane));

        var bottomLoop = EdgeLoop(e, new[] { (bottomEdge, false) });
        var bottomBound = e.Add($"FACE_OUTER_BOUND('',{R(bottomLoop)},.T.)");
        var bottomPlane = Plane(e, cx, cy, z0, 0, 0, -1, 1, 0, 0);
        faces.Add(Face(e, new List<int> { bottomBound }, bottomPlane));

        return Brep(e, solid.Name, faces);
    }

    private static int WriteExtrusion(Emitter e, ExtrudedProfileSolid solid)
    {
        var start = solid.WorldFrame;
        var end = start.TranslateLocal(0, 0, solid.Depth);

        var path = new PathSegment3D[] { new LineSegment3D(start.Origin, end.Origin) };

        return WriteSweep(e, solid.Name, solid.Profile, new[] { start, end }, path);
    }

    private static int WriteSweepSolid(Emitter e, SweepSolid solid)
    {
        var world = solid.WorldFrame;
        var stations = new List<Frame3D>();

        foreach (var frame in solid.StartFrames)
        {
            stations.Add(world.Compose(frame));
        }

        stations.Add(world.Compose(solid.EndFrame));

        var path = new List<PathSegment3D>();

        foreach (var segment in solid.Route.Segments)
        {
            if (segment is ArcSegment3D arc)
            {
                path.Add(new ArcSegment3D(
                    world.ToWorld(arc.Center),
                    world.ToWorld(arc.Start),
                    world.ToWorldDirection(arc.Normal),
                    arc.SweepDegrees));
            }
            else
            {
                path.Add(new LineSegment3D(world.ToWorld(segment.Start), world.ToWorld(segment.End)));
            }
        }

        return WriteSweep(e, solid.Name, solid.Profile, stations, path);
    }

    private static int WriteSweep(
        Emitter e,
        string name,
        Profile2D profile,
        IReadOnlyList<Frame3D> stations,
        IReadOnlyList<PathSegment3D> path)
    {
        var loops = new List<ProfileLoop> { profile.Outer };
        loops.AddRange(profile.Holes);

        var stationCount = stations.Count;
        var segmentCount = path.Count;

        var points = new Vec3[stationCount][][];
        var vertices = new int[stationCount][][];
        var curves = new int[stationCount][][];

        for (var j = 0; j < stationCount; j++)
        {
            points[j] = new Vec3[loops.Count][];
            vertices[j] = new int[loops.Count][];
            curves[j] = new int[loops.Count][];

            for (var l = 0; l < loops.Count; l++)
            {
                var segments = loops[l].Segments;
                var count = segments.Count;

                points[j][l] = new Vec3[count];
                vertices[j][l] = new int[count];
                curves[j][l] = new int[count];

                for (var k = 0; k < count; k++)
                {
                    var s = segments[k].Start;
                    var p = stations[j].ToWorld(s.X, s.Y, 0);

                    points[j][l][k] = p;
                    vertices[j][l][k] = Vertex(e, p);
                }

                for (var k = 0; k < count; k++)
                {
                    var next = (k + 1) % count;

                    curves[j][l][k] = ProfileEdge(
                        e,
                        segments[k],
                        stations[j],
                        vertices[j][l][k],
                        vertices[j][l][next],
                        points[j][l][k],
                        points[j][l][next]);
                }
            }
        }

        var rails = new int[segmentCount][][];

        for (var i = 0; i < segmentCount; i++)
        {
            rails[i] = new int[loops.Count][];

            for (var l = 0; l < loops.Count; l++)
            {
                var count = loops[l].Segments.Count;
                rails[i][l] = new int[count];

                for (var k = 0; k < count; k++)
                {
                    rails[i][l][k] = RailEdge(
                        e,
                        path[i],
                        vertices[i][l][k],
                        vertices[i + 1][l][k],
                        points[i][l][k],
                        points[i + 1][l][k]);
                }
            }
        }

        var faces = new List<int>();

        for (var i = 0; i < segmentCount; i++)
        {
            for (var l = 0; l < loops.Count; l++)
            {
                var segments = loops[l].Segments;
                var count = segments.Count;

                for (var k = 0; k < count; k++)
                {
                    var next = (k + 1) % count;

                    var loop = EdgeLoop(e, new[]
                    {
                        (curves[i][l][k], true),
                        (rails[i][l][next], true),
                        (curves[i + 1][l][k], false),
                        (rails[i][l][k], false)
                    });

                    var bound = e.Add($"FACE_OUTER_BOUND('',{R(loop)},.T.)");
                    var surface = SweepSurface(e, segments[k], stations[i], path[i], points[i][l][k], points[i][l][next]);

                    faces.Add(Face(e, new List<int> { bound }, surface.Surface, surface.SameSense));
                }
            }
        }

        var startFrame = stations[0];
        var endFrame = stations[stationCount - 1];
        var startBounds = new List<int>();
        var endBounds = new List<int>();

        for (var l = 0; l < loops.Count; l++)
        {
            var loopIndex = l;
            var count = loops[l].Segments.Count;

            var reverse = Enumerable.Range(0, count).Select(i => (curves[0][loopIndex][count - 1 - i], false));
            var forward = Enumerable.Range(0, count).Select(i => (curves[stationCount - 1][loopIndex][i], true));

            startBounds.Add(Bound(e, EdgeLoop(e, reverse), l == 0));
            endBounds.Add(Bound(e, EdgeLoop(e, forward), l == 0));
        }

        var startPlane = PlaneV(e, points[0][0][0], -startFrame.ZAxis, startFrame.XAxis);
        faces.Add(Face(e, startBounds, startPlane));

        var endPlane = PlaneV(e, points[stationCount - 1][0][0], endFrame.ZAxis, endFrame.XAxis);
        faces.Add(Face(e, endBounds, endPlane));

        return Brep(e, name, faces);
    }

    private static int ProfileEdge(
        Emitter e,
        ProfileSegment segment,
        Frame3D frame,
        int startVertex,
        int endVertex,
        Vec3 start,
        Vec3 end)
    {
        if (segment is ArcSegment2D arc)
        {
            var center = frame.ToWorld(arc.Center.X, arc.Center.Y, 0);
            var axis = arc.SweepAngle > 0 ? frame.ZAxis : -frame.ZAxis;

            return ArcEdge(e, center, axis, start, arc.Radius, startVertex, endVertex);
        }

        return LineEdge(e, startVertex, endVertex, start, end);
    }

    private static int RailEdge(
        Emitter e,
        PathSegment3D segment,
        int startVertex,
        int endVertex,
        Vec3 start,
        Vec3 end)
    {
        if (segment is ArcSegment3D arc)
        {
            var offset = (start - arc.Center).Dot(arc.Normal);
            var center = arc.Center + arc.Normal * offset;
            var radius = (start - center).Length;
            var axis = arc.SweepDegrees > 0 ? arc.Normal : -arc.Normal;

            return ArcEdge(e, center, axis, start, radius, startVertex, endVertex);
        }

        return LineEdge(e, startVertex, endVertex, start, end);
    }

    private static (int Surface, bool SameSense) SweepSurface(
        Emitter e,
        ProfileSegment segment,
        Frame3D frame,
        PathSegment3D path,
        Vec3 p0,
        Vec3 p1)
    {
        if (path is ArcSegment3D bend)
        {
            return BendSurface(e, segment, frame, bend, p0, p1);
        }

        var tangent = frame.ZAxis;

        if (segment is ArcSegment2D arc)
        {
            var center = frame.ToWorld(arc.Center.X, arc.Center.Y, 0);
            var placement = PlacementV(e, center, tangent, frame.XAxis);
            var cylinder = e.Add($"CYLINDRICAL_SURFACE('',{R(placement)},{Real(arc.Radius)})");

            return (cylinder, arc.SweepAngle > 0);
        }

        var direction = (p1 - p0).Normalized();
        var normal = direction.Cross(tangent).Normalized();

        return (PlaneV(e, p0, normal, direction), true);
    }

    private static (int Surface, bool SameSense) BendSurface(
        Emitter e,
        ProfileSegment segment,
        Frame3D frame,
        ArcSegment3D bend,
        Vec3 p0,
        Vec3 p1)
    {
        var axis = bend.Normal;
        var origin = bend.Center;

        if (segment is ArcSegment2D arc)
        {
            var center = frame.ToWorld(arc.Center.X, arc.Center.Y, 0);
            var (height, radial, distance) = AxisCoordinates(origin, axis, center);

            if (distance <= SurfaceTolerance)
            {
                var spherePlacement = PlacementV(e, center, axis, frame.XAxis);
                var sphere = e.Add($"SPHERICAL_SURFACE('',{R(spherePlacement)},{Real(arc.Radius)})");

                return (sphere, arc.SweepAngle > 0);
            }

            if (arc.Radius >= distance - SurfaceTolerance)
            {
                throw new InvalidOperationException(
                    "A profile arc swept around a bend must have a radius smaller than its distance from the bend axis.");
            }

            var location = origin + axis * height;
            var torusPlacement = PlacementV(e, location, axis, radial);
            var torus = e.Add($"TOROIDAL_SURFACE('',{R(torusPlacement)},{Real(distance)},{Real(arc.Radius)})");

            return (torus, arc.SweepAngle > 0);
        }

        var start = AxisCoordinates(origin, axis, p0);
        var end = AxisCoordinates(origin, axis, p1);

        var dRadius = end.Distance - start.Distance;
        var dHeight = end.Height - start.Height;

        var direction = (p1 - p0).Normalized();
        var outward = direction.Cross(frame.ZAxis).Normalized();
        var anchor = origin + axis * start.Height;

        if (Math.Abs(dRadius) <= SurfaceTolerance)
        {
            var placement = PlacementV(e, anchor, axis, start.Radial);
            var cylinder = e.Add($"CYLINDRICAL_SURFACE('',{R(placement)},{Real(start.Distance)})");

            return (cylinder, outward.Dot(start.Radial) > 0);
        }

        if (Math.Abs(dHeight) <= SurfaceTolerance)
        {
            return (PlaneV(e, p0, outward, start.Radial), true);
        }

        var ratio = dRadius / dHeight;
        var semiAngle = Math.Atan(Math.Abs(ratio));
        var coneAxis = ratio > 0 ? axis : -axis;
        var conePlacement = PlacementV(e, anchor, coneAxis, start.Radial);
        var cone = e.Add($"CONICAL_SURFACE('',{R(conePlacement)},{Real(start.Distance)},{Real(semiAngle)})");

        return (cone, outward.Dot(start.Radial) > 0);
    }

    private static (double Height, Vec3 Radial, double Distance) AxisCoordinates(Vec3 origin, Vec3 axis, Vec3 point)
    {
        var offset = point - origin;
        var height = offset.Dot(axis);
        var radialVector = offset - axis * height;
        var distance = radialVector.Length;

        var radial = distance > 1e-12 ? radialVector / distance : Vec3.Zero;

        return (height, radial, distance);
    }

    private static int WriteRevolution(Emitter e, string name, Profile2D profile, Frame3D frame)
    {
        var loops = new List<ProfileLoop> { profile.Outer };
        loops.AddRange(profile.Holes);

        var faces = new List<int>();

        foreach (var loop in loops)
        {
            var segments = loop.Segments;
            var count = segments.Count;

            var radii = new double[count];
            var heights = new double[count];
            var vertices = new int[count];
            var circles = new int[count];

            Array.Fill(circles, -1);

            for (var k = 0; k < count; k++)
            {
                var s = segments[k].Start;

                radii[k] = Math.Abs(s.X) <= AxisTolerance ? 0.0 : s.X;
                heights[k] = s.Y;
                vertices[k] = Vertex(e, frame.ToWorld(radii[k], 0, heights[k]));
            }

            int Circle(int index)
            {
                if (radii[index] <= AxisTolerance)
                {
                    return -1;
                }

                if (circles[index] < 0)
                {
                    var center = frame.ToWorld(0, 0, heights[index]);
                    var start = frame.ToWorld(radii[index], 0, heights[index]);

                    circles[index] = ArcEdge(e, center, frame.ZAxis, start, radii[index], vertices[index], vertices[index]);
                }

                return circles[index];
            }

            for (var k = 0; k < count; k++)
            {
                var segment = segments[k];

                if (RevolveSolid.IsOnAxis(segment))
                {
                    continue;
                }

                var next = (k + 1) % count;

                var r0 = radii[k];
                var r1 = radii[next];
                var h0 = heights[k];
                var h1 = heights[next];

                var start = frame.ToWorld(r0, 0, h0);
                var end = frame.ToWorld(r1, 0, h1);
                var seam = SeamEdge(e, frame, segment, vertices[k], vertices[next], start, end);

                if (segment is ArcSegment2D arc)
                {
                    var centerRadius = Math.Abs(arc.Center.X) <= AxisTolerance ? 0.0 : arc.Center.X;
                    var location = frame.ToWorld(0, 0, arc.Center.Y);
                    var placement = PlacementV(e, location, frame.ZAxis, frame.XAxis);

                    int surface;

                    if (centerRadius <= AxisTolerance)
                    {
                        surface = e.Add($"SPHERICAL_SURFACE('',{R(placement)},{Real(arc.Radius)})");
                    }
                    else
                    {
                        surface = e.Add($"TOROIDAL_SURFACE('',{R(placement)},{Real(centerRadius)},{Real(arc.Radius)})");
                    }

                    var bounds = RevolveBounds(e, Circle(k), seam, Circle(next));
                    faces.Add(Face(e, bounds, surface, arc.SweepAngle > 0));
                    continue;
                }

                var dRadius = r1 - r0;
                var dHeight = h1 - h0;

                if (Math.Abs(dRadius) <= SurfaceTolerance)
                {
                    var placement = PlacementV(e, frame.ToWorld(0, 0, h0), frame.ZAxis, frame.XAxis);
                    var cylinder = e.Add($"CYLINDRICAL_SURFACE('',{R(placement)},{Real(r0)})");

                    var bounds = RevolveBounds(e, Circle(k), seam, Circle(next));
                    faces.Add(Face(e, bounds, cylinder, dHeight > 0));
                    continue;
                }

                if (Math.Abs(dHeight) <= SurfaceTolerance)
                {
                    var inward = dRadius < 0;
                    var normal = inward ? frame.ZAxis : -frame.ZAxis;

                    var outerIndex = r0 >= r1 ? k : next;
                    var innerIndex = outerIndex == k ? next : k;

                    var planeBounds = new List<int>
                    {
                        Bound(e, EdgeLoop(e, new[] { (Circle(outerIndex), inward) }), true)
                    };

                    var innerEdge = Circle(innerIndex);

                    if (innerEdge >= 0)
                    {
                        planeBounds.Add(Bound(e, EdgeLoop(e, new[] { (innerEdge, !inward) }), false));
                    }

                    var plane = PlaneV(e, frame.ToWorld(0, 0, h0), normal, frame.XAxis);
                    faces.Add(Face(e, planeBounds, plane));
                    continue;
                }

                var ratio = dRadius / dHeight;
                var semiAngle = Math.Atan(Math.Abs(ratio));
                var coneAxis = ratio > 0 ? frame.ZAxis : -frame.ZAxis;
                var conePlacement = PlacementV(e, frame.ToWorld(0, 0, h0), coneAxis, frame.XAxis);
                var cone = e.Add($"CONICAL_SURFACE('',{R(conePlacement)},{Real(r0)},{Real(semiAngle)})");

                var coneBounds = RevolveBounds(e, Circle(k), seam, Circle(next));
                faces.Add(Face(e, coneBounds, cone, dHeight > 0));
            }
        }

        return Brep(e, name, faces);
    }

    private static List<int> RevolveBounds(Emitter e, int startCircle, int seam, int endCircle)
    {
        var edges = new List<(int Edge, bool Forward)>();

        if (startCircle >= 0)
        {
            edges.Add((startCircle, true));
        }

        edges.Add((seam, true));

        if (endCircle >= 0)
        {
            edges.Add((endCircle, false));
        }

        edges.Add((seam, false));

        return new List<int> { Bound(e, EdgeLoop(e, edges), true) };
    }

    private static int SeamEdge(
        Emitter e,
        Frame3D frame,
        ProfileSegment segment,
        int startVertex,
        int endVertex,
        Vec3 start,
        Vec3 end)
    {
        if (segment is ArcSegment2D arc)
        {
            var center = frame.ToWorld(arc.Center.X, 0, arc.Center.Y);
            var axis = arc.SweepAngle > 0 ? -frame.YAxis : frame.YAxis;

            return ArcEdge(e, center, axis, start, arc.Radius, startVertex, endVertex);
        }

        return LineEdge(e, startVertex, endVertex, start, end);
    }

    private static int WriteCone(Emitter e, ConeSolid solid)
    {
        var points = new List<(double X, double Y)>
        {
            (0.0, 0.0),
            (solid.BaseRadius, 0.0)
        };

        if (solid.TopRadius > AxisTolerance)
        {
            points.Add((solid.TopRadius, solid.Height));
        }

        points.Add((0.0, solid.Height));

        return WriteRevolution(e, solid.Name, Profile2D.Polygon(points), solid.WorldFrame);
    }

    private static int WriteSphere(Emitter e, SphereSolid solid)
    {
        var arc = new ArcSegment2D((0.0, 0.0), solid.Radius, -Math.PI / 2.0, Math.PI);
        var line = new LineSegment2D(arc.End, arc.Start);
        var profile = Profile2D.Create(ProfileLoop.Create(new ProfileSegment[] { arc, line }));

        return WriteRevolution(e, solid.Name, profile, solid.WorldFrame);
    }

    private static int WriteTorus(Emitter e, TorusSolid solid)
    {
        var profile = Profile2D.Create(ProfileLoop.Circle(solid.MajorRadius, 0.0, solid.MinorRadius));

        return WriteRevolution(e, solid.Name, profile, solid.WorldFrame);
    }

    private static int ArcEdge(
        Emitter e,
        Vec3 center,
        Vec3 axis,
        Vec3 startPoint,
        double radius,
        int startVertex,
        int endVertex)
    {
        var placement = PlacementV(e, center, axis, startPoint - center);
        var circle = e.Add($"CIRCLE('',{R(placement)},{Real(radius)})");

        return e.Add($"EDGE_CURVE('',{R(startVertex)},{R(endVertex)},{R(circle)},.T.)");
    }

    private static int Brep(Emitter e, string name, List<int> faces)
    {
        var shell = e.Add($"CLOSED_SHELL('',({string.Join(",", faces.Select(R))}))");
        return e.Add($"MANIFOLD_SOLID_BREP({Str(name)},{R(shell)})");
    }

    private static int Face(Emitter e, List<int> bounds, int surface)
    {
        return Face(e, bounds, surface, true);
    }

    private static int Face(Emitter e, List<int> bounds, int surface, bool sameSense)
    {
        return e.Add($"ADVANCED_FACE('',({string.Join(",", bounds.Select(R))}),{R(surface)},{(sameSense ? ".T." : ".F.")})");
    }

    private static int Bound(Emitter e, int loop, bool outer)
    {
        return outer
            ? e.Add($"FACE_OUTER_BOUND('',{R(loop)},.T.)")
            : e.Add($"FACE_BOUND('',{R(loop)},.T.)");
    }

    private static int EdgeLoop(Emitter e, IEnumerable<(int Edge, bool Forward)> edges)
    {
        var oriented = edges
            .Select(item => e.Add($"ORIENTED_EDGE('',*,*,{R(item.Edge)},{(item.Forward ? ".T." : ".F.")})"))
            .ToList();

        return e.Add($"EDGE_LOOP('',({string.Join(",", oriented.Select(R))}))");
    }

    private static int Point(Emitter e, double x, double y, double z)
    {
        return e.Add($"CARTESIAN_POINT('',({Real(x)},{Real(y)},{Real(z)}))");
    }

    private static int Point(Emitter e, Vec3 value)
    {
        return Point(e, value.X, value.Y, value.Z);
    }

    private static int Direction(Emitter e, double x, double y, double z)
    {
        return e.Add($"DIRECTION('',({Real(x)},{Real(y)},{Real(z)}))");
    }

    private static int Direction(Emitter e, Vec3 value)
    {
        return Direction(e, value.X, value.Y, value.Z);
    }

    private static int Placement(
        Emitter e,
        double x, double y, double z,
        double ax, double ay, double az,
        double rx, double ry, double rz)
    {
        var location = Point(e, x, y, z);
        var axis = Direction(e, ax, ay, az);
        var reference = Direction(e, rx, ry, rz);

        return e.Add($"AXIS2_PLACEMENT_3D('',{R(location)},{R(axis)},{R(reference)})");
    }

    private static int PlacementV(Emitter e, Vec3 location, Vec3 axis, Vec3 reference)
    {
        var z = axis.Normalized();
        var x = reference - z * reference.Dot(z);

        x = x.Length <= 1e-9 ? PerpendicularTo(z, Vec3.UnitX) : x.Normalized();

        var locationId = Point(e, location);
        var axisId = Direction(e, z);
        var referenceId = Direction(e, x);

        return e.Add($"AXIS2_PLACEMENT_3D('',{R(locationId)},{R(axisId)},{R(referenceId)})");
    }

    private static Vec3 PerpendicularTo(Vec3 axis, Vec3 hint)
    {
        var candidate = hint - axis * hint.Dot(axis);

        if (candidate.Length <= 1e-9)
        {
            candidate = Vec3.UnitY - axis * Vec3.UnitY.Dot(axis);
        }

        if (candidate.Length <= 1e-9)
        {
            candidate = Vec3.UnitZ - axis * Vec3.UnitZ.Dot(axis);
        }

        return candidate.Normalized();
    }

    private static int Plane(
        Emitter e,
        double x, double y, double z,
        double ax, double ay, double az,
        double rx, double ry, double rz)
    {
        var placement = Placement(e, x, y, z, ax, ay, az, rx, ry, rz);
        return e.Add($"PLANE('',{R(placement)})");
    }

    private static int PlaneV(Emitter e, Vec3 location, Vec3 normal, Vec3 reference)
    {
        var placement = PlacementV(e, location, normal, reference);
        return e.Add($"PLANE('',{R(placement)})");
    }

    private static int Vertex(Emitter e, double x, double y, double z)
    {
        var point = Point(e, x, y, z);
        return e.Add($"VERTEX_POINT('',{R(point)})");
    }

    private static int Vertex(Emitter e, Vec3 value)
    {
        return Vertex(e, value.X, value.Y, value.Z);
    }

    private static int LineEdge(
        Emitter e,
        int startVertex,
        int endVertex,
        (double X, double Y, double Z) start,
        (double X, double Y, double Z) end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        var origin = Point(e, start.X, start.Y, start.Z);
        var direction = Direction(e, dx / length, dy / length, dz / length);
        var vector = e.Add($"VECTOR('',{R(direction)},1.)");
        var line = e.Add($"LINE('',{R(origin)},{R(vector)})");

        return e.Add($"EDGE_CURVE('',{R(startVertex)},{R(endVertex)},{R(line)},.T.)");
    }

    private static int LineEdge(Emitter e, int startVertex, int endVertex, Vec3 start, Vec3 end)
    {
        return LineEdge(
            e,
            startVertex,
            endVertex,
            (start.X, start.Y, start.Z),
            (end.X, end.Y, end.Z));
    }

    private static string R(int id)
    {
        return "#" + id.ToString(CultureInfo.InvariantCulture);
    }

    private static string Real(double value)
    {
        if (!double.IsFinite(value))
        {
            return "0.";
        }

        var text = value.ToString("0.############", CultureInfo.InvariantCulture);

        if (text == "-0" || text == "0" || text == "-0.")
        {
            return "0.";
        }

        return text.Contains('.') ? text : text + ".";
    }

    private static string Str(string value)
    {
        var builder = new StringBuilder("'");

        foreach (var ch in value)
        {
            if (ch == '\'')
            {
                builder.Append("''");
            }
            else if (ch == '\\')
            {
                builder.Append("\\\\");
            }
            else if (ch >= 32 && ch < 127)
            {
                builder.Append(ch);
            }
            else if (ch == '\r' || ch == '\n' || ch == '\t')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append("\\X2\\").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append("\\X0\\");
            }
        }

        builder.Append('\'');
        return builder.ToString();
    }

    private sealed class Emitter
    {
        private readonly StringBuilder _data = new();
        private int _next = 1;

        public string Data => _data.ToString();

        public int Add(string body)
        {
            var id = _next++;

            _data.Append('#')
                .Append(id.ToString(CultureInfo.InvariantCulture))
                .Append('=')
                .Append(body)
                .Append(";\n");

            return id;
        }
    }
}