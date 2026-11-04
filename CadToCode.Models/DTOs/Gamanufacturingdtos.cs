namespace CadToCode.Models.Dtos;

public enum GaValueSource
{
    Extracted = 0,
    Calculated = 1,
    Inferred = 2,
    Default = 3,
    UserApproved = 4
}

public sealed class GaSourcedValue
{
    public string Field { get; set; } = string.Empty;

    public string? Value { get; set; }

    public GaValueSource Source { get; set; }

    public string? Note { get; set; }
}

public sealed class GaViewRelation
{
    public string NodeId { get; set; } = string.Empty;

    public string? Layer { get; set; }

    /// <summary>Primary, EdgeOn, WholeView or Supporting.</summary>
    public string Role { get; set; } = "Supporting";

    public double WidthMm { get; set; }

    public double HeightMm { get; set; }

    public double Score { get; set; }
}

public sealed class GaReconstructedComponent
{
    public string PartName { get; set; } = string.Empty;

    public string? DrawingNumber { get; set; }

    public string Category { get; set; } = string.Empty;

    public string PartType { get; set; } = string.Empty;

    public List<GaViewRelation> Views { get; set; } = new();

    public double WidthMm { get; set; }

    public double HeightMm { get; set; }

    public double? ThicknessMm { get; set; }

    public GaValueSource? ThicknessSource { get; set; }

    public string? Material { get; set; }

    public GaValueSource? MaterialSource { get; set; }

    public int Quantity { get; set; } = 1;

    public int HoleCount { get; set; }

    public int SlotCount { get; set; }

    public bool OutlineClosed { get; set; }

    public double Confidence { get; set; }

    public List<string> Notes { get; set; } = new();
}

public enum GaReviewStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public sealed class GaReviewItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

    public string PartName { get; set; } = string.Empty;

    /// <summary>ThicknessMm, Material, Quantity or GeneralTolerance.</summary>
    public string Field { get; set; } = string.Empty;

    public string? CurrentValue { get; set; }

    public string? SuggestedValue { get; set; }

    public string? ApprovedValue { get; set; }

    public GaValueSource Source { get; set; }

    /// <summary>A required item blocks final drawing generation until it is approved.</summary>
    public bool Required { get; set; }

    public GaReviewStatus Status { get; set; } = GaReviewStatus.Pending;

    public string Reason { get; set; } = string.Empty;
}

public sealed class GaReviewState
{
    public int ProjectId { get; set; }

    public List<GaReviewItem> Items { get; set; } = new();

    /// <summary>Required items still waiting for a decision. A rejected required item leaves its part out of the package.</summary>
    public int PendingRequired => Items.Count(i => i.Required && i.Status == GaReviewStatus.Pending);

    public bool AllRequiredResolved => PendingRequired == 0;
}
