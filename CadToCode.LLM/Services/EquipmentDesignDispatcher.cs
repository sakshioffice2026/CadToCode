using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CadToCode.Core.Domain;
using CadToCode.Core.Manifests;
using CadToCode.LLM.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CadToCode.LLM.Services;

public sealed class EquipmentDesignDispatcher : IEquipmentDesignDispatcher
{
    private static readonly Regex GenerateRegex = new(
        @"\b(generate|build|create|make|export|produce|write|regenerate)\b",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex ScriptRequestRegex = new(
        @"\b(give\s+me|need|want)\b.{0,100}\b(script|code|step|stp)\b",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private readonly IEquipmentManifestRegistry _manifests;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ConcurrentDictionary<SessionKey, SessionState> _sessions = new();

    public EquipmentDesignDispatcher(
        IEquipmentManifestRegistry manifests,
        IServiceScopeFactory scopeFactory)
    {
        _manifests = manifests
            ?? throw new ArgumentNullException(nameof(manifests));

        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public bool TryHandle(
        int projectId,
        string sessionId,
        string? prompt,
        EquipmentType requestedEquipment,
        out EquipmentDesignTurn turn)
    {
        turn = null!;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var key = new SessionKey(projectId, sessionId);
        var text = prompt?.Trim() ?? string.Empty;

        SessionState? existing = null;
        _sessions.TryGetValue(key, out existing);

        EquipmentManifest? manifest;

        if (requestedEquipment != EquipmentType.Unknown)
        {
            // Use the existing manifest's declared calculator ID.
            // Do not guess an equipment type or substitute another manifest.
            manifest = _manifests.All.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.CalculatorId,
                    requestedEquipment.ToString(),
                    StringComparison.OrdinalIgnoreCase));

            if (manifest is null)
            {
                return false;
            }
        }
        else if (existing is not null)
        {
            // A follow-up message continues the equipment already being designed.
            if (!_manifests.TryGet(existing.ManifestId, out manifest))
            {
                return false;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var resolution = _manifests.ResolveDetailed(text);

            // Do not choose between ambiguous equipment types.
            if (!resolution.IsResolved || resolution.Manifest is null)
            {
                return false;
            }

            manifest = resolution.Manifest;
        }

        using var scope = _scopeFactory.CreateScope();

        var flow = scope.ServiceProvider
            .GetServices<IEquipmentDesignFlow>()
            .FirstOrDefault(candidate => candidate.CanHandle(manifest.Id));

        if (flow is null)
        {
            return false;
        }

        var initialState = new SessionState(manifest.Id);

        var state = _sessions.AddOrUpdate(
            key,
            initialState,
            (_, current) =>
                string.Equals(
                    current.ManifestId,
                    manifest.Id,
                    StringComparison.OrdinalIgnoreCase)
                    ? current
                    : initialState);

        lock (state.SyncRoot)
        {
            // Refresh the manifest in case the session was replaced before
            // this request acquired its lock.
            if (!string.Equals(
                    state.ManifestId,
                    manifest.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var request = new EquipmentDesignRequest
            {
                ManifestId = manifest.Id,
                Text = text,
                Values = new Dictionary<string, double>(
                    state.Values,
                    StringComparer.OrdinalIgnoreCase),
                GenerateRequested = IsGenerateRequested(text),
                PartKeys = FindRequestedPartKeys(manifest, text)
            };

            turn = flow.Handle(request);

            if (turn is null)
            {
                _sessions.TryRemove(
                    new KeyValuePair<SessionKey, SessionState>(key, state));

                return false;
            }

            state.Values = new Dictionary<string, double>(
                turn.Values,
                StringComparer.OrdinalIgnoreCase);

            return true;
        }
    }

    public bool IsActive(int projectId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return _sessions.ContainsKey(new SessionKey(projectId, sessionId));
    }

    public void Reset(int projectId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _sessions.TryRemove(new SessionKey(projectId, sessionId), out _);
    }

    private static bool IsGenerateRequested(string text)
    {
        return GenerateRegex.IsMatch(text)
               || ScriptRequestRegex.IsMatch(text);
    }

    private static IReadOnlyList<string> FindRequestedPartKeys(
        EquipmentManifest manifest,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text) || manifest.Parts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var requested = manifest.Parts
            .Where(part =>
                ContainsPhrase(text, part.Key)
                || ContainsPhrase(text, part.Title))
            .Select(part => part.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An empty list means the existing design flow should build all
        // parts declared by the manifest.
        return requested;
    }

    private static bool ContainsPhrase(string text, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        return text.Contains(
            phrase.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct SessionKey(
        int ProjectId,
        string SessionId);

    private sealed class SessionState
    {
        public SessionState(string manifestId)
        {
            ManifestId = manifestId;
        }

        public object SyncRoot { get; } = new();

        public string ManifestId { get; }

        public Dictionary<string, double> Values { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}