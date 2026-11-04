using System.Globalization;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaEngineeringInferenceService
    {
        public const string FieldThickness = "ThicknessMm";
        public const string FieldMaterial = "Material";
        public const string FieldQuantity = "Quantity";
        public const string FieldTolerance = "GeneralTolerance";

        private const string FallbackMaterial = "MS";

        public List<GaReviewItem> Infer(
            IReadOnlyList<GaReconstructedComponent> components,
            string? defaultMaterial,
            double? defaultThicknessMm,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(components);
            ArgumentNullException.ThrowIfNull(report);

            var items = new List<GaReviewItem>();

            foreach (var component in components)
            {
                if (string.Equals(component.Category, GaManufacturingCategory.BoughtOut.ToString(), StringComparison.Ordinal))
                {
                    continue;
                }

                InferThickness(component, defaultThicknessMm, items);
                InferMaterial(component, defaultMaterial, items);
                InferQuantity(component, items);
                InferTolerance(component, items);
            }

            foreach (var item in items.Where(i => i.Required))
            {
                report.Warn(
                    "REVIEW_REQUIRED",
                    item.PartName,
                    item.Field + " needs approval"
                    + (string.IsNullOrWhiteSpace(item.SuggestedValue) ? " (no suggestion)." : " (suggested " + item.SuggestedValue + ")."));
            }

            return items;
        }

        private static void InferThickness(GaReconstructedComponent component, double? defaultThicknessMm, List<GaReviewItem> items)
        {
            if (component.ThicknessMm.HasValue
                && component.ThicknessSource is GaValueSource.Extracted or GaValueSource.UserApproved or GaValueSource.Calculated)
            {
                return;
            }

            if (component.ThicknessMm.HasValue)
            {
                items.Add(new GaReviewItem
                {
                    PartName = component.PartName,
                    Field = FieldThickness,
                    CurrentValue = null,
                    SuggestedValue = Format(component.ThicknessMm.Value),
                    Source = GaValueSource.Inferred,
                    Required = true,
                    Reason = "Thickness is not in the callout. It was read from an edge-on view."
                });
                return;
            }

            items.Add(new GaReviewItem
            {
                PartName = component.PartName,
                Field = FieldThickness,
                CurrentValue = null,
                SuggestedValue = defaultThicknessMm is > 0.0 ? Format(defaultThicknessMm.Value) : null,
                Source = defaultThicknessMm is > 0.0 ? GaValueSource.Default : GaValueSource.Inferred,
                Required = true,
                Reason = defaultThicknessMm is > 0.0
                    ? "Thickness is not in the callout or any view. The request default is suggested."
                    : "Thickness is not in the callout or any view. Enter a value."
            });
        }

        private static void InferMaterial(GaReconstructedComponent component, string? defaultMaterial, List<GaReviewItem> items)
        {
            if (!string.IsNullOrWhiteSpace(component.Material))
            {
                return;
            }

            items.Add(new GaReviewItem
            {
                PartName = component.PartName,
                Field = FieldMaterial,
                CurrentValue = null,
                SuggestedValue = string.IsNullOrWhiteSpace(defaultMaterial) ? FallbackMaterial : defaultMaterial.Trim(),
                Source = GaValueSource.Default,
                Required = false,
                Reason = "The callout gives no material."
            });
        }

        private static void InferQuantity(GaReconstructedComponent component, List<GaReviewItem> items)
        {
            if (component.Quantity > 0)
            {
                return;
            }

            items.Add(new GaReviewItem
            {
                PartName = component.PartName,
                Field = FieldQuantity,
                CurrentValue = component.Quantity.ToString(CultureInfo.InvariantCulture),
                SuggestedValue = "1",
                Source = GaValueSource.Inferred,
                Required = false,
                Reason = "Quantity is zero or missing."
            });
        }

        private static void InferTolerance(GaReconstructedComponent component, List<GaReviewItem> items)
        {
            var category = Enum.TryParse<GaManufacturingCategory>(component.Category, out var parsed)
                ? parsed
                : GaManufacturingCategory.Unknown;

            items.Add(new GaReviewItem
            {
                PartName = component.PartName,
                Field = FieldTolerance,
                CurrentValue = null,
                SuggestedValue = GaDrawingTemplates.Get(category).DefaultTolerance,
                Source = GaValueSource.Inferred,
                Required = false,
                Reason = "No tolerance is stated in the GA. The category default is suggested."
            });
        }

        private static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
