namespace CadToCode.Business.Interfaces;

public interface IDefinitionLearningService
{
    Task<int> ConfirmAsync(int projectId, CancellationToken cancellationToken = default);
}