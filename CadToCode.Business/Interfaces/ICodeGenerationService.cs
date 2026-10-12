namespace CadToCode.Business.Interfaces;

public interface ICodeGenerationService
{
    Task<string> GenerateAsync(int projectId, CancellationToken cancellationToken = default);
}