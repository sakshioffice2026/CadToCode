using System.IO.Compression;
using System.Text;

namespace CadToCode.Business.Services
{
    public sealed class GaArchiveValidation
    {
        public List<string> Errors { get; } = new();

        public List<string> Warnings { get; } = new();

        public int EntryCount { get; set; }

        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>Checks manufacturing files and the ZIP that carries them before anything is handed to the user.</summary>
    public sealed class GaArchiveValidator
    {
        private const int MinDxfBytes = 200;
        private const int MinPdfBytes = 300;

        public GaArchiveValidation ValidateFile(string name, byte[] content)
        {
            var validation = new GaArchiveValidation { EntryCount = 1 };
            CheckContent(name, content, validation);
            return validation;
        }

        public GaArchiveValidation Validate(byte[]? zip, IReadOnlyCollection<string> expectedEntries)
        {
            ArgumentNullException.ThrowIfNull(expectedEntries);

            var validation = new GaArchiveValidation();

            if (zip is null || zip.Length == 0)
            {
                validation.Errors.Add("The archive is empty.");
                return validation;
            }

            try
            {
                using var input = new MemoryStream(zip, writable: false);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in archive.Entries)
                {
                    validation.EntryCount++;

                    if (!seen.Add(entry.FullName))
                    {
                        validation.Errors.Add("Duplicate entry " + entry.FullName + ".");
                        continue;
                    }

                    byte[] bytes;
                    using (var stream = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        bytes = buffer.ToArray();
                    }

                    if (bytes.Length != entry.Length)
                    {
                        validation.Errors.Add("Entry " + entry.FullName + " has a length mismatch.");
                    }

                    CheckContent(entry.FullName, bytes, validation);
                }

                foreach (var expected in expectedEntries)
                {
                    if (!seen.Contains(expected))
                    {
                        validation.Errors.Add("Expected entry " + expected + " is missing from the archive.");
                    }
                }

                foreach (var name in seen)
                {
                    if (!expectedEntries.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && !name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        validation.Warnings.Add("Entry " + name + " was not expected.");
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                validation.Errors.Add("The archive is corrupt: " + ex.Message);
            }

            return validation;
        }

        private static void CheckContent(string name, byte[] content, GaArchiveValidation validation)
        {
            if (content.Length == 0)
            {
                validation.Errors.Add("Entry " + name + " is empty.");
                return;
            }

            if (name.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                CheckDxf(name, content, validation);
            }
            else if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                CheckPdf(name, content, validation);
            }
            else if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && content.Length < 3)
            {
                validation.Errors.Add("Entry " + name + " has no rows.");
            }
        }

        private static void CheckDxf(string name, byte[] content, GaArchiveValidation validation)
        {
            if (content.Length < MinDxfBytes)
            {
                validation.Errors.Add("DXF " + name + " is too small to hold a drawing.");
                return;
            }

            var head = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 64));
            if (head.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            {
                return;
            }

            var text = Encoding.ASCII.GetString(content);
            if (!text.Contains("SECTION", StringComparison.Ordinal))
            {
                validation.Errors.Add("DXF " + name + " has no SECTION.");
            }

            if (!text.Contains("ENTITIES", StringComparison.Ordinal))
            {
                validation.Errors.Add("DXF " + name + " has no ENTITIES section.");
            }

            if (!text.TrimEnd().EndsWith("EOF", StringComparison.Ordinal))
            {
                validation.Errors.Add("DXF " + name + " does not end with EOF.");
            }
        }

        private static void CheckPdf(string name, byte[] content, GaArchiveValidation validation)
        {
            if (content.Length < MinPdfBytes)
            {
                validation.Errors.Add("PDF " + name + " is too small to hold a drawing.");
                return;
            }

            var head = Encoding.ASCII.GetString(content, 0, 5);
            if (!string.Equals(head, "%PDF-", StringComparison.Ordinal))
            {
                validation.Errors.Add("PDF " + name + " has no PDF header.");
            }

            var tailStart = Math.Max(0, content.Length - 32);
            var tail = Encoding.ASCII.GetString(content, tailStart, content.Length - tailStart);
            if (!tail.Contains("%%EOF", StringComparison.Ordinal))
            {
                validation.Errors.Add("PDF " + name + " does not end with %%EOF.");
            }
        }
    }
}
