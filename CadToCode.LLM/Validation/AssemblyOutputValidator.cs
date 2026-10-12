using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadToCode.LLM.Prompts;
using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Validation;

public static class AssemblyOutputValidator
{
    private const int MaxCodeChars = 20000;
    private const int MaxNameChars = 60;
    private const int MaxSummaryChars = 200;
    private const int CoverageNodeLimit = 60;
    private const double MinCoverage = 0.6;
    private const double NumberAbsoluteTolerance = 0.0005;
    private const double NumberRelativeTolerance = 0.0001;

    private static readonly string[] StopMarkers = { "<|end|>", "<|endoftext|>", "<|user|>", "<|assistant|>", "<|system|>" };

    private static readonly Regex[] ForbiddenPatterns =
    {
        new(@"\busing\b", RegexOptions.Compiled),
        new(@"\bnamespace\b", RegexOptions.Compiled),
        new(@"\bunsafe\b", RegexOptions.Compiled),
        new(@"\bfixed\b", RegexOptions.Compiled),
        new(@"\bextern\b", RegexOptions.Compiled),
        new(@"\bstackalloc\b", RegexOptions.Compiled),
        new(@"\bgoto\b", RegexOptions.Compiled),
        new(@"\basync\b", RegexOptions.Compiled),
        new(@"\bawait\b", RegexOptions.Compiled),
        new(@"\btypeof\b", RegexOptions.Compiled),
        new(@"\bDllImport\b", RegexOptions.Compiled),
        new(@"\bMain\s*\(", RegexOptions.Compiled),
        new(@"\bGetType\s*\(", RegexOptions.Compiled),
        new(@"\bInvoke\s*\(", RegexOptions.Compiled),
        new(@"\bSystem\s*\.\s*(IO|Net|Diagnostics|Reflection|Runtime|Threading|Security|Data)\b", RegexOptions.Compiled),
        new(@"\b(File|Directory|Path|Process|HttpClient|WebClient|Socket|Thread|Task|Environment|Console|Activator|Assembly|AppDomain|Marshal|Type)\s*\.", RegexOptions.Compiled),
        new(@"#", RegexOptions.Compiled)
    };

    public static (AssemblyAnalysisResult Result, bool Retryable) ParseAnalysis(
        string raw,
        AssemblyTree tree,
        decimal minConfidence)
    {
        var json = ExtractJsonObject(raw);
        if (json == null)
        {
            return (FailAnalysis("The AI answer did not contain a JSON object.", raw), true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (FailAnalysis("The AI answer was not valid JSON.", raw), true);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (FailAnalysis("The AI answer was not a JSON object.", raw), true);
            }

            var assemblyName = Clean(GetString(root, "assemblyName"), MaxNameChars);
            if (assemblyName.Length == 0)
            {
                return (FailAnalysis("The AI answer had no assembly name.", raw), true);
            }

            if (!TryGetConfidence(root, out var confidence))
            {
                return (FailAnalysis("The AI answer had an invalid confidence value.", raw), true);
            }

            var knownIds = new HashSet<string>(tree.Nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var result = new AssemblyAnalysisResult
            {
                AssemblyName = assemblyName,
                Summary = Clean(GetString(root, "summary"), MaxSummaryChars),
                Confidence = confidence,
                RawResponse = Truncate(raw, 4000)
            };

            if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            {
                return (FailAnalysis("The AI answer had no nodes list.", raw), true);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in nodesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var nodeId = Clean(GetString(item, "nodeId"), 40);
                var name = Clean(GetString(item, "name"), MaxNameChars);

                if (nodeId.Length == 0 || name.Length == 0 || !knownIds.Contains(nodeId) || !seen.Add(nodeId))
                {
                    continue;
                }

                var nodeConfidence = confidence;
                if (TryGetConfidence(item, out var parsedNodeConfidence))
                {
                    nodeConfidence = parsedNodeConfidence;
                }

                var canonicalId = tree.Nodes.First(n => string.Equals(n.Id, nodeId, StringComparison.OrdinalIgnoreCase)).Id;

                result.Nodes.Add(new NodeSemanticName
                {
                    NodeId = canonicalId,
                    Name = name,
                    Role = NullIfEmpty(Clean(GetString(item, "role"), MaxNameChars)),
                    Confidence = nodeConfidence
                });
            }

            if (result.Nodes.Count == 0)
            {
                return (FailAnalysis("The AI answer named no known nodes.", raw), true);
            }

            if (root.TryGetProperty("relationships", out var relationsElement)
                && relationsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in relationsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var fromId = Clean(GetString(item, "fromId"), 40);
                    var toId = Clean(GetString(item, "toId"), 40);
                    var summary = Clean(GetString(item, "summary"), MaxSummaryChars);

                    if (summary.Length == 0 || !knownIds.Contains(fromId) || !knownIds.Contains(toId))
                    {
                        continue;
                    }

                    result.Relationships.Add(new RelationshipInsight
                    {
                        FromId = tree.Nodes.First(n => string.Equals(n.Id, fromId, StringComparison.OrdinalIgnoreCase)).Id,
                        ToId = tree.Nodes.First(n => string.Equals(n.Id, toId, StringComparison.OrdinalIgnoreCase)).Id,
                        Summary = summary
                    });
                }
            }

            if (confidence < minConfidence)
            {
                result.Success = false;
                result.Error = "The AI answer confidence was below the minimum.";
                return (result, false);
            }

            result.Success = true;
            return (result, false);
        }
    }

