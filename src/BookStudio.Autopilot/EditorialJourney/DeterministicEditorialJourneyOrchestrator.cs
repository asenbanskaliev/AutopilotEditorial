using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Autopilot.EditorialJourney;

public enum EditorialJourneyStage
{
    Briefing = 1,
    Outline = 2,
    Chapter = 3,
    ChapterValidation = 4,
    IndependentReview = 5,
    ReleasePreparation = 6,
    Preflight = 7,
    Complete = 8,
}

public enum EditorialReviewDecision
{
    Pass,
    Revise,
    Blocked,
}

public sealed record EditorialJourneyRequest(
    string ProjectId,
    string Idea,
    string Language = "es-ES",
    string Title = "Untitled",
    int ChapterNumber = 1);

public sealed record GeneratedEditorialContent(
    string Markdown,
    string Provider,
    string Model,
    string PromptHash);

public sealed record PersistedEditorialArtifact(
    string ArtifactId,
    int Version,
    string Sha256,
    string MediaType,
    long Length);

public sealed record EditorialValidationResult(
    bool IsValid,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, long> Metrics);

public sealed record EditorialReviewResult(
    EditorialReviewDecision Decision,
    IReadOnlyList<string> Reasons,
    string ReviewerId);

public sealed record EditorialReleaseResult(
    string ReleaseArtifactId,
    int Version,
    string Sha256);

public sealed record EditorialPreflightResult(
    bool Passed,
    IReadOnlyList<string> BlockingReasons);

public sealed record EditorialJourneyEvent(
    EditorialJourneyStage Stage,
    string Status,
    string Code,
    DateTimeOffset OccurredAtUtc);

public sealed record EditorialJourneyCheckpoint(
    string ProjectId,
    string RequestFingerprint,
    EditorialJourneyStage NextStage,
    IReadOnlyDictionary<string, PersistedEditorialArtifact> Artifacts,
    EditorialReleaseResult? Release,
    EditorialReviewResult? Review,
    IReadOnlyList<EditorialJourneyEvent> Events,
    bool Completed)
{
    public static EditorialJourneyCheckpoint New(string projectId, string fingerprint) =>
        new(
            projectId,
            fingerprint,
            EditorialJourneyStage.Briefing,
            new ReadOnlyDictionary<string, PersistedEditorialArtifact>(
                new Dictionary<string, PersistedEditorialArtifact>(StringComparer.Ordinal)),
            null,
            null,
            [],
            false);
}

public sealed record EditorialJourneyResult(
    string ProjectId,
    EditorialJourneyStage FinalStage,
    bool Completed,
    bool Resumed,
    EditorialReviewDecision? ReviewDecision,
    IReadOnlyDictionary<string, PersistedEditorialArtifact> Artifacts,
    EditorialReleaseResult? Release,
    IReadOnlyList<EditorialJourneyEvent> Events);

public interface IEditorialContentGenerator
{
    ValueTask<GeneratedEditorialContent> GenerateBriefingAsync(
        EditorialJourneyRequest request,
        CancellationToken cancellationToken);

    ValueTask<GeneratedEditorialContent> GenerateOutlineAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        CancellationToken cancellationToken);

    ValueTask<GeneratedEditorialContent> GenerateChapterAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        CancellationToken cancellationToken);
}

public interface IEditorialArtifactGateway
{
    ValueTask<PersistedEditorialArtifact?> GetAsync(
        string projectId,
        string artifactId,
        int version,
        CancellationToken cancellationToken);

    ValueTask<PersistedEditorialArtifact> RegisterAsync(
        string projectId,
        string artifactId,
        int expectedVersion,
        string mediaType,
        string content,
        CancellationToken cancellationToken);

    ValueTask<EditorialValidationResult> ValidateAsync(
        string projectId,
        string artifactId,
        int version,
        CancellationToken cancellationToken);

    ValueTask<EditorialReleaseResult?> GetReleaseAsync(
        string projectId,
        string releaseArtifactId,
        int version,
        CancellationToken cancellationToken);

    ValueTask<EditorialReleaseResult> PrepareReleaseAsync(
        string projectId,
        string releaseId,
        string title,
        string language,
        PersistedEditorialArtifact manuscript,
        CancellationToken cancellationToken);

    ValueTask<EditorialPreflightResult> PreflightAsync(
        string projectId,
        EditorialReleaseResult release,
        CancellationToken cancellationToken);
}

public interface IEditorialIndependentReviewer
{
    ValueTask<EditorialReviewResult> ReviewAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken);
}

