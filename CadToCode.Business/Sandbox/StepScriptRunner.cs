using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Step;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;

namespace CadToCode.Business.Sandbox;

public sealed class StepScriptRunner : IStepScriptRunner
{
    private const int MaxCodeLength = 100_000;
    private const int MaxDiagnostics = 25;
    private const int MaxTotalPoints = 20_000;
    private const long MaxSteps = 200_000;
    private const long MaxAllocatedBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan CompileLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GraceLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QueueLimit = TimeSpan.FromSeconds(3);

    private static readonly SemaphoreSlim Gate = new(2, 2);

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithImports(Array.Empty<string>())
        .AddReferences(typeof(StepDocument).Assembly)
        .WithOptimizationLevel(OptimizationLevel.Release)
        .WithEmitDebugInformation(false);

    private readonly ILogger<StepScriptRunner> _logger;

    public StepScriptRunner(ILogger<StepScriptRunner> logger)
    {
        _logger = logger;
    }

    public async Task<SandboxRunResult> RunAsync(string? code, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail(clock, SandboxStatus.Invalid, "There is no code to run.");
        }

        if (code.Length > MaxCodeLength)
        {
            return Fail(clock, SandboxStatus.Invalid, "The code is longer than 100,000 characters.");
        }

        if (!await Gate.WaitAsync(QueueLimit, cancellationToken).ConfigureAwait(false))
        {
            return Fail(clock, SandboxStatus.Error, "The sandbox is busy. Try again in a moment.");
        }

        var handedOff = false;

        try
        {
            Prepared prepared;

            using (var compileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                compileCts.CancelAfter(CompileLimit);

                try
                {
                    var token = compileCts.Token;
                    prepared = await Task.Run(() => Prepare(code, token), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Fail(clock, SandboxStatus.Timeout, "Preparing the script took too long.");
                }
            }

            if (prepared.Script is null)
            {
                var failure = Fail(clock, prepared.Status, prepared.Message);
                failure.Diagnostics = prepared.Diagnostics;
                return failure;
            }

            var session = new SandboxSession(RunLimit, MaxSteps, MaxAllocatedBytes);
            var script = prepared.Script;

            var runTask = Task.Run(async () =>
            {
                using (SandboxSession.Enter(session))
                {
                    await script.RunAsync().ConfigureAwait(false);
                }
            });

            handedOff = true;
            _ = runTask.ContinueWith(_ => Gate.Release(), TaskScheduler.Default);

            var finished = await Task.WhenAny(runTask, Task.Delay(RunLimit + GraceLimit, cancellationToken)).ConfigureAwait(false);

            if (!ReferenceEquals(finished, runTask))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Fail(clock, SandboxStatus.Timeout, "The script did not finish in time.");
            }

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return FailFromException(clock, session, ex);
            }