    public static (CodeSynthesisResult Result, bool Retryable) ValidateCode(string raw, CodeSynthesisRequest request)
    {
        var code = NormalizeCode(raw);

        if (code.Length == 0)
        {
            return (FailCode("The AI answer contained no code.", raw), true);
        }

        if (code.Length > MaxCodeChars)
        {
            return (FailCode("The AI answer was too long.", raw), true);
        }

        var scan = Scan(code);
        if (!scan.Ok)
        {
            return (FailCode(scan.Error, raw), true);
        }

        var sanitized = scan.Sanitized;
        var numbers = scan.Numbers;

        foreach (var pattern in ForbiddenPatterns)
        {
            if (pattern.IsMatch(sanitized))
            {
                return (FailCode("The generated code used a disallowed construct.", raw), true);
            }
        }

        var className = AssemblyPromptTemplates.ToIdentifier(request.ClassName, "Assembly");
        var classPattern = new Regex(@"\bpublic\s+(sealed\s+|partial\s+)?class\s+" + Regex.Escape(className) + @"\b");
        if (!classPattern.IsMatch(sanitized))
        {
            return (FailCode("The generated code did not declare the expected class.", raw), true);
        }

        var allowed = CollectAllowedNumbers(request.Tree);
        foreach (var number in numbers)
        {
            if (!IsAllowedNumber(Math.Abs(number), allowed))
            {
                return (FailCode("The generated code contained a number that was not measured.", raw), true);
            }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(sanitized, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)"))
        {
            declared.Add(match.Groups[1].Value);
        }

        var identifiers = AssemblyPromptTemplates.BuildIdentifierMap(request.Tree, request.Analysis);
        var expected = request.Tree.Nodes
            .OrderByDescending(n => n.Bounds.Area)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(CoverageNodeLimit)
            .ToList();

        var matched = expected.Count(n => identifiers.TryGetValue(n.Id, out var id) && declared.Contains(id));
        var coverage = expected.Count == 0 ? 1.0 : (double)matched / expected.Count;

        if (coverage < MinCoverage)
        {
            return (FailCode("The generated code did not cover enough of the assembly nodes.", raw), true);
        }

        return (new CodeSynthesisResult
        {
            Success = true,
            Code = code,
            Confidence = (decimal)Math.Round(coverage, 2),
            RawResponse = Truncate(raw, 4000)
        }, false);
    }

    private static string NormalizeCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw;

        foreach (var marker in StopMarkers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                text = text[..index];
            }
        }

        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var lineEnd = text.IndexOf('\n', fenceStart);
            if (lineEnd < 0)
            {
                return string.Empty;
            }

