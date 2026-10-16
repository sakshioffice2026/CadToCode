using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;

namespace CadToCode.Core.Mapping;

/// <summary>Stable codes for mapping findings.</summary>
public static class MappingIssueCodes
{
    public const string SourceMissing = "MAP_SOURCE_MISSING";
    public const string InputMissing = "MAP_INPUT_MISSING";
    public const string InputInvalid = "MAP_INPUT_INVALID";
    public const string NotCadReady = "MAP_NOT_CAD_READY";
    public const string WrongEquipment = "MAP_WRONG_EQUIPMENT";
    public const string DuplicateTarget = "MAP_DUPLICATE_TARGET";
    public const string NotFinite = "MAP_VALUE_NOT_FINITE";
    public const string DerivedValue = "MAP_DERIVED_VALUE";
    public const string ManifestMismatch = "MAP_MANIFEST_MISMATCH";
}

/// <summary>
/// A single mapping finding.
/// </summary>
/// <param name="Code">Stable machine code, see <see cref="MappingIssueCodes"/>.</param>
/// <param name="Message">Human-readable message suitable for a clarifying prompt.</param>
/// <param name="Key">Source or target key the finding refers to.</param>
/// <param name="IsBlocking">True when the mapped values must not be used for CAD generation.</param>
public sealed record MappingIssue(string Code, string Message, string? Key = null, bool IsBlocking = true);

/// <summary>
/// One mapped target value with provenance.
/// </summary>
/// <param name="Key">Target key (template parameter or script composer key).</param>
/// <param name="Value">Value in <paramref name="Unit"/>.</param>
/// <param name="Unit">Target unit symbol, for example "mm", "deg", "m", "-".</param>
/// <param name="Origin">Where the value came from.</param>
/// <param name="SourceKeys">Calculator or user keys the value was produced from.</param>
/// <param name="Rule">Short description of the conversion applied.</param>
public sealed record MappedParameter(
    string Key,
    double Value,
    string Unit,
    ValueOrigin Origin,
    IReadOnlyList<string> SourceKeys,
    string Rule);

/// <summary>
/// Output of a calculator-to-template mapper. Immutable once built.
/// </summary>
public sealed class TemplateMappingResult
{
    private readonly Dictionary<string, MappedParameter> _byKey;

    public string MapperId { get; }

    public EquipmentType EquipmentType { get; }

    /// <summary>Catalog template the values target. Null when the equipment is composed directly from SI values.</summary>
    public string? TemplateKey { get; }

    public IReadOnlyList<MappedParameter> Parameters { get; }

    public IReadOnlyList<MappingIssue> Issues { get; }

    /// <summary>Keys the user must still supply. Drives the clarifying-question generator.</summary>
    public IReadOnlyList<string> MissingInputKeys { get; }

    /// <summary>True when at least one value was mapped and no blocking issue exists.</summary>
    public bool Succeeded => Parameters.Count > 0 && Issues.All(i => !i.IsBlocking);

    public IEnumerable<MappingIssue> BlockingIssues => Issues.Where(i => i.IsBlocking);

    private TemplateMappingResult(
        string mapperId,
        EquipmentType equipmentType,
        string? templateKey,
        IEnumerable<MappedParameter> parameters,
        IEnumerable<MappingIssue> issues)
    {
        MapperId = mapperId;
        EquipmentType = equipmentType;
        TemplateKey = string.IsNullOrWhiteSpace(templateKey) ? null : templateKey.Trim();

        var parameterList = parameters.ToList();
        _byKey = new Dictionary<string, MappedParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameterList)
            _byKey[p.Key] = p;

        var issueList = issues.ToList();

