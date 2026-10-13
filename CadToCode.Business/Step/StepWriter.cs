using System.Globalization;
using System.Text;

namespace CadToCode.Step;

internal static class StepWriter
{
    public static string Write(StepDocument document)
    {
        var emitter = new Emitter();
        var name = document.Name;

        var appContext = emitter.Add("APPLICATION_CONTEXT('core data for automotive mechanical design processes')");
        emitter.Add($"APPLICATION_PROTOCOL_DEFINITION('international standard','automotive_design',2000,{R(appContext)})");
        var productContext = emitter.Add($"PRODUCT_CONTEXT('',{R(appContext)},'mechanical')");
        var product = emitter.Add($"PRODUCT({Str(name)},{Str(name)},'',({R(productContext)}))");
        emitter.Add($"PRODUCT_RELATED_PRODUCT_CATEGORY('part',$,({R(product)}))");
        var formation = emitter.Add($"PRODUCT_DEFINITION_FORMATION('','',{R(product)})");
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
            }
        }

        var representation = emitter.Add(
            $"ADVANCED_BREP_SHAPE_REPRESENTATION({Str(name)},({string.Join(",", items.Select(R))}),{R(context)})");
        emitter.Add($"SHAPE_DEFINITION_REPRESENTATION({R(shape)},{R(representation)})");

        var fileName = name + ".step";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append("ISO-10303-21;\n");
        builder.Append("HEADER;\n");
        builder.Append("FILE_DESCRIPTION(('CadToCode STEP export'),'2;1');\n");
        builder.Append("FILE_NAME(").Append(Str(fileName)).Append(',').Append(Str(timestamp))
            .Append(",('CadToCode'),('CadToCode'),'CadToCode STEP writer','CadToCode','');\n");
        builder.Append("FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));\n");
        builder.Append("ENDSEC;\n");
        builder.Append("DATA;\n");
        builder.Append(emitter.Data);
        builder.Append("ENDSEC;\n");
        builder.Append("END-ISO-10303-21;\n");

        return builder.ToString();
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

    private static int Brep(Emitter e, string name, List<int> faces)
    {
        var shell = e.Add($"CLOSED_SHELL('',({string.Join(",", faces.Select(R))}))");
        return e.Add($"MANIFOLD_SOLID_BREP({Str(name)},{R(shell)})");
    }

    private static int Face(Emitter e, List<int> bounds, int surface)
    {
        return e.Add($"ADVANCED_FACE('',({string.Join(",", bounds.Select(R))}),{R(surface)},.T.)");
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

    private static int Direction(Emitter e, double x, double y, double z)
    {
        return e.Add($"DIRECTION('',({Real(x)},{Real(y)},{Real(z)}))");
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

    private static int Plane(
        Emitter e,
        double x, double y, double z,
        double ax, double ay, double az,
        double rx, double ry, double rz)
    {
        var placement = Placement(e, x, y, z, ax, ay, az, rx, ry, rz);
        return e.Add($"PLANE('',{R(placement)})");
    }

    private static int Vertex(Emitter e, double x, double y, double z)
    {
        var point = Point(e, x, y, z);
        return e.Add($"VERTEX_POINT('',{R(point)})");
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