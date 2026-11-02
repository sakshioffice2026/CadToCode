using System.Globalization;
using System.IO.Compression;
using CadToCode.Core.Rules;
using IxMilia.Dxf;

namespace CadToCode.Business.Services;

/// <summary>ZIP of generated sheets plus every issue raised while they were made.</summary>
public sealed class ParametricSheetPackage
{
    public byte[]? Zip { get; init; }

    public int SheetCount { get; init; }

    public int RequestedCount { get; init; }

    public List<GenerationIssue> Issues { get; init; } = new();

    public List<string> Messages { get; init; } = new();

    public bool HasErrors => Issues.Any(i => i.Severity == GenerationSeverity.Error);
}

/// <summary>
/// Builds one manufacturing sheet per BOM row with <see cref="ParametricPartRulesEngine"/>.
/// Parts are never traced from a General Arrangement drawing. Every skipped part is reported.
/// </summary>
public sealed class ParametricPartSheetService
{
    private const string RulesRelativePath = "EngineeringKnowledge/centrifugal-fans/manufacturing-rules.json";

    private readonly object _rulesLock = new();
    private readonly ParametricPartRulesEngine _engine = new();
    private ManufacturingRules? _rules;

    public ParametricSheetPackage BuildFromCsv(string csvText, string? projectCode, string? defaultMaterial, double? defaultThicknessMm)
    {
        var report = new GenerationReport();
        var items = PartBomReader.ReadCsv(csvText, report);
        return Build(items, projectCode, defaultMaterial, defaultThicknessMm, report);
    }

    public ParametricSheetPackage BuildFromJson(string json, string? projectCode, string? defaultMaterial, double? defaultThicknessMm)
    {
        var report = new GenerationReport();
        var items = PartBomReader.ReadJson(json, report);
        return Build(items, projectCode, defaultMaterial, defaultThicknessMm, report);
    }

    public ParametricSheetPackage Build(
        IReadOnlyList<PartBomItem> items,
        string? projectCode,
        string? defaultMaterial,
        double? defaultThicknessMm,
        GenerationReport report)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(report);

        ManufacturingRules rules;
        try
        {
            rules = LoadRules();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            report.Error("RULES_LOAD", "RULES", "The manufacturing rules could not be loaded: " + ex.Message);
            return Finish(report, new List<(string, byte[])>(), items.Count);
        }

        var files = new List<(string FileName, byte[] Content)>();
        var usedNumbers = new HashSet<int>();

        foreach (var item in items.OrderBy(i => i.ItemNo))
        {
            if (!usedNumbers.Add(item.ItemNo))
            {
                var original = item.ItemNo;
                var next = usedNumbers.Max() + 1;
                item.ItemNo = next;
                usedNumbers.Add(next);
                report.Warn("ITEM_NO_DUPLICATE", item.DisplayName, "Item number " + original.ToString(CultureInfo.InvariantCulture) + " is used twice. " + next.ToString(CultureInfo.InvariantCulture) + " was assigned.");
            }

            var frame = BuildFrame(projectCode, item);
            var sheet = _engine.Generate(rules, item, frame, defaultMaterial, defaultThicknessMm, report);
            if (sheet is null)
            {
                continue;
            }

            files.Add((FileName(sheet.ItemNo, sheet.Name), Save(sheet.Dxf)));
        }

        return Finish(report, files, items.Count);
    }

    private static ParametricSheetPackage Finish(GenerationReport report, List<(string FileName, byte[] Content)> files, int requested)
    {
        var issues = report.Issues.OrderByDescending(i => i.Severity).ToList();

        if (files.Count == 0)
        {
            if (!issues.Any(i => i.Severity == GenerationSeverity.Error))
            {
                report.Error("NO_SHEETS", "BOM", "No sheet could be generated from the BOM.");
                issues = report.Issues.OrderByDescending(i => i.Severity).ToList();
            }

            return new ParametricSheetPackage
            {
                Zip = null,
                SheetCount = 0,
                RequestedCount = requested,
                Issues = issues,
                Messages = report.ToMessages()
            };
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (fileName, content) in files)
            {
                var entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        return new ParametricSheetPackage
        {
            Zip = output.ToArray(),
            SheetCount = files.Count,
            RequestedCount = requested,
            Issues = issues,
            Messages = report.ToMessages()
        };
    }

    private ManufacturingRules LoadRules()
    {
        lock (_rulesLock)
        {
            return _rules ??= ManufacturingRules.FromFile(Path.Combine(AppContext.BaseDirectory, RulesRelativePath));
        }
    }

    private static SheetFrameRequest BuildFrame(string? projectCode, PartBomItem item)
    {
        var job = string.IsNullOrWhiteSpace(projectCode) ? string.Empty : projectCode.Trim();
        var number = item.ItemNo.ToString("00", CultureInfo.InvariantCulture);

        return new SheetFrameRequest
        {
            JobNo = job,
            DrawingNo = job.Length > 0 ? job + "-" + number : number,
            Title = item.DisplayName.ToUpperInvariant(),
            Date = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
        };
    }

    private static string FileName(int itemNo, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        return itemNo.ToString("00", CultureInfo.InvariantCulture) + "-" + cleaned + ".dxf";
    }

    private static byte[] Save(DxfFile dxf)
    {
        using var stream = new MemoryStream();
        dxf.Save(stream);
        return stream.ToArray();
    }
}
