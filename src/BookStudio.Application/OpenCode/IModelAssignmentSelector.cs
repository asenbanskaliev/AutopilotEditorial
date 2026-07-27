namespace BookStudio.Application.OpenCode;

public interface IModelAssignmentSelector
{
    ModelAssignmentDecision Select(
        ModelAssignmentRequest request,
        CancellationToken cancellationToken = default);
}
