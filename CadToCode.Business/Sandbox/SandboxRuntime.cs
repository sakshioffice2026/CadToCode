using System.Diagnostics;
using CadToCode.Step;

namespace CadToCode.Business.Sandbox;

public sealed class SandboxLimitException : Exception
{
    public SandboxLimitException(string message) : base(message)
    {
    }
}

public sealed class SandboxSession
{
    public const int MaxSolids = 2000;
    public const int MaxOutputLines = 200;
    public const int MaxOutputLineLength = 500;

    private const int TimeCheckInterval = 64;

    private static readonly AsyncLocal<SandboxSession?> CurrentSession = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _timeLimit;
    private readonly long _maxSteps;
    private readonly long _maxMemoryBytes;

    private long _baselineAllocated;
    private long _steps;
    private int _solidCount;

    public SandboxSession(TimeSpan timeLimit, long maxSteps, long maxMemoryBytes)
    {
        _timeLimit = timeLimit;
        _maxSteps = maxSteps;
        _maxMemoryBytes = maxMemoryBytes;
    }

    public static SandboxSession? Current => CurrentSession.Value;

    public StepDocument? Document { get; private set; }

    public string? SavedAs { get; private set; }

    public List<string> Output { get; } = new();

    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;

    public static IDisposable Enter(SandboxSession session)
    {
        session.Begin();
        CurrentSession.Value = session;
        return new Scope();
    }

    private void Begin()
    {
        _baselineAllocated = GC.GetAllocatedBytesForCurrentThread();
        _clock.Restart();
    }

    public void Tick()
    {
        _steps++;

        if (_steps > _maxSteps)
        {
            throw new SandboxLimitException("The script ran too many loop iterations.");
        }

        if (_steps % TimeCheckInterval == 0)
        {
            CheckTime();
        }

        CheckMemory();
    }

    public void CheckTime()
    {
        if (_clock.Elapsed > _timeLimit)
        {
            throw new SandboxLimitException("The script ran for too long.");
        }
    }

    public void CheckMemory()
    {
        if (GC.GetAllocatedBytesForCurrentThread() - _baselineAllocated > _maxMemoryBytes)
        {
            throw new SandboxLimitException("The script used too much memory.");
        }
    }

    public void RegisterDocument(StepDocument document)
    {
        Document = document;
    }

    public void RegisterSolid()
    {
        _solidCount++;

        if (_solidCount > MaxSolids)
        {
            throw new SandboxLimitException("The script created more than " + MaxSolids + " solids.");
        }

        CheckTime();
    }

    public void RecordSave(string? path)
    {
        var name = string.IsNullOrWhiteSpace(path) ? "model.step" : System.IO.Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "model.step";
        }

        var extension = System.IO.Path.GetExtension(name);
        if (!string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
        {
            name += ".step";
        }

        SavedAs = Truncate(name, 200);
    }

    public void WriteOutput(string? text)
    {
        if (Output.Count >= MaxOutputLines)
        {
            return;
        }

        Output.Add(Truncate(text ?? string.Empty, MaxOutputLineLength));
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            CurrentSession.Value = null;
        }
    }
}

public static class SandboxGuard
{
    public static void Tick()
    {
        SandboxSession.Current?.Tick();
    }
}

public static class SandboxConsole
{
    public static void WriteLine()
    {
        SandboxSession.Current?.WriteOutput(string.Empty);
    }

    public static void WriteLine(string? value)
    {
        SandboxSession.Current?.WriteOutput(value);
    }

    public static void WriteLine(object? value)
    {
        SandboxSession.Current?.WriteOutput(value?.ToString());
    }

    public static void WriteLine(string format, params object?[] args)
    {
        SandboxSession.Current?.WriteOutput(string.Format(format, args));
    }
}

public static class SandboxPath
{
    public static string GetFullPath(string path)
    {
        return path ?? throw new ArgumentNullException(nameof(path));
    }
}