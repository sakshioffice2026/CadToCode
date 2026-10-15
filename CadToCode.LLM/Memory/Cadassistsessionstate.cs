using System.Collections.Concurrent;
using System.Globalization;
using CadToCode.LLM.Templates;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Memory;

public enum CadAssistStage
{
    None,
    Uploaded,
    Matched,
    AwaitingParameters,
    ParametersConfirmed,
    ScopeSelected,
    Generated,
    Refining
}

public sealed class DetectedComponentInfo
{
    public string Label { get; set; } = string.Empty;
    public string? MappedComponentKey { get; set; }
}

public sealed class DetectedViewInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed record ParameterEvidence(string VisionLabel, string Text, bool Corroborated);

public sealed class CadAssistSessionState
{
    public int ProjectId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public CadAssistStage Stage { get; set; } = CadAssistStage.None;

    public string? SourceFileName { get; set; }
    public bool SourceIsImage { get; set; }

    public string? TemplateKey { get; set; }
    public string? TemplateTitle { get; set; }
    public double MatchScore { get; set; }
    public List<DetectedComponentInfo> DetectedComponents { get; set; } = new();

    public List<DetectedViewInfo> VisionViews { get; set; } = new();
    public List<string> VisionUncertainties { get; set; } = new();
    public Dictionary<string, ParameterEvidence> Evidence { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ParameterValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DefaultsAccepted { get; set; }

    public bool FullAssembly { get; set; }
    public List<string> ScopeComponentKeys { get; set; } = new();

    public string? LastScript { get; set; }
    public string? LastFileBaseName { get; set; }
    public List<string> LastIncludedNames { get; set; } = new();

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public bool HasTemplate => !string.IsNullOrWhiteSpace(TemplateKey);

    public TemplateParameterSchema? Schema => TemplateParameterCatalog.Find(TemplateKey);

    public void SetTemplate(string templateKey, string templateTitle, double score, IEnumerable<DetectedComponentInfo> components)
    {
        TemplateKey = templateKey;
        TemplateTitle = templateTitle;
        MatchScore = score;
        DetectedComponents = components.ToList();
        Values = new Dictionary<string, ParameterValue>(StringComparer.OrdinalIgnoreCase);
        DefaultsAccepted = false;
        FullAssembly = false;
        ScopeComponentKeys = new List<string>();
        LastScript = null;
        LastFileBaseName = null;
        LastIncludedNames = new List<string>();
        Stage = CadAssistStage.Matched;
    }

    public void SetValue(string key, double value, ParameterSource source)
    {
        var schema = Schema;
        var canonical = schema?.FindParameter(key)?.Key ?? key;
        Values[canonical] = new ParameterValue(value, source);

        if (source != ParameterSource.ImageUnverified)
        {
            Evidence.Remove(canonical);
        }

        if (Stage == CadAssistStage.Generated)
        {
            Stage = CadAssistStage.Refining;
        }
        else if (Stage is CadAssistStage.Matched or CadAssistStage.AwaitingParameters)
        {
            Stage = CadAssistStage.AwaitingParameters;
        }
    }

    public void SetVisionData(IEnumerable<DetectedViewInfo> views, IEnumerable<string> uncertainties)
    {
        VisionViews = views.ToList();
        VisionUncertainties = uncertainties.ToList();
    }

    public void SetEvidence(string key, ParameterEvidence evidence)
    {
        var canonical = Schema?.FindParameter(key)?.Key ?? key;
        Evidence[canonical] = evidence;
    }

    public bool IsVerified(string key)
    {
        return !Values.TryGetValue(key, out var value) || value.Source != ParameterSource.ImageUnverified;
    }

    public IReadOnlyList<TemplateParameterDefinition> MissingParameters()
    {
        var schema = Schema;

        if (schema is null)
        {
            return Array.Empty<TemplateParameterDefinition>();
        }

        return schema.Parameters
            .Where(p => !Values.TryGetValue(p.Key, out var v) || v.Source is ParameterSource.Default or ParameterSource.ImageUnverified)
            .ToList();
    }

    public IReadOnlyList<TemplateParameterDefinition> MissingParametersFor(IEnumerable<string> componentKeys)
    {
        var schema = Schema;

        if (schema is null)
        {
            return Array.Empty<TemplateParameterDefinition>();
        }

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in componentKeys)
        {
            var component = schema.FindComponent(key);

            if (component is null)
            {
                continue;
            }

            foreach (var parameterKey in component.ParameterKeys)
            {
                needed.Add(parameterKey);
            }
        }

        return MissingParameters().Where(p => needed.Contains(p.Key)).ToList();
    }

    public bool ReadyToGenerate()
    {
        return HasTemplate && (DefaultsAccepted || MissingParameters().Count == 0);
    }

    public void AcceptDefaults()
    {
        DefaultsAccepted = true;

        if (Stage is CadAssistStage.Matched or CadAssistStage.AwaitingParameters)
        {
            Stage = CadAssistStage.ParametersConfirmed;
        }
    }

