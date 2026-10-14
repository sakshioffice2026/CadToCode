using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.LLM.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using Microsoft.Extensions.Options;

namespace CadToCode.LLM.Memory;

public sealed class ChatMemoryStore : IChatMemoryStore
{
    private static readonly Regex SessionIdPattern = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.Compiled);

    private readonly LocalAiOptions _options;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public ChatMemoryStore(IOptions<LocalAiOptions> options)
    {
        _options = options.Value;
    }

    public string EnsureSession(int projectId, string? sessionId)
    {
        Evict();

        var id = !string.IsNullOrWhiteSpace(sessionId) && SessionIdPattern.IsMatch(sessionId)
            ? sessionId
            : Guid.NewGuid().ToString("N");

        var session = _sessions.GetOrAdd(Key(projectId, id), _ => new Session());
        session.Touch();
        return id;
    }

    public void Append(int projectId, string sessionId, CadAssistMessageDto message)
    {
        var session = _sessions.GetOrAdd(Key(projectId, sessionId), _ => new Session());

        lock (session.Lock)
        {
            session.Messages.Add(message);

            var limit = Math.Max(2, _options.MaxHistoryTurns * 2);
            if (session.Messages.Count > limit)
            {
                session.Messages.RemoveRange(0, session.Messages.Count - limit);
            }

            session.LastUsedUtc = DateTime.UtcNow;
        }
    }

    public IReadOnlyList<CadAssistMessageDto> GetMessages(int projectId, string sessionId)
    {
        if (!_sessions.TryGetValue(Key(projectId, sessionId), out var session))
        {
            return Array.Empty<CadAssistMessageDto>();
        }

        lock (session.Lock)
        {
            session.LastUsedUtc = DateTime.UtcNow;
            return session.Messages.ToList();
        }
    }

    public void Reset(int projectId, string sessionId)
    {
        _sessions.TryRemove(Key(projectId, sessionId), out _);
    }

    private void Evict()
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, _options.SessionIdleMinutes));
        var cutoff = DateTime.UtcNow - idle;

        foreach (var pair in _sessions)
        {
            if (pair.Value.LastUsedUtc < cutoff)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }

        var max = Math.Max(1, _options.MaxSessions);

        while (_sessions.Count > max)
        {
            var oldest = _sessions.OrderBy(p => p.Value.LastUsedUtc).Select(p => p.Key).FirstOrDefault();
            if (oldest is null || !_sessions.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private static string Key(int projectId, string sessionId)
    {
        return projectId.ToString(CultureInfo.InvariantCulture) + ":" + sessionId;
    }

    private sealed class Session
    {
        public object Lock { get; } = new();

        public List<CadAssistMessageDto> Messages { get; } = new();

        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

        public void Touch()
        {
            LastUsedUtc = DateTime.UtcNow;
        }
    }
}