using System.Globalization;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaComponentReconstructionService
    {
        private const double MaxInferredThicknessMm = 100.0;

        public List<GaReconstructedComponent> Reconstruct(
            IReadOnlyList<GaProfileResult> profiles,
            IReadOnlyList<GaFlatPatternResult> flats,
            ViewSelectionResult views,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(flats);
            ArgumentNullException.ThrowIfNull(views);
            ArgumentNullException.ThrowIfNull(report);

            var list = new List<GaReconstructedComponent>();

            foreach (var profile in profiles)
            {
                if (profile.Skipped)
                {
                    continue;
                }

                var classification = profile.Classification;
                var part = classification.Part;
                var flat = flats.FirstOrDefault(f => ReferenceEquals(f.Profile, profile));

                var component = new GaReconstructedComponent
                {
                    PartName = part.Name,
                    DrawingNumber = part.DrawingNumber,
                    Category = classification.Category.ToString(),
                    PartType = classification.Type.ToString(),
                    WidthMm = profile.WidthMm,
                    HeightMm = profile.HeightMm,
                    Quantity = Math.Max(1, part.Qty),
                    HoleCount = profile.Holes.Sum(h => h.Count),
                    SlotCount = profile.SlotCount,
                    OutlineClosed = profile.OutlineClosed
                };

                if (!string.IsNullOrWhiteSpace(part.Material))
                {
                    component.Material = part.Material;
                    component.MaterialSource = part.ApprovedFields.Contains("Material")
                        ? GaValueSource.UserApproved
                        : GaValueSource.Extracted;
                }

                var related = views.ViewsByPart.TryGetValue(part.Name, out var found)
                    ? found
                    : new List<ViewSelection>();

                var primaryId = classification.View?.Node.Id;

                foreach (var view in related)
                {
                    var role = string.Equals(view.Node.Id, primaryId, StringComparison.Ordinal)
                        ? "Primary"
                        : view.IsEdgeOn
                            ? "EdgeOn"
                            : view.IsWholeView
                                ? "WholeView"
                                : "Supporting";

                    component.Views.Add(new GaViewRelation
                    {
                        NodeId = view.Node.Id,
                        Layer = view.Node.Layer,
                        Role = role,
                        WidthMm = Math.Round(view.WidthMm, 2),
                        HeightMm = Math.Round(view.HeightMm, 2),
                        Score = Math.Round(view.Score, 3)
                    });
                }

                if (component.Views.Count == 0 && classification.View is not null)
                {
                    component.Views.Add(new GaViewRelation
                    {
                        NodeId = classification.View.Node.Id,
                        Layer = classification.View.Node.Layer,
                        Role = "Primary",
                        WidthMm = Math.Round(classification.View.WidthMm, 2),
                        HeightMm = Math.Round(classification.View.HeightMm, 2),
                        Score = Math.Round(classification.View.Score, 3)
                    });
                }

                ResolveThickness(component, part, profile, related);

                if (flat is not null && flat.Resolved)
                {
                    component.Notes.Add("Flat pattern resolved, sweep " + flat.SweepDegrees.ToString("0.#", CultureInfo.InvariantCulture) + " deg.");
                    component.Notes.AddRange(flat.Assumptions);
                }

                if (!profile.Success)
                {
                    component.Notes.Add("No profile could be built: " + profile.Message);
                }

                component.Confidence = Math.Round(Confidence(component, profile), 2);
                list.Add(component);

                report.Info(
                    "RECONSTRUCTED",
                    part.Name,
                    component.Views.Count.ToString(CultureInfo.InvariantCulture) + " view(s), "
                    + component.WidthMm.ToString("0.#", CultureInfo.InvariantCulture) + " x "
                    + component.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm, "
                    + component.HoleCount.ToString(CultureInfo.InvariantCulture) + " hole(s), "
                    + component.SlotCount.ToString(CultureInfo.InvariantCulture) + " slot(s), confidence "
                    + component.Confidence.ToString("0.00", CultureInfo.InvariantCulture) + ".");
            }

            return list;
        }

        private static void ResolveThickness(
            GaReconstructedComponent component,
            CalloutPart part,
            GaProfileResult profile,
            List<ViewSelection> related)
        {
            if (part.ThicknessMm is > 0.0)
            {
                component.ThicknessMm = part.ThicknessMm;
                component.ThicknessSource = part.ApprovedFields.Contains("ThicknessMm")
                    ? GaValueSource.UserApproved
                    : GaValueSource.Extracted;
                return;
            }

            if (profile.Item?.ThicknessMm is > 0.0)
            {
                component.ThicknessMm = profile.Item.ThicknessMm;
                component.ThicknessSource = GaValueSource.Calculated;
                return;
            }

            var edgeOn = related
                .Where(v => v.IsEdgeOn)
                .Select(v => Math.Min(v.WidthMm, v.HeightMm))
                .Where(v => v > 0.0 && v <= MaxInferredThicknessMm)
                .OrderBy(v => v)
                .ToList();

            if (edgeOn.Count > 0)
            {
                component.ThicknessMm = Math.Round(edgeOn[0], 2);
                component.ThicknessSource = GaValueSource.Inferred;
                component.Notes.Add("Thickness read from an edge-on view; needs approval.");
            }
        }

        private static double Confidence(GaReconstructedComponent component, GaProfileResult profile)
        {
            var score = 0.3;

            if (profile.Success)
            {
                score += 0.2;
            }

            if (component.OutlineClosed)
            {
                score += 0.2;
            }

            if (component.ThicknessMm.HasValue && component.ThicknessSource != GaValueSource.Inferred)
            {
                score += 0.2;
            }

            if (component.Views.Count > 1)
            {
                score += 0.1;
            }

            return Math.Min(score, 1.0);
        }
    }
}
