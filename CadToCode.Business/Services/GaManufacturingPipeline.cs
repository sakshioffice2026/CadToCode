using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Core.Rules;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using IxMilia.Dxf;

namespace CadToCode.Business.Services
{
    public sealed class GaPipelineRequest
    {
        public ExtractedGeometry Geometry { get; init; } = new();

        public IReadOnlyList<AssemblyNode> Nodes { get; init; } = Array.Empty<AssemblyNode>();

        public string? ProjectCode { get; init; }

        public string? DefaultMaterial { get; init; }

        public double? DefaultThicknessMm { get; init; }

        public int ProjectId { get; init; }

        /// <summary>When true, generation stops until every required review item is approved.</summary>
        public bool RequireApproval { get; init; }

        /// <summary>When true, the run stops after inference and returns only the review items.</summary>
        public bool ReviewOnly { get; init; }

        public IReadOnlyList<GaReviewItem> ApprovedItems { get; init; } = Array.Empty<GaReviewItem>();

        public ManufacturingRules? Rules { get; init; }


        public IReadOnlyList<ReferenceCalloutEntry> ReferenceCallouts { get; init; } = Array.Empty<ReferenceCalloutEntry>();
    }

    public sealed class GaPipelineFile
    {
        public int ItemNo { get; init; }

        public string PartName { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public byte[] Content { get; init; } = Array.Empty<byte>();

        public string PdfFileName { get; init; } = string.Empty;

        public byte[] PdfContent { get; init; } = Array.Empty<byte>();

        public GaSheetData Data { get; init; } = new();
    }

    public sealed class GaPipelineResult
    {
        public GenerationReport Report { get; init; } = new();

        public CalloutReadResult Callouts { get; set; } = new();

        public GaClassificationResult Classification { get; set; } = new();

        public List<GaPipelineFile> Files { get; } = new();

        public GaCutListResult CutList { get; set; } = new();

        public CompletenessResult Completeness { get; set; } = new();

        public double CalculatedFinishTotalKg { get; set; }

        public bool HasFiles => Files.Count > 0;

        public List<GaReconstructedComponent> Components { get; } = new();

        public List<GaReviewItem> ReviewItems { get; } = new();

        public bool AwaitingApproval { get; set; }

        public List<string> BlockingReasons { get; } = new();

        public bool IsReleasable =>
            !AwaitingApproval
            && BlockingReasons.Count == 0
            && (HasFiles || CutList.CutRows.Count > 0 || CutList.BoughtOutRows.Count > 0);

        public bool HasErrors => Report.Issues.Any(i => i.Severity == GenerationSeverity.Error);

        public List<string> Messages { get; } = new();
    }

    public sealed class GaManufacturingPipeline
    {
        private const string RulesRelativePath = "EngineeringKnowledge/centrifugal-fans/manufacturing-rules.json";
        private const double MaxPlausibleExtentMm = 60000.0;

