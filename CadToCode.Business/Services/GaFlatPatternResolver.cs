using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaFlatPatternResult
    {
        public GaProfileResult Profile { get; init; } = new();

        public PartBomItem? Item { get; set; }

        public bool Resolved => Item is not null;

        public double SweepDegrees { get; set; } = 360.0;

        public List<string> Assumptions { get; } = new();

        public string Message { get; set; } = string.Empty;

        public GaPartType Type => Profile.Classification.Type;

        public CalloutPart Part => Profile.Classification.Part;
    }

    public sealed class GaFlatPatternResolver
    {
        private const double FullConeFraction = 0.02;
        private const double EqualEndsFraction = 0.01;
        private const double DefaultSweepDegrees = 360.0;

        private static readonly Regex HeightRx = new(
            @"\b(?:ht|hgt|height)\.?\s*[:=\-]?\s*(?<v>\d+(?:\.\d+)?)|\bh\s*[:=]\s*(?<w>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WidthRx = new(
            @"\b(?:width|wdth|wd)\.?\s*[:=\-]?\s*(?<v>\d+(?:\.\d+)?)|\bw\s*[:=]\s*(?<w>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SweepRx = new(
            @"(?<v>\d{2,3}(?:\.\d+)?)\s*(?:°|deg(?:ree)?s?\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DiameterRx = new(
            @"(?:Ø|\bdia\.?\s*)(?<v>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class Hints
        {
            public double? Height { get; init; }

            public double? Width { get; init; }

            public double? Sweep { get; init; }

            public List<double> Diameters { get; init; } = new();
        }

        public IReadOnlyList<GaFlatPatternResult> Resolve(
            IReadOnlyList<GaProfileResult> profiles,
            IReadOnlyList<AssemblyNode> nodes,
            double mmPerUnit,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(profiles);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(report);

            var mm = mmPerUnit > 0.0 ? mmPerUnit : 1.0;
            var list = new List<GaFlatPatternResult>();

            foreach (var profile in profiles)
            {
                if (profile.Classification.Family != GaPartFamily.Developed)
                {
                    continue;
                }

                list.Add(ResolveOne(profile, nodes, mm, report));
            }

            return list;
        }

        public GaFlatPatternResult ResolveOne(
            GaProfileResult profile,
            IReadOnlyList<AssemblyNode> nodes,
            double mm,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(nodes);
            ArgumentNullException.ThrowIfNull(report);

            var result = new GaFlatPatternResult { Profile = profile };
            var part = result.Part;

            if (profile.Item is null)
            {
                result.Message = "No measured geometry is available: " + profile.Message;
                return result;
            }

            if (!part.ThicknessMm.HasValue || part.ThicknessMm.Value <= 0.0)
            {
                result.Message = "The callout gives no thickness.";
                report.Error("FLAT_NO_THICKNESS", part.Name, "Flat pattern cannot be developed without thickness.");
                return result;
            }

            var hints = ReadHints(part.Raw);

            switch (result.Type)
            {
                case GaPartType.Cone:
                    ResolveCone(result, hints, nodes, mm, report);
                    break;

                case GaPartType.Scroll:
                    ResolveScroll(result, hints, nodes, mm, report);
                    break;

                case GaPartType.TransitionPiece:
                    ResolveTransition(result, hints, nodes, mm, report);
                    break;

                case GaPartType.Blade:
                    ResolveBlade(result, hints, nodes, mm, report);
                    break;

                default:
                    result.Message = "Type " + result.Type.ToString() + " has no flat pattern rule.";
                    report.Error("FLAT_NO_RULE", part.Name, result.Message);
                    break;
            }

            if (result.Resolved)
            {
                foreach (var note in result.Assumptions)
                {
                    report.Warn("FLAT_ASSUMPTION", part.Name, note);
                }
            }

            return result;
        }

        private static void ResolveCone(
            GaFlatPatternResult result,
            Hints hints,
            IReadOnlyList<AssemblyNode> nodes,
            double mm,
            GenerationReport report)
        {
            var item = result.Profile.Item!;
            var part = result.Part;
            var view = result.Profile.Classification.View;

            var large = Positive(item.LargeDiameterMm);
            var small = Positive(item.SmallDiameterMm);

            if (large is null && hints.Diameters.Count > 0)
            {
                large = hints.Diameters.Max();
                result.Assumptions.Add("Large diameter " + Fmt(large) + " mm taken from the callout text.");
            }

            if (small is null && hints.Diameters.Count > 1)
            {
                small = hints.Diameters.Min();
                result.Assumptions.Add("Small diameter " + Fmt(small) + " mm taken from the callout text.");
            }

            var height = IsRoundView(view) ? null : Positive(item.HeightMm);

            if (height is null && hints.Height.HasValue)
            {
                height = hints.Height;
                result.Assumptions.Add("Height " + Fmt(height) + " mm taken from the callout text.");
            }

            if (height is null && view is not null)
            {
                height = OtherViewDimension(view.Node, nodes, mm);
                if (height.HasValue)
                {
                    result.Assumptions.Add("Height " + Fmt(height) + " mm measured from another view on the same layer. Please verify.");
                }
            }

            var missing = new List<string>();
            if (large is null)
            {
                missing.Add("large diameter");
            }

            if (height is null)
            {
                missing.Add("height");
            }

            if (missing.Count > 0)
            {
                Fail(result, report, "FLAT_CONE_MISSING", "Cone is missing " + string.Join(", ", missing) + ".");
                return;
            }

            if (small.HasValue && large!.Value - small.Value <= large.Value * EqualEndsFraction)
            {
                Fail(result, report, "FLAT_CONE_EQUAL_ENDS", "Both ends measure about " + Fmt(large) + " mm. This is a cylinder, not a cone.");
                return;
            }

            if (small.HasValue && small.Value <= large!.Value * FullConeFraction)
            {
                small = null;
                result.Assumptions.Add("Top end is a point; a full cone is developed.");
            }

            item.Kind = BomPartKind.ConeFlatPattern;
            item.LargeDiameterMm = Math.Round(large!.Value, 2);
            item.SmallDiameterMm = small.HasValue ? Math.Round(small.Value, 2) : null;
            item.HeightMm = Math.Round(height!.Value, 2);
            item.OuterDiameterMm = null;
            item.InnerDiameterMm = null;
            item.LengthMm = null;
            item.WidthMm = null;
            item.HoleCount = null;
            item.HoleDiameterMm = null;
            item.HolePcdMm = null;

            result.Item = item;

            report.Info(
                "FLAT_CONE",
                part.Name,
                "Large Ø" + Fmt(item.LargeDiameterMm) + ", small Ø" + (item.SmallDiameterMm.HasValue ? Fmt(item.SmallDiameterMm) : "0")
                + ", height " + Fmt(item.HeightMm) + ", THK " + Fmt(part.ThicknessMm) + " mm.");
        }

        private static void ResolveScroll(
            GaFlatPatternResult result,
            Hints hints,
            IReadOnlyList<AssemblyNode> nodes,
            double mm,
            GenerationReport report)
        {
            var item = result.Profile.Item!;
            var part = result.Part;
            var view = result.Profile.Classification.View;

            double? startRadius = null;
            double? endRadius = null;

            if (view is not null)
            {
                var radii = ViewRadii(view.Node, mm);

                if (radii.Count >= 2)
                {
                    startRadius = radii.Min();
                    endRadius = radii.Max();
                    result.Assumptions.Add(
                        "Start radius " + Fmt(startRadius) + " mm and end radius " + Fmt(endRadius)
                        + " mm are the smallest and largest arcs in the view. Please verify.");
                }
            }

            if ((startRadius is null || endRadius is null) && hints.Diameters.Count >= 2)
            {
                startRadius = hints.Diameters.Min() / 2.0;
                endRadius = hints.Diameters.Max() / 2.0;
                result.Assumptions.Add("Start and end radius taken from the diameters in the callout text.");
            }

            var width = hints.Width;

            if (width is null && view is not null)
            {
                width = OtherViewDimension(view.Node, nodes, mm);
                if (width.HasValue)
                {
                    result.Assumptions.Add("Casing width " + Fmt(width) + " mm measured from another view on the same layer. Please verify.");
                }
            }
            else if (width.HasValue)
            {
                result.Assumptions.Add("Casing width " + Fmt(width) + " mm taken from the callout text.");
            }

            var missing = new List<string>();
            if (startRadius is null || endRadius is null)
            {
                missing.Add("start and end radius");
            }

            if (width is null)
            {
                missing.Add("width");
            }

            if (missing.Count > 0)
            {
                Fail(result, report, "FLAT_SCROLL_MISSING", "Scroll is missing " + string.Join(", ", missing) + ".");
                return;
            }

            result.SweepDegrees = hints.Sweep is > 0.0 and <= 720.0 ? hints.Sweep.Value : DefaultSweepDegrees;

            if (!hints.Sweep.HasValue)
            {
                result.Assumptions.Add("Sweep angle is not in the callout. " + Fmt(DefaultSweepDegrees) + " degrees was used.");
            }

            item.Kind = BomPartKind.ScrollFlatPattern;
            item.SmallDiameterMm = Math.Round(startRadius!.Value * 2.0, 2);
            item.LargeDiameterMm = Math.Round(endRadius!.Value * 2.0, 2);
            item.WidthMm = Math.Round(width!.Value, 2);
            item.OuterDiameterMm = null;
            item.InnerDiameterMm = null;
            item.LengthMm = null;
            item.HeightMm = null;
            item.HoleCount = null;
            item.HoleDiameterMm = null;
            item.HolePcdMm = null;
            item.Remarks = (item.Remarks ?? string.Empty) + " Sweep " + Fmt(result.SweepDegrees) + " deg.";

            result.Item = item;

            report.Info(
                "FLAT_SCROLL",
                part.Name,
                "Start Ø" + Fmt(item.SmallDiameterMm) + ", end Ø" + Fmt(item.LargeDiameterMm) + ", width " + Fmt(item.WidthMm)
                + ", sweep " + Fmt(result.SweepDegrees) + " deg, THK " + Fmt(part.ThicknessMm) + " mm.");
        }

        private static void ResolveTransition(
            GaFlatPatternResult result,
            Hints hints,
            IReadOnlyList<AssemblyNode> nodes,
            double mm,
            GenerationReport report)
        {
            var profile = result.Profile;
            var item = profile.Item!;
            var part = result.Part;
            var view = profile.Classification.View;

            double? length = null;
            double? width = null;
            double? round = null;
            double? height = null;

            var plan = profile.Holes.Count > 0;

            if (plan)
            {
                length = Positive(profile.WidthMm);
                width = Positive(profile.HeightMm);
                round = profile.Holes.OrderByDescending(h => h.DiameterMm).First().DiameterMm;
            }
            else
            {
                length = Positive(profile.LargeWidthMm);
                round = Positive(profile.SmallWidthMm);
                height = Positive(profile.HeightMm);
                width = hints.Width;

                if (width.HasValue)
                {
                    result.Assumptions.Add("Rectangle width " + Fmt(width) + " mm taken from the callout text.");
                }
                else if (view is not null)
                {
                    width = OtherViewDimension(view.Node, nodes, mm);
                    if (width.HasValue)
                    {
                        result.Assumptions.Add("Rectangle width " + Fmt(width) + " mm measured from another view on the same layer. Please verify.");
                    }
                }
            }

            if (round is null && hints.Diameters.Count > 0)
            {
                round = hints.Diameters.Min();
                result.Assumptions.Add("Round end Ø" + Fmt(round) + " mm taken from the callout text.");
            }

            if (height is null && hints.Height.HasValue)
            {
                height = hints.Height;
                result.Assumptions.Add("Height " + Fmt(height) + " mm taken from the callout text.");
            }

            if (height is null && plan && view is not null)
            {
                height = OtherViewDimension(view.Node, nodes, mm);
                if (height.HasValue)
                {
                    result.Assumptions.Add("Height " + Fmt(height) + " mm measured from another view on the same layer. Please verify.");
                }
            }

            var missing = new List<string>();
            if (length is null)
            {
                missing.Add("rectangle length");
            }

            if (width is null)
            {
                missing.Add("rectangle width");
            }

            if (round is null)
            {
                missing.Add("round diameter");
            }

            if (height is null)
            {
                missing.Add("height");
            }

            if (missing.Count > 0)
            {
                Fail(result, report, "FLAT_TRANSITION_MISSING", "Transition piece is missing " + string.Join(", ", missing) + ".");
                return;
            }

            item.Kind = BomPartKind.TransitionFlatPattern;
            item.LengthMm = Math.Round(Math.Max(length!.Value, width!.Value), 2);
            item.WidthMm = Math.Round(Math.Min(length.Value, width.Value), 2);
            item.OuterDiameterMm = Math.Round(round!.Value, 2);
            item.HeightMm = Math.Round(height!.Value, 2);
            item.InnerDiameterMm = null;
            item.SmallDiameterMm = null;
            item.LargeDiameterMm = null;
            item.HoleCount = null;
            item.HoleDiameterMm = null;
            item.HolePcdMm = null;

            result.Item = item;

            report.Info(
                "FLAT_TRANSITION",
                part.Name,
                "Rectangle " + Fmt(item.LengthMm) + " x " + Fmt(item.WidthMm) + ", round Ø" + Fmt(item.OuterDiameterMm)
                + ", height " + Fmt(item.HeightMm) + ", THK " + Fmt(part.ThicknessMm) + " mm.");
        }

        private static void ResolveBlade(
            GaFlatPatternResult result,
            Hints hints,
            IReadOnlyList<AssemblyNode> nodes,
            double mm,
            GenerationReport report)
        {
            var profile = result.Profile;
            var item = profile.Item!;
            var part = result.Part;
            var view = profile.Classification.View;

            var edgeOn = view is not null && view.EdgeOnOnly;
            var hasProfile = item.Kind == BomPartKind.ProfilePlate && item.Profile is { Count: > 0 };

            if (hasProfile && !edgeOn)
            {
                result.Item = item;

                report.Info(
                    "FLAT_BLADE",
                    part.Name,
                    "Blade layout taken from the blade profile: " + Fmt(profile.WidthMm) + " x " + Fmt(profile.HeightMm)
                    + " mm, " + item.Profile!.Count.ToString(CultureInfo.InvariantCulture) + " entities.");
                return;
            }

            if (view is null)
            {
                Fail(result, report, "FLAT_BLADE_NO_VIEW", "Blade has no selected view.");
                return;
            }

            var length = Math.Max(view.WidthMm, view.HeightMm);
            var width = hints.Width;

            if (width.HasValue)
            {
                result.Assumptions.Add("Blade width " + Fmt(width) + " mm taken from the callout text.");
            }
            else
            {
                width = OtherViewDimension(view.Node, nodes, mm);
                if (width.HasValue)
                {
                    result.Assumptions.Add("Blade width " + Fmt(width) + " mm measured from another view on the same layer. Please verify.");
                }
            }

            if (length <= 0.0 || width is null || width.Value <= 0.0)
            {
                Fail(result, report, "FLAT_BLADE_MISSING", "Blade true shape is not in the GA and its width is not stated.");
                return;
            }

            var w = Math.Round(width.Value, 2);
            var l = Math.Round(length, 2);

            var entities = new List<ProfileEntity>
            {
                new(ProfileEntityKind.Line, 0, 0, l, 0, 0, 0, 0, 0, 0),
                new(ProfileEntityKind.Line, l, 0, l, w, 0, 0, 0, 0, 0),
                new(ProfileEntityKind.Line, l, w, 0, w, 0, 0, 0, 0, 0),
                new(ProfileEntityKind.Line, 0, w, 0, 0, 0, 0, 0, 0, 0)
            };

            item.Kind = BomPartKind.ProfilePlate;
            item.LengthMm = l;
            item.WidthMm = w;
            item.Profile = entities;

            result.Item = item;
            result.Assumptions.Add("Only an edge-on blade view exists. A " + Fmt(l) + " x " + Fmt(w) + " mm rectangular layout was used. Please verify.");
        }

        private static List<double> ViewRadii(AssemblyNode node, double mm)
        {
            var radii = new List<double>();
            var g = node.Geometry;
            var box = node.Bounds;
            var margin = Math.Max(box.Width, box.Height) * 0.01 + 1e-6;
            var wide = box.Expand(margin);

            foreach (var a in g.Arcs)
            {
                if (IsNoiseLayer(a.Layer) || !PointInside(wide, a.Center.X, a.Center.Y))
                {
                    continue;
                }

                radii.Add(a.Radius * mm);
            }

            foreach (var c in g.Circles)
            {
                if (IsNoiseLayer(c.Layer) || !PointInside(wide, c.Center.X, c.Center.Y))
                {
                    continue;
                }

                radii.Add(c.Radius * mm);
            }

            return radii
                .Where(r => r > 1e-6)
                .Select(r => Math.Round(r, 2))
                .Distinct()
                .ToList();
        }

        private static bool PointInside(BoundingBox2D box, double x, double y)
        {
            return x >= box.MinX && x <= box.MaxX && y >= box.MinY && y <= box.MaxY;
        }

        private static double? OtherViewDimension(AssemblyNode self, IReadOnlyList<AssemblyNode> nodes, double mm)
        {
            if (string.IsNullOrWhiteSpace(self.Layer))
            {
                return null;
            }

            var other = nodes
                .Where(n => !string.Equals(n.Id, self.Id, StringComparison.Ordinal)
                    && string.Equals(n.Layer, self.Layer, StringComparison.OrdinalIgnoreCase)
                    && n.Bounds.Area > 0.0)
                .OrderByDescending(n => n.Bounds.Area)
                .FirstOrDefault();

            if (other is null)
            {
                return null;
            }

            var value = Math.Min(other.Bounds.Width, other.Bounds.Height) * mm;
            return value > 0.0 ? Math.Round(value, 2) : null;
        }

        private static bool IsRoundView(ViewSelection? view)
        {
            return view is not null && (view.Node.Shape == PartShape.Circular || view.Node.Shape == PartShape.Annular);
        }

        private static bool IsNoiseLayer(string? layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return layer.Contains("dim", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("text", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("note", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("center", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("centre", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("axis", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("defpoints", StringComparison.OrdinalIgnoreCase);
        }

        private static Hints ReadHints(string raw)
        {
            var text = raw ?? string.Empty;

            return new Hints
            {
                Height = ReadNumber(HeightRx, text),
                Width = ReadNumber(WidthRx, text),
                Sweep = ReadNumber(SweepRx, text),
                Diameters = DiameterRx.Matches(text)
                    .Select(m => ParseDouble(m.Groups["v"].Value))
                    .Where(v => v.HasValue && v.Value > 0.0)
                    .Select(v => v!.Value)
                    .ToList()
            };
        }

        private static double? ReadNumber(Regex rx, string text)
        {
            var match = rx.Match(text);

            if (!match.Success)
            {
                return null;
            }

            var raw = match.Groups["v"].Success ? match.Groups["v"].Value : match.Groups["w"].Value;
            var value = ParseDouble(raw);
            return value is > 0.0 ? value : null;
        }

        private static double? ParseDouble(string raw)
        {
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static double? Positive(double? value)
        {
            return value is > 0.0 ? value : null;
        }

        private static void Fail(GaFlatPatternResult result, GenerationReport report, string code, string message)
        {
            result.Message = message;
            report.Error(code, result.Part.Name, message);
        }

        private static string Fmt(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";
        }
    }
}