            return BuildResult(clock, session);
        }
        finally
        {
            if (!handedOff)
            {
                Gate.Release();
            }
        }
    }

    private static Prepared Prepare(string code, CancellationToken cancellationToken)
    {
        var script = CSharpScript.Create(code, Options);
        var compilation = script.GetCompilation();

        var errors = compilation.GetDiagnostics(cancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count > 0)
        {
            return Prepared.Failed(SandboxStatus.Invalid, "The script has compile errors.", ToDiagnostics(errors));
        }

        var tree = compilation.SyntaxTrees.First();
        var validation = SandboxScriptValidator.Validate(compilation, tree);

        if (!validation.IsValid)
        {
            return Prepared.Failed(
                SandboxStatus.Invalid,
                "The script uses something that is not allowed in the sandbox.",
                validation.Errors);
        }

        var rewritten = SandboxScriptRewriter.Rewrite(tree, validation);
        var runScript = CSharpScript.Create(rewritten, Options);

        var runErrors = runScript.Compile(cancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (runErrors.Count > 0)
        {
            return Prepared.Failed(
                SandboxStatus.Error,
                "The script could not be prepared for execution.",
                ToDiagnostics(runErrors));
        }

        return Prepared.Ready(runScript);
    }

    private SandboxRunResult BuildResult(Stopwatch clock, SandboxSession session)
    {
        var document = session.Document;

        if (document is null)
        {
            return Fail(clock, SandboxStatus.Error, "The script did not create a StepDocument.", session);
        }

        if (document.SolidCount == 0)
        {
            return Fail(clock, SandboxStatus.Error, "The script finished but added no solids to the StepDocument.", session);
        }

        var solids = document.Solids;

        var points = solids.Sum(SandboxSolidKind.PointCount);

        if (points > MaxTotalPoints)
        {
            return Fail(clock, SandboxStatus.Error, "The model has more than " + MaxTotalPoints + " outline points.", session);
        }

        var faces = solids.Sum(s => s.FaceCount);

        if (faces > SandboxSession.MaxFaces)
        {
            return Fail(clock, SandboxStatus.Error, "The model has more than " + SandboxSession.MaxFaces + " faces.", session);
        }

        string step;

        try
        {
            step = document.ToStep();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            var reason = "The model could not be written as STEP: " + ex.Message;
            return Fail(clock, SandboxStatus.Error, reason.Length > 300 ? reason[..300] : reason, session);
        }

        var result = new SandboxRunResult
        {
            Success = true,
            Status = SandboxStatus.Ok,
            SolidCount = solids.Count,
            FaceCount = faces,
            FileName = session.SavedAs ?? ToFileName(document.Name),
            StepContent = step,
            Output = session.Output,
            Bounds = BoundsOf(solids)
        };

        var connectivity = StepConnectivity.Analyze(solids);
        result.IsConnected = connectivity.IsConnected;
        result.ComponentCount = connectivity.ComponentCount;
        result.IsolatedParts = connectivity.IsolatedNames.ToList();

        var approximated = 0;
        var previewPoints = 0;

        foreach (var solid in solids)
        {
            switch (solid)
            {
                case PrismSolid prism:
                    result.Solids.Add(new SandboxSolid
                    {
                        Type = "prism",
                        Name = prism.Name,
                        Z = prism.Z,
                        Depth = prism.Depth,
                        Outer = prism.Outline.Select(p => new SandboxPoint { X = p.X, Y = p.Y }).ToList(),
                        Holes = prism.Holes
                            .Select(h => h.Select(p => new SandboxPoint { X = p.X, Y = p.Y }).ToList())
                            .ToList()
                    });
                    break;

                case CylinderSolid cylinder:
                    result.Solids.Add(new SandboxSolid
                    {
                        Type = "cylinder",
                        Name = cylinder.Name,
                        Z = cylinder.Z,
                        X = cylinder.Cx,
                        Y = cylinder.Cy,
                        Radius = cylinder.Radius,
                        Height = cylinder.Height
                    });
                    break;

                case FramedSolid framed:
                    if (!AddFramedPreview(result, framed, ref previewPoints))
                    {
                        approximated++;
                    }

                    break;
            }
        }

        result.Message = result.SolidCount.ToString(CultureInfo.InvariantCulture)
            + (result.SolidCount == 1 ? " solid" : " solids")
            + " and "
            + result.FaceCount.ToString(CultureInfo.InvariantCulture)
            + " faces written to "
            + result.FileName
            + ". Contains "
            + SandboxSolidKind.Summary(solids)
            + "."
            + (approximated > 0
                ? " " + approximated.ToString(CultureInfo.InvariantCulture)
                    + " of them appear as bounding boxes in the preview; the STEP file holds the exact geometry."
                : string.Empty);

        result.ElapsedMs = clock.ElapsedMilliseconds;
        return result;
    }

    private static bool AddFramedPreview(SandboxRunResult result, FramedSolid solid, ref int previewPoints)
    {
        if (solid is ExtrudedProfileSolid extrusion && IsAxisAligned(extrusion.WorldFrame))
        {
            var origin = extrusion.WorldFrame.Origin;
            var profile = extrusion.Profile;

            if (profile.IsCircle && profile.HoleCount == 0)
            {
                var arc = profile.Outer.CircleArc!;

                result.Solids.Add(new SandboxSolid
                {
                    Type = "cylinder",
                    Name = extrusion.Name,
                    Z = origin.Z,
                    X = origin.X + arc.Center.X,
                    Y = origin.Y + arc.Center.Y,
                    Radius = arc.Radius,
                    Height = extrusion.Depth
                });

                return true;
            }

            var bounds = profile.Bounds;
            var deviation = Math.Max(
                ProfileLoop.DefaultFlattenDeviation,
                Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY) / 400.0);

            var outer = profile.FlattenOuter(deviation);
            var holes = profile.FlattenHoles(deviation);
            var count = outer.Count + holes.Sum(h => h.Count);

            if (previewPoints + count <= MaxTotalPoints)
            {
                previewPoints += count;

                result.Solids.Add(new SandboxSolid
                {
                    Type = "prism",
                    Name = extrusion.Name,
                    Z = origin.Z,
                    Depth = extrusion.Depth,
                    Outer = outer.Select(p => new SandboxPoint { X = p.X + origin.X, Y = p.Y + origin.Y }).ToList(),
                    Holes = holes
                        .Select(h => h.Select(p => new SandboxPoint { X = p.X + origin.X, Y = p.Y + origin.Y }).ToList())
                        .ToList()
                });

                return true;
            }
        }

        var box = solid.Bounds;

        result.Solids.Add(new SandboxSolid
        {
            Type = "prism",
            Name = solid.Name,
            Z = box.MinZ,
            Depth = Math.Max(box.MaxZ - box.MinZ, 1e-6),
            Outer = new List<SandboxPoint>
            {
                new() { X = box.MinX, Y = box.MinY },
                new() { X = box.MaxX, Y = box.MinY },
                new() { X = box.MaxX, Y = box.MaxY },
                new() { X = box.MinX, Y = box.MaxY }
            },
            Holes = new List<List<SandboxPoint>>()
        });

        return false;
    }

    private static bool IsAxisAligned(Frame3D frame)
    {
        return frame.XAxis.IsApproximately(Vec3.UnitX, 1e-9)
            && frame.YAxis.IsApproximately(Vec3.UnitY, 1e-9)
            && frame.ZAxis.IsApproximately(Vec3.UnitZ, 1e-9);
    }

    private SandboxRunResult FailFromException(Stopwatch clock, SandboxSession session, Exception exception)
    {
        var root = Unwrap(exception);

        if (root is SandboxLimitException limit)
        {
            var status = limit.Message.Contains("too long", StringComparison.Ordinal)
                ? SandboxStatus.Timeout
                : SandboxStatus.Error;

            return Fail(clock, status, limit.Message, session);
        }

        if (root is CompilationErrorException compilationError)
        {
            var failure = Fail(clock, SandboxStatus.Invalid, "The script has compile errors.", session);
            failure.Diagnostics = ToDiagnostics(compilationError.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            return failure;
        }

        _logger.LogInformation(root, "The sandbox script failed at run time.");

        var message = root.GetType().Name + ": " + root.Message;
        if (message.Length > 300)
        {
            message = message[..300];
        }

        return Fail(clock, SandboxStatus.Error, message, session);
    }

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;

        while (true)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                current = aggregate.InnerExceptions[0];
            }
            else if (current is TargetInvocationException { InnerException: not null } invocation)
            {
                current = invocation.InnerException;
            }
            else
            {
                return current;
            }
        }
    }

    private static SandboxBounds? BoundsOf(IReadOnlyList<Solid> solids)
    {
        if (solids.Count == 0)
        {
            return null;
        }

        var first = solids[0].Bounds;
        var bounds = new SandboxBounds
        {
            MinX = first.MinX,
            MinY = first.MinY,
            MinZ = first.MinZ,
            MaxX = first.MaxX,
            MaxY = first.MaxY,
            MaxZ = first.MaxZ
        };

        foreach (var solid in solids.Skip(1))
        {
            var b = solid.Bounds;
            bounds.MinX = Math.Min(bounds.MinX, b.MinX);
            bounds.MinY = Math.Min(bounds.MinY, b.MinY);
            bounds.MinZ = Math.Min(bounds.MinZ, b.MinZ);
            bounds.MaxX = Math.Max(bounds.MaxX, b.MaxX);
            bounds.MaxY = Math.Max(bounds.MaxY, b.MaxY);
            bounds.MaxZ = Math.Max(bounds.MaxZ, b.MaxZ);
        }

        return bounds;
    }

    private static string ToFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return (clean.Length == 0 ? "model" : clean) + ".step";
    }

    private static List<SandboxDiagnostic> ToDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics
            .Take(MaxDiagnostics)
            .Select(d =>
            {
                var position = d.Location.GetLineSpan().StartLinePosition;

                return new SandboxDiagnostic
                {
                    Severity = "error",
                    Message = d.GetMessage(CultureInfo.InvariantCulture),
                    Line = position.Line + 1,
                    Column = position.Character + 1
                };
            })
            .ToList();
    }

    private static SandboxRunResult Fail(Stopwatch clock, string status, string? message, SandboxSession? session = null)
    {
        return new SandboxRunResult
        {
            Success = false,
            Status = status,
            Message = message,
            ElapsedMs = clock.ElapsedMilliseconds,
            Output = session?.Output ?? new List<string>()
        };
    }

    private sealed class Prepared
    {
        public Script<object>? Script { get; private init; }

        public string Status { get; private init; } = SandboxStatus.Error;

        public string? Message { get; private init; }

        public List<SandboxDiagnostic> Diagnostics { get; private init; } = new();

        public static Prepared Ready(Script<object> script)
        {
            return new Prepared { Script = script, Status = SandboxStatus.Ok };
        }

        public static Prepared Failed(string status, string message, List<SandboxDiagnostic> diagnostics)
        {
            return new Prepared { Status = status, Message = message, Diagnostics = diagnostics };
        }
    }
}