public interface IEditorialJourneyCheckpointStore
{
    ValueTask<EditorialJourneyCheckpoint?> LoadAsync(
        string projectId,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        EditorialJourneyCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public interface IEditorialJourneyProgressSink
{
    ValueTask ReportAsync(EditorialJourneyEvent journeyEvent, CancellationToken cancellationToken);
}

public sealed class NullEditorialJourneyProgressSink : IEditorialJourneyProgressSink
{
    public static NullEditorialJourneyProgressSink Instance { get; } = new();

    private NullEditorialJourneyProgressSink() { }

    public ValueTask ReportAsync(EditorialJourneyEvent journeyEvent, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public static partial class EditorialArtifactIdFactory
{
    public static string Briefing(string projectId) => Draft(projectId, "briefing");
    public static string Outline(string projectId) => Draft(projectId, "outline");
    public static string Chapter(string projectId, int chapterNumber) =>
        Draft(projectId, $"chapter-{chapterNumber:00}");
    public static string Release(string projectId, string releaseId) =>
        $"{ValidateProjectId(projectId)}.release.{ValidateSlug(releaseId, nameof(releaseId))}";

    private static string Draft(string projectId, string name) =>
        $"{ValidateProjectId(projectId)}.draft.{ValidateSlug(name, nameof(name))}";

    private static string ValidateProjectId(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!ProjectIdPattern().IsMatch(projectId))
        {
            throw new ArgumentException("Project ID must match ^[a-z0-9][a-z0-9-]{0,63}$.", nameof(projectId));
        }
        return projectId;
    }

    private static string ValidateSlug(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!SlugPattern().IsMatch(value))
        {
            throw new ArgumentException("Artifact slug contains unsupported characters.", parameterName);
        }
        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}

public static class EditorialGeneratedContentPolicy
{
    public static string NormalizeAndValidate(
        GeneratedEditorialContent generated,
        EditorialJourneyStage stage)
    {
        ArgumentNullException.ThrowIfNull(generated);
        var content = generated.Markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new EditorialJourneyException(stage, "empty_generation", "Generated content is empty.");
        }
        if (content.Length > 524_288)
        {
            throw new EditorialJourneyException(stage, "generation_too_large", "Generated content exceeds the bounded draft limit.");
        }
        if (content.Contains('\0'))
        {
            throw new EditorialJourneyException(stage, "invalid_generation_controls", "Generated content contains NUL characters.");
        }
        if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("\"type\":\"tool", StringComparison.OrdinalIgnoreCase))
        {
            throw new EditorialJourneyException(stage, "generation_wrapper_detected", "Generated content contains an execution wrapper instead of editorial prose.");
        }
        if (stage == EditorialJourneyStage.Chapter && !content.StartsWith('#'))
        {
            throw new EditorialJourneyException(stage, "chapter_heading_missing", "Generated chapter must begin with a Markdown heading.");
        }
        return content + "\n";
    }
}

public sealed class EditorialJourneyException : Exception
{
    public EditorialJourneyException(EditorialJourneyStage stage, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        Code = code;
    }

    public EditorialJourneyStage Stage { get; }
    public string Code { get; }
}

public sealed class DeterministicEditorialJourneyOrchestrator
{
    private const int ArtifactVersion = 1;
    private readonly IEditorialContentGenerator _generator;
    private readonly IEditorialArtifactGateway _artifacts;
    private readonly IEditorialIndependentReviewer _reviewer;
    private readonly IEditorialJourneyCheckpointStore _checkpoints;
    private readonly IEditorialJourneyProgressSink _progress;
    private readonly TimeProvider _timeProvider;

