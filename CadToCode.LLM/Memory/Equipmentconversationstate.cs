using System.Collections.Concurrent;

namespace CadToCode.LLM.Memory;

/// <summary>
/// Where a single equipment type stands in the guided design conversation.
/// Each equipment keeps its own stage; nothing is shared between equipment types.
/// </summary>
public enum EquipmentConversationStage
{
    /// <summary>The equipment is selected. Dimension and duty values are being collected.</summary>
    AwaitingParameters = 0,

    /// <summary>All required values are present and the calculator output passed the rulepack limits.</summary>
    Calculated = 1,

    /// <summary>"What would you like to do next?" was shown. Waiting for the user's choice.</summary>
    AwaitingNextAction = 2,

    /// <summary>A script (part or assembly) was generated. The menu can be shown again.</summary>
    Generated = 3
}

/// <summary>Session level stage. Decides whether the equipment type is known yet.</summary>
public enum EquipmentSessionStage
{
    /// <summary>No equipment flow is in progress.</summary>
    None = 0,

    /// <summary>An image was uploaded and the user has not confirmed the equipment type yet.</summary>
    AwaitingEquipmentType = 1,

    /// <summary>The equipment type is confirmed and a per-equipment state is active.</summary>
    Active = 2
}

/// <summary>Choices offered by the post-calculation menu.</summary>
public enum EquipmentNextAction
{
    None = 0,
    ViewBreakdown = 1,
    GeneratePartScripts = 2,
    GenerateAssembly = 3,
    ChangeValues = 4
}

/// <summary>
/// Conversation state of one equipment type (for example cyclone-separator or centrifugal-fan).
/// </summary>
public sealed class EquipmentConversationState
{
    public EquipmentConversationState(string manifestId)
    {
        if (string.IsNullOrWhiteSpace(manifestId))
        {
            throw new ArgumentException("A manifest id is required.", nameof(manifestId));
        }

        ManifestId = manifestId.Trim();
    }

    public string ManifestId { get; }

    public EquipmentConversationStage Stage { get; set; } = EquipmentConversationStage.AwaitingParameters;

    /// <summary>SI values collected so far, keyed by calculator and mapper keys.</summary>
    public Dictionary<string, double> Values { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys that were filled from the built-in engineering defaults, not typed by the user.</summary>
    public HashSet<string> DefaultedKeys { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once the user said to use defaults for this equipment.</summary>
    public bool DefaultsAccepted { get; set; }

    /// <summary>True only after the calculator ran and the result passed the rulepack boundaries.</summary>
    public bool CalculationValidated { get; set; }

    /// <summary>Part keys or "Assembly" from the last generation. Empty when nothing was generated.</summary>
    public List<string> LastGeneratedScope { get; private set; } = new();

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public bool MenuShown => Stage is EquipmentConversationStage.AwaitingNextAction
                             or EquipmentConversationStage.Generated;

    /// <summary>Merges values read from a user message. User values replace earlier values and defaults.</summary>
    public void MergeUserValues(IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var pair in values)
        {
            Values[pair.Key] = pair.Value;
            DefaultedKeys.Remove(pair.Key);
        }

        if (values.Count > 0)
        {
            InvalidateCalculation();
        }
    }

    /// <summary>
    /// Fills only the keys that are still missing from the built-in defaults of this equipment type.
    /// Values the user already gave are never overwritten.
    /// </summary>
    public int ApplyDefaults(IReadOnlyDictionary<string, double> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var added = 0;

        foreach (var pair in defaults)
        {
            if (Values.ContainsKey(pair.Key))
            {
                continue;
            }

            Values[pair.Key] = pair.Value;
            DefaultedKeys.Add(pair.Key);
            added++;
        }

        DefaultsAccepted = true;

        if (added > 0)
        {
            InvalidateCalculation();
        }

        return added;
    }

    /// <summary>Replaces the stored values with the merged set returned by a design flow turn.</summary>
    public void ReplaceValues(IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        Values = new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase);
        DefaultedKeys.RemoveWhere(key => !Values.ContainsKey(key));
    }

    /// <summary>Called when the calculator output passed validation against the rulepack boundaries.</summary>
    public void MarkCalculated()
    {
        CalculationValidated = true;

        if (Stage == EquipmentConversationStage.AwaitingParameters)
        {
            Stage = EquipmentConversationStage.Calculated;
        }
    }