        Parameters = new ReadOnlyCollection<MappedParameter>(parameterList);
        Issues = new ReadOnlyCollection<MappingIssue>(issueList);
        MissingInputKeys = new ReadOnlyCollection<string>(
            issueList
                .Where(i => i.Code == MappingIssueCodes.InputMissing && !string.IsNullOrWhiteSpace(i.Key))
                .Select(i => i.Key!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public MappedParameter? Find(string key)
        => !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key.Trim(), out var p) ? p : null;

    public bool TryGetValue(string key, out double value)
    {
        var found = Find(key);
        value = found?.Value ?? default;
        return found is not null;
    }

    /// <summary>Case-insensitive copy of the mapped values as target key to number in the target unit.</summary>
    public IReadOnlyDictionary<string, double> AsDictionary()
        => new ReadOnlyDictionary<string, double>(
            Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>Plain-language reasons the mapped values cannot be used. Empty when <see cref="Succeeded"/> is true.</summary>
    public IReadOnlyList<string> GetBlockers()
    {
        var blockers = BlockingIssues.Select(i => i.Message).ToList();

        if (blockers.Count == 0 && Parameters.Count == 0)
            blockers.Add("No values were mapped.");

        return blockers;
    }

    public static TemplateMappingResult Failure(
        string mapperId,
        EquipmentType equipmentType,
        IEnumerable<MappingIssue> issues,
        string? templateKey = null)
        => new(
            mapperId,
            equipmentType,
            templateKey,
            Enumerable.Empty<MappedParameter>(),
            issues ?? throw new ArgumentNullException(nameof(issues)));

    public static Builder Create(string mapperId, EquipmentType equipmentType, string? templateKey = null)
        => new(mapperId, equipmentType, templateKey);

    public sealed class Builder
    {
        private readonly string _mapperId;
        private readonly EquipmentType _equipmentType;
        private readonly string? _templateKey;
        private readonly List<MappedParameter> _parameters = new();
        private readonly List<MappingIssue> _issues = new();

        internal Builder(string mapperId, EquipmentType equipmentType, string? templateKey)
        {
            if (string.IsNullOrWhiteSpace(mapperId))
                throw new ArgumentException("Mapper id is required.", nameof(mapperId));

            _mapperId = mapperId;
            _equipmentType = equipmentType;
            _templateKey = templateKey;
        }

        public bool HasValue(string key)
            => !string.IsNullOrWhiteSpace(key)
               && _parameters.Any(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

        public Builder AddValue(MappedParameter parameter)
        {
            if (parameter is null) throw new ArgumentNullException(nameof(parameter));
            if (string.IsNullOrWhiteSpace(parameter.Key))
                throw new ArgumentException("Target key is required.", nameof(parameter));

            if (!double.IsFinite(parameter.Value))
            {
                AddIssue(
                    MappingIssueCodes.NotFinite,
                    "Mapped value '" + parameter.Key + "' is not a finite number.",
                    parameter.Key);
                return this;
            }

            if (HasValue(parameter.Key))
            {
                AddIssue(
                    MappingIssueCodes.DuplicateTarget,
                    "Target '" + parameter.Key + "' was mapped more than once.",
                    parameter.Key);
                return this;
            }

            _parameters.Add(parameter);
            return this;
        }

        public Builder AddValue(
            string key,
            double value,
            string unit,
            ValueOrigin origin,
            IEnumerable<string> sourceKeys,
            string rule)
            => AddValue(new MappedParameter(
                key,
                value,
                unit,
                origin,
                new ReadOnlyCollection<string>((sourceKeys ?? Enumerable.Empty<string>()).ToList()),
                rule));

        public Builder AddIssue(MappingIssue issue)
        {
            if (issue is null) throw new ArgumentNullException(nameof(issue));
            _issues.Add(issue);
            return this;
        }

        public Builder AddIssue(string code, string message, string? key = null, bool isBlocking = true)
            => AddIssue(new MappingIssue(code, message, key, isBlocking));

        public TemplateMappingResult Build()
            => new(_mapperId, _equipmentType, _templateKey, _parameters, _issues);
    }
}

/// <summary>
/// Converts a deterministic <see cref="CalculationResult"/> into the values a CAD template
/// or script composer consumes. Mappers contain no physics and never invent engineering values:
/// anything the calculator does not produce must come from the user or be reported as missing.
/// </summary>
public interface ICalculatorTemplateMapper
{
    /// <summary>Id that equipment manifests use in <c>mapperId</c>, for example "cyclone-template-mapper".</summary>
    string MapperId { get; }

    /// <summary>Equipment type this mapper serves.</summary>
    EquipmentType EquipmentType { get; }

    /// <summary>Catalog template keys the mapper can target. Empty when the equipment is composed directly from SI values.</summary>
    IReadOnlyList<string> TemplateKeys { get; }

    /// <summary>Keys the calculator does not produce and the user must supply before mapping can succeed.</summary>
    IReadOnlyList<string> RequiredUserInputKeys => Array.Empty<string>();

    /// <summary>
    /// Maps calculated values to target values. Never throws for bad data: problems are returned as
    /// <see cref="MappingIssue"/> entries and the result is not <see cref="TemplateMappingResult.Succeeded"/>.
    /// </summary>
    /// <param name="result">Calculator output. Must be CAD ready.</param>
    /// <param name="manifest">Manifest of the equipment, supplying declarative parameter mappings.</param>
    /// <param name="userSupplied">User-confirmed values keyed by <see cref="RequiredUserInputKeys"/>, in SI units.</param>
    TemplateMappingResult Map(
        CalculationResult result,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double>? userSupplied = null);
}

/// <summary>
/// Resolves mappers by the mapper id declared in an equipment manifest.
/// </summary>
public interface ICalculatorTemplateMapperRegistry
{
    IReadOnlyCollection<ICalculatorTemplateMapper> All { get; }

    /// <summary>Ids of all registered mappers. Feeds <c>ConsistencyInputs.MapperIds</c>.</summary>
    IReadOnlyCollection<string> Ids { get; }

    bool TryGet(string? mapperId, out ICalculatorTemplateMapper mapper);

    /// <exception cref="NotSupportedException">Thrown when no mapper has that id. Callers must not substitute a generic template.</exception>
    ICalculatorTemplateMapper Get(string? mapperId);

    /// <summary>Returns the mapper a manifest declares, or false when it declares none or it is not registered.</summary>
    bool TryGetFor(EquipmentManifest manifest, out ICalculatorTemplateMapper mapper)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));

        return TryGet(manifest.MapperId, out mapper);
    }
}