    public DeterministicEditorialJourneyOrchestrator(
        IEditorialContentGenerator generator,
        IEditorialArtifactGateway artifacts,
        IEditorialIndependentReviewer reviewer,
        IEditorialJourneyCheckpointStore checkpoints,
        IEditorialJourneyProgressSink? progress = null,
        TimeProvider? timeProvider = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _reviewer = reviewer ?? throw new ArgumentNullException(nameof(reviewer));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _progress = progress ?? NullEditorialJourneyProgressSink.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<EditorialJourneyResult> RunAsync(
        EditorialJourneyRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var fingerprint = Fingerprint(request);
        var existing = await _checkpoints.LoadAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var resumed = existing is not null;
        var checkpoint = existing ?? EditorialJourneyCheckpoint.New(request.ProjectId, fingerprint);

        if (!string.Equals(checkpoint.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new EditorialJourneyException(
                checkpoint.NextStage,
                "request_fingerprint_conflict",
                "The persisted journey belongs to a different request and cannot be resumed safely.");
        }
        if (checkpoint.Completed)
        {
            return ToResult(checkpoint, resumed);
        }

        try
        {
            checkpoint = await EnsureDraftAsync(
                request,
                checkpoint,
                EditorialJourneyStage.Briefing,
                EditorialArtifactIdFactory.Briefing(request.ProjectId),
                ct => _generator.GenerateBriefingAsync(request, ct),
                cancellationToken).ConfigureAwait(false);

            var briefing = RequireArtifact(checkpoint, "briefing");
            checkpoint = await EnsureDraftAsync(
                request,
                checkpoint,
                EditorialJourneyStage.Outline,
                EditorialArtifactIdFactory.Outline(request.ProjectId),
                ct => _generator.GenerateOutlineAsync(request, briefing, ct),
                cancellationToken).ConfigureAwait(false);

            var outline = RequireArtifact(checkpoint, "outline");
            checkpoint = await EnsureDraftAsync(
                request,
                checkpoint,
                EditorialJourneyStage.Chapter,
                EditorialArtifactIdFactory.Chapter(request.ProjectId, request.ChapterNumber),
                ct => _generator.GenerateChapterAsync(request, briefing, outline, ct),
                cancellationToken).ConfigureAwait(false);

            var chapter = RequireArtifact(checkpoint, "chapter");
            checkpoint = await EnsureValidationAsync(request, checkpoint, chapter, cancellationToken).ConfigureAwait(false);
            checkpoint = await EnsureReviewAsync(request, checkpoint, briefing, outline, chapter, cancellationToken).ConfigureAwait(false);
            checkpoint = await EnsureReleaseAsync(request, checkpoint, chapter, cancellationToken).ConfigureAwait(false);
            checkpoint = await EnsurePreflightAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);

            checkpoint = checkpoint with
            {
                NextStage = EditorialJourneyStage.Complete,
                Completed = true,
            };
            checkpoint = await RecordAndSaveAsync(checkpoint, EditorialJourneyStage.Complete, "PASS", "journey_complete", cancellationToken)
                .ConfigureAwait(false);
            return ToResult(checkpoint, resumed);
        }
        catch (EditorialJourneyException exception)
        {
            await RecordFailureBestEffortAsync(checkpoint, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var wrapped = new EditorialJourneyException(
                checkpoint.NextStage,
                "unexpected_orchestrator_failure",
                "The deterministic journey failed before its postconditions were verified.",
                exception);
            await RecordFailureBestEffortAsync(checkpoint, wrapped, cancellationToken).ConfigureAwait(false);
            throw wrapped;
        }
    }

    private async ValueTask<EditorialJourneyCheckpoint> EnsureDraftAsync(
        EditorialJourneyRequest request,
        EditorialJourneyCheckpoint checkpoint,
        EditorialJourneyStage stage,
        string artifactId,
        Func<CancellationToken, ValueTask<GeneratedEditorialContent>> generate,
        CancellationToken cancellationToken)
    {
        var key = ArtifactKey(stage);
        if (checkpoint.Artifacts.TryGetValue(key, out var recorded))
        {
            var persisted = await _artifacts.GetAsync(request.ProjectId, recorded.ArtifactId, recorded.Version, cancellationToken)
                .ConfigureAwait(false);
            VerifyArtifactPostcondition(stage, recorded, persisted);
            return checkpoint;
        }

        var alreadyPersisted = await _artifacts.GetAsync(request.ProjectId, artifactId, ArtifactVersion, cancellationToken)
            .ConfigureAwait(false);
        PersistedEditorialArtifact artifact;
        if (alreadyPersisted is not null)
        {
            artifact = alreadyPersisted;
        }
        else
        {
            await ReportAsync(stage, "START", "generation_started", cancellationToken).ConfigureAwait(false);
            var generated = await generate(cancellationToken).ConfigureAwait(false);
            var content = EditorialGeneratedContentPolicy.NormalizeAndValidate(generated, stage);
            artifact = await _artifacts.RegisterAsync(
                    request.ProjectId,
                    artifactId,
                    ArtifactVersion,
                    "text/markdown",
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var verified = await _artifacts.GetAsync(request.ProjectId, artifactId, ArtifactVersion, cancellationToken)
            .ConfigureAwait(false);
        VerifyArtifactPostcondition(stage, artifact, verified);
        checkpoint = WithArtifact(checkpoint, key, artifact) with { NextStage = Next(stage) };
        return await RecordAndSaveAsync(checkpoint, stage, "PASS", "artifact_verified", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EditorialJourneyCheckpoint> EnsureValidationAsync(
        EditorialJourneyRequest request,
        EditorialJourneyCheckpoint checkpoint,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken)
    {
        if (checkpoint.NextStage > EditorialJourneyStage.ChapterValidation)
        {
            return checkpoint;
        }
        await ReportAsync(EditorialJourneyStage.ChapterValidation, "START", "validation_started", cancellationToken)
            .ConfigureAwait(false);
        var validation = await _artifacts.ValidateAsync(
                request.ProjectId,
                chapter.ArtifactId,
                chapter.Version,
                cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new EditorialJourneyException(
                EditorialJourneyStage.ChapterValidation,
                "chapter_validation_failed",
                "The persisted chapter failed deterministic validation.");
        }
        checkpoint = checkpoint with { NextStage = EditorialJourneyStage.IndependentReview };
        return await RecordAndSaveAsync(checkpoint, EditorialJourneyStage.ChapterValidation, "PASS", "chapter_valid", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EditorialJourneyCheckpoint> EnsureReviewAsync(
        EditorialJourneyRequest request,
        EditorialJourneyCheckpoint checkpoint,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Review is not null)
        {
            if (checkpoint.Review.Decision != EditorialReviewDecision.Pass)
            {
                throw new EditorialJourneyException(
                    EditorialJourneyStage.IndependentReview,
                    "review_not_passed",
                    "The independent review did not approve the chapter.");
            }
            return checkpoint;
        }
        await ReportAsync(EditorialJourneyStage.IndependentReview, "START", "review_started", cancellationToken)
            .ConfigureAwait(false);
        var review = await _reviewer.ReviewAsync(request, briefing, outline, chapter, cancellationToken)
            .ConfigureAwait(false);
        if (review.Decision != EditorialReviewDecision.Pass)
        {
            throw new EditorialJourneyException(
                EditorialJourneyStage.IndependentReview,
                review.Decision == EditorialReviewDecision.Revise ? "review_requires_revision" : "review_blocked",
                "The independent reviewer did not return PASS.");
        }
        checkpoint = checkpoint with
        {
            Review = review,
            NextStage = EditorialJourneyStage.ReleasePreparation,
        };
        return await RecordAndSaveAsync(checkpoint, EditorialJourneyStage.IndependentReview, "PASS", "review_passed", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EditorialJourneyCheckpoint> EnsureReleaseAsync(
        EditorialJourneyRequest request,
        EditorialJourneyCheckpoint checkpoint,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Release is not null)
        {
            var existing = await _artifacts.GetReleaseAsync(
                    request.ProjectId,
                    checkpoint.Release.ReleaseArtifactId,
                    checkpoint.Release.Version,
                    cancellationToken)
                .ConfigureAwait(false);
            VerifyReleasePostcondition(checkpoint.Release, existing);
            return checkpoint;
        }

        var releaseId = "editorial-proof";
        var releaseArtifactId = EditorialArtifactIdFactory.Release(request.ProjectId, releaseId);
        var persisted = await _artifacts.GetReleaseAsync(request.ProjectId, releaseArtifactId, ArtifactVersion, cancellationToken)
            .ConfigureAwait(false);
        var release = persisted ?? await _artifacts.PrepareReleaseAsync(
                request.ProjectId,
                releaseId,
                request.Title,
                request.Language,
                chapter,
                cancellationToken)
            .ConfigureAwait(false);
        var verified = await _artifacts.GetReleaseAsync(request.ProjectId, release.ReleaseArtifactId, release.Version, cancellationToken)
            .ConfigureAwait(false);
        VerifyReleasePostcondition(release, verified);
        checkpoint = checkpoint with
        {
            Release = release,
            NextStage = EditorialJourneyStage.Preflight,
        };
        return await RecordAndSaveAsync(checkpoint, EditorialJourneyStage.ReleasePreparation, "PASS", "release_verified", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EditorialJourneyCheckpoint> EnsurePreflightAsync(
        EditorialJourneyRequest request,
        EditorialJourneyCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint.NextStage > EditorialJourneyStage.Preflight)
        {
            return checkpoint;
        }
        var release = checkpoint.Release ?? throw new EditorialJourneyException(
            EditorialJourneyStage.Preflight,
            "release_missing",
            "Preflight cannot run without a verified release.");
        var preflight = await _artifacts.PreflightAsync(request.ProjectId, release, cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.Passed || preflight.BlockingReasons.Count > 0)
        {
            throw new EditorialJourneyException(
                EditorialJourneyStage.Preflight,
                "preflight_blocked",
                "The release preflight returned blocking reasons.");
        }
        checkpoint = checkpoint with { NextStage = EditorialJourneyStage.Complete };
        return await RecordAndSaveAsync(checkpoint, EditorialJourneyStage.Preflight, "PASS", "preflight_passed", cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<EditorialJourneyCheckpoint> RecordAndSaveAsync(
        EditorialJourneyCheckpoint checkpoint,
        EditorialJourneyStage stage,
        string status,
        string code,
        CancellationToken cancellationToken)
    {
        var journeyEvent = NewEvent(stage, status, code);
        var events = checkpoint.Events.Concat([journeyEvent]).ToArray();
        checkpoint = checkpoint with { Events = events };
        await _checkpoints.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        await _progress.ReportAsync(journeyEvent, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private async ValueTask ReportAsync(
        EditorialJourneyStage stage,
        string status,
        string code,
        CancellationToken cancellationToken) =>
        await _progress.ReportAsync(NewEvent(stage, status, code), cancellationToken).ConfigureAwait(false);

    private async ValueTask RecordFailureBestEffortAsync(
        EditorialJourneyCheckpoint checkpoint,
        EditorialJourneyException exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordAndSaveAsync(checkpoint, exception.Stage, "FAIL", exception.Code, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Preserve the primary deterministic failure. Evidence persistence is best effort here.
        }
    }

    private EditorialJourneyEvent NewEvent(EditorialJourneyStage stage, string status, string code) =>
        new(stage, status, code, _timeProvider.GetUtcNow());

    private static EditorialJourneyCheckpoint WithArtifact(
        EditorialJourneyCheckpoint checkpoint,
        string key,
        PersistedEditorialArtifact artifact)
    {
        var artifacts = new Dictionary<string, PersistedEditorialArtifact>(checkpoint.Artifacts, StringComparer.Ordinal)
        {
            [key] = artifact,
        };
        return checkpoint with
        {
            Artifacts = new ReadOnlyDictionary<string, PersistedEditorialArtifact>(artifacts),
        };
    }

    private static PersistedEditorialArtifact RequireArtifact(EditorialJourneyCheckpoint checkpoint, string key) =>
        checkpoint.Artifacts.TryGetValue(key, out var artifact)
            ? artifact
            : throw new EditorialJourneyException(checkpoint.NextStage, "checkpoint_artifact_missing", $"Checkpoint artifact '{key}' is missing.");

    private static void VerifyArtifactPostcondition(
        EditorialJourneyStage stage,
        PersistedEditorialArtifact expected,
        PersistedEditorialArtifact? actual)
    {
        if (actual is null ||
            !string.Equals(actual.ArtifactId, expected.ArtifactId, StringComparison.Ordinal) ||
            actual.Version != expected.Version ||
            !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) ||
            actual.Length != expected.Length)
        {
            throw new EditorialJourneyException(stage, "artifact_postcondition_failed", "Persisted artifact postcondition verification failed.");
        }
    }

    private static void VerifyReleasePostcondition(EditorialReleaseResult expected, EditorialReleaseResult? actual)
    {
        if (actual is null ||
            !string.Equals(actual.ReleaseArtifactId, expected.ReleaseArtifactId, StringComparison.Ordinal) ||
            actual.Version != expected.Version ||
            !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal))
        {
            throw new EditorialJourneyException(
                EditorialJourneyStage.ReleasePreparation,
                "release_postcondition_failed",
                "Persisted release postcondition verification failed.");
        }
    }

    private static string ArtifactKey(EditorialJourneyStage stage) => stage switch
    {
        EditorialJourneyStage.Briefing => "briefing",
        EditorialJourneyStage.Outline => "outline",
        EditorialJourneyStage.Chapter => "chapter",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static EditorialJourneyStage Next(EditorialJourneyStage stage) => stage switch
    {
        EditorialJourneyStage.Briefing => EditorialJourneyStage.Outline,
        EditorialJourneyStage.Outline => EditorialJourneyStage.Chapter,
        EditorialJourneyStage.Chapter => EditorialJourneyStage.ChapterValidation,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static EditorialJourneyResult ToResult(EditorialJourneyCheckpoint checkpoint, bool resumed) =>
        new(
            checkpoint.ProjectId,
            checkpoint.NextStage,
            checkpoint.Completed,
            resumed,
            checkpoint.Review?.Decision,
            checkpoint.Artifacts,
            checkpoint.Release,
            checkpoint.Events);

    private static string Fingerprint(EditorialJourneyRequest request)
    {
        var canonical = string.Join('|',
            request.ProjectId,
            request.Idea.Trim(),
            request.Language.Trim(),
            request.Title.Trim(),
            request.ChapterNumber);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateRequest(EditorialJourneyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = EditorialArtifactIdFactory.Briefing(request.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Idea);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        if (request.ChapterNumber is < 1 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Chapter number must be between 1 and 999.");
        }
    }
}
