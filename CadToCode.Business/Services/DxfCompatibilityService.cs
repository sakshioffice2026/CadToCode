using System.Text;
using CadToCode.Business.Interfaces;

namespace CadToCode.Business.Services;

public class DxfCompatibilityService : IDxfCompatibilityService
{
    private const string TargetVersion = "AC1015";

    private static readonly Encoding ByteSafe = Encoding.Latin1;
    private static readonly byte[] BinarySentinel = Encoding.ASCII.GetBytes("AutoCAD Binary DXF");
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly Dictionary<string, string> VersionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AC1002"] = "AutoCAD R2.5",
        ["AC1003"] = "AutoCAD R2.6",
        ["AC1004"] = "AutoCAD R9",
        ["AC1006"] = "AutoCAD R10",
        ["AC1009"] = "AutoCAD R11/R12",
        ["AC1012"] = "AutoCAD R13",
        ["AC1014"] = "AutoCAD R14",
        ["AC1015"] = "AutoCAD 2000",
        ["AC1018"] = "AutoCAD 2004",
        ["AC1021"] = "AutoCAD 2007",
        ["AC1024"] = "AutoCAD 2010",
        ["AC1027"] = "AutoCAD 2013",
        ["AC1032"] = "AutoCAD 2018"
    };

    private static readonly HashSet<string> LegacyVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AC1002", "AC1003", "AC1004", "AC1006", "AC1009", "AC1012", "AC1014"
    };

    public async Task<DxfCompatibilityResult> PrepareAsync(Stream input, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        if (LooksLikeDwg(bytes))
        {
            var dwg = new DxfCompatibilityResult(new MemoryStream(bytes, writable: false))
            {
                IsDwg = true,
                OriginalVersionCode = Encoding.ASCII.GetString(bytes, 0, 6)
            };
            dwg.OriginalVersionName = DescribeVersion(dwg.OriginalVersionCode);
            dwg.Notes.Add("The file is a DWG binary drawing, not a DXF. Export it as DXF to process it.");
            return dwg;
        }

        if (StartsWith(bytes, BinarySentinel))
        {
            var binary = new DxfCompatibilityResult(new MemoryStream(bytes, writable: false))
            {
                IsBinaryDxf = true
            };
            binary.Notes.Add("Binary DXF detected; passed through without header patching.");
            return binary;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var bomLength = StartsWith(bytes, Utf8Bom) ? Utf8Bom.Length : 0;
        var text = ByteSafe.GetString(bytes, bomLength, bytes.Length - bomLength);
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var lines = text.Split('\n').ToList();

        var scan = ScanHeader(lines);

        if (scan.VersionLineIndex >= 0)
        {
            var current = lines[scan.VersionLineIndex].Trim();

            if (!LegacyVersions.Contains(current))
            {
                var passthrough = new DxfCompatibilityResult(new MemoryStream(bytes, writable: false))
                {
                    OriginalVersionCode = current,
                    OriginalVersionName = DescribeVersion(current),
                    TargetVersionCode = current
                };
                return passthrough;
            }

            lines[scan.VersionLineIndex] = Line(TargetVersion, crlf);

            var patched = Build(lines, bytes, bomLength);
            patched.OriginalVersionCode = current;
            patched.OriginalVersionName = DescribeVersion(current);
            patched.TargetVersionCode = TargetVersion;
            patched.HeaderPatched = true;
            patched.Notes.Add($"Header version {current} patched to {TargetVersion}.");
            return patched;
        }

        if (scan.HeaderSectionIndex >= 0)
        {
            var insert = new List<string>
            {
                Line("  9", crlf),
                Line("$ACADVER", crlf),
                Line("  1", crlf),
                Line(TargetVersion, crlf)
            };
            lines.InsertRange(scan.HeaderSectionIndex + 4, insert);
        }
        else
        {
            var insert = new List<string>
            {
                Line("  0", crlf),
                Line("SECTION", crlf),
                Line("  2", crlf),
                Line("HEADER", crlf),
                Line("  9", crlf),
                Line("$ACADVER", crlf),
                Line("  1", crlf),
                Line(TargetVersion, crlf),
                Line("  0", crlf),
                Line("ENDSEC", crlf)
            };
            lines.InsertRange(0, insert);
        }

        var injected = Build(lines, bytes, bomLength);
        injected.OriginalVersionCode = null;
        injected.OriginalVersionName = "Unversioned legacy DXF";
        injected.TargetVersionCode = TargetVersion;
        injected.HeaderPatched = true;
        injected.Notes.Add($"No version found in header; {TargetVersion} header injected.");
        return injected;
    }

    private static DxfCompatibilityResult Build(List<string> lines, byte[] original, int bomLength)
    {
        var payload = ByteSafe.GetBytes(string.Join("\n", lines));
        var output = new byte[bomLength + payload.Length];

        if (bomLength > 0)
        {
            Buffer.BlockCopy(original, 0, output, 0, bomLength);
        }

        Buffer.BlockCopy(payload, 0, output, bomLength, payload.Length);
        return new DxfCompatibilityResult(new MemoryStream(output, writable: false));
    }

    private static HeaderScan ScanHeader(List<string> lines)
    {
        var scan = new HeaderScan();

        for (var i = 0; i + 3 < lines.Count; i += 2)
        {
            if (lines[i].Trim() != "0" || !IsValue(lines[i + 1], "SECTION"))
            {
                continue;
            }

            if (lines[i + 2].Trim() != "2" || !IsValue(lines[i + 3], "HEADER"))
            {
                return scan;
            }

            scan.HeaderSectionIndex = i;

            for (var j = i + 4; j + 1 < lines.Count; j += 2)
            {
                var code = lines[j].Trim();

                if (code == "0" && IsValue(lines[j + 1], "ENDSEC"))
                {
                    return scan;
                }

                if (code == "9" && IsValue(lines[j + 1], "$ACADVER") && j + 3 < lines.Count)
                {
                    if (lines[j + 2].Trim() == "1")
                    {
                        scan.VersionLineIndex = j + 3;
                    }

                    return scan;
                }
            }

            return scan;
        }

        return scan;
    }

    private static bool IsValue(string line, string expected)
    {
        return string.Equals(line.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Line(string value, bool crlf)
    {
        return crlf ? value + "\r" : value;
    }

    private static string? DescribeVersion(string? code)
    {
        if (code is null)
        {
            return null;
        }

        return VersionNames.TryGetValue(code, out var name) ? name : "Unknown (" + code + ")";
    }

    private static bool LooksLikeDwg(byte[] bytes)
    {
        return bytes.Length >= 6
            && bytes[0] == (byte)'A'
            && bytes[1] == (byte)'C'
            && bytes[2] == (byte)'1'
            && bytes[3] >= (byte)'0'
            && bytes[3] <= (byte)'9';
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private sealed class HeaderScan
    {
        public int HeaderSectionIndex { get; set; } = -1;
        public int VersionLineIndex { get; set; } = -1;
    }
}