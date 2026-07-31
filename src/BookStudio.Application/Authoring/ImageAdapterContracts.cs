namespace BookStudio.Application.Authoring;

public interface IImageAdapter
{
    string AdapterId { get; }
    string AdapterVersion { get; }
    ImageAdapterKind Kind { get; }
    ImageAdapterCapabilities Capabilities { get; }

    ValueTask<ImageAdapterAttemptResult> ExecuteAsync(
        ImageAdapterExecution execution,
        CancellationToken ct = default);

    ValueTask CancelAsync(
        ImageAdapterCancellation cancellation,
        CancellationToken ct = default);
}

public interface IImageAdapterRegistry
{
    IImageAdapter Resolve(string adapterId, string adapterVersion);
}

public interface IImageAdapterRequestStore
{
    ValueTask<ImageAdapterSubmissionResult> SubmitAsync(
        ImageAdapterRequest request,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<ImageAdapterRequestState> RecordAttemptAsync(
        ImageAdapterAttempt attempt,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<ImageAdapterRequestState> CompleteAsync(
        ImageAdapterCompletion completion,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<ImageAdapterRequestState> FailAsync(
        ImageAdapterFailure failure,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<ImageAdapterRequestState> CancelAsync(
        ImageAdapterCancellation cancellation,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<ImageAdapterRequestState?> GetAsync(
        string workspaceId,
        Guid requestId,
        CancellationToken ct = default);
}

public sealed record ImageAdapterCapabilities(
    bool Generate,
    bool Variation,
    bool Inpaint,
    bool ImageToImage,
    bool Upscale,
    bool DeterministicSeed,
    bool Cancellation,
    bool ManualImport,
    IReadOnlySet<string> MediaFormats,
    int? MaxWidth,
    int? MaxHeight,
    string CapabilityDigest);

public sealed record ImageAdapterRequest(
    Guid RequestId,
    Guid ProjectId,
    string WorkspaceId,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    VisualAssetType AssetType,
    string AdapterId,
    string AdapterVersion,
    ImageAdapterKind AdapterKind,
    ImageOperationMode Operation,
    IReadOnlySet<ImageCapability> RequiredCapabilities,
    string Prompt,
    string? NegativePrompt,
    string? ManualSourceIdentity,
    string GenerationParametersJson,
    ImageOutputPolicy OutputPolicy,
    ImageRetryPolicy RetryPolicy,
    AssetRightsEvidence Rights,
    AssetAccessibilityEvidence Accessibility,
    string Actor,
    string RequestFingerprint);

public sealed record ImageOutputPolicy(
    string StorageRoot,
    string RelativeDirectory,
    IReadOnlySet<string> AllowedMediaFormats,
    int MinimumWidth,
    int MinimumHeight,
    int MaximumWidth,
    int MaximumHeight,
    long MaximumBytes,
    bool RequireImmutableDigest,
    string TechnicalPolicyVersion);

public sealed record ImageRetryPolicy(
    int MaximumAttempts,
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    double BackoffMultiplier,
    IReadOnlySet<ImageFailureKind> RetryableFailures);

public sealed record ImageAdapterExecution(
    ImageAdapterRequest Request,
    int AttemptNumber,
    Guid AttemptId,
    DateTimeOffset StartedAtUtc);

public sealed record ImageAdapterAttemptResult(
    bool Succeeded,
    IReadOnlyList<ImageAdapterOutput> Outputs,
    IReadOnlyList<ImageAdapterWarning> Warnings,
    ImageAdapterError? Error,
    ImageAdapterUsage Usage,
    string ProviderEvidenceJson,
    string ProviderEvidenceDigest,
    DateTimeOffset CompletedAtUtc);

public sealed record ImageAdapterOutput(
    Guid OutputId,
    string StorageRoot,
    string RelativePath,
    string MediaFormat,
    int Width,
    int Height,
    long Bytes,
    string ColorProfile,
    string ContentDigest,
    string TechnicalMetadataJson,
    AssetProvenanceEvidence Provenance,
    IReadOnlyList<AssetRelationshipDraft> Relationships);

public sealed record ImageAdapterWarning(string Code, string Message, string Evidence);

public sealed record ImageAdapterError(
    string Code,
    string Message,
    ImageFailureKind Kind,
    bool Retryable,
    string Evidence,
    string EvidenceDigest);

public sealed record ImageAdapterUsage(
    long InputUnits,
    long OutputUnits,
    decimal? CostAmount,
    string? CostCurrency,
    TimeSpan Duration,
    string NativeUsageJson);

public sealed record ImageAdapterAttempt(
    Guid RequestId,
    string WorkspaceId,
    long ExpectedRevision,
    Guid AttemptId,
    int AttemptNumber,
    string AdapterId,
    string AdapterVersion,
    ImageAdapterAttemptResult Result,
    string Actor,
    string RequestFingerprint);

public sealed record ImageAdapterCompletion(
    Guid RequestId,
    string WorkspaceId,
    long ExpectedRevision,
    Guid AttemptId,
    IReadOnlyList<ImageAdapterRegisteredOutput> Outputs,
    string Actor,
    string RequestFingerprint);

public sealed record ImageAdapterRegisteredOutput(
    Guid OutputId,
    Guid AssetId,
    long AssetRevision,
    string ContentDigest,
    Guid? OutboxMessageId);

public sealed record ImageAdapterFailure(
    Guid RequestId,
    string WorkspaceId,
    long ExpectedRevision,
    Guid? AttemptId,
    ImageAdapterError Error,
    string Actor,
    string RequestFingerprint);

public sealed record ImageAdapterCancellation(
    Guid RequestId,
    string WorkspaceId,
    long ExpectedRevision,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record ImageAdapterRequestState(
    Guid RequestId,
    Guid ProjectId,
    string WorkspaceId,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    VisualAssetType AssetType,
    string AdapterId,
    string AdapterVersion,
    ImageAdapterKind AdapterKind,
    ImageOperationMode Operation,
    IReadOnlySet<ImageCapability> RequiredCapabilities,
    string PromptDigest,
    string GenerationParametersJson,
    ImageOutputPolicy OutputPolicy,
    ImageRetryPolicy RetryPolicy,
    IReadOnlyList<ImageAdapterAttempt> Attempts,
    IReadOnlyList<ImageAdapterRegisteredOutput> Outputs,
    ImageAdapterRequestStatus Status,
    ImageAdapterError? LastError,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ImageAdapterSubmissionResult(
    ImageAdapterRequestState Request,
    bool Replayed);

public enum ImageAdapterKind
{
    ComfyUi,
    LocalEngine,
    RemoteProvider,
    ManualIngestion
}

public enum ImageOperationMode
{
    Generate,
    Variation,
    Inpaint,
    ImageToImage,
    Upscale,
    ManualImport
}

public enum ImageCapability
{
    Generate,
    Variation,
    Inpaint,
    ImageToImage,
    Upscale,
    DeterministicSeed,
    Cancellation,
    ManualImport
}

public enum ImageFailureKind
{
    UnsupportedCapability,
    ProviderMismatch,
    InvalidRequest,
    UnsafePath,
    InvalidMedia,
    StaleAuthority,
    CrossBoundaryAccess,
    DigestConflict,
    ConflictingReplay,
    ConcurrencyConflict,
    ProviderTransient,
    ProviderPermanent,
    Cancelled,
    RegistryRejected,
    PartialProviderFailure
}

public enum ImageAdapterRequestStatus
{
    Submitted,
    Running,
    RetryPending,
    Completed,
    Failed,
    Cancelled
}

public sealed class ImageAdapterValidationException : Exception
{
    public ImageAdapterValidationException(string message) : base(message) { }
}

public sealed class ImageAdapterConflictException : Exception
{
    public ImageAdapterConflictException(string message) : base(message) { }
}

public sealed class ImageAdapterTransitionException : Exception
{
    public ImageAdapterTransitionException(string message) : base(message) { }
}
