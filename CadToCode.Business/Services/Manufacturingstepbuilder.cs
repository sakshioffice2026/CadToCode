using CadToCode.Step;

namespace CadToCode.Business.Services;

/// <summary>
/// Turns the 2D outline of one manufacturing part into an extruded STEP solid.
/// </summary>
internal static class ManufacturingStepBuilder
{
    internal sealed class Outcome
    {
        public bool Success { get; init; }

        public string? Error { get; init; }

        public string? Warning { get; init; }
    }

    public static Outcome Build(
        IReadOnlyList<DrawingEntity> entities,
        string partName,
        string drawingNumber,
        double thickness,
        string path)
    {
        if (entities.Count == 0)
        {
            return new Outcome { Error = "no usable line, arc, circle or polyline geometry" };
        }

        ProfileBuildResult built;

        try
        {
            built = ProfileBuilder.Build(entities);
        }
        catch (Exception ex)
        {
            return new Outcome { Error = "the outline could not be built (" + ex.Message + ")" };
        }

        if (!built.HasProfiles)
        {
            return new Outcome { Error = "no closed outline was found" };
        }

        var profile = built.Profiles.OrderByDescending(p => p.Area).First();

        string? warning = null;

        if (built.Profiles.Count > 1)
        {
            warning = partName + ": " + built.Profiles.Count + " separate outlines were found, the largest one was extruded.";
        }

        try
        {
            var document = new StepDocument(partName);
            document.Add.Extrude(partName, profile, thickness);

            document.Save(path, new StepExportOptions
            {
                PartNumber = string.IsNullOrWhiteSpace(drawingNumber) ? null : drawingNumber,
                PartDescription = partName
            });
        }
        catch (Exception ex)
        {
            return new Outcome { Error = "the STEP file could not be written (" + ex.Message + ")" };
        }

        return new Outcome { Success = true, Warning = warning };
    }
}