    /// <summary>Called right after "What would you like to do next?" was shown.</summary>
    public void MarkMenuShown()
    {
        if (!CalculationValidated)
        {
            return;
        }

        Stage = EquipmentConversationStage.AwaitingNextAction;
    }

    public void MarkGenerated(IEnumerable<string> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        LastGeneratedScope = scope
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Stage = EquipmentConversationStage.Generated;
    }

    /// <summary>Back to value collection. Used when the user changes a value after a calculation.</summary>
    public void Reopen()
    {
        InvalidateCalculation();
    }

    private void InvalidateCalculation()
    {
        CalculationValidated = false;
        LastGeneratedScope = new List<string>();
        Stage = EquipmentConversationStage.AwaitingParameters;
    }

    public EquipmentConversationState Clone()
    {
        return new EquipmentConversationState(ManifestId)
        {
            Stage = Stage,
            Values = new Dictionary<string, double>(Values, StringComparer.OrdinalIgnoreCase),
            DefaultedKeys = new HashSet<string>(DefaultedKeys, StringComparer.OrdinalIgnoreCase),
            DefaultsAccepted = DefaultsAccepted,
            CalculationValidated = CalculationValidated,
            LastGeneratedScope = LastGeneratedScope.ToList(),
            LastUsedUtc = LastUsedUtc
        };
    }
}

/// <summary>
/// All equipment conversations of one chat session. Holds one independent state per equipment type
/// and remembers which equipment is active, so switching equipment never reuses another equipment's values.
/// </summary>
public sealed class EquipmentConversationSession
{
    public EquipmentConversationSession(int projectId, string sessionId)
    {
        ProjectId = projectId;
        SessionId = sessionId ?? string.Empty;
    }

    public int ProjectId { get; }

    public string SessionId { get; }

    public EquipmentSessionStage Stage { get; set; } = EquipmentSessionStage.None;

    /// <summary>Name of the uploaded image that is waiting for an equipment type answer.</summary>
    public string? PendingSourceFileName { get; set; }

    /// <summary>Equipment type the image looks like. Offered as a proposal, never applied without confirmation.</summary>
    public string? ProposedManifestId { get; set; }

    /// <summary>Manifest ids offered to the user in the equipment question.</summary>
    public List<string> OfferedManifestIds { get; private set; } = new();

    public string? ActiveManifestId { get; private set; }

