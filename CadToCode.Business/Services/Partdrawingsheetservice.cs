using System.IO.Compression;
using System.Globalization;
using CadToCode.Core.Rules;
using IxMilia.Dxf;

namespace CadToCode.Business.Services;

/// <summary>A part that was found in the GA text labels but for which no sheet could be made.</summary>
public sealed record SkippedPartSheet(string Name, string Label, string Reason);

/// <summary>ZIP of part drawing sheets plus the list of parts that were skipped.</summary>
public sealed class PartDrawingPackage
{
    public byte[]? Zip { get; init; }

    public int SheetCount { get; init; }

    public List<SkippedPartSheet> Skipped { get; init; } = new();
}

/// <summary>
/// Reads part sizes from the TEXT and MTEXT labels of a GA DXF, builds one A3 part sheet per part with
/// the matching builder (flange, rectangular plate, round bar) and packs the sheets into a ZIP.
/// </summary>
public sealed class PartDrawingSheetService
{
    private const string RulesRelativePath = "EngineeringKnowledge/centrifugal-fans/manufacturing-rules.json";

    private readonly object _rulesLock = new();
    private ManufacturingRules? _rules;

    public PartDrawingPackage Build(DxfFile gaDxf, string? projectCode, string? defaultMaterial, double? defaultThicknessMm)
    {
        ArgumentNullException.ThrowIfNull(gaDxf);

        var rules = LoadRules();
        var candidates = new GaTextPartSizeSource().Read(gaDxf);
        var skipped = new List<SkippedPartSheet>();
        var files = new List<(string FileName, byte[] Content)>();

        var flangeBuilder = new FlangePartDrawingBuilder();
        var plateBuilder = new RectangularPlatePartDrawingBuilder();
        var barBuilder = new RoundBarPartDrawingBuilder();

        var itemNo = 0;
        foreach (var candidate in candidates)
        {
            var name = string.IsNullOrWhiteSpace(candidate.Name) ? "PART" : candidate.Name;
            var thickness = candidate.ThicknessMm ?? defaultThicknessMm;
            var material = string.IsNullOrWhiteSpace(candidate.Material) ? (defaultMaterial ?? "MS") : candidate.Material!;
            var qty = candidate.Qty ?? 1;

            try
            {
                itemNo++;
                var frame = BuildFrame(projectCode, itemNo, name);

                switch (candidate.Shape)
                {
                    case SheetPartShape.Flange:
                        {
                            if (candidate.OuterDiameterMm is not > 0.0 || thickness is not > 0.0)
                            {
                                itemNo--;
                                skipped.Add(new SkippedPartSheet(name, candidate.Label, "Outer diameter or thickness is missing."));
                                continue;
                            }

                            var result = flangeBuilder.Build(rules, new FlangePartRequest
                            {
                                Name = name,
                                Material = material,
                                ItemNo = itemNo,
                                Qty = qty,
                                OuterDiameterMm = candidate.OuterDiameterMm.Value,
                                InnerDiameterMm = candidate.InnerDiameterMm ?? 0.0,
                                ThicknessMm = thickness.Value,
                                HoleCount = candidate.HoleCount ?? 0,
                                HoleDiameterMm = candidate.HoleDiameterMm ?? 0.0,
                                HolePcdMm = candidate.HolePcdMm ?? 0.0,
                                Frame = frame
                            });
                            files.Add((FileName(itemNo, name), Save(result.Dxf)));
                            break;
                        }

                    case SheetPartShape.Plate:
                        {
                            if (candidate.LengthMm is not > 0.0 || candidate.WidthMm is not > 0.0 || thickness is not > 0.0)
                            {
                                itemNo--;
                                skipped.Add(new SkippedPartSheet(name, candidate.Label, "Length, width or thickness is missing."));
                                continue;
                            }

                            var result = plateBuilder.Build(rules, new RectangularPlateRequest
                            {
                                Name = name,
                                Material = material,
                                ItemNo = itemNo,
                                Qty = qty,
                                LengthMm = candidate.LengthMm.Value,
                                WidthMm = candidate.WidthMm.Value,
                                ThicknessMm = thickness.Value,
                                Frame = frame
                            });
                            files.Add((FileName(itemNo, name), Save(result.Dxf)));
                            break;
                        }

                    case SheetPartShape.Bar:
                        {
                            if (candidate.OuterDiameterMm is not > 0.0 || candidate.LengthMm is not > 0.0)
                            {
                                itemNo--;
                                skipped.Add(new SkippedPartSheet(name, candidate.Label, "Diameter or length is missing."));
                                continue;
                            }

                            var result = barBuilder.Build(rules, new RoundBarRequest
                            {
                                Name = name,
                                Material = material,
                                ItemNo = itemNo,
                                Qty = qty,
                                DiameterMm = candidate.OuterDiameterMm.Value,
                                LengthMm = candidate.LengthMm.Value,
                                Frame = frame
                            });
                            files.Add((FileName(itemNo, name), Save(result.Dxf)));
                            break;
                        }

                    default:
                        itemNo--;
                        skipped.Add(new SkippedPartSheet(name, candidate.Label, "The part shape is not supported."));
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                itemNo--;
                skipped.Add(new SkippedPartSheet(name, candidate.Label, ex.Message));
            }
        }

        if (files.Count == 0)
        {
            return new PartDrawingPackage { Zip = null, SheetCount = 0, Skipped = skipped };
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

        return new PartDrawingPackage { Zip = output.ToArray(), SheetCount = files.Count, Skipped = skipped };
    }

    private ManufacturingRules LoadRules()
    {
        lock (_rulesLock)
        {
            return _rules ??= ManufacturingRules.FromFile(Path.Combine(AppContext.BaseDirectory, RulesRelativePath));
        }
    }

    private static SheetFrameRequest BuildFrame(string? projectCode, int itemNo, string name)
    {
        var job = string.IsNullOrWhiteSpace(projectCode) ? string.Empty : projectCode.Trim();
        var item = itemNo.ToString("00", CultureInfo.InvariantCulture);

        return new SheetFrameRequest
        {
            JobNo = job,
            DrawingNo = job.Length > 0 ? job + "-" + item : item,
            Title = name.ToUpperInvariant(),
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