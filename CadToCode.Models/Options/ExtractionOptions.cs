namespace CadToCode.Models.Options;

public class ExtractionOptions
{
    public const string SectionName = "ExtractionOptions";

    public double DimensionTolerancePercent { get; set; } = 2.0;
    public double AngleToleranceDegrees { get; set; } = 1.0;
    public double ParallelLineToleranceDegrees { get; set; } = 1.0;
    public double OcrConfidenceThreshold { get; set; } = 0.60;
    public string TessdataPath { get; set; } = "tessdata";
}