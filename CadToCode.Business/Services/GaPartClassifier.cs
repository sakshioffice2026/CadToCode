using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public enum GaPartFamily
    {
        Unknown = 0,
        Ring = 1,
        Rectangle = 2,
        Developed = 3,
        RolledSection = 4,
        Perforated = 5,
        BoughtOut = 6
    }

    public enum GaPartType
    {
        Unknown = 0,
        Flange = 1,
        BackPlate = 2,
        Shroud = 3,
        SparkProofRing = 4,
        Hub = 5,
        Plate = 6,
        Stiffener = 7,
        BasePlate = 8,
        Cone = 9,
        Scroll = 10,
        TransitionPiece = 11,
        Blade = 12,
        Ismc = 13,
        FlatBar = 14,
        RoundBar = 15,
        PerforatedSheet = 16,
        BoughtOut = 17
    }

    public sealed class GaPartClassification
    {
        public CalloutPart Part { get; init; } = new();

        public ViewSelection? View { get; init; }

        public GaPartType Type { get; init; }

        public GaPartFamily Family { get; init; }

        public BomPartKind BomKind { get; init; }

        public string Reason { get; init; } = string.Empty;

        public GaManufacturingCategory Category => GaDrawingTemplates.CategoryOf(Family, Type);

        public bool NeedsGeometry =>
            Family != GaPartFamily.BoughtOut &&
            Family != GaPartFamily.RolledSection &&
            Family != GaPartFamily.Unknown;
    }

    public sealed class GaClassificationResult
    {
        public List<GaPartClassification> Parts { get; } = new();

        public List<CalloutPart> Unclassified { get; } = new();

        public GaPartClassification? Find(CalloutPart part)
        {
            return Parts.FirstOrDefault(p => ReferenceEquals(p.Part, part));
        }
    }

    public sealed class GaPartClassifier
    {
        private const int PerforatedMinCircles = 8;

        private static readonly Regex BoughtOutRx = new(
            @"bearing|motor|bellow|bolt|\bnut\b|hex|wire\s?mesh|rockwool|plummer|coupling|actuator|gasket|washer\s*(?:m\d+)|frp|silencer media",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IsmcRx = new(
            @"\b(?:ismc|ismb|isa|islb)\b|\bchannel\b|\bangle\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FlatBarRx = new(
            @"\bflat\s*\d",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RoundBarRx = new(
            @"\bbar\s*dia|\bround\s*bar|\bpipe\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PerforatedRx = new(
            @"perforat|sieve|strainer|grating|grid\s?plate|distribution\s?plate",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BladeRx = new(
            @"\bblades?\b|\bvane\b|\bimpeller\s*blade",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ConeRx = new(
            @"\bcone\b|\bconical\b|\bhopper\b|\bfrustum\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ScrollRx = new(
            @"\bscroll\b|\bcasing\b|\bvolute\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TransitionRx = new(
            @"transition|\breducer\b|rect(?:angular)?\s*to\s*round|square\s*to\s*round",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SparkProofRx = new(
            @"spark\s?proof|\bris\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ShroudRx = new(
            @"\bshroud\b|\bfront\s?plate\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BackPlateRx = new(
            @"back\s?plate|\bbackplate\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HubRx = new(
            @"\bhub\b|\bboss\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FlangeRx = new(
            @"flange|\bring\b|\bdisc\b|\bdisk\b|\bwasher\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StiffenerRx = new(
            @"stiffener|stiffner|\brib\b|\bgusset\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BasePlateRx = new(
            @"base\s?plate|\bbase\b|\bfoot\s?plate",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);


        private static readonly Regex FrameRx = new(
            @"\bframe\b|\bskid\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PlateRx = new(
            @"plate|\bdoor\b|bracket|strip|cutoff|cut\s?off|spacer|hook|extractor|\bguard\b|\bsill\b|\bsil\b|\bpanel\b|\bcover\b|\bsheet\b|\bpedestal\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public GaClassificationResult Classify(
            IReadOnlyList<CalloutPart> callouts,
            IReadOnlyList<ViewSelection> views,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(callouts);
            ArgumentNullException.ThrowIfNull(views);
            ArgumentNullException.ThrowIfNull(report);

            var result = new GaClassificationResult();

            foreach (var part in callouts)
            {
                var view = views.FirstOrDefault(v => ReferenceEquals(v.Part, part));
                var classified = ClassifyOne(part, view, report);

                if (classified.Type == GaPartType.Unknown)
                {
                    result.Unclassified.Add(part);
                    report.Error("CLASSIFY_UNKNOWN", part.Name, classified.Reason);
                }
                else
                {
                    report.Info(
                        "CLASSIFY",
                        part.Name,
                        classified.Type.ToString() + " (" + classified.Family.ToString() + "). " + classified.Reason);
                }

                result.Parts.Add(classified);
            }

            return result;
        }

        public GaPartClassification ClassifyOne(CalloutPart part, ViewSelection? view, GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(part);
            ArgumentNullException.ThrowIfNull(report);

            var name = part.Name ?? string.Empty;
            var text = name + " " + (part.Section ?? string.Empty);

            if (BoughtOutRx.IsMatch(name) || GaPartNameRules.IsConsumable(name))
            {
                return Build(part, view, GaPartType.BoughtOut, GaPartFamily.BoughtOut, BomPartKind.Unknown, "Bought-out item.");
            }

            var hasSection = !string.IsNullOrWhiteSpace(part.Section)
                && !string.Equals(part.Section.Trim(), "STD", StringComparison.OrdinalIgnoreCase);

            if (hasSection || IsmcRx.IsMatch(text) || FlatBarRx.IsMatch(text) || RoundBarRx.IsMatch(text) || FrameRx.IsMatch(name))
            {
                var sectionType = ResolveSectionType(text);
                var bomKind = sectionType == GaPartType.RoundBar ? BomPartKind.RoundBar : BomPartKind.Unknown;

                return Build(part, view, sectionType, GaPartFamily.RolledSection, bomKind, "Rolled section " + (part.Section ?? name) + ".");
            }

            if (PerforatedRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.PerforatedSheet, GaPartFamily.Perforated, BomPartKind.ProfilePlate, "Name states a perforated sheet.");
            }

            if (BladeRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Blade, GaPartFamily.Developed, BomPartKind.ProfilePlate, "Name states a blade.");
            }

            if (TransitionRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.TransitionPiece, GaPartFamily.Developed, BomPartKind.TransitionFlatPattern, "Name states a transition piece.");
            }

            if (ScrollRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Scroll, GaPartFamily.Developed, BomPartKind.ScrollFlatPattern, "Name states a scroll or casing.");
            }

            if (ConeRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Cone, GaPartFamily.Developed, BomPartKind.ConeFlatPattern, "Name states a cone.");
            }

            if (SparkProofRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.SparkProofRing, GaPartFamily.Ring, BomPartKind.Flange, "Name states a spark proof ring.");
            }

            if (ShroudRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Shroud, GaPartFamily.Ring, BomPartKind.Flange, "Name states a shroud.");
            }

            if (BackPlateRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.BackPlate, GaPartFamily.Ring, BomPartKind.Flange, "Name states a back plate.");
            }

            if (HubRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Hub, GaPartFamily.Ring, BomPartKind.Hub, "Name states a hub.");
            }

            if (FlangeRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Flange, GaPartFamily.Ring, BomPartKind.Flange, "Name states a flange or ring.");
            }

            if (StiffenerRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.Stiffener, GaPartFamily.Rectangle, BomPartKind.RectangularPlate, "Name states a stiffener.");
            }

            if (BasePlateRx.IsMatch(name))
            {
                return Build(part, view, GaPartType.BasePlate, GaPartFamily.Rectangle, BomPartKind.RectangularPlate, "Name states a base plate.");
            }

            if (PlateRx.IsMatch(name))
            {
                if (view is not null && IsRound(view.Node))
                {
                    return Build(part, view, GaPartType.Flange, GaPartFamily.Ring, BomPartKind.Flange, "Plate name, but the selected view is round.");
                }

                if (view is not null && IsPerforated(view.Node))
                {
                    return Build(part, view, GaPartType.PerforatedSheet, GaPartFamily.Perforated, BomPartKind.ProfilePlate, "Plate name with a regular hole pattern in the view.");
                }

                return Build(part, view, GaPartType.Plate, GaPartFamily.Rectangle, BomPartKind.RectangularPlate, "Name states a plate.");
            }

            if (view is null)
            {
                return Build(part, view, GaPartType.Unknown, GaPartFamily.Unknown, BomPartKind.Unknown, "The name gives no part type and no view was selected.");
            }

            return ClassifyByShape(part, view);
        }

        private static GaPartClassification ClassifyByShape(CalloutPart part, ViewSelection view)
        {
            var node = view.Node;

            if (IsRound(node))
            {
                return Build(part, view, GaPartType.Flange, GaPartFamily.Ring, BomPartKind.Flange, "Shape of the selected view is " + node.Shape.ToString() + ".");
            }

            if (IsPerforated(node))
            {
                return Build(part, view, GaPartType.PerforatedSheet, GaPartFamily.Perforated, BomPartKind.ProfilePlate, "Selected view is a rectangle with a regular hole pattern.");
            }

            if (node.Shape == PartShape.Rectangular)
            {
                return Build(part, view, GaPartType.Plate, GaPartFamily.Rectangle, BomPartKind.RectangularPlate, "Shape of the selected view is rectangular.");
            }

            if (node.Shape == PartShape.Polygonal || node.Shape == PartShape.Irregular)
            {
                return Build(part, view, GaPartType.Plate, GaPartFamily.Rectangle, BomPartKind.ProfilePlate, "Shape of the selected view is " + node.Shape.ToString() + "; real outline will be used.");
            }

            return Build(
                part,
                view,
                GaPartType.Unknown,
                GaPartFamily.Unknown,
                BomPartKind.Unknown,
                "Shape " + node.Shape.ToString() + " of the selected view gives no part type ("
                + view.WidthMm.ToString("0.#", CultureInfo.InvariantCulture) + " x "
                + view.HeightMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm).");
        }

        private static GaPartType ResolveSectionType(string text)
        {
            if (IsmcRx.IsMatch(text))
            {
                return GaPartType.Ismc;
            }

            if (FlatBarRx.IsMatch(text))
            {
                return GaPartType.FlatBar;

            }

            if (FrameRx.IsMatch(text))
            {
                return GaPartType.Ismc;
            }

            if (RoundBarRx.IsMatch(text))
            {
                return GaPartType.RoundBar;
            }

            return GaPartType.FlatBar;
        }

        private static bool IsRound(AssemblyNode node)
        {
            return node.Shape == PartShape.Circular || node.Shape == PartShape.Annular;
        }

        private static bool IsPerforated(AssemblyNode node)
        {
            return node.Shape == PartShape.Rectangular && node.CircleCount >= PerforatedMinCircles;
        }

        private static GaPartClassification Build(
            CalloutPart part,
            ViewSelection? view,
            GaPartType type,
            GaPartFamily family,
            BomPartKind bomKind,
            string reason)
        {
            return new GaPartClassification
            {
                Part = part,
                View = view,
                Type = type,
                Family = family,
                BomKind = bomKind,
                Reason = reason
            };
        }
    }
}