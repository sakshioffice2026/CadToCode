using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace CadToCode.Business.Services;

/// <summary>
/// Writes real DXF DIMENSION entities (linear, rotated) instead of loose lines and text.
/// The text is always the real part size, so scaled views still read true values.
/// </summary>
public static class DxfDimensionWriter
{
    /// <summary>Sets the drawing-wide dimension text height and arrow size. Call once per DXF.</summary>
    public static void Configure(DxfFile dxf, double textHeight = 3.5, double arrowSize = 3.0)
    {
        ArgumentNullException.ThrowIfNull(dxf);
        dxf.Header.DimensionTextHeight = textHeight;
        dxf.Header.DimensionArrowSize = arrowSize;
        dxf.Header.DimensionExtensionLineExtension = 2.0;
        dxf.Header.DimensionLineGap = 1.5;
    }

    /// <summary>Horizontal dimension between x positions xa and xb, dimension line at yDim, measured from features at yFeature.</summary>
    public static void Horizontal(DxfFile dxf, double xa, double xb, double yDim, double yFeature, string text, string layer)
    {
        ArgumentNullException.ThrowIfNull(dxf);

        var dimension = new DxfRotatedDimension
        {
            DefinitionPoint1 = new DxfPoint(xa, yDim, 0),
            DefinitionPoint2 = new DxfPoint(xa, yFeature, 0),
            DefinitionPoint3 = new DxfPoint(xb, yFeature, 0),
            TextMidPoint = new DxfPoint((xa + xb) / 2.0, yDim, 0),
            RotationAngle = 0.0,
            Text = text,
            Layer = layer
        };

        dxf.Entities.Add(dimension);
    }

    /// <summary>Vertical dimension between y positions ya and yb, dimension line at xDim, measured from features at xFeature.</summary>
    public static void Vertical(DxfFile dxf, double ya, double yb, double xDim, double xFeature, string text, string layer)
    {
        ArgumentNullException.ThrowIfNull(dxf);

        var dimension = new DxfRotatedDimension
        {
            DefinitionPoint1 = new DxfPoint(xDim, ya, 0),
            DefinitionPoint2 = new DxfPoint(xFeature, ya, 0),
            DefinitionPoint3 = new DxfPoint(xFeature, yb, 0),
            TextMidPoint = new DxfPoint(xDim, (ya + yb) / 2.0, 0),
            RotationAngle = 90.0,
            Text = text,
            Layer = layer
        };

        dxf.Entities.Add(dimension);
    }
}
