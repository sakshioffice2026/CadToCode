namespace CadToCode.Business.Services
{
    public enum GaManufacturingCategory
    {
        Unknown = 0,
        SheetMetalPlate = 1,
        SheetMetalRing = 2,
        SheetMetalDeveloped = 3,
        SheetMetalPerforated = 4,
        FabricatedSection = 5,
        MachinedTurned = 6,
        BoughtOut = 7
    }

    public sealed class GaDrawingTemplate
    {
        public GaManufacturingCategory Category { get; init; }

        public string Title { get; init; } = string.Empty;

        public string DefaultTolerance { get; init; } = "ISO 2768-m";

        public string[] TitleBlockFields { get; init; } = Array.Empty<string>();

        public string[] DefaultNotes { get; init; } = Array.Empty<string>();

        public bool ShowRawMaterial { get; init; } = true;
    }

    public static class GaDrawingTemplates
    {
        private static readonly string[] CommonFields =
        {
            "DRAWING NO", "PART", "MATERIAL", "THICKNESS", "QTY", "FINISH WT", "TOLERANCE", "DATE"
        };

        private static readonly Dictionary<GaManufacturingCategory, GaDrawingTemplate> Templates = new()
        {
            [GaManufacturingCategory.SheetMetalPlate] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.SheetMetalPlate,
                Title = "SHEET METAL PLATE",
                TitleBlockFields = CommonFields,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "REMOVE SHARP EDGES AND BURRS.",
                    "LASER OR PLASMA CUT."
                }
            },
            [GaManufacturingCategory.SheetMetalRing] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.SheetMetalRing,
                Title = "SHEET METAL RING / DISC",
                TitleBlockFields = CommonFields,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "REMOVE SHARP EDGES AND BURRS.",
                    "HOLES EQUISPACED ON PCD UNLESS NOTED."
                }
            },
            [GaManufacturingCategory.SheetMetalDeveloped] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.SheetMetalDeveloped,
                Title = "DEVELOPED FLAT PATTERN",
                TitleBlockFields = CommonFields,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "FLAT PATTERN SHOWN. ROLL OR FORM AS PER GA.",
                    "WELD SEAM AS PER GA."
                }
            },
            [GaManufacturingCategory.SheetMetalPerforated] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.SheetMetalPerforated,
                Title = "PERFORATED SHEET",
                TitleBlockFields = CommonFields,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "PERFORATION PATTERN AS PER GA."
                }
            },
            [GaManufacturingCategory.FabricatedSection] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.FabricatedSection,
                Title = "FABRICATED SECTION",
                TitleBlockFields = CommonFields,
                ShowRawMaterial = false,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "CUT LENGTH AS PER CUT LIST.",
                    "WELDING AS PER GA."
                }
            },
            [GaManufacturingCategory.MachinedTurned] = new GaDrawingTemplate
            {
                Category = GaManufacturingCategory.MachinedTurned,
                Title = "MACHINED / TURNED PART",
                DefaultTolerance = "ISO 2768-m, FITS AS NOTED",
                TitleBlockFields = CommonFields,
                DefaultNotes = new[]
                {
                    "ALL DIMENSIONS IN MM.",
                    "BREAK SHARP EDGES 0.5 X 45 DEG.",
                    "SURFACE FINISH Ra 3.2 UNLESS NOTED."
                }
            }
        };

        private static readonly GaDrawingTemplate Fallback = new()
        {
            Category = GaManufacturingCategory.Unknown,
            Title = "MANUFACTURING DRAWING",
            TitleBlockFields = CommonFields,
            DefaultNotes = new[] { "ALL DIMENSIONS IN MM." }
        };

        public static GaDrawingTemplate Get(GaManufacturingCategory category)
        {
            return Templates.TryGetValue(category, out var template) ? template : Fallback;
        }

        public static GaManufacturingCategory CategoryOf(GaPartFamily family, GaPartType type)
        {
            switch (family)
            {
                case GaPartFamily.BoughtOut:
                    return GaManufacturingCategory.BoughtOut;
                case GaPartFamily.RolledSection:
                    return GaManufacturingCategory.FabricatedSection;
                case GaPartFamily.Developed:
                    return GaManufacturingCategory.SheetMetalDeveloped;
                case GaPartFamily.Perforated:
                    return GaManufacturingCategory.SheetMetalPerforated;
                case GaPartFamily.Rectangle:
                    return GaManufacturingCategory.SheetMetalPlate;
                case GaPartFamily.Ring:
                    return type == GaPartType.Hub || type == GaPartType.RoundBar
                        ? GaManufacturingCategory.MachinedTurned
                        : GaManufacturingCategory.SheetMetalRing;
                default:
                    return GaManufacturingCategory.Unknown;
            }
        }
    }
}