        private static readonly Regex DrawingInMmRx = new(
            @"\b(?:in|are\s+in)\s+mm\b|millimet",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly GaCalloutReader _reader;
        private readonly GaCalloutGeometryLinker _linker;
        private readonly GaViewSelector _selector;
        private readonly GaPartClassifier _classifier;
        private readonly GaProfileBuilder _profileBuilder;
        private readonly GaFlatPatternResolver _flatResolver;
        private readonly GaPartSheetDataBuilder _sheetData;
        private readonly GaCutListBuilder _cutList;
        private readonly GaCompletenessChecker _completeness;
        private readonly GaComponentReconstructionService _reconstruction;
        private readonly GaEngineeringInferenceService _inference;
        private readonly GaPdfDrawingService _pdf;
        private readonly GaArchiveValidator _validator;
        private readonly ParametricPartRulesEngine _engine = new();
        private readonly object _rulesLock = new();
        private ManufacturingRules? _rules;

        public GaManufacturingPipeline(
            GaCalloutReader reader,
            GaCalloutGeometryLinker linker,
            GaViewSelector selector,
            GaPartClassifier classifier,
            GaProfileBuilder profileBuilder,
            GaFlatPatternResolver flatResolver,
            GaPartSheetDataBuilder sheetData,
            GaCutListBuilder cutList,
            GaCompletenessChecker completeness,
            GaComponentReconstructionService reconstruction,
            GaEngineeringInferenceService inference,
            GaPdfDrawingService pdf,
            GaArchiveValidator validator)
        {
            _reconstruction = reconstruction ?? throw new ArgumentNullException(nameof(reconstruction));
            _inference = inference ?? throw new ArgumentNullException(nameof(inference));
            _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _linker = linker ?? throw new ArgumentNullException(nameof(linker));
            _selector = selector ?? throw new ArgumentNullException(nameof(selector));
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _profileBuilder = profileBuilder ?? throw new ArgumentNullException(nameof(profileBuilder));
            _flatResolver = flatResolver ?? throw new ArgumentNullException(nameof(flatResolver));
            _sheetData = sheetData ?? throw new ArgumentNullException(nameof(sheetData));
            _cutList = cutList ?? throw new ArgumentNullException(nameof(cutList));
            _completeness = completeness ?? throw new ArgumentNullException(nameof(completeness));
        }

        public GaPipelineResult Run(GaPipelineRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var report = new GenerationReport();
            var result = new GaPipelineResult { Report = report };

            ManufacturingRules rules;
            try
            {
                rules = request.Rules ?? LoadRules();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                report.Error("RULES_LOAD", "RULES", "The manufacturing rules could not be loaded: " + ex.Message);
                result.Messages.AddRange(report.ToMessages());
                return result;
            }

            var geometry = request.Geometry;
            var nodes = request.Nodes;
            var mm = ResolveMmPerUnit(geometry);

            var callouts = _reader.Read(geometry, report);
            result.Callouts = callouts;

            ApplyApproved(callouts, request.ApprovedItems, report);

            if (callouts.Parts.Count == 0)
            {
                report.Error("PIPELINE_NO_CALLOUTS", "GA", "No part callout was found in the drawing text.");

                foreach (var entry in request.ReferenceCallouts)
                {
                    var p = entry.Part;
                    report.Warn(
                        "REFERENCE_PART_BLOCKED",
                        p.Name,
                        "From " + entry.SourceFile + ": "
                        + (p.Material ?? "material not stated") + ", "
                        + (p.ThicknessMm.HasValue ? p.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture) + " mm" : "thickness not stated")
                        + ", qty " + (p.QtyText ?? p.Qty.ToString(CultureInfo.InvariantCulture))
                        + (p.WeightKg.HasValue ? ", labelled weight " + p.WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) + " kg" : string.Empty)
                        + ". Not generated: dimensions not taken from the GA.");
                }

                result.Messages.AddRange(report.ToMessages());
                return result;
            }

            var links = _linker.Link(callouts, geometry, nodes, report);
            var views = _selector.Select(links, nodes, geometry, mm, report);

            var classification = _classifier.Classify(callouts.Parts, views.Selections, report);
            result.Classification = classification;

            var profiles = _profileBuilder.Build(classification.Parts, mm, report);
            var flats = _flatResolver.Resolve(profiles, nodes, mm, report);

            var components = _reconstruction.Reconstruct(profiles, flats, views, report);
            result.Components.AddRange(components);

            var reviewItems = _inference.Infer(components, request.DefaultMaterial, request.DefaultThicknessMm, report);
            result.ReviewItems.AddRange(reviewItems);

            var pendingRequired = reviewItems.Where(i => i.Required).ToList();

            if (request.ReviewOnly || (request.RequireApproval && pendingRequired.Count > 0))
            {
                result.AwaitingApproval = pendingRequired.Count > 0;

                foreach (var item in pendingRequired)
                {
                    report.Error(
                        "APPROVAL_REQUIRED",
                        item.PartName,
                        item.Field + " must be approved before drawings are generated"
                        + (string.IsNullOrWhiteSpace(item.SuggestedValue) ? "." : " (suggested " + item.SuggestedValue + ")."));
                }

                result.Messages.AddRange(report.ToMessages());
                return result;
            }

            var sheets = _sheetData.Build(rules, profiles, flats, request.ProjectCode, request.DefaultMaterial, report);

            var cutList = _cutList.Build(rules, classification, links, mm, report);
            result.CutList = cutList;

            var fileDtos = new List<PartManufacturingFileDto>();

