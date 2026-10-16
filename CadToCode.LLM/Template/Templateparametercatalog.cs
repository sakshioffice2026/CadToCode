using System.Globalization;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;

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

/// <summary>
/// Result of checking a calculator mapping against a catalog template schema.
/// Valid only when every template parameter is covered, none is unknown, and all values pass range and cross checks.
/// </summary>
public sealed class TemplateMappingValidation
{
    public string? TemplateKey { get; init; }

    /// <summary>Mapped values in template units, keyed by template parameter.</summary>
    public IReadOnlyDictionary<string, double> Values { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>Template parameters the mapping did not provide. Never filled from defaults.</summary>
    public IReadOnlyList<string> UncoveredKeys { get; init; } = Array.Empty<string>();

    /// <summary>Mapped keys the template does not define.</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = Array.Empty<string>();

    public bool IsValid => Errors.Count == 0 && UncoveredKeys.Count == 0 && UnknownKeys.Count == 0;
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
        BuildWetScrubberTankPump(),
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

    /// <summary>
    /// Finds the catalog schema an equipment manifest renders through. When <paramref name="templateKey"/> is given it must
    /// be listed by the manifest; otherwise the first manifest template key present in the catalog is used.
    /// Returns null when the manifest has no catalog template (for example the axial fan, which is composed from SI values).
    /// </summary>
    public static TemplateParameterSchema? FindForManifest(EquipmentManifest manifest, string? templateKey = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.IsNullOrWhiteSpace(templateKey))
        {
            var listed = manifest.TemplateKeys.Any(k => string.Equals(k, templateKey.Trim(), StringComparison.OrdinalIgnoreCase));

            return listed ? Find(templateKey) : null;
        }

        foreach (var key in manifest.TemplateKeys)
        {
            var schema = Find(key);

            if (schema is not null)
            {
                return schema;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks a mapper result against the template it targets: unit match, full parameter coverage, known keys only,
    /// parameter ranges and template cross-checks. Missing parameters are reported, never filled from defaults.
    /// </summary>
    public static TemplateMappingValidation ValidateMapped(TemplateMappingResult mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (!mapping.Succeeded)
        {
            return new TemplateMappingValidation
            {
                TemplateKey = mapping.TemplateKey,
                Errors = mapping.GetBlockers()
            };
        }

        if (string.IsNullOrWhiteSpace(mapping.TemplateKey))
        {
            return new TemplateMappingValidation
            {
                Errors = new[] { "The mapping does not target a catalog template." }
            };
        }

        var schema = Find(mapping.TemplateKey);

        if (schema is null)
        {
            return new TemplateMappingValidation
            {
                TemplateKey = mapping.TemplateKey,
                Errors = new[] { "Template '" + mapping.TemplateKey + "' is not in the catalog." }
            };
        }

        var errors = new List<string>();
        var known = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();

        foreach (var mapped in mapping.Parameters)
        {
            var def = schema.FindParameter(mapped.Key);

            if (def is null)
            {
                unknown.Add(mapped.Key);
                continue;
            }

            if (!UnitMatches(def, mapped.Unit))
            {
                errors.Add(
                    def.Label + " was mapped in '" + mapped.Unit + "' but the template expects '" + def.Unit + "'.");
                continue;
            }

            known[def.Key] = mapped.Value;
        }

        var uncovered = schema.Parameters
            .Where(p => !known.ContainsKey(p.Key))
            .Select(p => p.Key)
            .ToList();

        if (errors.Count == 0)
        {
            if (uncovered.Count == 0 && unknown.Count == 0)
            {
                errors.AddRange(ValidateSet(schema, known));
            }
            else
            {
                foreach (var pair in known)
                {
                    var error = ValidateValue(schema, pair.Key, pair.Value);

                    if (error is not null)
                    {
                        errors.Add(error);
                    }
                }
            }
        }

        return new TemplateMappingValidation
        {
            TemplateKey = schema.TemplateKey,
            Values = known,
            Errors = errors,
            UncoveredKeys = uncovered,
            UnknownKeys = unknown
        };
    }

    private static bool UnitMatches(TemplateParameterDefinition def, string mappedUnit)
    {
        if (string.Equals(def.Unit, mappedUnit, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(def.Unit, "pcs", StringComparison.OrdinalIgnoreCase) && mappedUnit == "-";
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

            case "builtin-wet-scrubber-tank-pump":
                if (V("Wall") * 2.0 >= V("Radius"))
                {
                    errors.Add("Wall is too thick for the vessel Radius.");
                }
                if (V("TankWall") * 2.0 >= Math.Min(V("TankLength"), V("TankWidth")))
                {
                    errors.Add("TankWall is too thick for the tank footprint.");
                }
                if (V("TankWall") >= V("TankHeight"))
                {
                    errors.Add("TankWall must be smaller than TankHeight.");
                }
                if (V("TankLength") <= V("Radius") * 2.0 || V("TankWidth") <= V("Radius") * 2.0)
                {
                    errors.Add("TankLength and TankWidth must exceed the vessel diameter.");
                }
                if (V("StackDiameter") >= V("Radius") * 2.0)
                {
                    errors.Add("StackDiameter must be smaller than the vessel diameter.");
                }
                if (V("Wall") * 2.0 >= V("StackDiameter"))
                {
                    errors.Add("Wall is too thick for StackDiameter.");
                }
                if (V("Wall") * 2.0 >= V("InletDiameter"))
                {
                    errors.Add("Wall is too thick for InletDiameter.");
                }
                if (V("InletHeight") + V("InletDiameter") / 2.0 >= V("ShellHeight"))
                {
                    errors.Add("InletHeight plus half of InletDiameter must stay below the top of the shell (ShellHeight).");
                }
                if (V("PackingOffset") + V("PackingHeight") + V("SprayGap") >= V("ShellHeight"))
                {
                    errors.Add("PackingOffset + PackingHeight + SprayGap must be smaller than ShellHeight.");
                }
                if (V("NozzleLength") > V("SprayGap"))
                {
                    errors.Add("NozzleLength must not exceed SprayGap.");
                }
                if (V("SprayWall") * 2.0 >= V("SprayDiameter"))
                {
                    errors.Add("SprayWall is too thick for SprayDiameter.");
                }
                if (V("SprayWall") * 2.0 >= V("DischargeDiameter"))
                {
                    errors.Add("SprayWall is too thick for DischargeDiameter.");
                }
                if (V("TankWall") * 2.0 >= V("DrainDiameter"))
                {
                    errors.Add("TankWall is too thick for DrainDiameter.");
                }
                if (V("DrainDiameter") >= V("TankWidth"))
                {
                    errors.Add("DrainDiameter must be smaller than TankWidth.");
                }
                if (V("TankWall") * 2.0 >= V("SuctionDiameter"))
                {
                    errors.Add("TankWall is too thick for SuctionDiameter.");
                }
                if (V("SuctionDiameter") >= V("PumpHeight") || V("DischargeDiameter") >= V("PumpHeight"))
                {
                    errors.Add("SuctionDiameter and DischargeDiameter must be smaller than PumpHeight.");
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

    private static TemplateParameterSchema BuildWetScrubberTankPump()
    {
        return new TemplateParameterSchema
        {
            TemplateKey = "builtin-wet-scrubber-tank-pump",
            Title = "Wet scrubber with tank, pump and spray header",
            Parameters = new[]
            {
                P("Radius", "Vessel outer radius", 600, 50, 10000, aliases: new[] { "vessel radius", "shell radius", "r" }),
                P("Wall", "Shell wall thickness", 8, 1, 100, aliases: new[] { "thickness", "shell wall", "wall thk" }),
                P("ShellHeight", "Cylindrical shell height", 2400, 100, 50000, aliases: new[] { "shell height", "straight height", "cylinder height", "column height" }),
                P("TankLength", "Recirculation tank length", 2000, 200, 50000, aliases: new[] { "tank length", "sump length" }),
                P("TankWidth", "Recirculation tank width", 1400, 200, 50000, aliases: new[] { "tank width", "sump width" }),
                P("TankHeight", "Recirculation tank height", 800, 100, 20000, aliases: new[] { "tank height", "sump height" }),
                P("TankWall", "Tank wall thickness", 8, 1, 100, aliases: new[] { "tank wall", "tank thickness" }),
                P("PackingHeight", "Packing bed height", 600, 50, 20000, aliases: new[] { "packing height", "bed height" }),
                P("PackingOffset", "Packing bed offset above tank top", 700, 50, 20000, aliases: new[] { "packing offset", "bed offset" }),
                P("SprayGap", "Gap between packing top and spray header", 200, 20, 5000, aliases: new[] { "spray gap", "header gap" }),
                P("SprayDiameter", "Spray header diameter", 80, 10, 1000, aliases: new[] { "spray dia", "spray header diameter", "header diameter" }),
                P("SprayWall", "Spray header wall thickness", 4, 0.5, 50, aliases: new[] { "spray wall", "header wall" }),
                P("NozzleLength", "Spray nozzle length", 150, 10, 2000, aliases: new[] { "nozzle length", "spray nozzle length" }),
                P("FeedLength", "Spray header feed length beyond shell", 400, 10, 20000, aliases: new[] { "feed length", "header feed length" }),
                P("StackDiameter", "Gas outlet stack diameter", 300, 20, 5000, aliases: new[] { "stack dia", "outlet diameter", "stack od", "stack" }),
                P("StackHeight", "Gas outlet stack height", 400, 10, 20000, aliases: new[] { "stack height", "outlet height" }),
                P("InletDiameter", "Side inlet diameter", 250, 20, 5000, aliases: new[] { "inlet dia", "inlet od", "inlet" }),
                P("InletLength", "Side inlet length", 400, 10, 20000, aliases: new[] { "inlet length" }),
                P("InletHeight", "Side inlet elevation above tank top", 250, 10, 20000, aliases: new[] { "inlet height", "inlet elevation", "inlet offset" }),
                P("DrainDiameter", "Drain pipe diameter", 100, 10, 2000, aliases: new[] { "drain dia", "drain od", "drain" }),
                P("DrainLength", "Drain pipe length", 250, 10, 10000, aliases: new[] { "drain length" }),
                P("PumpRadius", "Pump casing radius", 120, 20, 2000, aliases: new[] { "pump radius", "pump casing radius" }),
                P("PumpHeight", "Pump casing height", 250, 50, 5000, aliases: new[] { "pump height", "pump casing height" }),
                P("PumpGap", "Gap between tank and pump", 200, 10, 5000, aliases: new[] { "pump gap", "pump spacing" }),
                P("MotorRadius", "Motor radius", 100, 20, 2000, aliases: new[] { "motor radius" }),
                P("MotorHeight", "Motor height", 300, 50, 5000, aliases: new[] { "motor height" }),
                P("SuctionDiameter", "Pump suction pipe diameter", 100, 10, 1000, aliases: new[] { "suction dia", "suction diameter", "suction" }),
                P("DischargeDiameter", "Pump discharge pipe diameter", 80, 10, 1000, aliases: new[] { "discharge dia", "discharge diameter", "discharge" }),
                P("RiserOffset", "Discharge riser offset from pump", 150, 10, 5000, aliases: new[] { "riser offset" })
            },
            Components = new[]
            {
                C("tank", "Recirculation tank", new[] { "tank", "recirculation tank", "sump", "basin" },
                    new[] { "TankFloor", "TankWalls", "TankRoof" },
                    new[] { "Radius", "Wall", "TankLength", "TankWidth", "TankHeight", "TankWall" }),
                C("shell", "Vessel shell", new[] { "shell", "vessel", "vessel shell", "column", "tower", "body" },
                    new[] { "Shell" },
                    new[] { "Radius", "Wall", "ShellHeight", "TankHeight", "TankWall" }),
                C("packing", "Packing bed", new[] { "packing", "packing bed", "bed" },
                    new[] { "PackingBed" },
                    new[] { "Radius", "Wall", "PackingHeight", "PackingOffset", "TankHeight" }),
                C("cover", "Top cover", new[] { "cover", "top cover", "lid", "top plate" },
                    new[] { "TopCover" },
                    new[] { "Radius", "Wall", "ShellHeight", "TankHeight", "StackDiameter" }),
                C("stack", "Gas outlet stack", new[] { "stack", "outlet", "gas outlet", "chimney" },
                    new[] { "Stack" },
                    new[] { "Wall", "ShellHeight", "TankHeight", "StackDiameter", "StackHeight" }),
                C("inlet", "Side inlet", new[] { "inlet", "side inlet", "gas inlet", "inlet nozzle" },
                    new[] { "Inlet" },
                    new[] { "Radius", "Wall", "TankHeight", "InletDiameter", "InletLength", "InletHeight" }),
                C("drain", "Drain pipe", new[] { "drain", "drain pipe", "bottom outlet" },
                    new[] { "Drain" },
                    new[] { "TankLength", "TankWall", "DrainDiameter", "DrainLength" }),
                C("spray", "Spray header and nozzles", new[] { "spray", "spray header", "spray nozzle", "spray nozzles", "header" },
                    new[] { "SprayHeader", "SprayNozzleA", "SprayNozzleB" },
                    new[] { "Radius", "Wall", "TankHeight", "PackingOffset", "PackingHeight", "SprayGap", "SprayDiameter", "SprayWall", "NozzleLength", "FeedLength" }),
                C("pump", "Pump and motor", new[] { "pump", "motor", "pump and motor" },
                    new[] { "PumpCasing", "PumpMotor" },
                    new[] { "TankLength", "PumpGap", "PumpRadius", "PumpHeight", "MotorRadius", "MotorHeight" }),
                C("piping", "Pump piping", new[] { "piping", "suction", "discharge", "pump piping", "riser" },
                    new[] { "Suction", "DischargeRun", "DischargeRiser", "DischargeFeed" },
                    new[] { "Radius", "TankHeight", "TankLength", "TankWall", "PackingOffset", "PackingHeight", "SprayGap", "SprayDiameter", "SprayWall", "FeedLength", "PumpGap", "PumpRadius", "PumpHeight", "RiserOffset", "SuctionDiameter", "DischargeDiameter" })
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