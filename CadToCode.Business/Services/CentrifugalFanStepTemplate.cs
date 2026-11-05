using System.Globalization;
using System.Text.RegularExpressions;

namespace CadToCode.Business.Services;

/// <summary>
/// Default values and dimension parsing for the "template" STEP choice.
/// Lengths are typed in millimetres and angles in degrees; the composer receives metres.
/// Any value that is not typed is derived from the impeller outer diameter,
/// so entering only the impeller diameter already gives a consistent fan.
/// </summary>
public static class CentrifugalFanStepTemplate
{
    public const double DefaultImpellerOuterDiameterM = 0.8;

    private static readonly Regex DefaultWord = new(
        @"^\s*(default|defaults|use default|use defaults|yes|ok|okay|sure)\s*[.!]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Separator = new(@"[,;\r\n]+|\band\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Segment = new(
        @"^\s*(?<name>.+?)\s*[:=]?\s*(?<num>-?\d+(?:[.,]\d+)?)\s*(?:mm|deg|degrees|°)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, (string Key, double Scale)> Aliases = BuildAliases();

    public const string Help =
        "Enter dimensions in mm, for example: impeller 900, impeller inlet 560, hub 170, blades 14, "
        + "inlet angle 30, outlet angle 60, casing 1150, casing width 450, outlet 450. "
        + "Anything you do not enter is derived from the impeller diameter.";

    public static bool TryResolve(string? text, out Dictionary<string, double> values, out string? error)
    {
        values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        error = null;

        var given = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(text) && !DefaultWord.IsMatch(text))
        {
            foreach (var raw in Separator.Split(text))
            {
                var part = raw.Trim();

                if (part.Length == 0 || DefaultWord.IsMatch(part))
                {
                    continue;
                }

                var match = Segment.Match(part);

                if (!match.Success)
                {
                    error = "Could not read \"" + part + "\". " + Help;
                    return false;
                }

                var name = Normalize(match.Groups["name"].Value);

                if (!Aliases.TryGetValue(name, out var alias))
                {
                    error = "Unknown dimension \"" + match.Groups["name"].Value.Trim() + "\". " + Help;
                    return false;
                }

                var number = double.Parse(match.Groups["num"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                given[alias.Key] = number * alias.Scale;
            }
        }

        var d2 = given.TryGetValue(CentrifugalFanScriptComposer.ImpellerOuterDiameterM, out var typed)
            ? typed
            : DefaultImpellerOuterDiameterM;

        if (!double.IsFinite(d2) || d2 <= 0)
        {
            error = "The impeller diameter must be greater than zero.";
            return false;
        }

        foreach (var pair in Derive(d2))
        {
            values[pair.Key] = given.TryGetValue(pair.Key, out var over) ? over : pair.Value;
        }

        return true;
    }

    private static Dictionary<string, double> Derive(double d2)
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [CentrifugalFanScriptComposer.ImpellerOuterDiameterM] = d2,
            [CentrifugalFanScriptComposer.ImpellerInletDiameterM] = d2 * 0.625,
            [CentrifugalFanScriptComposer.BladeWidthM] = d2 * 0.225,
            [CentrifugalFanScriptComposer.BladeInletWidthM] = d2 * 0.275,
            [CentrifugalFanScriptComposer.HubDiameterM] = d2 * 0.1875,
            [CentrifugalFanScriptComposer.BladeCount] = 12,
            [CentrifugalFanScriptComposer.BladeInletAngleDeg] = 30,
            [CentrifugalFanScriptComposer.BladeOutletAngleDeg] = 60,
            [CentrifugalFanScriptComposer.BladeThicknessM] = d2 * 0.00375,
            [CentrifugalFanScriptComposer.BladeProfileCode] = 0,
            [CentrifugalFanScriptComposer.PlateThicknessM] = d2 * 0.0075,
            [CentrifugalFanScriptComposer.CasingBaseDiameterM] = d2 * 1.25,
            [CentrifugalFanScriptComposer.CasingWidthM] = d2 * 0.5,
            [CentrifugalFanScriptComposer.CasingWallM] = d2 * 0.005,
            [CentrifugalFanScriptComposer.OutletHeightM] = d2 * 0.5,
            [CentrifugalFanScriptComposer.CasingInletDiameterM] = d2 * 0.5625
        };
    }

    private static string Normalize(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    private static Dictionary<string, (string Key, double Scale)> BuildAliases()
    {
        const double Mm = 0.001;
        var map = new Dictionary<string, (string Key, double Scale)>(StringComparer.Ordinal);

        void Add(string key, double scale, params string[] names)
        {
            foreach (var name in names)
            {
                map[name] = (key, scale);
            }
        }

        Add(CentrifugalFanScriptComposer.ImpellerOuterDiameterM, Mm, "impeller", "impellerod", "impellerdiameter", "impelleroutsidediameter", "d2");
        Add(CentrifugalFanScriptComposer.ImpellerInletDiameterM, Mm, "impellerinlet", "impellerinletdiameter", "d1");
        Add(CentrifugalFanScriptComposer.BladeWidthM, Mm, "bladewidth", "impellerwidth", "bladeoutletwidth", "b2");
        Add(CentrifugalFanScriptComposer.BladeInletWidthM, Mm, "bladeinletwidth", "b1");
        Add(CentrifugalFanScriptComposer.HubDiameterM, Mm, "hub", "hubdiameter");
        Add(CentrifugalFanScriptComposer.BladeCount, 1.0, "blades", "bladecount", "numberofblades", "count");
        Add(CentrifugalFanScriptComposer.BladeInletAngleDeg, 1.0, "beta1", "inletangle", "bladeinletangle");
        Add(CentrifugalFanScriptComposer.BladeOutletAngleDeg, 1.0, "beta2", "outletangle", "bladeoutletangle");
        Add(CentrifugalFanScriptComposer.BladeThicknessM, Mm, "thickness", "bladethickness");
        Add(CentrifugalFanScriptComposer.BladeProfileCode, 1.0, "profile", "bladeprofile");
        Add(CentrifugalFanScriptComposer.PlateThicknessM, Mm, "plate", "platethickness");
        Add(CentrifugalFanScriptComposer.CasingBaseDiameterM, Mm, "casing", "casingdiameter", "casingbasediameter", "d3");
        Add(CentrifugalFanScriptComposer.CasingWidthM, Mm, "casingwidth");
        Add(CentrifugalFanScriptComposer.CasingWallM, Mm, "wall", "casingwall", "casingwallthickness");
        Add(CentrifugalFanScriptComposer.OutletHeightM, Mm, "outlet", "outletheight");
        Add(CentrifugalFanScriptComposer.CasingInletDiameterM, Mm, "casinginlet", "inleteye", "eye", "inletdiameter", "din");

        return map;
    }
}