/// <summary>
/// Default <see cref="ICalculatorTemplateMapperRegistry"/>. Built from all mappers registered in dependency injection.
/// </summary>
public sealed class CalculatorTemplateMapperRegistry : ICalculatorTemplateMapperRegistry
{
    private readonly IReadOnlyDictionary<string, ICalculatorTemplateMapper> _byId;
    private readonly IReadOnlyCollection<ICalculatorTemplateMapper> _all;
    private readonly IReadOnlyCollection<string> _ids;

    public CalculatorTemplateMapperRegistry(IEnumerable<ICalculatorTemplateMapper> mappers)
    {
        if (mappers is null) throw new ArgumentNullException(nameof(mappers));

        var byId = new Dictionary<string, ICalculatorTemplateMapper>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapper in mappers)
        {
            if (mapper is null)
                throw new ArgumentException("Mapper collection contains a null entry.", nameof(mappers));

            if (string.IsNullOrWhiteSpace(mapper.MapperId))
                throw new ArgumentException(
                    "Mapper '" + mapper.GetType().Name + "' has an empty MapperId and cannot be registered.",
                    nameof(mappers));

            var id = mapper.MapperId.Trim();

            if (!byId.TryAdd(id, mapper))
                throw new ArgumentException(
                    "More than one mapper uses the id '" + id + "': '" +
                    byId[id].GetType().Name + "' and '" + mapper.GetType().Name + "'.",
                    nameof(mappers));
        }

        _byId = byId;
        _all = byId.Values.ToList().AsReadOnly();
        _ids = byId.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public IReadOnlyCollection<ICalculatorTemplateMapper> All => _all;

    public IReadOnlyCollection<string> Ids => _ids;

    public bool TryGet(string? mapperId, out ICalculatorTemplateMapper mapper)
    {
        if (!string.IsNullOrWhiteSpace(mapperId) && _byId.TryGetValue(mapperId.Trim(), out var found))
        {
            mapper = found;
            return true;
        }

        mapper = null!;
        return false;
    }

    public ICalculatorTemplateMapper Get(string? mapperId)
        => TryGet(mapperId, out var mapper)
            ? mapper
            : throw new NotSupportedException(
                "No mapper is registered with id '" + mapperId + "'. Ask the user to clarify the equipment; do not substitute a generic template.");
}

/// <summary>
/// Shared helpers for mappers that apply the manifest's declarative <c>parameterMappings</c>.
/// </summary>
internal static class DeclarativeMapping
{
    /// <summary>Template and script values are rounded to three decimals (micrometres when the unit is mm).</summary>
    public static double RoundValue(double value)
        => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Applies every manifest parameter mapping: target = source x scale. A missing source is reported
    /// as a blocking issue; nothing is defaulted.
    /// </summary>
    public static void Apply(
        TemplateMappingResult.Builder builder,
        EquipmentManifest manifest,
        IReadOnlyDictionary<string, double> calculated)
    {
        foreach (var map in manifest.ParameterMappings)
        {
            if (!calculated.TryGetValue(map.SourceKey, out var source))
            {
                builder.AddIssue(
                    MappingIssueCodes.SourceMissing,
                    "Calculated value '" + map.SourceKey + "' is required for '" + map.TargetKey + "' but was not produced.",
                    map.SourceKey);
                continue;
            }

            builder.AddValue(
                map.TargetKey,
                RoundValue(source * map.Scale),
                TargetUnit(manifest, map),
                ValueOrigin.Calculated,
                new[] { map.SourceKey },
                "manifest mapping, scale " + map.Scale.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static string TargetUnit(EquipmentManifest manifest, ParameterMapping map)
    {
        var output = manifest.Outputs.FirstOrDefault(
            o => string.Equals(o.Key, map.SourceKey, StringComparison.OrdinalIgnoreCase));

        var unit = output?.Unit ?? string.Empty;

        if (string.Equals(unit, "m", StringComparison.OrdinalIgnoreCase) && Math.Abs(map.Scale - 1000.0) < 1e-9)
            return "mm";

        return string.IsNullOrWhiteSpace(unit) ? "-" : unit;
    }
}
