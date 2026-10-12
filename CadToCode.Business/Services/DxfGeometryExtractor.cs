using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using netDxf;
using netDxf.Entities;
using netDxf.Header;
using netDxf.Units;
using System.Xml.Linq;

namespace CadToCode.Business.Services;

public class DxfGeometryExtractor : IGeometryExtractor
{
    public bool CanHandle(SourceFileType fileType) => fileType == SourceFileType.Dxf;

    public Task<ExtractedGeometry> ExtractAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Extract(fileStream, cancellationToken), cancellationToken);
    }

    private static ExtractedGeometry Extract(Stream fileStream, CancellationToken cancellationToken)
    {
        var document = DxfDocument.Load(fileStream)
            ?? throw new InvalidOperationException("The DXF file could not be read.");

        var result = new ExtractedGeometry
        {
            DetectedUnit = MapUnit(document.DrawingVariables.InsUnits)
        };

        foreach (var line in document.Entities.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Lines.Add(new GeometryLine
            {
                Start = new GeometryPoint(line.StartPoint.X, line.StartPoint.Y),
                End = new GeometryPoint(line.EndPoint.X, line.EndPoint.Y),
                Layer = line.Layer?.Name,
                LineType = line.Linetype?.Name
            });
        }

        foreach (var polyline in document.Entities.Polylines2D)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddPolylineSegments(result, polyline);
        }

        foreach (var arc in document.Entities.Arcs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Arcs.Add(new GeometryArc
            {
                Center = new GeometryPoint(arc.Center.X, arc.Center.Y),
                Radius = arc.Radius,
                StartAngle = arc.StartAngle,
                EndAngle = arc.EndAngle,
                Layer = arc.Layer?.Name,
                LineType = arc.Linetype?.Name
            });
        }

        foreach (var circle in document.Entities.Circles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Circles.Add(new GeometryCircle
            {
                Center = new GeometryPoint(circle.Center.X, circle.Center.Y),
                Radius = circle.Radius,
                Layer = circle.Layer?.Name,
                LineType = circle.Linetype?.Name
            });
        }

        foreach (var hatch in document.Entities.Hatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Hatches.Add(ToGeometryHatch(hatch));
        }

        foreach (var dimension in document.Entities.Dimensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Dimensions.Add(new GeometryDimension
            {
                Text = string.IsNullOrWhiteSpace(dimension.UserText) ? null : dimension.UserText,
                Value = dimension.Measurement,
                Layer = dimension.Layer?.Name
            });
        }

        return result;
    }

    private static void AddPolylineSegments(ExtractedGeometry result, Polyline2D polyline)
    {
        var vertexes = polyline.Vertexes;
        if (vertexes.Count < 2)
        {
            return;
        }

        var count = polyline.IsClosed ? vertexes.Count : vertexes.Count - 1;

        for (var i = 0; i < count; i++)
        {
            var a = vertexes[i].Position;
            var b = vertexes[(i + 1) % vertexes.Count].Position;

            result.Lines.Add(new GeometryLine
            {
                Start = new GeometryPoint(a.X, a.Y),
                End = new GeometryPoint(b.X, b.Y),
                Layer = polyline.Layer?.Name,
                LineType = polyline.Linetype?.Name
            });
        }
    }

    private static GeometryHatch ToGeometryHatch(Hatch hatch)
    {
        var geometryHatch = new GeometryHatch
        {
            PatternName = hatch.Pattern?.Name,
            Layer = hatch.Layer?.Name,
            LineType = hatch.Linetype?.Name
        };

        foreach (var path in hatch.BoundaryPaths)
        {
            var points = new List<GeometryPoint>();

            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case HatchBoundaryPath.Line line:
                        points.Add(new GeometryPoint(line.Start.X, line.Start.Y));
                        points.Add(new GeometryPoint(line.End.X, line.End.Y));
                        break;

                    case HatchBoundaryPath.Polyline polyline:
                        foreach (var vertex in polyline.Vertexes)
                        {
                            points.Add(new GeometryPoint(vertex.X, vertex.Y));
                        }
                        break;

                    case HatchBoundaryPath.Arc arc:
                        points.Add(new GeometryPoint(arc.Center.X, arc.Center.Y));
                        break;
                }
            }

            if (points.Count > 0)
            {
                geometryHatch.BoundaryPaths.Add(points);
            }
        }

        return geometryHatch;
    }

    private static DrawingUnit MapUnit(DrawingUnits unit)
    {
        return unit switch
        {
            DrawingUnits.Centimeters => DrawingUnit.Centimeter,
            DrawingUnits.Meters => DrawingUnit.Meter,
            DrawingUnits.Inches => DrawingUnit.Inch,
            DrawingUnits.Feet => DrawingUnit.Foot,
            _ => DrawingUnit.Millimeter
        };
    }
}