using System.Text.Json;
using System.Text.Json.Serialization;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Models.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Memory;

public interface IPatternMemoryService
{
    int Count { get; }

    IReadOnlyList<PatternMatch> Search(ExtractedGeometry geometry);

    PatternRecord Learn(ExtractedGeometry geometry, ExtractedComponent component, bool userConfirmed = true);
}

public class PatternMemoryService : IPatternMemoryService
{
    private const double DuplicateSimilarity = 0.985;
    private const int MaxPatterns = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LlmOptions _options;
    private readonly GeometryEmbedder _embedder;
    private readonly ILogger<PatternMemoryService> _logger;
    private readonly List<PatternRecord> _patterns = new();
    private readonly object _sync = new();
    private bool _loaded;

    public PatternMemoryService(
        IOptions<LlmOptions> options,
        GeometryEmbedder embedder,
        ILogger<PatternMemoryService> logger)
    {
        _options = options.Value;
        _embedder = embedder;
        _logger = logger;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded();
                return _patterns.Count;
            }
        }
    }

    public IReadOnlyList<PatternMatch> Search(ExtractedGeometry geometry)
    {
        var query = _embedder.Embed(geometry);

        lock (_sync)
        {
            EnsureLoaded();

            return _patterns
                .Select(pattern => new PatternMatch
                {
                    Pattern = Clone(pattern),
                    Similarity = GeometryEmbedder.CosineSimilarity(query, pattern.Vector)
                })
                .Where(match => match.Similarity >= _options.MinSimilarity)
                .OrderByDescending(match => match.Similarity)
                .Take(Math.Max(1, _options.TopKPatterns))
                .ToList();
        }
    }

    public PatternRecord Learn(ExtractedGeometry geometry, ExtractedComponent component, bool userConfirmed = true)
    {
        var vector = _embedder.Embed(geometry);

        lock (_sync)
        {
            EnsureLoaded();

            var existing = _patterns
                .Where(p => !p.IsSeed && p.ComponentType == component.ComponentType)
                .Select(p => new { Pattern = p, Similarity = GeometryEmbedder.CosineSimilarity(vector, p.Vector) })
                .Where(x => x.Similarity >= DuplicateSimilarity)
                .OrderByDescending(x => x.Similarity)
                .FirstOrDefault();

            PatternRecord record;

            if (existing != null)
            {
                record = existing.Pattern;
                record.Name = component.Name;
                record.Description = DescribeGeometry(geometry, component);
                record.Vector = vector;
                ApplyDimensions(record, component);
                record.IsUserConfirmed = record.IsUserConfirmed || userConfirmed;
                record.UseCount++;
            }
            else
            {
                record = new PatternRecord
                {
                    ComponentType = component.ComponentType,
                    Name = component.Name,
                    Description = DescribeGeometry(geometry, component),
                    Vector = vector,
                    IsSeed = false,
                    IsUserConfirmed = userConfirmed,
                    UseCount = 1
                };
                ApplyDimensions(record, component);
                _patterns.Add(record);
                TrimToLimit();
            }

            Save();
            return Clone(record);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var changed = false;
        var path = ResolvePath(_options.VectorStorePath);

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<PatternRecord>>(json, JsonOptions);

                if (loaded != null)
                {
                    var valid = loaded
                        .Where(p => p.Vector != null && p.Vector.Length == _embedder.Dimensions)
                        .ToList();

                    _patterns.AddRange(valid);

                    var dropped = loaded.Count - valid.Count;
                    if (dropped > 0)
                    {
                        _logger.LogWarning(
                            "Dropped {Count} stored patterns because their vector size does not match {Dimensions}.",
                            dropped,
                            _embedder.Dimensions);
                        changed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern snapshot could not be read from {Path}. Starting with an empty memory.", path);
                changed = true;
            }
        }

        if (_options.SeedBuiltInPatterns && EnsureSeeds())
        {
            changed = true;
        }

        if (changed)
        {
            Save();
        }

        _logger.LogInformation("Pattern memory ready with {Count} patterns.", _patterns.Count);
    }

    private bool EnsureSeeds()
    {
        var added = false;

        foreach (var seed in CreateSeedDefinitions())
        {
            var id = "seed-" + seed.Type.ToString().ToLowerInvariant();
            if (_patterns.Any(p => p.IsSeed && p.Id == id))
            {
                continue;
            }

            var geometry = seed.Build();
            var record = new PatternRecord
            {
                Id = id,
                ComponentType = seed.Type,
                Name = seed.Name,
                Description = seed.Description,
                Vector = _embedder.Embed(geometry),
                Height = seed.Height,
                Width = seed.Width,
                Diameter = seed.Diameter,
                TopDiameter = seed.TopDiameter,
                BottomDiameter = seed.BottomDiameter,
                Angle = seed.Angle,
                IsSeed = true,
                IsUserConfirmed = true,
                UseCount = 0
            };

            _patterns.Add(record);
            added = true;
        }

        return added;
    }

    private void Save()
    {
        try
        {
            var path = ResolvePath(_options.VectorStorePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_patterns, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pattern snapshot could not be saved.");
        }
    }

    private void TrimToLimit()
    {
        while (_patterns.Count > MaxPatterns)
        {
            var oldest = _patterns
                .Where(p => !p.IsSeed)
                .OrderBy(p => p.UseCount)
                .ThenBy(p => p.CreatedAtUtc)
                .FirstOrDefault();

            if (oldest == null)
            {
                return;
            }

            _patterns.Remove(oldest);
        }
    }

    private static void ApplyDimensions(PatternRecord record, ExtractedComponent component)
    {
        record.Height = component.Height;
        record.Width = component.Width;
        record.Diameter = component.Diameter;
        record.TopDiameter = component.TopDiameter;
        record.BottomDiameter = component.BottomDiameter;
        record.Angle = component.Angle;
    }

    private static string DescribeGeometry(ExtractedGeometry geometry, ExtractedComponent component)
    {
        var texts = geometry.Texts
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5);

        var textPart = string.Join("; ", texts);

        return $"{component.ComponentType} '{component.Name}' learned from a confirmed drawing with "
            + $"{geometry.Lines.Count} lines, {geometry.Arcs.Count} arcs, {geometry.Circles.Count} circles"
            + (textPart.Length > 0 ? $"; texts: {textPart}" : string.Empty);
    }

    private static PatternRecord Clone(PatternRecord source)
    {
        return new PatternRecord
        {
            Id = source.Id,
            ComponentType = source.ComponentType,
            Name = source.Name,
            Description = source.Description,
            Vector = source.Vector,
            Height = source.Height,
            Width = source.Width,
            Diameter = source.Diameter,
            TopDiameter = source.TopDiameter,
            BottomDiameter = source.BottomDiameter,
            Angle = source.Angle,
            IsSeed = source.IsSeed,
            IsUserConfirmed = source.IsUserConfirmed,
            UseCount = source.UseCount,
            CreatedAtUtc = source.CreatedAtUtc
        };
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    private sealed class SeedDefinition
    {
        public string Name { get; init; } = string.Empty;
        public ComponentType Type { get; init; }
        public string Description { get; init; } = string.Empty;
        public Func<ExtractedGeometry> Build { get; init; } = () => new ExtractedGeometry();
        public decimal? Height { get; init; }
        public decimal? Width { get; init; }
        public decimal? Diameter { get; init; }
        public decimal? TopDiameter { get; init; }
        public decimal? BottomDiameter { get; init; }
        public decimal? Angle { get; init; }
    }

    private static List<SeedDefinition> CreateSeedDefinitions()
    {
        return new List<SeedDefinition>
        {
            new()
            {
                Name = "Cyclone separator",
                Type = ComponentType.Cyclone,
                Description = "Side view of a cyclone: vertical cylindrical body, conical bottom, gas outlet pipe on top, side inlet duct, small dust outlet.",
                Build = BuildCyclone,
                Height = 3000, Diameter = 1000, TopDiameter = 1000, BottomDiameter = 200
            },
            new()
            {
                Name = "Axial fan",
                Type = ComponentType.AxialFan,
                Description = "Front view of an axial fan: concentric casing circles, central hub, radial blades, square mounting flange.",
                Build = BuildAxialFan,
                Diameter = 1000
            },
            new()
            {
                Name = "Centrifugal fan",
                Type = ComponentType.CentrifugalFan,
                Description = "Side view of a centrifugal fan: circular scroll casing, central inlet eye, hub, curved blades around the impeller rim, tangential rectangular outlet.",
                Build = BuildCentrifugalFan,
                Diameter = 1000
            },
            new()
            {
                Name = "Rectangular duct",
                Type = ComponentType.Duct,
                Description = "Long rectangular duct run with end flanges, a joint line and a centre line.",
                Build = BuildDuct,
                Width = 400, Height = 300
            },
            new()
            {
                Name = "Door",
                Type = ComponentType.Door,
                Description = "Door elevation: outer frame, inner panel, swing arc and handle.",
                Build = BuildDoor,
                Width = 900, Height = 2100
            },
            new()
            {
                Name = "Window",
                Type = ComponentType.Window,
                Description = "Window elevation: outer frame, inner sash and a cross mullion.",
                Build = BuildWindow,
                Width = 1200, Height = 1200
            },
            new()
            {
                Name = "Cylinder",
                Type = ComponentType.Cylinder,
                Description = "Cylinder side view: rectangle with rounded end arcs and a centre line.",
                Build = BuildCylinder,
                Height = 1500, Diameter = 500
            },
            new()
            {
                Name = "Cone",
                Type = ComponentType.Cone,
                Description = "Cone or frustum side view: trapezoid with a wide top, narrow bottom and a centre line.",
                Build = BuildCone,
                Height = 1000, TopDiameter = 1000, BottomDiameter = 300
            }
        };
    }

    private static ExtractedGeometry BuildCyclone()
    {
        var g = new ExtractedGeometry();

        Line(g, -500, 3000, 500, 3000);
        Line(g, -500, 1500, -500, 3000);
        Line(g, 500, 1500, 500, 3000);
        Line(g, -500, 1500, -100, 0);
        Line(g, 500, 1500, 100, 0);
        Line(g, -100, 0, -100, -300);
        Line(g, 100, 0, 100, -300);
        Line(g, -100, -300, 100, -300);
        Rect(g, -200, 3000, 400, 500);
        Line(g, -1000, 2800, -500, 2800);
        Line(g, -1000, 2300, -500, 2300);
        Line(g, -1000, 2300, -1000, 2800);
        Line(g, -550, 1500, 550, 1500);
        Line(g, 0, -300, 0, 3500, "CENTER");

        Text(g, "CYCLONE", 0, 2000, 100);
        return g;
    }

    private static ExtractedGeometry BuildAxialFan()
    {
        var g = new ExtractedGeometry();

        Circle(g, 0, 0, 500);
        Circle(g, 0, 0, 520);
        Circle(g, 0, 0, 100);
        Rect(g, -600, -600, 1200, 1200);

        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4.0;
            var b = a + 10.0 * Math.PI / 180.0;
            Line(g, 100 * Math.Cos(a), 100 * Math.Sin(a), 480 * Math.Cos(b), 480 * Math.Sin(b));
        }

        Text(g, "AXIAL FAN", 0, -700, 80);
        return g;
    }

    private static ExtractedGeometry BuildCentrifugalFan()
    {
        var g = new ExtractedGeometry();

        Circle(g, 0, 0, 500);
        Circle(g, 0, 0, 350);
        Circle(g, 0, 0, 200);
        Circle(g, 0, 0, 60);
        Rect(g, 500, 0, 450, 500);

        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6.0;
            var b = a + 15.0 * Math.PI / 180.0;
            Line(g, 200 * Math.Cos(a), 200 * Math.Sin(a), 350 * Math.Cos(b), 350 * Math.Sin(b));
        }

        Text(g, "CENTRIFUGAL FAN", 0, -650, 80);
        return g;
    }

    private static ExtractedGeometry BuildDuct()
    {
        var g = new ExtractedGeometry();

        Rect(g, 0, 0, 3000, 300);
        Line(g, 100, 0, 100, 300);
        Line(g, 2900, 0, 2900, 300);
        Rect(g, -20, -30, 20, 360);
        Rect(g, 3000, -30, 20, 360);
        Line(g, 1500, 0, 1500, 300);
        Line(g, -100, 150, 3100, 150, "CENTER");

        Text(g, "DUCT 400X300", 1500, 400, 60);
        return g;
    }

    private static ExtractedGeometry BuildDoor()
    {
        var g = new ExtractedGeometry();

        Rect(g, 0, 0, 900, 2100);
        Rect(g, 50, 50, 800, 2000);
        Line(g, 0, 0, 0, 2100);
        Arc(g, 0, 0, 900, 0, 90);
        Circle(g, 800, 1000, 25);

        Text(g, "DOOR", 450, 2200, 80);
        return g;
    }

    private static ExtractedGeometry BuildWindow()
    {
        var g = new ExtractedGeometry();

        Rect(g, 0, 0, 1200, 1200);
        Rect(g, 40, 40, 1120, 1120);
        Line(g, 600, 40, 600, 1160);
        Line(g, 40, 600, 1160, 600);

        Text(g, "WINDOW", 600, 1300, 80);
        return g;
    }

    private static ExtractedGeometry BuildCylinder()
    {
        var g = new ExtractedGeometry();

        Rect(g, 0, 0, 500, 1500);
        Arc(g, 250, 1500, 250, 0, 180);
        Arc(g, 250, 0, 250, 180, 360);
        Line(g, 250, -300, 250, 1800, "CENTER");

        Text(g, "CYLINDER", 250, 750, 60);
        return g;
    }

    private static ExtractedGeometry BuildCone()
    {
        var g = new ExtractedGeometry();

        Line(g, 0, 1000, 1000, 1000);
        Line(g, 350, 0, 650, 0);
        Line(g, 0, 1000, 350, 0);
        Line(g, 1000, 1000, 650, 0);
        Line(g, 500, -200, 500, 1200, "CENTER");

        Text(g, "CONE", 500, 500, 60);
        return g;
    }

    private static void Line(ExtractedGeometry g, double x1, double y1, double x2, double y2, string lineType = "CONTINUOUS")
    {
        g.Lines.Add(new GeometryLine
        {
            Start = new GeometryPoint(x1, y1),
            End = new GeometryPoint(x2, y2),
            Layer = "0",
            LineType = lineType
        });
    }

    private static void Rect(ExtractedGeometry g, double x, double y, double w, double h)
    {
        Line(g, x, y, x + w, y);
        Line(g, x + w, y, x + w, y + h);
        Line(g, x + w, y + h, x, y + h);
        Line(g, x, y + h, x, y);
    }

    private static void Circle(ExtractedGeometry g, double cx, double cy, double r)
    {
        g.Circles.Add(new GeometryCircle
        {
            Center = new GeometryPoint(cx, cy),
            Radius = r,
            Layer = "0",
            LineType = "CONTINUOUS"
        });
    }

    private static void Arc(ExtractedGeometry g, double cx, double cy, double r, double start, double end)
    {
        g.Arcs.Add(new GeometryArc
        {
            Center = new GeometryPoint(cx, cy),
            Radius = r,
            StartAngle = start,
            EndAngle = end,
            Layer = "0",
            LineType = "CONTINUOUS"
        });
    }

    private static void Text(ExtractedGeometry g, string text, double x, double y, double height)
    {
        g.Texts.Add(new GeometryText
        {
            Text = text,
            Position = new GeometryPoint(x, y),
            Height = height,
            Layer = "0"
        });
    }
}