    public Dictionary<string, EquipmentConversationState> States { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public EquipmentConversationState? Active =>
        ActiveManifestId is not null && States.TryGetValue(ActiveManifestId, out var state) ? state : null;

    /// <summary>An image arrived. The equipment type is not known until the user confirms it.</summary>
    public void BeginIdentification(
        string? fileName,
        string? proposedManifestId,
        IEnumerable<string> offeredManifestIds)
    {
        ArgumentNullException.ThrowIfNull(offeredManifestIds);

        PendingSourceFileName = fileName;
        ProposedManifestId = string.IsNullOrWhiteSpace(proposedManifestId) ? null : proposedManifestId.Trim();
        OfferedManifestIds = offeredManifestIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Stage = EquipmentSessionStage.AwaitingEquipmentType;
    }

    /// <summary>
    /// The user confirmed the equipment type. Creates that equipment's own state on first use
    /// and keeps it when the user comes back to the same equipment later.
    /// </summary>
    public EquipmentConversationState Activate(string manifestId)
    {
        if (string.IsNullOrWhiteSpace(manifestId))
        {
            throw new ArgumentException("A manifest id is required.", nameof(manifestId));
        }

        var id = manifestId.Trim();

        if (!States.TryGetValue(id, out var state))
        {
            state = new EquipmentConversationState(id);
            States[id] = state;
        }

        ActiveManifestId = state.ManifestId;
        Stage = EquipmentSessionStage.Active;
        PendingSourceFileName = null;
        ProposedManifestId = null;
        OfferedManifestIds = new List<string>();
        state.LastUsedUtc = DateTime.UtcNow;

        return state;
    }

    /// <summary>Drops the state of one equipment and leaves the others untouched.</summary>
    public void Discard(string manifestId)
    {
        if (string.IsNullOrWhiteSpace(manifestId))
        {
            return;
        }

        States.Remove(manifestId.Trim());

        if (string.Equals(ActiveManifestId, manifestId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ActiveManifestId = null;
            Stage = EquipmentSessionStage.None;
        }
    }

    public EquipmentConversationSession Clone()
    {
        var copy = new EquipmentConversationSession(ProjectId, SessionId)
        {
            Stage = Stage,
            PendingSourceFileName = PendingSourceFileName,
            ProposedManifestId = ProposedManifestId,
            OfferedManifestIds = OfferedManifestIds.ToList(),
            LastUsedUtc = LastUsedUtc
        };

        foreach (var pair in States)
        {
            copy.States[pair.Key] = pair.Value.Clone();
        }

        copy.ActiveManifestId = ActiveManifestId;

        return copy;
    }
}

public interface IEquipmentConversationStore
{
    /// <summary>Snapshot of the session. Changes to the snapshot are not stored.</summary>
    EquipmentConversationSession Get(int projectId, string sessionId);

    /// <summary>Applies a change under the session lock and returns a snapshot of the result.</summary>
    EquipmentConversationSession Update(int projectId, string sessionId, Action<EquipmentConversationSession> mutate);

    /// <summary>True when an equipment type was confirmed for this session.</summary>
    bool IsActive(int projectId, string sessionId);

    /// <summary>True when an image is waiting for the user to name the equipment type.</summary>
    bool IsAwaitingEquipmentType(int projectId, string sessionId);

    void Reset(int projectId, string sessionId);
}

public sealed class EquipmentConversationStore : IEquipmentConversationStore
{
    private static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(120);

    private const int DefaultMaxSessions = 500;

    private readonly TimeSpan _idle;
    private readonly int _maxSessions;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public EquipmentConversationStore()
        : this(DefaultIdle, DefaultMaxSessions)
    {
    }

    public EquipmentConversationStore(TimeSpan idle, int maxSessions)
    {
        _idle = idle <= TimeSpan.Zero ? DefaultIdle : idle;
        _maxSessions = Math.Max(1, maxSessions);
    }

    public EquipmentConversationSession Get(int projectId, string sessionId)
    {
        Evict();

        var entry = _entries.GetOrAdd(Key(projectId, sessionId), _ => NewEntry(projectId, sessionId));

        lock (entry.Lock)
        {
            entry.Session.LastUsedUtc = DateTime.UtcNow;
            return entry.Session.Clone();
        }
    }

    public EquipmentConversationSession Update(
        int projectId,
        string sessionId,
        Action<EquipmentConversationSession> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        Evict();

        var entry = _entries.GetOrAdd(Key(projectId, sessionId), _ => NewEntry(projectId, sessionId));

        lock (entry.Lock)
        {
            mutate(entry.Session);
            entry.Session.LastUsedUtc = DateTime.UtcNow;
            return entry.Session.Clone();
        }
    }

    public bool IsActive(int projectId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_entries.TryGetValue(Key(projectId, sessionId), out var entry))
        {
            return false;
        }

        lock (entry.Lock)
        {
            return entry.Session.Stage == EquipmentSessionStage.Active && entry.Session.Active is not null;
        }
    }

    public bool IsAwaitingEquipmentType(int projectId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_entries.TryGetValue(Key(projectId, sessionId), out var entry))
        {
            return false;
        }

        lock (entry.Lock)
        {
            return entry.Session.Stage == EquipmentSessionStage.AwaitingEquipmentType;
        }
    }

    public void Reset(int projectId, string sessionId)
    {
        _entries.TryRemove(Key(projectId, sessionId), out _);
    }

    private void Evict()
    {
        var cutoff = DateTime.UtcNow - _idle;

        foreach (var pair in _entries)
        {
            if (pair.Value.Session.LastUsedUtc < cutoff)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        while (_entries.Count > _maxSessions)
        {
            var oldest = _entries
                .OrderBy(pair => pair.Value.Session.LastUsedUtc)
                .Select(pair => pair.Key)
                .FirstOrDefault();

            if (oldest is null || !_entries.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private static Entry NewEntry(int projectId, string sessionId)
    {
        return new Entry(new EquipmentConversationSession(projectId, sessionId));
    }

    private static string Key(int projectId, string sessionId)
    {
        return projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + sessionId;
    }

    private sealed class Entry
    {
        public Entry(EquipmentConversationSession session)
        {
            Session = session;
        }

        public object Lock { get; } = new();

        public EquipmentConversationSession Session { get; }
    }
}