            var fenceEnd = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            text = fenceEnd >= 0 ? text[(lineEnd + 1)..fenceEnd] : text[(lineEnd + 1)..];
        }

        text = text.Replace("\r\n", "\n").Trim();

        var firstClass = Regex.Match(text, @"\b(public|internal)\s+(sealed\s+|partial\s+)?class\b");
        if (firstClass.Success && firstClass.Index > 0)
        {
            text = text[firstClass.Index..];
        }

        return text.Trim();
    }

    private sealed record ScanResult(bool Ok, string Sanitized, List<double> Numbers, string Error);

    private static ScanResult Scan(string code)
    {
        var clean = new StringBuilder(code.Length);
        var found = new List<double>();
        var stack = new Stack<char>();
        var length = code.Length;
        var i = 0;

        ScanResult Fail(string message)
        {
            return new ScanResult(false, clean.ToString(), found, message);
        }

        while (i < length)
        {
            var c = code[i];
            var next = i + 1 < length ? code[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < length && code[i] != '\n')
                {
                    clean.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return Fail("The generated code had an unterminated comment.");
                }

                for (var k = i; k < end + 2; k++)
                {
                    clean.Append(code[k] == '\n' ? '\n' : ' ');
                }

                i = end + 2;
                continue;
            }

            var quote = i;
            while (quote < length && (code[quote] == '$' || code[quote] == '@') && quote - i < 2)
            {
                quote++;
            }

            if (quote < length && code[quote] == '"')
            {
                var verbatim = code.Substring(i, quote - i).Contains('@');

                if (quote + 2 < length && code[quote + 1] == '"' && code[quote + 2] == '"')
                {
                    return Fail("Raw string literals are not allowed.");
                }

                var pos = quote + 1;
                var closed = false;

                while (pos < length)
                {
                    var ch = code[pos];

                    if (verbatim)
                    {
                        if (ch == '"')
                        {
                            if (pos + 1 < length && code[pos + 1] == '"')
                            {
                                pos += 2;
                                continue;
                            }

                            pos++;
                            closed = true;
                            break;
                        }

                        pos++;
                    }
                    else
                    {
                        if (ch == '\\')
                        {
                            pos += 2;
                            continue;
                        }

                        if (ch == '"')
                        {
                            pos++;
                            closed = true;
                            break;
                        }

                        if (ch == '\n')
                        {
                            break;
                        }

                        pos++;
                    }
                }

                if (!closed)
                {
                    return Fail("The generated code had an unterminated string.");
                }

                clean.Append("\"\"");
                i = pos;
                continue;
            }

            if (c == '$' || c == '@')
            {
                clean.Append(c);
                i++;
                continue;
            }

            if (c == '\'')
            {
                var pos = i + 1;
                var closed = false;

                while (pos < length && pos - i <= 10)
                {
                    if (code[pos] == '\\')
                    {
                        pos += 2;
                        continue;
                    }

                    if (code[pos] == '\'')
                    {
                        pos++;
                        closed = true;
                        break;
                    }

                    pos++;
                }

                if (!closed)
                {
                    return Fail("The generated code had an invalid character literal.");
                }

                clean.Append("' '");
                i = pos;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                {
                    clean.Append(code[i]);
                    i++;
                }

                continue;
            }

            var previous = i > 0 ? code[i - 1] : '\0';
            var startsNumber = char.IsDigit(c)
                || (c == '.' && char.IsDigit(next)
                    && !(char.IsLetterOrDigit(previous) || previous == ')' || previous == ']'));

            if (startsNumber)
            {
                if (c == '0' && (next == 'x' || next == 'X' || next == 'b' || next == 'B'))
                {
                    return Fail("Hex and binary literals are not allowed.");
                }

                var start = i;
                while (i < length)
                {
                    var ch = code[i];

                    if (char.IsDigit(ch))
                    {
                        i++;
                    }
                    else if (ch == '.' && i + 1 < length && char.IsDigit(code[i + 1]))
                    {
                        i++;
                    }
                    else if ((ch == 'e' || ch == 'E')
                        && i + 1 < length
                        && (char.IsDigit(code[i + 1])
                            || ((code[i + 1] == '+' || code[i + 1] == '-') && i + 2 < length && char.IsDigit(code[i + 2]))))
                    {
                        i += 2;
                    }
                    else
                    {
                        break;
                    }
                }

                var text = code.Substring(start, i - start);

                while (i < length && "dDfFmMlLuU".Contains(code[i]))
                {
                    i++;
                }

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !double.IsFinite(value))
                {
                    return Fail("The generated code had an unreadable number.");
                }

                found.Add(value);
                clean.Append('0');
                continue;
            }

            if (c == '(' || c == '[' || c == '{')
            {
                stack.Push(c);
            }
            else if (c == ')' || c == ']' || c == '}')
            {
                var expected = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (stack.Count == 0 || stack.Pop() != expected)
                {
                    return Fail("The generated code had unbalanced brackets.");
                }
            }

            clean.Append(c);
            i++;
        }

        if (stack.Count > 0)
        {
            return Fail("The generated code had unbalanced brackets.");
        }

        return new ScanResult(true, clean.ToString(), found, string.Empty);
    }

    private static List<double> CollectAllowedNumbers(AssemblyTree tree)
    {
        var allowed = new List<double> { 0, 1 };

        void Add(double value)
        {
            if (double.IsFinite(value))
            {
                allowed.Add(Math.Abs(value));
            }
        }

        Add(tree.Bounds.MinX);
        Add(tree.Bounds.MinY);
        Add(tree.Bounds.MaxX);
        Add(tree.Bounds.MaxY);
        Add(tree.Bounds.Width);
        Add(tree.Bounds.Height);
        Add(tree.Nodes.Count);
        Add(tree.Arrays.Count);
        Add(tree.Relations.Count);

        foreach (var node in tree.Nodes)
        {
            Add(node.Bounds.MinX);
            Add(node.Bounds.MinY);
            Add(node.Bounds.MaxX);
            Add(node.Bounds.MaxY);
            Add(node.Bounds.Width);
            Add(node.Bounds.Height);
            Add(node.Bounds.CenterX);
            Add(node.Bounds.CenterY);
            Add(node.Local.MinX);
            Add(node.Local.MinY);
            Add(node.Local.MaxX);
            Add(node.Local.MaxY);
            Add(node.LineCount);
            Add(node.ArcCount);
            Add(node.CircleCount);
            Add(node.PolylineCount);
            Add(node.HatchCount);

            foreach (var value in node.Parameters.Values)
            {
                Add(value);
            }
        }

        foreach (var array in tree.Arrays)
        {
            Add(array.Count);
            Add(array.Spacing);
            Add(array.DirectionDegrees);
            Add(array.CenterX);
            Add(array.CenterY);
            Add(array.Radius);
            Add(array.StartAngleDegrees);
            Add(array.AngleStepDegrees);
        }

        foreach (var relation in tree.Relations)
        {
            Add(relation.OffsetX);
            Add(relation.OffsetY);
            Add(relation.Gap);
            Add(relation.OverlapX);
            Add(relation.OverlapY);
        }

        return allowed;
    }

    private static bool IsAllowedNumber(double value, List<double> allowed)
    {
        foreach (var candidate in allowed)
        {
            if (Math.Abs(value - candidate) <= NumberAbsoluteTolerance + NumberRelativeTolerance * candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return raw.Substring(start, i - start + 1);
                }
            }
        }

        return null;
    }

    private static string GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool TryGetConfidence(JsonElement element, out decimal confidence)
    {
        confidence = 0;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "confidence", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double value;

            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                if (!property.Value.TryGetDouble(out value))
                {
                    return false;
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.String
                && double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
            }
            else
            {
                return false;
            }

            if (!double.IsFinite(value) || value < 0 || value > 100)
            {
                return false;
            }

            if (value > 1)
            {
                value /= 100.0;
            }

            confidence = (decimal)Math.Round(value, 2);
            return true;
        }

        return false;
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastSpace = false;

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastSpace = false;
        }

        var text = builder.ToString().Trim();
        return text.Length > maxLength ? text[..maxLength].Trim() : text;
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private static AssemblyAnalysisResult FailAnalysis(string error, string raw)
    {
        return new AssemblyAnalysisResult
        {
            Success = false,
            Error = error,
            RawResponse = Truncate(raw, 4000)
        };
    }

    private static CodeSynthesisResult FailCode(string error, string raw)
    {
        return new CodeSynthesisResult
        {
            Success = false,
            Error = error,
            RawResponse = Truncate(raw, 4000)
        };
    }
}