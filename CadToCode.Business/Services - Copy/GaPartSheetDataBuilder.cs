using System.Globalization;
using CadToCode.Core.Rules;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaSheetData
    {
        public int ItemNo { get; init; }

        public GaManufacturingCategory Category { get; init; }

        public List<GaSourcedValue> Sources { get; } = new();

        public string? Tolerance { get; set; }

        public string Name { get; init; } = string.Empty;

        public GaPartType Type { get; init; }

        public PartBomItem Item { get; init; } = new();

        public SheetFrameRequest Frame { get; init; } = new();

        public string DrawingNo { get; init; } = string.Empty;

        public string Material { get; init; } = string.Empty;

        public double? ThicknessMm { get; init; }

        public int Qty { get; init; }

        public string RmSize { get; set; } = string.Empty;

        public double? RmWeightKg { get; set; }

        public double? FinishWeightKg { get; set; }

        public double? CalloutWeightKg { get; init; }

        public double? DifferencePercent { get; set; }

        public bool WeightMatchesCallout { get; set; }

        public bool HasWeights => FinishWeightKg.HasValue;

        public List<string> Notes { get; } = new();
    }

    public sealed class GaPartSheetDataBuilder
    {
        private const double PlateAllowanceMm = 5.0;
        private const double DiscAllowanceMm = 5.0;
        private const double ConeAllowanceMm = 10.0;
        private const double ScrollAllowanceMm = 10.0;
        private const double DefaultSweepDegrees = 360.0;
        private const string DefaultMaterial = "MS";
        private const int TransitionStepsPerQuarter = 24;
        private const double SectorStepDegrees = 0.25;

        public IReadOnlyList<GaSheetData> Build(
            ManufacturingRules rules,
            IReadOnlyList<GaProfileResult> profiles,
            IReadOnlyList<GaFlatPatternResult> flats,
            string? projectCode,
            string? defaultMaterial,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(flats);
            ArgumentNullException.ThrowIfNull(report);

            var list = new List<GaSheetData>();
            var number = 0;

            foreach (var profile in profiles)
            {
                if (profile.Skipped
                    || profile.Classification.Family == GaPartFamily.BoughtOut
                    || profile.Classification.Family == GaPartFamily.RolledSection
                    || profile.Classification.Family == GaPartFamily.Unknown)
                {
                    continue;
                }

                PartBomItem? item;
                GaFlatPatternResult? flat = null;

                if (profile.Classification.Family == GaPartFamily.Developed)
                {
                    flat = flats.FirstOrDefault(f => ReferenceEquals(f.Profile, profile));
                    item = flat?.Item;
                }
                else
                {
                    item = profile.Item;
                }

                if (item is null)
                {
                    continue;
                }

                number++;
                list.Add(BuildOne(rules, profile, flat, item, number, projectCode, defaultMaterial, report));
            }

            return list;
        }

        private static GaSheetData BuildOne(
            ManufacturingRules rules,
            GaProfileResult profile,
            GaFlatPatternResult? flat,
            PartBomItem item,
            int number,
            string? projectCode,
            string? defaultMaterial,
            GenerationReport report)
        {
            var part = profile.Classification.Part;
            var name = part.Name.ToUpperInvariant();

            item.ItemNo = number;
            item.Name = name;
            item.Qty = Math.Max(1, part.Qty);

            var material = ResolveMaterial(item, part.Material, defaultMaterial, report, name);
            item.Material = material;

            if (part.ThicknessMm.HasValue && !item.ThicknessMm.HasValue)
            {
                item.ThicknessMm = part.ThicknessMm;
            }

            var job = string.IsNullOrWhiteSpace(projectCode) ? string.Empty : projectCode.Trim();
            var itemText = number.ToString("00", CultureInfo.InvariantCulture);

            var data = new GaSheetData
            {
                ItemNo = number,
                Name = name,
                Type = profile.Classification.Type,
                Category = profile.Classification.Category,
                Item = item,
                Material = material,
                ThicknessMm = item.ThicknessMm,
                Qty = item.Qty,
                CalloutWeightKg = part.WeightKg,
                DrawingNo = job.Length > 0 ? job + "-" + itemText : itemText,
                Frame = new SheetFrameRequest
                {
                    JobNo = job,
                    DrawingNo = job.Length > 0 ? job + "-" + itemText : itemText,
                    Title = name,
                    Date = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                }
            };

            data.Sources.Add(new GaSourcedValue
            {
                Field = "Material",
                Value = material,
                Source = SourceOf(part, "Material", !string.IsNullOrWhiteSpace(part.Material), GaValueSource.Default)
            });

            data.Sources.Add(new GaSourcedValue
            {
                Field = "Quantity",
                Value = item.Qty.ToString(CultureInfo.InvariantCulture),
                Source = SourceOf(part, "Quantity", part.QtyText is not null, GaValueSource.Default)
            });

            if (item.ThicknessMm.HasValue)
            {
                data.Sources.Add(new GaSourcedValue
                {
                    Field = "ThicknessMm",
                    Value = Fmt(item.ThicknessMm),
                    Source = SourceOf(part, "ThicknessMm", part.ThicknessMm.HasValue, GaValueSource.Calculated)
                });
            }

            var grade = material.Trim().ToUpperInvariant();
            if (grade != "MS" && !grade.StartsWith("IS 2062", StringComparison.Ordinal) && !grade.StartsWith("E250", StringComparison.Ordinal))
            {
                data.Notes.Add("Weights use the rules density of " + Fmt(rules.Weight.DensityKgPerM3) + " kg/m3. Material " + material + " may differ.");
            }

            if (item.ThicknessMm is not > 0.0)
            {
                data.Notes.Add("No thickness. RM and finish weight were not calculated.");
                report.Error("SHEET_NO_THICKNESS", name, "RM and finish weight need a thickness.");
                return data;
            }

            try
            {
                if (item.Kind == BomPartKind.Hub)
                {
                    item.LengthMm = null;
                }

                Calculate(rules, profile, flat, item, data);
            }
            catch (ArgumentException ex)
            {
                data.Notes.Add("Weight calculation rejected: " + ex.Message);
                report.Error("SHEET_WEIGHT_REJECTED", name, ex.Message);
                return data;
            }

            Compare(rules, data, report);

            if (data.FinishWeightKg.HasValue)
            {
                data.Sources.Add(new GaSourcedValue { Field = "FinishWeightKg", Value = FmtKg(data.FinishWeightKg), Source = GaValueSource.Calculated });
            }

            if (data.RmWeightKg.HasValue)
            {
                data.Sources.Add(new GaSourcedValue { Field = "RmWeightKg", Value = FmtKg(data.RmWeightKg), Source = GaValueSource.Calculated });
            }

            if (data.RmSize.Length > 0)
            {
                data.Sources.Add(new GaSourcedValue { Field = "RmSize", Value = data.RmSize, Source = GaValueSource.Calculated });
            }

            report.Info(
                "SHEET_DATA",
                name,
                "ITEM " + itemText + ", MAT " + material + ", THK " + Fmt(item.ThicknessMm) + ", QTY " + item.Qty.ToString(CultureInfo.InvariantCulture)
                + ", RM " + (data.RmSize.Length > 0 ? data.RmSize : "-")
                + ", RM WT " + FmtKg(data.RmWeightKg) + ", FINISH WT " + FmtKg(data.FinishWeightKg) + ".");

            foreach (var note in data.Notes)
            {
                report.Warn("SHEET_NOTE", name, note);
            }

            return data;
        }

        private static void Calculate(
            ManufacturingRules rules,
            GaProfileResult profile,
            GaFlatPatternResult? flat,
            PartBomItem item,
            GaSheetData data)
        {
            var thk = item.ThicknessMm!.Value;

            switch (item.Kind)
            {
                case BomPartKind.Flange:
                    CalculateFlange(rules, item, thk, data);
                    break;

                case BomPartKind.Hub:
                    CalculateHub(rules, item, thk, data);
                    break;

                case BomPartKind.RectangularPlate:
                    CalculatePlate(rules, item, thk, data);
                    break;

                case BomPartKind.ProfilePlate:
                    CalculateProfile(rules, profile, item, thk, data);
                    break;

                case BomPartKind.ConeFlatPattern:
                    CalculateCone(rules, item, thk, data);
                    break;

                case BomPartKind.ScrollFlatPattern:
                    CalculateScroll(rules, item, thk, flat?.SweepDegrees ?? DefaultSweepDegrees, data);
                    break;

                case BomPartKind.TransitionFlatPattern:
                    CalculateTransition(rules, item, thk, data);
                    break;

                default:
                    data.Notes.Add("No weight formula for part kind " + item.Kind.ToString() + ".");
                    break;
            }

            if (data.FinishWeightKg.HasValue)
            {
                data.FinishWeightKg = Math.Round(data.FinishWeightKg.Value, 2);
            }

            if (data.RmWeightKg.HasValue)
            {
                data.RmWeightKg = Math.Round(data.RmWeightKg.Value, 2);
            }
        }

        private static void CalculateFlange(ManufacturingRules rules, PartBomItem item, double thk, GaSheetData data)
        {
            var od = item.OuterDiameterMm ?? 0.0;
            if (od <= 0.0)
            {
                data.Notes.Add("No outer diameter. Weights were not calculated.");
                return;
            }

            var id = item.InnerDiameterMm ?? 0.0;

            var finish = id > 0.0 ? rules.RingWeightKg(od, id, thk) : rules.DiscWeightKg(od, thk);

            if ((item.HoleCount ?? 0) > 0 && (item.HoleDiameterMm ?? 0.0) > 0.0)
            {
                var holeArea = item.HoleCount!.Value * Math.PI / 4.0 * item.HoleDiameterMm!.Value * item.HoleDiameterMm.Value;
                finish -= rules.PlateWeightKg(holeArea, thk);
            }

            var rawDiameter = od + DiscAllowanceMm;

            data.FinishWeightKg = Math.Max(finish, 0.0);
            data.RmWeightKg = rules.DiscWeightKg(rawDiameter, thk);
            data.RmSize = "%%C" + Fmt(rawDiameter) + " X " + Fmt(thk);
        }

        private static void CalculateHub(ManufacturingRules rules, PartBomItem item, double thk, GaSheetData data)
        {
            var plate = item.OuterDiameterMm ?? 0.0;
            var bossDiameter = item.SmallDiameterMm ?? 0.0;
            var bossLength = item.LengthMm ?? 0.0;

            if (plate <= 0.0 || bossDiameter <= 0.0 || bossLength <= 0.0)
            {
                data.Notes.Add("Hub boss diameter and boss length are not in the GA. Weights were not calculated.");
                return;
            }

            var bore = item.InnerDiameterMm ?? 0.0;

            var platePart = bore > 0.0 ? rules.RingWeightKg(plate, bore, thk) : rules.DiscWeightKg(plate, thk);
            var bossPart = bore > 0.0 ? rules.RingWeightKg(bossDiameter, bore, bossLength) : rules.DiscWeightKg(bossDiameter, bossLength);
            var finish = platePart + bossPart;

            if ((item.HoleCount ?? 0) > 0 && (item.HoleDiameterMm ?? 0.0) > 0.0)
            {
                var holeArea = item.HoleCount!.Value * Math.PI / 4.0 * item.HoleDiameterMm!.Value * item.HoleDiameterMm.Value;
                finish -= rules.PlateWeightKg(holeArea, thk);
            }

            var rawDiameter = plate + DiscAllowanceMm;
            var rawLength = thk + bossLength + DiscAllowanceMm;

            data.FinishWeightKg = Math.Max(finish, 0.0);
            data.RmWeightKg = rules.DiscWeightKg(rawDiameter, rawLength);
            data.RmSize = "%%C" + Fmt(rawDiameter) + " X " + Fmt(rawLength) + " LG";
        }

        private static void CalculatePlate(ManufacturingRules rules, PartBomItem item, double thk, GaSheetData data)
        {
            var length = item.LengthMm ?? 0.0;
            var width = item.WidthMm ?? 0.0;

            if (length <= 0.0 || width <= 0.0)
            {
                data.Notes.Add("No plate length or width. Weights were not calculated.");
                return;
            }

            var rawLength = length + PlateAllowanceMm;
            var rawWidth = width + PlateAllowanceMm;

            data.FinishWeightKg = rules.PlateWeightKg(length * width, thk);
            data.RmWeightKg = rules.PlateWeightKg(rawLength * rawWidth, thk);
            data.RmSize = Fmt(rawLength) + " X " + Fmt(rawWidth) + " X " + Fmt(thk);
        }

        private static void CalculateProfile(
            ManufacturingRules rules,
            GaProfileResult profile,
            PartBomItem item,
            double thk,
            GaSheetData data)
        {
            var layoutReplaced = profile.Classification.View is { EdgeOnOnly: true };
            var length = layoutReplaced ? item.LengthMm ?? 0.0 : profile.WidthMm > 0.0 ? profile.WidthMm : item.LengthMm ?? 0.0;
            var width = layoutReplaced ? item.WidthMm ?? 0.0 : profile.HeightMm > 0.0 ? profile.HeightMm : item.WidthMm ?? 0.0;

            if (length <= 0.0 || width <= 0.0)
            {
                data.Notes.Add("No outline size. Weights were not calculated.");
                return;
            }

            var trueArea = !layoutReplaced && profile.AreaMm2 > 0.0;
            var area = trueArea ? profile.AreaMm2 : length * width;

            if (!trueArea)
            {
                data.Notes.Add("Finish weight uses the bounding rectangle " + Fmt(length) + " x " + Fmt(width) + " mm, not the true area.");
            }

            var rawWidth = length + PlateAllowanceMm;
            var rawHeight = width + PlateAllowanceMm;

            data.FinishWeightKg = rules.PlateWeightKg(area, thk);
            data.RmWeightKg = rules.PlateWeightKg(rawWidth * rawHeight, thk);
            data.RmSize = Fmt(rawWidth) + " X " + Fmt(rawHeight) + " X " + Fmt(thk);
        }

        private static void CalculateCone(ManufacturingRules rules, PartBomItem item, double thk, GaSheetData data)
        {
            var bottom = item.LargeDiameterMm ?? item.OuterDiameterMm ?? 0.0;
            var top = item.SmallDiameterMm ?? 0.0;
            var height = item.HeightMm ?? 0.0;

            if (bottom <= 0.0 || height <= 0.0 || top < 0.0 || top >= bottom)
            {
                data.Notes.Add("Cone diameters or height are not valid. Weights were not calculated.");
                return;
            }

            var difference = bottom - top;
            var slant = Math.Sqrt(height * height + difference * difference / 4.0);
            var outerRadius = slant * bottom / difference;
            var innerRadius = outerRadius - slant;
            var angleDegrees = 180.0 * bottom / outerRadius;
            var startAngle = 90.0 - angleDegrees / 2.0;
            var endAngle = 90.0 + angleDegrees / 2.0;

            MeasureSector(outerRadius, innerRadius, startAngle, endAngle, out var boundsWidth, out var boundsHeight);

            var sectorArea = 0.5 * (angleDegrees * Math.PI / 180.0) * (outerRadius * outerRadius - innerRadius * innerRadius);

            data.FinishWeightKg = rules.PlateWeightKg(sectorArea, thk);
            data.RmWeightKg = rules.PlateWeightKg((boundsWidth + ConeAllowanceMm) * (boundsHeight + ConeAllowanceMm), thk);
            data.RmSize = Fmt(Math.Ceiling(boundsWidth + ConeAllowanceMm)) + " X " + Fmt(Math.Ceiling(boundsHeight + ConeAllowanceMm)) + " X " + Fmt(thk);
        }

        private static void CalculateScroll(ManufacturingRules rules, PartBomItem item, double thk, double sweepDegrees, GaSheetData data)
        {
            var startRadius = (item.SmallDiameterMm ?? 0.0) / 2.0;
            var endRadius = (item.LargeDiameterMm ?? 0.0) / 2.0;
            var width = item.WidthMm ?? 0.0;

            if (startRadius <= 0.0 || endRadius <= 0.0 || width <= 0.0 || sweepDegrees <= 0.0)
            {
                data.Notes.Add("Scroll radii, width or sweep are not valid. Weights were not calculated.");
                return;
            }

            var sweep = sweepDegrees * Math.PI / 180.0;
            var growth = (endRadius - startRadius) / sweep;
            var length = SpiralLength(startRadius, growth, sweep);

            var rawLength = Math.Ceiling(length + ScrollAllowanceMm);
            var rawWidth = Math.Ceiling(width + ScrollAllowanceMm);

            data.FinishWeightKg = rules.PlateWeightKg(length * width, thk);
            data.RmWeightKg = rules.PlateWeightKg(rawLength * rawWidth, thk);
            data.RmSize = Fmt(rawLength) + " X " + Fmt(rawWidth) + " X " + Fmt(thk);
        }

        private static void CalculateTransition(ManufacturingRules rules, PartBomItem item, double thk, GaSheetData data)
        {
            var length = item.LengthMm ?? 0.0;
            var width = item.WidthMm ?? 0.0;
            var round = item.OuterDiameterMm ?? 0.0;
            var height = item.HeightMm ?? 0.0;

            if (length <= 0.0 || width <= 0.0 || round <= 0.0 || height <= 0.0)
            {
                data.Notes.Add("Transition piece sizes are not valid. Weights were not calculated.");
                return;
            }

            data.FinishWeightKg = rules.PlateWeightKg(TransitionArea(length, width, round, height), thk);
            data.RmWeightKg = null;
            data.RmSize = "AS PER DEVELOPED PATTERN";
            data.Notes.Add("RM weight depends on the developed pattern bounding size and is shown on the sheet only.");
        }

        private static double TransitionArea(double length, double width, double round, double height)
        {
            var steps = TransitionStepsPerQuarter * 4;
            var radius = round / 2.0;
            var rect = new (double X, double Y, double Z)[steps + 1];
            var circle = new (double X, double Y, double Z)[steps + 1];

            for (var i = 0; i <= steps; i++)
            {
                var theta = 2.0 * Math.PI * i / steps;
                var cos = Math.Cos(theta);
                var sin = Math.Sin(theta);

                var tx = Math.Abs(cos) > 1e-12 ? length / 2.0 / Math.Abs(cos) : double.MaxValue;
                var ty = Math.Abs(sin) > 1e-12 ? width / 2.0 / Math.Abs(sin) : double.MaxValue;
                var t = Math.Min(tx, ty);

                rect[i] = (t * cos, t * sin, 0.0);
                circle[i] = (radius * cos, radius * sin, height);
            }

            var area = 0.0;

            for (var i = 0; i < steps; i++)
            {
                area += TriangleArea(rect[i], rect[i + 1], circle[i]);
                area += TriangleArea(rect[i + 1], circle[i + 1], circle[i]);
            }

            return area;
        }

        private static double TriangleArea((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        {
            var ux = b.X - a.X;
            var uy = b.Y - a.Y;
            var uz = b.Z - a.Z;
            var vx = c.X - a.X;
            var vy = c.Y - a.Y;
            var vz = c.Z - a.Z;

            var cx = uy * vz - uz * vy;
            var cy = uz * vx - ux * vz;
            var cz = ux * vy - uy * vx;

            return Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2.0;
        }

        private static void MeasureSector(double outerRadius, double innerRadius, double startAngle, double endAngle, out double width, out double height)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            void Add(double radius, double angle)
            {
                var rad = angle * Math.PI / 180.0;
                var x = radius * Math.Cos(rad);
                var y = radius * Math.Sin(rad);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            for (var angle = startAngle; angle < endAngle; angle += SectorStepDegrees)
            {
                Add(outerRadius, angle);
                Add(innerRadius, angle);
            }

            Add(outerRadius, endAngle);
            Add(innerRadius, endAngle);

            width = maxX - minX;
            height = maxY - minY;
        }

        private static double SpiralLength(double startRadius, double growth, double sweepRadians)
        {
            if (Math.Abs(growth) < 1e-9)
            {
                return startRadius * sweepRadians;
            }

            var endRadius = startRadius + growth * sweepRadians;

            double Primitive(double r)
            {
                var root = Math.Sqrt(r * r + growth * growth);
                return r * root + growth * growth * Math.Log(r + root);
            }

            return Math.Abs((Primitive(endRadius) - Primitive(startRadius)) / (2.0 * growth));
        }

        private static void Compare(ManufacturingRules rules, GaSheetData data, GenerationReport report)
        {
            if (!data.FinishWeightKg.HasValue || !data.CalloutWeightKg.HasValue || data.CalloutWeightKg.Value <= 0.0)
            {
                return;
            }

            var each = data.FinishWeightKg.Value;
            var total = each * data.Qty;
            var reference = data.CalloutWeightKg.Value;

            var eachDifference = Math.Abs(each - reference) / reference * 100.0;
            var totalDifference = Math.Abs(total - reference) / reference * 100.0;

            if (rules.IsWithinReference(each, reference))
            {
                data.WeightMatchesCallout = true;
                data.DifferencePercent = Math.Round(eachDifference, 1);
                return;
            }

            if (data.Qty > 1 && rules.IsWithinReference(total, reference))
            {
                data.WeightMatchesCallout = true;
                data.DifferencePercent = Math.Round(totalDifference, 1);
                data.Notes.Add("Callout weight " + Fmt(reference) + " kg matches the total for QTY " + data.Qty.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            data.WeightMatchesCallout = false;
            data.DifferencePercent = Math.Round(Math.Min(eachDifference, totalDifference), 1);

            report.Warn(
                "SHEET_WEIGHT_DIFFERS",
                data.Name,
                "Finish weight " + Fmt(each) + " kg (each) differs from callout weight " + Fmt(reference) + " kg by "
                + data.DifferencePercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + " %.");
        }

        private static string ResolveMaterial(PartBomItem item, string? calloutMaterial, string? defaultMaterial, GenerationReport report, string name)
        {
            if (!string.IsNullOrWhiteSpace(item.Material))
            {
                return item.Material.Trim();
            }

            if (!string.IsNullOrWhiteSpace(calloutMaterial))
            {
                return calloutMaterial.Trim();
            }

            var material = string.IsNullOrWhiteSpace(defaultMaterial) ? DefaultMaterial : defaultMaterial.Trim();
            report.Warn("MATERIAL_DEFAULT", name, "Material is not in the callout. '" + material + "' was used.");
            return material;
        }

        private static GaValueSource SourceOf(CalloutPart part, string field, bool stated, GaValueSource fallback)
        {
            if (part.ApprovedFields.Contains(field))
            {
                return GaValueSource.UserApproved;
            }

            return stated ? GaValueSource.Extracted : fallback;
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Fmt(double? value)
        {
            return value.HasValue ? Fmt(value.Value) : "-";
        }

        private static string FmtKg(double? value)
        {
            return value.HasValue ? Fmt(value.Value) + " kg" : "-";
        }
    }
}
