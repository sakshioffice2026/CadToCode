using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface ICodeGenerationService
{
    Task<string> GenerateAsync(int projectId, CancellationToken cancellationToken = default);

    Task<AssemblyGenerationResult> GenerateAssemblyAsync(int projectId, bool useAi, CancellationToken cancellationToken = default);
}