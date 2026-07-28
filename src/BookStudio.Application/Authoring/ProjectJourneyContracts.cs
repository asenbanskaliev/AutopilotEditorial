namespace BookStudio.Application.Authoring;

public interface IEditorialProjectStore
{
    ValueTask<ProjectCreateResult> CreateAsync(
        CreateEditorialProject command,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<EditorialProject?> GetAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}

public sealed record CreateEditorialProject(
    Guid RequestId,
    Guid WorkspaceId,
    Guid ProjectId,
    string Name,
    EditorialProjectKind Kind,
    string LanguageTag,
    string Audience,
    string Objective,
    string RequestFingerprint);

public sealed record EditorialProject(
    Guid WorkspaceId,
    Guid ProjectId,
    string Name,
    EditorialProjectKind Kind,
    string LanguageTag,
    string Audience,
    string Objective,
    EditorialProjectStatus Status,
    Guid CreatedMessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProjectCreateResult(EditorialProject Project, bool Replayed);

public enum EditorialProjectKind
{
    Fiction,
    NonFiction,
    Technical,
    Educational,
    Other,
}

public enum EditorialProjectStatus
{
    Active,
    Archived,
}

public sealed class EditorialProjectConflictException : Exception
{
    public EditorialProjectConflictException(string message) : base(message) { }
}