            foreach (var data in sheets)
            {
                if (data.FinishWeightKg.HasValue && data.Item.Kind == BomPartKind.ProfilePlate)
                {
                    data.Item.FinishWeightKg = data.FinishWeightKg;
                }
                var sheet = _engine.Generate(rules, data.Item, data.Frame, request.DefaultMaterial, request.DefaultThicknessMm, report);

                if (sheet is null)
                {
                    continue;
                }

                var fileName = FileName(sheet.ItemNo, sheet.Name);
                var content = Save(sheet.Dxf);

                var dxfCheck = _validator.ValidateFile(fileName, content);
                if (!dxfCheck.IsValid)
                {
                    foreach (var error in dxfCheck.Errors)
                    {
                        report.Error("OUTPUT_INVALID_DXF", sheet.Name, error);
                    }

                    continue;
                }

                data.Tolerance = FindApproved(request.ApprovedItems, sheet.Name, GaEngineeringInferenceService.FieldTolerance)
                    ?? GaDrawingTemplates.Get(data.Category).DefaultTolerance;

                var pdfName = Path.ChangeExtension(fileName, ".pdf");
                byte[] pdf;

                try
                {
                    pdf = _pdf.Build(data, data.Tolerance);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
                {
                    report.Error("OUTPUT_PDF_FAILED", sheet.Name, "The PDF drawing could not be built: " + ex.Message);
                    continue;
                }

                var pdfCheck = _validator.ValidateFile(pdfName, pdf);
                if (!pdfCheck.IsValid)
                {
                    foreach (var error in pdfCheck.Errors)
                    {
                        report.Error("OUTPUT_INVALID_PDF", sheet.Name, error);
                    }

                    continue;
                }

                result.Files.Add(new GaPipelineFile
                {
                    ItemNo = sheet.ItemNo,
                    PartName = sheet.Name,
                    FileName = fileName,
                    Content = content,
                    PdfFileName = pdfName,
                    PdfContent = pdf,
                    Data = data
                });

                fileDtos.Add(new PartManufacturingFileDto
                {
                    PartName = sheet.Name,
                    FileName = fileName,
                    Material = data.Material,
                    Thickness = data.ThicknessMm.HasValue ? (decimal?)Convert.ToDecimal(data.ThicknessMm.Value, CultureInfo.InvariantCulture) : null,
                    Quantity = data.Qty,
                    Content = content
                });
            }

            result.Completeness = _completeness.Check(callouts, fileDtos, report);

            CompareTotals(rules, result, sheets, callouts, report);

            result.BlockingReasons.AddRange(result.Completeness.BlockingReasons());

            result.Messages.AddRange(report.ToMessages());
            result.Messages.AddRange(result.Completeness.ToMessages());

            foreach (var reason in result.BlockingReasons)
            {
                result.Messages.Add("[ERROR] PACKAGE (PACKAGE_BLOCKED): " + reason);
            }

            return result;
        }

        private static string? FindApproved(IReadOnlyList<GaReviewItem> items, string partName, string field)
        {
            var item = items.FirstOrDefault(i =>
                i.Status == GaReviewStatus.Approved
                && !string.IsNullOrWhiteSpace(i.ApprovedValue)
                && string.Equals(i.PartName, partName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Field, field, StringComparison.OrdinalIgnoreCase));

            return item?.ApprovedValue;
        }

        private static void ApplyApproved(CalloutReadResult callouts, IReadOnlyList<GaReviewItem> items, GenerationReport report)
        {
            foreach (var item in items)
            {
                if (item.Status != GaReviewStatus.Approved || string.IsNullOrWhiteSpace(item.ApprovedValue))
                {
                    continue;
                }

                var part = callouts.Parts.FirstOrDefault(p => string.Equals(p.Name, item.PartName, StringComparison.OrdinalIgnoreCase));
                if (part is null)
                {
                    continue;
                }

                var value = item.ApprovedValue.Trim();

                switch (item.Field)
                {
                    case GaEngineeringInferenceService.FieldThickness:
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thickness) && thickness > 0.0)
                        {
                            part.ThicknessMm = thickness;
                            part.ApprovedFields.Add(GaEngineeringInferenceService.FieldThickness);
                            report.Info("APPROVED_APPLIED", part.Name, "Thickness " + value + " mm applied from user approval.");
                        }

                        break;

                    case GaEngineeringInferenceService.FieldMaterial:
                        part.Material = value;
                        part.ApprovedFields.Add(GaEngineeringInferenceService.FieldMaterial);
                        report.Info("APPROVED_APPLIED", part.Name, "Material " + value + " applied from user approval.");
                        break;

                    case GaEngineeringInferenceService.FieldQuantity:
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) && qty > 0)
                        {
                            part.Qty = qty;
                            part.QtyText = value;
                            part.ApprovedFields.Add(GaEngineeringInferenceService.FieldQuantity);
                            report.Info("APPROVED_APPLIED", part.Name, "Quantity " + value + " applied from user approval.");
                        }

