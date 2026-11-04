using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CadToCode.Business.Services;

/// <summary>
/// Reads a Bill of Materials (CSV or JSON) into <see cref="PartBomItem"/> rows.
/// Every row that cannot be read is reported in the <see cref="GenerationReport"/>; nothing is dropped silently.
/// </summary>
public static class PartBomReader
{
    private static readonly Regex NumberPattern = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["item"] = "item", ["itemno"] = "item", ["slno"] = "item", ["sr"] = "item", ["srno"] = "item", ["no"] = "item",
        ["name"] = "name", ["part"] = "name", ["partname"] = "name", ["description"] = "name", ["component"] = "name",
        ["type"] = "kind", ["kind"] = "kind", ["shape"] = "kind", ["parttype"] = "kind",
        ["material"] = "material", ["mat"] = "material", ["grade"] = "material", ["materialgrade"] = "material",
        ["qty"] = "qty", ["quantity"] = "qty", ["nos"] = "qty",
        ["od"] = "od", ["outerdiameter"] = "od", ["outerdia"] = "od", ["dia"] = "od", ["diameter"] = "od", ["odmm"] = "od",
        ["id"] = "id", ["innerdiameter"] = "id", ["innerdia"] = "id", ["bore"] = "id", ["idmm"] = "id",
        ["thickness"] = "thk", ["thk"] = "thk", ["t"] = "thk", ["thicknessmm"] = "thk",
        ["length"] = "len", ["len"] = "len", ["l"] = "len", ["lengthmm"] = "len",
        ["width"] = "wid", ["w"] = "wid", ["widthmm"] = "wid",
        ["height"] = "hgt", ["h"] = "hgt", ["heightmm"] = "hgt",
        ["smalldiameter"] = "d1", ["smalldia"] = "d1", ["d1"] = "d1",
        ["largediameter"] = "d2", ["largedia"] = "d2", ["d2"] = "d2",
        ["chamfer"] = "chamfer", ["chamfermm"] = "chamfer",
        ["holes"] = "holes", ["holecount"] = "holes", ["noofholes"] = "holes", ["nholes"] = "holes",
        ["holedia"] = "holedia", ["holediameter"] = "holedia", ["holedm"] = "holedia",
        ["pcd"] = "pcd", ["holepcd"] = "pcd",
        ["bendradius"] = "bendr", ["ir"] = "bendr", ["innerradius"] = "bendr",
        ["remarks"] = "remarks", ["remark"] = "remarks", ["note"] = "remarks", ["notes"] = "remarks"
    };

    public static List<PartBomItem> ReadCsv(string text, GenerationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var items = new List<PartBomItem>();

        if (string.IsNullOrWhiteSpace(text))
        {
            report.Error("BOM_EMPTY", "BOM", "The BOM file is empty.");
            return items;
        }

        var rows = SplitCsv(text);
        if (rows.Count < 2)
        {
            report.Error("BOM_NO_ROWS", "BOM", "The BOM has a header but no part rows.");
            return items;
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < rows[0].Count; c++)
        {
            var key = Normalize(rows[0][c]);
            if (HeaderAliases.TryGetValue(key, out var field))
            {
                if (!columns.TryAdd(field, c))
                {
                    report.Warn("BOM_DUP_COLUMN", "BOM", "Column '" + rows[0][c].Trim() + "' repeats '" + field + "' and was ignored.");
                }
            }
            else if (key.Length > 0)
            {
                report.Warn("BOM_UNKNOWN_COLUMN", "BOM", "Column '" + rows[0][c].Trim() + "' is not recognised and was ignored.");
            }
        }

        if (!columns.ContainsKey("name") && !columns.ContainsKey("kind"))
        {
            report.Error("BOM_NO_NAME_COLUMN", "BOM", "The BOM needs a part name column or a type column.");
            return items;
        }

        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string Cell(string field) => columns.TryGetValue(field, out var index) && index < row.Count ? row[index].Trim() : string.Empty;

            var item = new PartBomItem
            {
                ItemNo = ParseInt(Cell("item")) ?? items.Count + 1,
                Name = Cell("name"),
                Material = NullIfEmpty(Cell("material")),
                Qty = ParseInt(Cell("qty")) ?? 1,
                OuterDiameterMm = ParseNumber(Cell("od")),
                InnerDiameterMm = ParseNumber(Cell("id")),
                ThicknessMm = ParseNumber(Cell("thk")),
                LengthMm = ParseNumber(Cell("len")),
                WidthMm = ParseNumber(Cell("wid")),
                HeightMm = ParseNumber(Cell("hgt")),
                SmallDiameterMm = ParseNumber(Cell("d1")),
                LargeDiameterMm = ParseNumber(Cell("d2")),
                ChamferMm = ParseNumber(Cell("chamfer")),
                HoleCount = ParseInt(Cell("holes")),
                HoleDiameterMm = ParseNumber(Cell("holedia")),
                HolePcdMm = ParseNumber(Cell("pcd")),
                BendRadiusMm = ParseNumber(Cell("bendr")),
                Remarks = NullIfEmpty(Cell("remarks"))
            };

            item.Kind = ResolveKind(Cell("kind"), item.Name);
            if (item.Kind == BomPartKind.Unknown)
            {
                report.Error("BOM_UNKNOWN_TYPE", item.DisplayName, "Row " + (r + 1).ToString(CultureInfo.InvariantCulture) + ": the part type could not be recognised from '" + Cell("kind") + "' or the part name.");
                continue;
            }

            if (item.Qty < 1)
            {
                report.Warn("BOM_QTY", item.DisplayName, "Quantity is below 1. 1 was used.");
                item.Qty = 1;
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            report.Error("BOM_NO_VALID_ROWS", "BOM", "No valid part rows were found in the BOM.");
        }

        return items;
    }

    public static List<PartBomItem> ReadJson(string json, GenerationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(json))
        {
            report.Error("BOM_EMPTY", "BOM", "The BOM file is empty.");
            return new List<PartBomItem>();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var items = JsonSerializer.Deserialize<List<PartBomItem>>(json, options) ?? new List<PartBomItem>();
            var valid = new List<PartBomItem>();

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.ItemNo <= 0)
                {
                    item.ItemNo = i + 1;
                }

                if (item.Kind == BomPartKind.Unknown)
                {
                    item.Kind = ResolveKind(string.Empty, item.Name);
                }

                if (item.Kind == BomPartKind.Unknown)
                {
                    report.Error("BOM_UNKNOWN_TYPE", item.DisplayName, "Entry " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": the part type is missing or not recognised.");
                    continue;
                }

                if (item.Qty < 1)
                {
                    report.Warn("BOM_QTY", item.DisplayName, "Quantity is below 1. 1 was used.");
                    item.Qty = 1;
                }

                valid.Add(item);
            }

            if (valid.Count == 0)
            {
                report.Error("BOM_NO_VALID_ROWS", "BOM", "No valid part rows were found in the BOM.");
            }

            return valid;
        }
        catch (JsonException ex)
        {
            report.Error("BOM_JSON", "BOM", "The BOM JSON could not be read: " + ex.Message);
            return new List<PartBomItem>();
        }
    }

    public static BomPartKind ResolveKind(string? typeText, string? nameText)
    {
        var kind = Match(typeText);
        return kind != BomPartKind.Unknown ? kind : Match(nameText);
    }

    private static BomPartKind Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BomPartKind.Unknown;
        }

        var t = text.ToLowerInvariant();

        if (t.Contains("transition") || t.Contains("square to round") || t.Contains("sq to rd") || t.Contains("rect to round"))
        {
            return BomPartKind.TransitionFlatPattern;
        }

        if (t.Contains("scroll") || t.Contains("casing") || t.Contains("volute"))
        {
            return BomPartKind.ScrollFlatPattern;
        }

        if (t.Contains("cone") || t.Contains("conical"))
        {
            return BomPartKind.ConeFlatPattern;
        }

        if (t.Contains("hub"))
        {
            return BomPartKind.Hub;
        }

        if (t.Contains("rolled ring") || t.Contains("ring"))
        {
            return BomPartKind.RolledRing;
        }

        if (t.Contains("flange"))
        {
            return BomPartKind.Flange;
        }

        if (t.Contains("plate") || t.Contains("flap") || t.Contains("rectang"))
        {
            return BomPartKind.RectangularPlate;
        }

        if (t.Contains("bar") || t.Contains("shaft") || t.Contains("pin") || t.Contains("handle") || t.Contains("rod"))
        {
            return BomPartKind.RoundBar;
        }

        return BomPartKind.Unknown;
    }

    private static string Normalize(string header)
    {
        var builder = new StringBuilder();
        foreach (var ch in header)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        var text = builder.ToString();
        return text.EndsWith("mm", StringComparison.Ordinal) && text.Length > 2 && HeaderAliases.ContainsKey(text[..^2]) ? text[..^2] : text;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double? ParseNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = NumberPattern.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static int? ParseInt(string value)
    {
        var number = ParseNumber(value);
        return number.HasValue ? (int)Math.Round(number.Value) : null;
    }

    private static List<List<string>> SplitCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var delimiter = DetectDelimiter(text);

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else
            {
                cell.Append(ch);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        var header = end < 0 ? text : text[..end];
        var semicolons = header.Count(c => c == ';');
        var tabs = header.Count(c => c == '\t');
        var commas = header.Count(c => c == ',');

        if (semicolons > commas && semicolons >= tabs)
        {
            return ';';
        }

        return tabs > commas ? '\t' : ',';
    }
}
