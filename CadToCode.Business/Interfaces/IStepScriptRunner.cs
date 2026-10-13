using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IStepScriptRunner
{
    Task<SandboxRunResult> RunAsync(string? code, CancellationToken cancellationToken = default);
}