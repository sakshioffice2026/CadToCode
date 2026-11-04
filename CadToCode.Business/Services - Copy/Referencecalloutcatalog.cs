using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Services
{
    public sealed class ReferenceCalloutEntry
    {
        public string SourceFile { get; init; } = string.Empty;

        public CalloutPart Part { get; init; } = new();
    }

    /// <summary>
    /// Reads part callouts (name, material, thickness, quantity, labelled weight) from reference drawings.
    /// Nothing is inferred: only what GaCalloutReader parses from the drawing text is returned.
    /// </summary>
    public sealed class ReferenceCalloutCatalog
    {
        private readonly IEnumerable<IGeometryExtractor> _extractors;
        private readonly GaCalloutReader _reader;

        public ReferenceCalloutCatalog(IEnumerable<IGeometryExtractor> extractors, GaCalloutReader reader)
        {
            _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public async Task<List<ReferenceCalloutEntry>> LoadAsync(
            IEnumerable<string> dxfPaths,
            GenerationReport report,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(report);

            var entries = new List<ReferenceCalloutEntry>();
            var extractor = _extractors.FirstOrDefault(e => e.CanHandle(SourceFileType.Dxf));

            if (extractor is null)
            {
                report.Error("REFERENCE_NO_EXTRACTOR", "REFERENCE", "No DXF geometry extractor is registered.");
                return entries;
            }

            foreach (var path in dxfPaths ?? Enumerable.Empty<string>())
            {
                var file = Path.GetFileName(path);

                if (!File.Exists(path))
                {
                    report.Error("REFERENCE_FILE_MISSING", file, "The reference drawing was not found.");
                    continue;
                }

                try
                {
                    await using var stream = File.OpenRead(path);
                    var geometry = await extractor.ExtractAsync(stream, cancellationToken);
                    var read = _reader.Read(geometry, report);

                    foreach (var part in read.Parts)
                    {
                        entries.Add(new ReferenceCalloutEntry { SourceFile = file, Part = part });
                    }

                    report.Info("REFERENCE_READ", file, read.Parts.Count + " callouts read.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.Error("REFERENCE_READ_FAILED", file, ex.Message);
                }
            }

            return entries;
        }
    }
}