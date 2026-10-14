using System.Security.Cryptography;
using System.Text;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Templates;
using CadToCode.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace CadToCode.LLM.Services;

public sealed class CadTemplateIndexer : ICadTemplateIndexer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _done;

    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStoreService _vectors;
    private readonly ILogger<CadTemplateIndexer> _logger;

    public CadTemplateIndexer(
        IEmbeddingService embeddings,
        IVectorStoreService vectors,
        ILogger<CadTemplateIndexer> logger)
    {
        _embeddings = embeddings;
        _vectors = vectors;
        _logger = logger;
    }

    public bool IsAvailable => _embeddings.IsConfigured && _vectors.IsConfigured;

    public static Guid PointIdFor(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("cadtemplate:" + key.Trim().ToLowerInvariant()));
        return new Guid(hash);
    }

    public static string BuildEmbeddingText(CadTemplateDefinition template)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Title: " + template.Title);
        sb.AppendLine("Category: " + template.Category);
        sb.AppendLine("Tags: " + string.Join(", ", template.Tags));
        sb.AppendLine("Description: " + template.Description);
        sb.Append("Type: verified parametric C# STEP script template");

        return sb.ToString();
    }

    public async Task EnsureIndexedAsync(CancellationToken cancellationToken = default)
    {
        if (_done || !IsAvailable)
        {
            return;
        }

        try
        {
            var result = await IndexBuiltInAsync(false, cancellationToken);

            _logger.LogInformation(
                "Built-in CAD templates indexed: {Indexed} of {Total}, failed {Failed}.",
                result.Indexed, result.Total, result.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not index the built-in CAD templates into Qdrant.");
        }
    }

    public async Task<CadTemplateIndexResult> IndexBuiltInAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        var result = new CadTemplateIndexResult { Total = BuiltInCadTemplates.All.Count };

        if (!IsAvailable)
        {
            result.Errors.Add("Qdrant or the embedding service is not configured.");
            return result;
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (_done && !force)
            {
                result.Skipped = result.Total;
                return result;
            }

            await _vectors.EnsureCollectionAsync(cancellationToken);

            var templates = BuiltInCadTemplates.All
                .Where(t => !string.IsNullOrWhiteSpace(t.Script))
                .ToList();

            result.Skipped = result.Total - templates.Count;

            if (templates.Count == 0)
            {
                _done = true;
                return result;
            }

            var texts = templates.Select(BuildEmbeddingText).ToList();
            var vectors = await _embeddings.EmbedBatchAsync(texts, EmbeddingTaskType.RetrievalDocument, cancellationToken);

            if (vectors.Count != templates.Count)
            {
                throw new InvalidOperationException(
                    "The embedding service returned " + vectors.Count + " vectors for " + templates.Count + " templates.");
            }

            for (var i = 0; i < templates.Count; i++)
            {
                var template = templates[i];

                try
                {
                    await _vectors.UpsertAsync(
                        new VectorUpsertRequest
                        {
                            PointId = PointIdFor(template.Key),
                            Vector = vectors[i],
                            LibraryItemId = -(i + 1),
                            Title = template.Title,
                            Category = template.Category,
                            FileType = CadLibraryFileType.CSharp,
                            Tags = string.Join(", ", template.Tags),
                            CodeSnippet = template.Script,
                            MetadataSummary = template.Description
                        },
                        cancellationToken);

                    result.Indexed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Failed++;
                    result.Errors.Add(template.Title + ": " + ex.Message);
                    _logger.LogWarning(ex, "Could not index CAD template {Template}.", template.Key);
                }
            }

            _done = result.Failed == 0;
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}