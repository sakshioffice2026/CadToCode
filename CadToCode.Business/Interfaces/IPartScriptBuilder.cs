using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;

namespace CadToCode.Business.Interfaces;

/// <summary>
/// Input for building one script per manufacturable part.
/// </summary>
public sealed class PartScriptRequest
{
    public EquipmentManifest Manifest { get; init; } = null!;

    /// <summary>Output of the equipment's calculator-to-template mapper. Must have succeeded.</summary>
    public TemplateMappingResult Mapping { get; init; } = null!;

    /// <summary>Part keys or titles to build. Empty means every part the manifest declares.</summary>
    public IReadOnlyList<string> PartKeys { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One part, built as a script that creates exactly one body.
/// </summary>
public sealed class PartScript
{
    public string PartKey { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Name of the single solid the script creates.</summary>
    public string BodyName { get; init; } = string.Empty;

    /// <summary>How many identical copies of the body the finished equipment needs.</summary>
    public int Quantity { get; init; } = 1;

    public bool IsRequired { get; init; } = true;

    public string Script { get; init; } = string.Empty;

    public string FileBaseName { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, double> AppliedValues { get; init; } = new Dictionary<string, double>();
}

public sealed class PartScriptResult
{
    public bool Success { get; init; }

    public string ManifestId { get; init; } = string.Empty;

    public IReadOnlyList<PartScript> Parts { get; init; } = Array.Empty<PartScript>();

    /// <summary>Required manifest parts that were not built. The export is incomplete while any remain.</summary>
    public IReadOnlyList<string> MissingRequiredPartKeys { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool IsCompleteSet => Success && MissingRequiredPartKeys.Count == 0;

    public static PartScriptResult Fail(params string[] errors)
    {
        return new PartScriptResult { Success = false, Errors = errors };
    }
}

/// <summary>
/// Builds one script per manufacturable part so each part exports as its own solid.
/// Builders never default or invent values: everything comes from the mapper result.
/// </summary>
public interface IPartScriptBuilder
{
    /// <summary>Stable id for logs and audit, for example "template-part-script-builder".</summary>
    string BuilderId { get; }

    /// <summary>True when this builder can build the parts the manifest declares.</summary>
    bool CanBuild(EquipmentManifest manifest);

    /// <summary>Builds the requested parts. Never throws for bad data; failures are returned in <see cref="PartScriptResult.Errors"/>.</summary>
    PartScriptResult Build(PartScriptRequest request);
}

/// <summary>
/// Resolves the part script builder for a manifest.
/// </summary>
public interface IPartScriptBuilderRegistry
{
    IReadOnlyCollection<IPartScriptBuilder> All { get; }

    /// <summary>Returns the single builder that can build the manifest, or false when none or several can.</summary>
    bool TryGetFor(EquipmentManifest manifest, out IPartScriptBuilder builder);

    /// <exception cref="NotSupportedException">Thrown when no builder, or more than one, can build the manifest.</exception>
    IPartScriptBuilder GetFor(EquipmentManifest manifest);
}

/// <summary>
/// Default <see cref="IPartScriptBuilderRegistry"/>. Built from all builders registered in dependency injection.
/// A manifest must match exactly one builder; ambiguity is an error, not a silent pick.
/// </summary>
public sealed class PartScriptBuilderRegistry : IPartScriptBuilderRegistry
{
    private readonly IReadOnlyCollection<IPartScriptBuilder> _all;

    public PartScriptBuilderRegistry(IEnumerable<IPartScriptBuilder> builders)
    {
        ArgumentNullException.ThrowIfNull(builders);

        var list = new List<IPartScriptBuilder>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builder in builders)
        {
            if (builder is null)
            {
                throw new ArgumentException("Builder collection contains a null entry.", nameof(builders));
            }

            if (string.IsNullOrWhiteSpace(builder.BuilderId))
            {
                throw new ArgumentException(
                    "Builder '" + builder.GetType().Name + "' has an empty BuilderId and cannot be registered.",
                    nameof(builders));
            }

            if (!ids.Add(builder.BuilderId.Trim()))
            {
                throw new ArgumentException(
                    "More than one part script builder uses the id '" + builder.BuilderId.Trim() + "'.",
                    nameof(builders));
            }

            list.Add(builder);
        }

        _all = list.AsReadOnly();
    }

    public IReadOnlyCollection<IPartScriptBuilder> All => _all;

    public bool TryGetFor(EquipmentManifest manifest, out IPartScriptBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var matches = _all.Where(b => b.CanBuild(manifest)).ToList();

        if (matches.Count == 1)
        {
            builder = matches[0];
            return true;
        }

        builder = null!;
        return false;
    }

    public IPartScriptBuilder GetFor(EquipmentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var matches = _all.Where(b => b.CanBuild(manifest)).ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new NotSupportedException(
            matches.Count == 0
                ? "No part script builder can build the parts of manifest '" + manifest.Id + "'. Do not substitute a generic template."
                : "More than one part script builder can build manifest '" + manifest.Id + "': "
                  + string.Join(", ", matches.Select(m => m.BuilderId)) + ".");
    }
}
