using CadToCode.Step;

namespace CadToCode.Business.Services;

/// <summary>
/// Turns the 2D outlines of manufacturing parts into STEP solids.
/// Round parts with a centre line are revolved, other closed outlines are extruded,
/// and small gaps in the drawing lines are bridged by retrying with a larger tolerance.
/// Every file is moved so that its lowest-left corner sits at the origin; the assembly
/// uses one shared shift so the parts keep their GA positions relative to each other.
/// </summary>
internal static class ManufacturingStepBuilder
{
    private static readonly double[] ToleranceFactors = { 1.0, 10.0, 100.0 };

    internal sealed class Outcome
    {
        public bool Success { get; init; }

        public string? Error { get; init; }

        public string? Warning { get; init; }
    }

    internal sealed record AssemblyPart(
        string Name,
        string DrawingNumber,
        IReadOnlyList<DrawingEntity> Entities);

    private sealed class Analysis
    {
        public ProfileBuildResult Built { get; init; } = null!;

        public FeatureRecognitionResult Features { get; init; } = null!;
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

        Analysis? analysis;

        try
        {
            analysis = Analyse(entities);
        }
        catch (Exception ex)
        {
            return new Outcome { Error = "the outline could not be built (" + ex.Message + ")" };
        }

        if (analysis is null)
        {
            return new Outcome { Error = "no closed outline was found" };
        }

        try
        {
            var shift = ShiftOf(analysis.Built.Profiles);
            var document = new StepDocument(partName);

            AddFeatures(document, analysis, partName, thickness, shift);

            if (document.SolidCount == 0)
            {
                throw new InvalidOperationException("no outline could be turned into a solid");
            }

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

        var open = analysis.Built.OpenChains.Count;

        return new Outcome
        {
            Success = true,
            Warning = open > 0
                ? partName + ": " + open + " open line chain(s) could not be closed and were left out."
                : null
        };
    }

    public static Outcome BuildAssembly(
        IReadOnlyList<AssemblyPart> parts,
        string assemblyName,
        string drawingNumber,
        double thickness,
        string path)
    {
        if (parts.Count == 0)
        {
            return new Outcome { Error = "no parts were available for the assembly" };
        }

        var analysed = new List<(string Name, Analysis Analysis)>();
        var left = new List<string>();

        foreach (var part in parts)
        {
            try
            {
                var analysis = Analyse(part.Entities);

                if (analysis is null)
                {
                    left.Add(part.Name);
                    continue;
                }

                analysed.Add((part.Name, analysis));
            }
            catch
            {
                left.Add(part.Name);
            }
        }

        if (analysed.Count == 0)
        {
            return new Outcome { Error = "no part outline could be placed in the assembly" };
        }

        try
        {
            var shift = ShiftOf(analysed.SelectMany(a => a.Analysis.Built.Profiles).ToList());
            var document = new StepDocument(assemblyName);

            foreach (var part in analysed)
            {
                AddFeatures(document, part.Analysis, part.Name, thickness, shift);
            }

            if (document.SolidCount == 0)
            {
                throw new InvalidOperationException("no outline could be turned into a solid");
            }

            document.Save(path, new StepExportOptions
            {
                PartNumber = string.IsNullOrWhiteSpace(drawingNumber) ? null : drawingNumber,
                PartDescription = assemblyName
            });
        }
        catch (Exception ex)
        {
            return new Outcome { Error = "the assembly STEP file could not be written (" + ex.Message + ")" };
        }

        return new Outcome
        {
            Success = true,
            Warning = left.Count > 0
                ? "ASSEMBLY: " + analysed.Count + " part(s) placed, left out: " + string.Join(", ", left) + "."
                : null
        };
    }

    /// <summary>
    /// Builds the outlines, retrying with a larger gap tolerance while open chains remain,
    /// and keeps the attempt with the fewest open chains (then the most closed outlines).
    /// </summary>
    private static Analysis? Analyse(IReadOnlyList<DrawingEntity> entities)
    {
        ProfileBuildResult? best = null;
        var baseTolerance = 0.0;

        foreach (var factor in ToleranceFactors)
        {
            ProfileBuildResult attempt;

            try
            {
                if (best is null)
                {
                    attempt = ProfileBuilder.Build(entities);
                    baseTolerance = attempt.Tolerance;
                }
                else
                {
                    attempt = ProfileBuilder.Build(entities, Math.Max(baseTolerance * factor, 1e-6));
                }
            }
            catch
            {
                continue;
            }

            if (best is null
                || attempt.OpenChains.Count < best.OpenChains.Count
                || (attempt.OpenChains.Count == best.OpenChains.Count && attempt.Profiles.Count > best.Profiles.Count))
            {
                best = attempt;
            }

            if (best.OpenChains.Count == 0 && best.HasProfiles)
            {
                break;
            }
        }

        if (best is null || !best.HasProfiles)
        {
            return null;
        }

        var features = FeatureRecognizer.Recognize(best, new FeatureRecognizerOptions
        {
            DetectRevolves = true,
            DetectPipes = false
        });

        return new Analysis { Built = best, Features = features };
    }

    private static (double X, double Y) ShiftOf(IReadOnlyList<Profile2D> profiles)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;

        foreach (var profile in profiles)
        {
            var b = profile.Bounds;
            minX = Math.Min(minX, b.MinX);
            minY = Math.Min(minY, b.MinY);
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || minX == double.MaxValue || minY == double.MaxValue)
        {
            return (0, 0);
        }

        return (-minX, -minY);
    }

    private static void AddFeatures(
        StepDocument document,
        Analysis analysis,
        string partName,
        double thickness,
        (double X, double Y) shift)
    {
        var placement = Frame3D.World.Translate(shift.X, shift.Y, 0);
        var features = analysis.Features.Features
            .Where(f => f.Profile is not null)
            .OrderByDescending(f => f.Profile!.Area)
            .ToList();

        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i];
            var name = features.Count == 1 ? partName : partName + " " + (i + 1);

            try
            {
                if (feature.Kind == RecognizedFeatureKind.Revolve)
                {
                    var frame = Frame3D.FromAxes(
                        new Vec3(feature.AxisOrigin.X + shift.X, feature.AxisOrigin.Y + shift.Y, 0),
                        new Vec3(feature.RadialDirection.X, feature.RadialDirection.Y, 0),
                        new Vec3(feature.AxisDirection.X, feature.AxisDirection.Y, 0));

                    document.Add.Revolve(name, feature.Profile!, frame);
                }
                else
                {
                    document.Add.Extrude(name, feature.Profile!, thickness, placement);
                }
            }
            catch
            {
                // one outline that cannot be turned into a solid must not stop the whole file
            }
        }
    }
}
