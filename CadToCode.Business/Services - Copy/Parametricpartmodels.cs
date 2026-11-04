using System.Globalization;

namespace CadToCode.Business.Services;

/// <summary>Kind of part the parametric generator can synthesize from BOM parameters.</summary>
public enum BomPartKind
{
    Unknown = 0,
    Flange = 1,
    RectangularPlate = 2,
    RoundBar = 3,
    RolledRing = 4,
    Hub = 5,
    ConeFlatPattern = 6,
    ScrollFlatPattern = 7,
    TransitionFlatPattern = 8,
    ProfilePlate = 9
}

public enum ProfileEntityKind
{
    Line = 0,
    Arc = 1,
    Circle = 2
}

/// <summary>One real outline entity taken from the GA drawing. All values are in mm.</summary>
public sealed record ProfileEntity(
    ProfileEntityKind Kind,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double Cx,
    double Cy,
    double Radius,
    double StartAngle,
    double EndAngle);


/// <summary>
/// One Bill of Materials row. All sizes are in mm. Values that the BOM does not state stay null so the
/// generator can report exactly which parameter is missing instead of dropping the part silently.
/// </summary>
public sealed class PartBomItem
{
    public int ItemNo { get; set; }

    public string Name { get; set; } = string.Empty;

    public BomPartKind Kind { get; set; } = BomPartKind.Unknown;

    public string? Material { get; set; }

    public int Qty { get; set; } = 1;

    public double? OuterDiameterMm { get; set; }

    public double? InnerDiameterMm { get; set; }

    public double? ThicknessMm { get; set; }

    public double? LengthMm { get; set; }

    public double? WidthMm { get; set; }

    public double? HeightMm { get; set; }

    public double? SmallDiameterMm { get; set; }

    public double? LargeDiameterMm { get; set; }

    public double? ChamferMm { get; set; }

    public int? HoleCount { get; set; }

    public double? HoleDiameterMm { get; set; }

    public double? HolePcdMm { get; set; }

    public double? BendRadiusMm { get; set; }


    /// <summary>Finish weight in kg for one piece, calculated from the true outline. Null when not calculated.</summary>
    public double? FinishWeightKg { get; set; }

    /// <summary>Real outline geometry for ProfilePlate parts, in mm. Null for parametric parts.</summary>
    public IReadOnlyList<ProfileEntity>? Profile { get; set; }
    public string? Remarks { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "ITEM " + ItemNo.ToString(CultureInfo.InvariantCulture) : Name.Trim();
}

public enum GenerationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>One processing message that must reach the UI or API. Nothing is dropped without one of these.</summary>
public sealed record GenerationIssue(GenerationSeverity Severity, string Code, string PartName, string Message);

/// <summary>Collects every warning, skipped part and error raised while sheets are generated.</summary>
public sealed class GenerationReport
{
    private readonly List<GenerationIssue> _issues = new();

    public IReadOnlyList<GenerationIssue> Issues => _issues;

    public int GeneratedCount { get; private set; }

    public int SkippedCount => _issues.Count(i => i.Severity == GenerationSeverity.Error);

    public bool HasWarnings => _issues.Any(i => i.Severity != GenerationSeverity.Info);

    public void MarkGenerated() => GeneratedCount++;

    public void Info(string code, string partName, string message) => Add(GenerationSeverity.Info, code, partName, message);

    public void Warn(string code, string partName, string message) => Add(GenerationSeverity.Warning, code, partName, message);

    public void Error(string code, string partName, string message) => Add(GenerationSeverity.Error, code, partName, message);

    public void Add(GenerationSeverity severity, string code, string partName, string message)
    {
        _issues.Add(new GenerationIssue(severity, code, string.IsNullOrWhiteSpace(partName) ? "PART" : partName, message));
    }

    public List<string> ToMessages()
    {
        return _issues
            .OrderByDescending(i => i.Severity)
            .Select(i => "[" + i.Severity.ToString().ToUpperInvariant() + "] " + i.PartName + " (" + i.Code + "): " + i.Message)
            .ToList();
    }
}