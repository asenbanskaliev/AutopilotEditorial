namespace BookStudio.Application.Authoring;

/// <summary>Provider-neutral deterministic draft registration and validation use cases.</summary>
public interface IDraftAuthoringService
{
    ValueTask<DraftRegistrationResult> RegisterAsync(
        DraftRegistrationCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<DraftValidationResult> ValidateAsync(
        DraftValidationQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<DraftResourceResult> ReadResourceAsync(
        DraftResourceQuery query,
        CancellationToken cancellationToken = default);
}
