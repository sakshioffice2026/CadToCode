using System.Globalization;

namespace CadToCode.LLM.Templates;

public enum ParameterSource
{
    Default,
    User,
    DxfVerified,
    ImageUnverified
}

public sealed class TemplateParameterDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Unit { get; init; } = "mm";
    public double Default { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public bool IsInteger { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
}

public sealed class TemplateComponentDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public string[] ScriptNames { get; init; } = Array.Empty<string>();
    public string[] ParameterKeys { get; init; } = Array.Empty<string>();
}

public sealed class TemplateParameterSchema
{
    public string TemplateKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<TemplateParameterDefinition> Parameters { get; init; } = Array.Empty<TemplateParameterDefinition>();
    public IReadOnlyList<TemplateComponentDefinition> Components { get; init; } = Array.Empty<TemplateComponentDefinition>();

    public TemplateParameterDefinition? FindParameter(string key)
    {
        return Parameters.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public TemplateComponentDefinition? FindComponent(string key)
    {
        return Components.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<TemplateParameterDefinition> ParametersFor(TemplateComponentDefinition component)
    {
        return Parameters
            .Where(p => component.ParameterKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public Dictionary<string, double> BuildDefaults()
    {
        return Parameters.ToDictionary(p => p.Key, p => p.Default, StringComparer.OrdinalIgnoreCase);
    }
}

public static class TemplateParameterCatalog
{
    public static IReadOnlyList<TemplateParameterSchema> All { get; } = new List<TemplateParameterSchema>
    {
        BuildStraightPipe(),
        BuildPipeElbow(),
        BuildPipeTee(),
        BuildFlangePlate(),
        BuildWetScrubber(),
        BuildCycloneSeparator()
    };

    public static TemplateParameterSchema? Find(string? templateKey)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            return null;
        }

        return All.FirstOrDefault(s => string.Equals(s.TemplateKey, templateKey, StringComparison.OrdinalIgnoreCase));
    }

    public static string? ValidateValue(TemplateParameterSchema schema, string key, double value)
    {
        var def = schema.FindParameter(key);

        if (def is null)
        {
            return "Unknown parameter '" + key + "' for " + schema.Title + ".";
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return def.Label + " must be a finite number.";
        }

        if (def.IsInteger && Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            return def.Label + " must be a whole number.";
        }

        if (value < def.Min || value > def.Max)
        {
            return def.Label + " must be between " + Format(def.Min) + " and " + Format(def.Max) + " " + def.Unit + ".";
        }

        return null;
    }

    public static IReadOnlyList<string> ValidateSet(TemplateParameterSchema schema, IReadOnlyDictionary<string, double> values)
    {
        var errors = new List<string>();

        foreach (var pair in values)
        {
            var error = ValidateValue(schema, pair.Key, pair.Value);

            if (error is not null)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        double V(string key) => values.TryGetValue(key, out var v) ? v : schema.FindParameter(key)?.Default ?? 0;

        switch (schema.TemplateKey)
        {
            case "builtin-straight-pipe":
                RequireWall(errors, V("Wall"), V("OuterDiameter"), "Wall", "OuterDiameter");
                break;

            case "builtin-pipe-elbow":
                RequireWall(errors, V("Wall"), V("OuterDiameter"), "Wall", "OuterDiameter");
                if (V("BendRadius") < V("OuterDiameter") / 2.0)
                {
                    errors.Add("BendRadius must be at least half of OuterDiameter.");
                }
                break;

            case "builtin-pipe-tee":
                RequireWall(errors, V("RunWall"), V("RunDiameter"), "RunWall", "RunDiameter");
                RequireWall(errors, V("BranchWall"), V("BranchDiameter"), "BranchWall", "BranchDiameter");
                if (V("BranchDiameter") > V("RunDiameter"))
                {
                    errors.Add("BranchDiameter must not exceed RunDiameter.");
                }
                break;

            case "builtin-flange-plate":
                if (V("BoreDiameter") >= V("FlangeDiameter"))
                {
                    errors.Add("BoreDiameter must be smaller than FlangeDiameter.");
                }
                if (V("BoltCircleDiameter") <= V("BoreDiameter") + V("BoltHoleDiameter"))
                {
                    errors.Add("BoltCircleDiameter must clear the bore plus one bolt hole.");
                }
                if (V("BoltCircleDiameter") + V("BoltHoleDiameter") >= V("FlangeDiameter"))
                {
                    errors.Add("Bolt holes must fit inside FlangeDiameter.");
                }
                break;

            case "builtin-wet-scrubber":
                if (V("Wall") * 2.0 >= V("Radius"))
                {
                    errors.Add("Wall is too thick for the vessel Radius.");
                }
                if (V("DrainDiameter") >= V("Radius") * 2.0)
                {
                    errors.Add("DrainDiameter must be smaller than the vessel diameter.");
                }
                if (V("StackDiameter") >= V("Radius") * 2.0)
                {
                    errors.Add("StackDiameter must be smaller than the vessel diameter.");
                }
                if (V("InletZ") <= V("ConeHeight") || V("InletZ") >= V("ConeHeight") + V("ShellHeight"))
                {
                    errors.Add("InletZ must lie on the cylindrical shell (between ConeHeight and ConeHeight + ShellHeight).");
                }
                break;

            case "builtin-cyclone-separator":
                if (V("Wall") * 2.0 >= V("Radius"))
                {
                    errors.Add("Wall is too thick for the cyclone Radius.");
                }
                if (V("ExitDiameter") >= V("Radius") * 2.0)
                {
                    errors.Add("ExitDiameter must be smaller than the cyclone diameter.");
                }
                if (V("DustDiameter") >= V("Radius") * 2.0)
                {
                    errors.Add("DustDiameter must be smaller than the cyclone diameter.");
                }
                if (V("FinderDepth") >= V("BodyHeight") + V("ConeHeight"))
                {
                    errors.Add("FinderDepth must be shorter than the overall cyclone height.");
                }
                if (V("InletDiameter") >= V("BodyHeight"))
                {
                    errors.Add("InletDiameter must be smaller than BodyHeight.");
                }
                break;
        }

        return errors;
    }

    public static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void RequireWall(List<string> errors, double wall, double diameter, string wallKey, string diameterKey)
    {
        if (wall * 2.0 >= diameter)
        {
            errors.Add(wallKey + " is too thick for " + diameterKey + ".");
        }
    }

    private static TemplateParameterDefinition P(
        string key, string label, double def, double min, double max,
        string unit = "mm", bool isInteger = false, params string[] aliases)
    {
        return new TemplateParameterDefinition
        {
            Key = key,
            Label = label,
            Default = def,
            Min = min,
            Max = max,
            Unit = unit,
            IsInteger = isInteger,
            Aliases = aliases
        };
    }

    private static TemplateComponentDefinition C(
        string key, string title, string[] aliases, string[] scriptNames, string[] parameterKeys)
    {
        return new TemplateComponentDefinition
        {
            Key = key,
            Title = title,
            Aliases = aliases,
            ScriptNames = scriptNames,
            ParameterKeys = parameterKeys
        };
    }

    private static TemplateParameterSchema BuildStraightPipe()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-straight-pipe",
            Title = "Straight pipe",
            Parameters = new[]
            {
                P("OuterDiameter", "Outer diameter", 100, 5, 5000, aliases: new[] { "od", "outer dia", "diameter", "dia" }),
                P("Wall", "Wall thickness", 5, 0.5, 200, aliases: new[] { "thickness", "wall thk" }),
                P("Length", "Length", 1000, 10, 100000, aliases: new[] { "long", "run length" })
            },
            Components = new[]
            {
                C("pipe", "Pipe", new[] { "pipe", "tube", "duct" }, new[] { "Pipe" }, new[] { "OuterDiameter", "Wall", "Length" })
            }
        };
    }

    private static TemplateParameterSchema BuildPipeElbow()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-pipe-elbow",
            Title = "Pipe elbow",
            Parameters = new[]
            {
                P("OuterDiameter", "Outer diameter", 100, 5, 5000, aliases: new[] { "od", "outer dia", "diameter", "dia" }),
                P("Wall", "Wall thickness", 5, 0.5, 200, aliases: new[] { "thickness", "wall thk" }),
                P("LegLength", "Straight leg length", 200, 10, 50000, aliases: new[] { "leg", "leg length", "straight length" }),
                P("BendRadius", "Bend radius", 150, 10, 20000, aliases: new[] { "radius", "bend r", "centerline radius" }),
                P("BendAngle", "Bend angle", 90, 1, 180, "deg", false, "angle", "bend")
            },
            Components = new[]
            {
                C("elbow", "Elbow", new[] { "elbow", "bend", "pipe" }, new[] { "Elbow" },
                    new[] { "OuterDiameter", "Wall", "LegLength", "BendRadius", "BendAngle" })
            }
        };
    }

    private static TemplateParameterSchema BuildPipeTee()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-pipe-tee",
            Title = "Pipe tee",
            Parameters = new[]
            {
                P("RunDiameter", "Run outer diameter", 150, 5, 5000, aliases: new[] { "run od", "main diameter", "header diameter" }),
                P("RunWall", "Run wall thickness", 6, 0.5, 200, aliases: new[] { "run thickness", "main wall" }),
                P("RunLength", "Run length", 600, 10, 100000, aliases: new[] { "main length", "header length" }),
                P("BranchDiameter", "Branch outer diameter", 100, 5, 5000, aliases: new[] { "branch od", "branch dia" }),
                P("BranchWall", "Branch wall thickness", 5, 0.5, 200, aliases: new[] { "branch thickness" }),
                P("BranchLength", "Branch length beyond run", 300, 10, 50000, aliases: new[] { "branch length", "branch leg" })
            },
            Components = new[]
            {
                C("run", "Run pipe", new[] { "run", "main", "header", "main pipe" }, new[] { "Run" },
                    new[] { "RunDiameter", "RunWall", "RunLength" }),
                C("branch", "Branch pipe", new[] { "branch", "tee branch", "side pipe" }, new[] { "Branch" },
                    new[] { "RunDiameter", "RunLength", "BranchDiameter", "BranchWall", "BranchLength" })
            }
        };
    }

    private static TemplateParameterSchema BuildFlangePlate()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-flange-plate",
            Title = "Flange with bolt circle and neck",
            Parameters = new[]
            {
                P("FlangeDiameter", "Flange outer diameter", 220, 20, 5000, aliases: new[] { "flange od", "outer diameter", "flange dia" }),
                P("FlangeThickness", "Flange thickness", 20, 1, 300, aliases: new[] { "plate thickness", "flange thk" }),
                P("BoreDiameter", "Bore diameter", 100, 5, 4900, aliases: new[] { "bore", "inner diameter", "id" }),
                P("BoltCount", "Number of bolts", 8, 2, 64, "pcs", true, "bolts", "bolt count", "holes"),
                P("BoltCircleDiameter", "Bolt circle diameter", 180, 10, 4950, aliases: new[] { "pcd", "bcd", "bolt circle" }),
                P("BoltHoleDiameter", "Bolt hole diameter", 18, 2, 200, aliases: new[] { "bolt hole", "hole diameter", "hole dia" }),
                P("NeckWall", "Neck wall thickness", 6, 0.5, 200, aliases: new[] { "neck thickness", "neck wall" }),
                P("NeckLength", "Neck length", 80, 1, 2000, aliases: new[] { "neck", "hub length" })
            },
            Components = new[]
            {
                C("flange", "Flange plate with bolt holes", new[] { "flange", "plate", "flange plate", "ring" }, new[] { "Flange" },
                    new[] { "FlangeDiameter", "FlangeThickness", "BoreDiameter", "BoltCount", "BoltCircleDiameter", "BoltHoleDiameter" }),
                C("neck", "Pipe neck", new[] { "neck", "hub", "pipe neck" }, new[] { "Neck" },
                    new[] { "FlangeThickness", "BoreDiameter", "NeckWall", "NeckLength" })
            }
        };
    }

    private static TemplateParameterSchema BuildWetScrubber()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-wet-scrubber",
            Title = "Wet scrubber vessel",
            Parameters = new[]
            {
                P("Radius", "Vessel outer radius", 600, 50, 10000, aliases: new[] { "vessel radius", "shell radius", "r" }),
                P("Wall", "Shell wall thickness", 8, 1, 100, aliases: new[] { "thickness", "shell wall", "wall thk" }),
                P("ShellHeight", "Cylindrical shell height", 1800, 100, 50000, aliases: new[] { "shell height", "straight height", "cylinder height" }),
                P("ConeHeight", "Conical bottom height", 500, 50, 20000, aliases: new[] { "cone height", "cone" }),
                P("DrainDiameter", "Drain pipe diameter", 150, 10, 2000, aliases: new[] { "drain dia", "drain od", "drain" }),
                P("DrainLength", "Drain pipe length", 300, 10, 10000, aliases: new[] { "drain length" }),
                P("StackDiameter", "Gas outlet stack diameter", 300, 20, 5000, aliases: new[] { "stack dia", "outlet diameter", "stack od", "stack" }),
                P("StackHeight", "Gas outlet stack height", 400, 10, 20000, aliases: new[] { "stack height", "outlet height" }),
                P("InletDiameter", "Side inlet diameter", 250, 20, 5000, aliases: new[] { "inlet dia", "inlet od", "inlet" }),
                P("InletLength", "Side inlet length", 400, 10, 20000, aliases: new[] { "inlet length" }),
                P("InletZ", "Side inlet elevation", 1500, 50, 70000, aliases: new[] { "inlet height", "inlet elevation", "inlet z" })
            },
            Components = new[]
            {
                C("shell", "Vessel shell with conical bottom", new[] { "shell", "vessel", "vessel shell", "body", "cone", "tank" }, new[] { "Shell" },
                    new[] { "Radius", "Wall", "ShellHeight", "ConeHeight", "DrainDiameter" }),
                C("cover", "Top cover", new[] { "cover", "top cover", "lid", "top plate" }, new[] { "TopCover" },
                    new[] { "Radius", "Wall", "ShellHeight", "ConeHeight", "StackDiameter" }),
                C("stack", "Gas outlet stack", new[] { "stack", "outlet", "gas outlet", "chimney" }, new[] { "Stack" },
                    new[] { "Wall", "ShellHeight", "ConeHeight", "StackDiameter", "StackHeight" }),
                C("drain", "Drain pipe", new[] { "drain", "drain pipe", "bottom outlet" }, new[] { "Drain" },
                    new[] { "Wall", "DrainDiameter", "DrainLength" }),
                C("inlet", "Side inlet", new[] { "inlet", "side inlet", "gas inlet", "nozzle" }, new[] { "Inlet" },
                    new[] { "Radius", "Wall", "InletDiameter", "InletLength", "InletZ" })
            }
        };
    }

    private static TemplateParameterSchema BuildCycloneSeparator()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-cyclone-separator",
            Title = "Cyclone separator",
            Parameters = new[]
            {
                P("Radius", "Cyclone body outer radius", 300, 20, 5000, aliases: new[] { "body radius", "cyclone radius", "r" }),
                P("Wall", "Wall thickness", 6, 1, 100, aliases: new[] { "thickness", "wall thk" }),
                P("BodyHeight", "Cylindrical body height", 600, 50, 20000, aliases: new[] { "body height", "cylinder height", "barrel height" }),
                P("ConeHeight", "Conical hopper height", 900, 50, 20000, aliases: new[] { "cone height", "hopper height" }),
                P("DustDiameter", "Dust outlet diameter", 100, 10, 2000, aliases: new[] { "dust dia", "dust outlet", "apex diameter" }),
                P("DustLength", "Dust outlet length", 150, 10, 10000, aliases: new[] { "dust length" }),
                P("ExitDiameter", "Vortex finder diameter", 150, 10, 5000, aliases: new[] { "exit dia", "finder diameter", "gas exit diameter" }),
                P("ExitHeight", "Vortex finder height above top", 250, 10, 10000, aliases: new[] { "exit height", "finder height" }),
                P("FinderDepth", "Vortex finder insertion depth", 350, 10, 20000, aliases: new[] { "finder depth", "insertion depth" }),
                P("InletDiameter", "Tangential inlet diameter", 120, 10, 2000, aliases: new[] { "inlet dia", "inlet od", "inlet" }),
                P("InletLength", "Tangential inlet length", 300, 10, 10000, aliases: new[] { "inlet length" })
            },
            Components = new[]
            {
                C("body", "Cyclone body and hopper", new[] { "body", "shell", "hopper", "cone", "cyclone body" }, new[] { "Body" },
                    new[] { "Radius", "Wall", "BodyHeight", "ConeHeight", "DustDiameter" }),
                C("topplate", "Top plate", new[] { "top plate", "plate", "cover", "lid" }, new[] { "TopPlate" },
                    new[] { "Radius", "Wall", "BodyHeight", "ConeHeight", "ExitDiameter" }),
                C("vortexfinder", "Vortex finder", new[] { "vortex finder", "finder", "gas exit", "exit pipe" }, new[] { "VortexFinder" },
                    new[] { "Wall", "BodyHeight", "ConeHeight", "ExitDiameter", "ExitHeight", "FinderDepth" }),
                C("dustoutlet", "Dust outlet", new[] { "dust outlet", "dust", "apex", "outlet pipe" }, new[] { "DustOutlet" },
                    new[] { "Wall", "DustDiameter", "DustLength" }),
                C("inlet", "Tangential inlet", new[] { "inlet", "tangential inlet", "feed" }, new[] { "TangentialInlet" },
                    new[] { "Radius", "Wall", "BodyHeight", "ConeHeight", "InletDiameter", "InletLength" })
            }
        };
    }
}