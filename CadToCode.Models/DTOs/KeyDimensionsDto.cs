namespace CadToCode.Models.Dtos;

public sealed class KeyDimensionsDto
{
    public double? VesselDiameter { get; set; }

    public double? VesselHeight { get; set; }

    public double? InletPipeDiameter { get; set; }

    public double? OutletPipeDiameter { get; set; }

    public double? TankLength { get; set; }

    public bool HasAny =>
        VesselDiameter is > 0
        || VesselHeight is > 0
        || InletPipeDiameter is > 0
        || OutletPipeDiameter is > 0
        || TankLength is > 0;
}