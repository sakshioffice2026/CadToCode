using System.Text;
using System.Text.RegularExpressions;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Templates;
using CadToCode.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace CadToCode.LLM.Services;

public sealed class CadTemplateMatcher : ICadTemplateMatcher
{
    private const double MinKeywordScore = 0.2;
    private const double StrongKeywordScore = 0.5;
    private const double ConfidentVectorScore = 0.75;
    private const double FullScoreWeight = 6.0;
    private const int MaxAttachmentTextChars = 2000;
    private const int MaxCorpusChars = 12000;

    private static readonly Regex CamelCase = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumeric = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly ICadRagRetriever _rag;
    private readonly ICadTemplateIndexer _indexer;
    private readonly ILogger<CadTemplateMatcher> _logger;
    private readonly IEquipmentManifestRegistry? _manifests;

    public CadTemplateMatcher(
        ICadRagRetriever rag,
        ICadTemplateIndexer indexer,
        ILogger<CadTemplateMatcher> logger,
        IEquipmentManifestRegistry? manifests = null)
    {
        _rag = rag;
        _indexer = indexer;
        _logger = logger;
        _manifests = manifests;
    }

    public async Task<CadTemplateMatch?> MatchAsync(
        ProjectResultDto? project,
        AssemblyTree? tree,
        string? prompt,
        string? drawingType,
        IReadOnlyList<CadAssistAttachmentDto>? attachments,
        CancellationToken cancellationToken = default)
    {
        var corpus = BuildCorpus(project, tree, prompt, drawingType, attachments);
        var requested = EquipmentRegistry.Resolve(corpus);

        if (_manifests is not null)
        {
            var resolution = _manifests.ResolveDetailed(corpus);

            if (resolution.IsAmbiguous)
            {
                _logger.LogInformation(
                    "Equipment is ambiguous ({Reason}); no template is chosen until the user confirms the type.",
                    resolution.Reason);
                return null;
            }

            if (resolution.IsResolved)
            {
                var fromManifest = MapManifestToEquipment(resolution.Manifest!);

                if (requested == EquipmentType.Unknown)
                {
                    requested = fromManifest;
                }
                else if (fromManifest != EquipmentType.Unknown && fromManifest != requested)
                {
                    _logger.LogInformation(
                        "Equipment sources disagree ({Registry} vs {Manifest}); no template is chosen.",
                        requested,
                        fromManifest);
                    return null;
                }
            }
        }

        var keywordMatch = MatchBuiltIn(corpus, requested);

        if (!_rag.IsAvailable)
        {
            return keywordMatch;
        }

        try
        {
            await _indexer.EnsureIndexedAsync(cancellationToken);

            var query = BuildQuery(project, tree, prompt, corpus);
            var hits = await _rag.RetrieveAsync(query, 3, CadLibraryFileType.CSharp, cancellationToken);

            var best = hits
                .Where(h => !string.IsNullOrWhiteSpace(h.CodeSnippet))
                .Where(h => IsCompatible(h.Category, h.Title, requested, h.Score))
                .OrderByDescending(h => h.Score)
                .FirstOrDefault();

            if (best is null)
            {
                return keywordMatch;
            }

            if (keywordMatch is not null
                && keywordMatch.Score >= StrongKeywordScore
                && best.Score < ConfidentVectorScore
                && !string.Equals(best.Category, keywordMatch.Category, StringComparison.OrdinalIgnoreCase))
            {
                return keywordMatch;
            }

            return new CadTemplateMatch
            {
                Title = best.Title,
                Category = best.Category,
                Source = CadTemplateSource.Vector,
                Score = best.Score,
                Script = best.CodeSnippet!,
                Description = best.MetadataSummary,
                MatchedKeywords = keywordMatch?.MatchedKeywords ?? new List<string>()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Template matching through Qdrant failed; using keyword matching.");
            return keywordMatch;
        }
    }

    private static EquipmentType MapManifestToEquipment(EquipmentManifest manifest)
    {
        var id = (manifest.Id ?? string.Empty).Trim().ToLowerInvariant();

        if (id.Contains("fan", StringComparison.Ordinal))
        {
            return EquipmentType.AxialFan;
        }

        if (id.Contains("cyclone", StringComparison.Ordinal))
        {
            return EquipmentType.Cyclone;
        }

        if (id.Contains("scrubber", StringComparison.Ordinal))
        {
            return EquipmentType.WetScrubber;
        }

        if (id.Contains("pressure-vessel", StringComparison.Ordinal))
        {
            return EquipmentType.PressureVessel;
        }

        if (id.Contains("pip", StringComparison.Ordinal))
        {
            return EquipmentType.PipingLayout;
        }

        return EquipmentType.Unknown;
    }

    private static CadTemplateMatch? MatchBuiltIn(string corpus, EquipmentType requested)
    {
        if (string.IsNullOrWhiteSpace(corpus))
        {
            return null;
        }

        var normalized = Normalize(corpus);
        var padded = " " + normalized + " ";
        var tokens = new HashSet<string>(normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        CadTemplateMatch? best = null;

        foreach (var template in BuiltInCadTemplates.All)
        {
            var weight = 0.0;
            var matched = new List<string>();

            if (ContainsTerm(padded, tokens, Normalize(template.Category)))
            {
                weight += 3.0;
                matched.Add(template.Category.ToLowerInvariant());
            }

            if (ContainsTerm(padded, tokens, Normalize(template.Title)))
            {
                weight += 3.0;
            }

            foreach (var tag in template.Tags)
            {
                var term = Normalize(tag);

                if (term.Length == 0 || matched.Contains(term))
                {
                    continue;
                }

                if (ContainsTerm(padded, tokens, term))
                {
                    weight += 1.0;
                    matched.Add(term);
                }
            }

            var score = Math.Min(1.0, weight / FullScoreWeight);

            if (score < MinKeywordScore || (best is not null && score <= best.Score))
            {
                continue;
            }

            if (!IsCompatible(template.Category, template.Title, requested, score))
            {
                continue;
            }

            best = new CadTemplateMatch
            {
                Title = template.Title,
                Category = template.Category,
                Source = CadTemplateSource.BuiltIn,
                Score = score,
                Script = template.Script,
                Description = template.Description,
                MatchedKeywords = matched
            };
        }

        return best;
    }

    private static bool IsCompatible(string? category, string? title, EquipmentType requested, double score)
    {
        var templateType = ClassifyTemplate(category, title);

        if (requested != EquipmentType.Unknown)
        {
            return templateType == EquipmentType.Unknown || templateType == requested;
        }

        // Equipment not identified: a piping template is only accepted on strong evidence.
        return templateType != EquipmentType.PipingLayout || score >= StrongKeywordScore;
    }

    private static EquipmentType ClassifyTemplate(string? category, string? title)
    {
        var text = Normalize((category ?? string.Empty) + " " + (title ?? string.Empty));

        if (text.Contains("scrubber", StringComparison.Ordinal))
        {
            return EquipmentType.WetScrubber;
        }

        if (text.Contains("cyclone", StringComparison.Ordinal))
        {
            return EquipmentType.Cyclone;
        }

        if (text.Contains("fan", StringComparison.Ordinal))
        {
            return EquipmentType.AxialFan;
        }

        if (text.Contains("pressure vessel", StringComparison.Ordinal))
        {
            return EquipmentType.PressureVessel;
        }

        if (text.Contains("pipe", StringComparison.Ordinal)
            || text.Contains("piping", StringComparison.Ordinal)
            || text.Contains("elbow", StringComparison.Ordinal)
            || text.Contains("flange", StringComparison.Ordinal))
        {
            return EquipmentType.PipingLayout;
        }

        return EquipmentType.Unknown;
    }

    private static bool ContainsTerm(string paddedText, HashSet<string> tokens, string term)
    {
        if (term.Length == 0)
        {
            return false;
        }

        if (term.Contains(' '))
        {
            return paddedText.Contains(" " + term + " ", StringComparison.Ordinal);
        }

        if (tokens.Contains(term))
        {
            return true;
        }

        return tokens.Any(t => t.Length <= term.Length + 2 && t.StartsWith(term, StringComparison.Ordinal));
    }

    private static string BuildQuery(ProjectResultDto? project, AssemblyTree? tree, string? prompt, string corpus)
    {
        var query = GeometryContextBuilder.BuildRetrievalQuery(project, tree, prompt);
        var keywords = corpus.Length > 600 ? corpus[..600] : corpus;

        return string.IsNullOrWhiteSpace(query)
            ? keywords
            : query + Environment.NewLine + "Keywords: " + keywords;
    }

    private static string BuildCorpus(
        ProjectResultDto? project,
        AssemblyTree? tree,
        string? prompt,
        string? drawingType,
        IReadOnlyList<CadAssistAttachmentDto>? attachments)
    {
        var sb = new StringBuilder();

        Append(sb, prompt);
        Append(sb, drawingType);

        if (project is not null)
        {
            Append(sb, project.Title);
            Append(sb, Path.GetFileNameWithoutExtension(project.OriginalFileName ?? string.Empty));

            foreach (var component in project.Components)
            {
                Append(sb, component.ComponentType.ToString());
                Append(sb, component.Name);
            }
        }

        if (tree is not null)
        {
            foreach (var node in tree.Nodes)
            {
                Append(sb, node.Name);

                foreach (var label in node.Labels)
                {
                    Append(sb, label);
                }
            }

            foreach (var assembly in tree.Assemblies)
            {
                Append(sb, assembly.AssemblyName);
                Append(sb, assembly.Category.ToString());
            }
        }

        if (attachments is not null)
        {
            foreach (var attachment in attachments.Where(a => a is not null))
            {
                Append(sb, Path.GetFileNameWithoutExtension(attachment.FileName ?? string.Empty));

                if (!string.IsNullOrWhiteSpace(attachment.TextContent))
                {
                    var text = attachment.TextContent;
                    Append(sb, text.Length > MaxAttachmentTextChars ? text[..MaxAttachmentTextChars] : text);
                }
            }
        }

        var corpus = sb.ToString();
        return corpus.Length > MaxCorpusChars ? corpus[..MaxCorpusChars] : corpus;
    }

    private static void Append(StringBuilder sb, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append(value.Trim()).Append(' ');
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var spaced = CamelCase.Replace(value, "$1 $2").ToLowerInvariant();
        return NonAlphaNumeric.Replace(spaced, " ").Trim();
    }
}