                        break;
                }
            }
        }

        public static string BuildBomCsv(CalloutReadResult callouts)
        {
            ArgumentNullException.ThrowIfNull(callouts);

            var sb = new StringBuilder();
            sb.AppendLine("NO,NAME,DRAWING NO,SECTION,MATERIAL,THK MM,QTY,WEIGHT KG");

            var number = 0;
            foreach (var part in callouts.Parts)
            {
                number++;
                sb.AppendLine(string.Join(",", new[]
                {
                    number.ToString(CultureInfo.InvariantCulture),
                    Csv(part.Name),
                    Csv(part.DrawingNumber ?? string.Empty),
                    Csv(part.Section ?? string.Empty),
                    Csv(part.Material ?? string.Empty),
                    part.ThicknessMm.HasValue ? part.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    part.Qty.ToString(CultureInfo.InvariantCulture),
                    part.WeightKg.HasValue ? part.WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty
                }));
            }

            return sb.ToString();
        }

        public static string BuildValueSourcesCsv(GaPipelineResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            var sb = new StringBuilder();
            sb.AppendLine("PART,FIELD,VALUE,SOURCE");

            foreach (var file in result.Files)
            {
                foreach (var source in file.Data.Sources)
                {
                    sb.AppendLine(string.Join(",", new[]
                    {
                        Csv(file.PartName),
                        Csv(source.Field),
                        Csv(source.Value ?? string.Empty),
                        source.Source.ToString()
                    }));
                }

                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(file.PartName),
                    "GeneralTolerance",
                    Csv(file.Data.Tolerance ?? string.Empty),
                    file.Data.Tolerance is null ? "Default" : GaValueSource.Inferred.ToString()
                }));
            }

            return sb.ToString();
        }

        public byte[]? BuildZip(GaPipelineResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (result.AwaitingApproval || result.BlockingReasons.Count > 0)
            {
                return null;
            }

            if (result.Files.Count == 0 && result.CutList.CutRows.Count == 0 && result.CutList.BoughtOutRows.Count == 0)
            {
                return null;
            }

            var expected = new List<string>();
            byte[] bytes;

            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var file in result.Files)
                    {
                        WriteEntry(zip, file.FileName, file.Content);
                        expected.Add(file.FileName);

                        WriteEntry(zip, file.PdfFileName, file.PdfContent);
                        expected.Add(file.PdfFileName);
                    }

                    if (result.CutList.CutRows.Count > 0 || result.CutList.BoughtOutRows.Count > 0)
                    {
                        WriteEntry(zip, "CUT-LIST.csv", Encoding.UTF8.GetBytes(BuildCutListCsv(result.CutList)));
                        expected.Add("CUT-LIST.csv");
                    }

                    WriteEntry(zip, "BOM.csv", Encoding.UTF8.GetBytes(BuildBomCsv(result.Callouts)));
                    expected.Add("BOM.csv");

                    WriteEntry(zip, "VALUE-SOURCES.csv", Encoding.UTF8.GetBytes(BuildValueSourcesCsv(result)));
                    expected.Add("VALUE-SOURCES.csv");

                    WriteEntry(zip, "COMPLETENESS-REPORT.csv", Encoding.UTF8.GetBytes(BuildCompletenessCsv(result.Completeness)));
                    expected.Add("COMPLETENESS-REPORT.csv");
                }

                bytes = output.ToArray();
            }

            var check = _validator.Validate(bytes, expected);

            if (!check.IsValid)
            {
                foreach (var error in check.Errors)
                {
                    result.BlockingReasons.Add("Archive check failed: " + error);
                    result.Messages.Add("[ERROR] PACKAGE (ARCHIVE_INVALID): " + error);
                }

                return null;
            }

            return bytes;
        }

        public static string BuildCutListCsv(GaCutListResult cutList)
        {
            ArgumentNullException.ThrowIfNull(cutList);

            var sb = new StringBuilder();
            sb.AppendLine("TYPE,NO,NAME,SECTION,MATERIAL,QTY,LENGTH MM,KG PER M,TOTAL KG,REMARK");

            foreach (var row in cutList.CutRows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    "CUT",
                    row.No.ToString(CultureInfo.InvariantCulture),
                    Csv(row.Name),
                    Csv(row.Description),
                    Csv(row.Material),
                    row.Qty.ToString(CultureInfo.InvariantCulture),
                    row.LengthMm.HasValue ? row.LengthMm.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    row.KgPerMetre.HasValue ? row.KgPerMetre.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
                    row.TotalWeightKg.HasValue ? row.TotalWeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    Csv(string.Join(" ", row.Notes))
                }));
            }

            foreach (var row in cutList.BoughtOutRows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    "BOUGHT-OUT",
                    row.No.ToString(CultureInfo.InvariantCulture),
                    Csv(row.Name),
                    Csv(row.Specification),
                    Csv(row.Material ?? string.Empty),
                    row.Qty.ToString(CultureInfo.InvariantCulture),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    Csv(row.Remark)
                }));
            }

            return sb.ToString();
        }

        public static string BuildCompletenessCsv(CompletenessResult completeness)
        {
            ArgumentNullException.ThrowIfNull(completeness);

            var sb = new StringBuilder();
            sb.AppendLine("PART,DISPOSITION,FILE,QTY,THK,WT KG,REASON");

            foreach (var row in completeness.Rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.Part.Name),
                    row.Disposition.ToString(),
                    Csv(row.FileName ?? string.Empty),
                    row.Part.Qty.ToString(CultureInfo.InvariantCulture),
                    row.Part.ThicknessMm.HasValue ? row.Part.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    row.Part.WeightKg.HasValue ? row.Part.WeightKg.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                    Csv(row.Reason)
                }));
            }

            foreach (var file in completeness.UnusedFiles)
            {
                sb.AppendLine(string.Join(",", new[] { string.Empty, "FILE WITHOUT CALLOUT", Csv(file), string.Empty, string.Empty, string.Empty, string.Empty }));
            }

            return sb.ToString();
        }

        private static void CompareTotals(
            ManufacturingRules rules,
            GaPipelineResult result,
            IReadOnlyList<GaSheetData> sheets,
            CalloutReadResult callouts,
            GenerationReport report)
        {
            var finished = result.Files
                .Where(f => f.Data.FinishWeightKg.HasValue)
                .Sum(f => f.Data.FinishWeightKg!.Value * Math.Max(1, f.Data.Qty));

            var computed = Math.Round(finished + result.CutList.TotalWeightKg, 2);
            result.CalculatedFinishTotalKg = computed;

            report.Info(
                "PIPELINE_TOTAL",
                "ALL PARTS",
                result.Files.Count.ToString(CultureInfo.InvariantCulture) + " DXF sheet(s) from " + sheets.Count.ToString(CultureInfo.InvariantCulture)
                + " sheet data row(s); calculated finish weight with cut list " + computed.ToString("0.##", CultureInfo.InvariantCulture) + " kg.");

            foreach (var total in callouts.Totals)
            {
                if (total.Kg <= 0.0)
                {
                    continue;
                }

                if (rules.IsWithinReference(computed, total.Kg))
                {
                    report.Info(
                        "PIPELINE_TOTAL_OK",
                        total.Label,
                        "Calculated " + computed.ToString("0.##", CultureInfo.InvariantCulture) + " kg matches drawing total "
                        + total.Kg.ToString("0.##", CultureInfo.InvariantCulture) + " kg.");
                    continue;
                }

                report.Warn(
                    "PIPELINE_TOTAL_DIFFERS",
                    total.Label,
                    "Calculated " + computed.ToString("0.##", CultureInfo.InvariantCulture) + " kg differs from drawing total "
                    + total.Kg.ToString("0.##", CultureInfo.InvariantCulture) + " kg. The drawing total may cover only part of the assembly.");
            }
        }

        private ManufacturingRules LoadRules()
        {
            lock (_rulesLock)
            {
                return _rules ??= ManufacturingRules.FromFile(Path.Combine(AppContext.BaseDirectory, RulesRelativePath));
            }
        }

        private static double ResolveMmPerUnit(ExtractedGeometry geometry)
        {
            var declared = geometry.DetectedUnit switch
            {
                DrawingUnit.Centimeter => 10.0,
                DrawingUnit.Meter => 1000.0,
                DrawingUnit.Inch => 25.4,
                DrawingUnit.Foot => 304.8,
                _ => 1.0
            };

            if (Math.Abs(declared - 1.0) <= 1e-9)
            {
                return declared;
            }

            var mentionsMm = geometry.Texts.Any(t => !string.IsNullOrEmpty(t.Text) && DrawingInMmRx.IsMatch(t.Text));
            var extent = geometry.Bounds is null ? 0.0 : Math.Max(geometry.Bounds.Width, geometry.Bounds.Height);
            var implausiblyLarge = extent <= MaxPlausibleExtentMm && extent * declared > MaxPlausibleExtentMm;

            return mentionsMm || implausiblyLarge ? 1.0 : declared;
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

        private static void WriteEntry(ZipArchive zip, string name, byte[] content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }

        private static string Csv(string value)
        {
            var text = value ?? string.Empty;

            if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }

            return text;
        }
    }
}