    public void ConfirmParameters()
    {
        if (Stage is CadAssistStage.Matched or CadAssistStage.AwaitingParameters)
        {
            Stage = CadAssistStage.ParametersConfirmed;
        }
    }

    public void SelectScope(bool fullAssembly, IEnumerable<string>? componentKeys)
    {
        FullAssembly = fullAssembly;
        ScopeComponentKeys = fullAssembly
            ? new List<string>()
            : (componentKeys ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Stage = CadAssistStage.ScopeSelected;
    }

    public void RecordGenerated(ComposedScriptResult result)
    {
        LastScript = result.Script;
        LastFileBaseName = result.FileBaseName;
        LastIncludedNames = result.IncludedScriptNames.ToList();
        Stage = CadAssistStage.Generated;
    }

    public ComposeRequest BuildComposeRequest()
    {
        return new ComposeRequest
        {
            TemplateKey = TemplateKey ?? string.Empty,
            ComponentKeys = FullAssembly ? Array.Empty<string>() : ScopeComponentKeys.ToList(),
            Values = new Dictionary<string, ParameterValue>(Values, StringComparer.OrdinalIgnoreCase)
        };
    }

    public string DescribeValues()
    {
        if (Values.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", Values
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value.Value.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    public CadAssistSessionState Clone()
    {
        return new CadAssistSessionState
        {
            ProjectId = ProjectId,
            SessionId = SessionId,
            Stage = Stage,
            SourceFileName = SourceFileName,
            SourceIsImage = SourceIsImage,
            TemplateKey = TemplateKey,
            TemplateTitle = TemplateTitle,
            MatchScore = MatchScore,
            DetectedComponents = DetectedComponents
                .Select(c => new DetectedComponentInfo { Label = c.Label, MappedComponentKey = c.MappedComponentKey })
                .ToList(),
            VisionViews = VisionViews
                .Select(v => new DetectedViewInfo { Name = v.Name, Description = v.Description })
                .ToList(),
            VisionUncertainties = VisionUncertainties.ToList(),
            Evidence = new Dictionary<string, ParameterEvidence>(Evidence, StringComparer.OrdinalIgnoreCase),
            Values = Values.ToDictionary(
                p => p.Key,
                p => new ParameterValue(p.Value.Value, p.Value.Source),
                StringComparer.OrdinalIgnoreCase),
            DefaultsAccepted = DefaultsAccepted,
            FullAssembly = FullAssembly,
            ScopeComponentKeys = ScopeComponentKeys.ToList(),
            LastScript = LastScript,
            LastFileBaseName = LastFileBaseName,
            LastIncludedNames = LastIncludedNames.ToList(),
            LastUsedUtc = LastUsedUtc
        };
    }
}

public interface ICadAssistSessionStore
{
    CadAssistSessionState Get(int projectId, string sessionId);

    CadAssistSessionState Update(int projectId, string sessionId, Action<CadAssistSessionState> mutate);

    void Reset(int projectId, string sessionId);
}

public sealed class CadAssistSessionStore : ICadAssistSessionStore
{
    private readonly LocalAiOptions _options;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public CadAssistSessionStore(IOptions<LocalAiOptions> options)
    {
        _options = options.Value;
    }

    public CadAssistSessionState Get(int projectId, string sessionId)
    {
        Evict();

        var entry = _entries.GetOrAdd(Key(projectId, sessionId), _ => NewEntry(projectId, sessionId));

        lock (entry.Lock)
        {
            entry.State.LastUsedUtc = DateTime.UtcNow;
            return entry.State.Clone();
        }
    }

    public CadAssistSessionState Update(int projectId, string sessionId, Action<CadAssistSessionState> mutate)
    {
        Evict();

        var entry = _entries.GetOrAdd(Key(projectId, sessionId), _ => NewEntry(projectId, sessionId));

        lock (entry.Lock)
        {
            mutate(entry.State);
            entry.State.LastUsedUtc = DateTime.UtcNow;
            return entry.State.Clone();
        }
    }

    public void Reset(int projectId, string sessionId)
    {
        _entries.TryRemove(Key(projectId, sessionId), out _);
    }

    private void Evict()
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, _options.SessionIdleMinutes));
        var cutoff = DateTime.UtcNow - idle;

        foreach (var pair in _entries)
        {
            if (pair.Value.State.LastUsedUtc < cutoff)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        var max = Math.Max(1, _options.MaxSessions);

        while (_entries.Count > max)
        {
            var oldest = _entries
                .OrderBy(p => p.Value.State.LastUsedUtc)
                .Select(p => p.Key)
                .FirstOrDefault();

            if (oldest is null || !_entries.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private static Entry NewEntry(int projectId, string sessionId)
    {
        return new Entry(new CadAssistSessionState { ProjectId = projectId, SessionId = sessionId });
    }

    private static string Key(int projectId, string sessionId)
    {
        return projectId.ToString(CultureInfo.InvariantCulture) + ":" + sessionId;
    }

    private sealed class Entry
    {
        public Entry(CadAssistSessionState state)
        {
            State = state;
        }

        public object Lock { get; } = new();

        public CadAssistSessionState State { get; }
    }
}