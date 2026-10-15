using System.Security.Cryptography;
using System.Text;
using CadToCode.Core.Domain;

namespace CadToCode.Core.Knowledge;

/// <summary>
/// Deterministic text chunker. Splits on headings and paragraphs, packs paragraphs
/// up to a size limit, and carries a small overlap between consecutive chunks.
/// Chunk ids are stable for identical input.
/// </summary>
public static class KnowledgeChunker
{
    public const int DefaultMaxChars = 1000;
    public const int DefaultOverlapChars = 150;

    public static IReadOnlyList<KnowledgeChunk> Chunk(
        EquipmentType equipmentType,
        string source,
        string text,
        int maxChars = DefaultMaxChars,
        int overlapChars = DefaultOverlapChars)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("source is required.", nameof(source));
        if (maxChars < 100)
            throw new ArgumentOutOfRangeException(nameof(maxChars), "maxChars must be at least 100.");
        if (overlapChars < 0 || overlapChars >= maxChars)
            throw new ArgumentOutOfRangeException(nameof(overlapChars), "overlapChars must be >= 0 and < maxChars.");

        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<KnowledgeChunk>();

        var chunks = new List<KnowledgeChunk>();
        var buffer = new StringBuilder();
        var title = source;
        var index = 0;

        void Flush()
        {
            var content = buffer.ToString().Trim();
            if (content.Length == 0)
                return;

            chunks.Add(new KnowledgeChunk(
                Id: BuildId(source, index++, content),
                EquipmentType: equipmentType,
                Source: source,
                Title: title,
                Content: content,
                Tags: BuildTags(title)));

            buffer.Clear();
            if (overlapChars > 0 && content.Length > overlapChars)
                buffer.Append(content.AsSpan(content.Length - overlapChars)).Append("\n\n");
        }

        foreach (var block in SplitBlocks(text))
        {
            if (IsHeading(block, out var headingText))
            {
                Flush();
                buffer.Clear();
                title = headingText;
                continue;
            }

            foreach (var piece in SplitOversize(block, maxChars))
            {
                if (buffer.Length > 0 && buffer.Length + piece.Length + 2 > maxChars)
                    Flush();

                if (buffer.Length > 0)
                    buffer.Append("\n\n");
                buffer.Append(piece);
            }
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> SplitBlocks(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var raw in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var block = raw.Trim();
            if (block.Length > 0)
                yield return block;
        }
    }

    private static bool IsHeading(string block, out string heading)
    {
        heading = string.Empty;
        if (block.Contains('\n') || !block.StartsWith('#'))
            return false;

        heading = block.TrimStart('#').Trim();
        return heading.Length > 0;
    }

    private static IEnumerable<string> SplitOversize(string block, int maxChars)
    {
        if (block.Length <= maxChars)
        {
            yield return block;
            yield break;
        }

        var sentences = block.Split(new[] { ". ", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();
            if (s.Length == 0)
                continue;

            while (s.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                yield return s[..maxChars];
                s = s[maxChars..];
            }

            if (current.Length > 0 && current.Length + s.Length + 2 > maxChars)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(". ");
            current.Append(s);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static IReadOnlyList<string> BuildTags(string title) =>
        title
            .Split(new[] { ' ', '-', '_', '/', ',', ':', ';', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 2)
            .Distinct()
            .ToArray();

    private static string BuildId(string source, int index, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{index}|{content}"));
        return $"{Sanitize(source)}-{index:D4}-{Convert.ToHexString(bytes, 0, 4).ToLowerInvariant()}";
    }

    private static string Sanitize(string source)
    {
        var sb = new StringBuilder(source.Length);
        foreach (var c in source)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        return sb.ToString().Trim('-');
    }
}
