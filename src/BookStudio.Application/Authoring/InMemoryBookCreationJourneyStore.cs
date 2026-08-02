using System.Collections.Concurrent;

namespace BookStudio.Application.Authoring;

public sealed class InMemoryBookCreationJourneyStore : IBookCreationJourneyStore
{
    private static readonly JourneyPhase[] Phases = Enum.GetValues<JourneyPhase>();
    private readonly ConcurrentDictionary<(string WorkspaceId, Guid JourneyId), BookCreationJourney> _journeys = new();
    private readonly ConcurrentDictionary<(string WorkspaceId, string Fingerprint), Guid> _creates = new();
    private readonly ConcurrentDictionary<(string WorkspaceId, Guid RequestId), string> _commands = new();

    public ValueTask<BookCreationJourneyCreateResult> CreateAsync(BookCreationJourneyDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateDraft(draft);
        var replayKey = (draft.WorkspaceId, draft.RequestFingerprint);
        if (_creates.TryGetValue(replayKey, out var existingId))
        {
            var existing = _journeys[(draft.WorkspaceId, existingId)];
            if (existing.JourneyId != draft.JourneyId || existing.ProjectId != draft.ProjectId)
                throw new BookCreationJourneyConflictException("Create fingerprint was reused with conflicting identity.");
            return ValueTask.FromResult(new BookCreationJourneyCreateResult(existing, true));
        }

        var progress = Phases.Select((phase, index) => new JourneyPhaseProgress(
            phase,
            index == 0 ? JourneyPhaseStatus.Ready : JourneyPhaseStatus.Pending,
            0,
            1,
            index == 0 ? "Ready to understand the book idea." : "Waiting for the previous phase.",
            null,
            null,
            null)).ToArray();

        var placeholder = new JourneyNextAction(JourneyActionKind.StartPhase, JourneyPhase.Intake,
            "Automatically start intake.", true, string.Empty);
        var journey = new BookCreationJourney(
            draft.JourneyId,
            draft.ProjectId,
            draft.WorkspaceId,
            draft.Brief,
            draft.Autonomy,
            JourneyStatus.Active,
            JourneyPhase.Intake,
            progress,
            Array.Empty<JourneyDecision>(),
            Array.Empty<JourneyRepairState>(),
            placeholder,
            1,
            Guid.NewGuid(),
            at,
            at);
        journey = journey with { NextAction = BookCreationJourneyPlanner.Plan(journey) };

        if (!_journeys.TryAdd((draft.WorkspaceId, draft.JourneyId), journey))
            throw new BookCreationJourneyConflictException("Journey already exists.");
        _creates[replayKey] = draft.JourneyId;
        return ValueTask.FromResult(new BookCreationJourneyCreateResult(journey, false));
    }

    public ValueTask<BookCreationJourney> ApplyAsync(BookCreationJourneyCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_journeys.TryGetValue((command.WorkspaceId, command.JourneyId), out var journey))
            throw new KeyNotFoundException("Journey was not found in the requested workspace.");
        if (journey.Revision != command.ExpectedRevision)
            throw new BookCreationJourneyConflictException("Expected revision is stale.");

        var requestKey = (command.WorkspaceId, command.RequestId);
        if (_commands.TryGetValue(requestKey, out var priorFingerprint))
        {
            if (!StringComparer.Ordinal.Equals(priorFingerprint, command.RequestFingerprint))
                throw new BookCreationJourneyConflictException("Request id was reused with a different fingerprint.");
            return ValueTask.FromResult(journey);
        }

        var updated = Apply(journey, command, at);
        updated = updated with
        {
            Revision = journey.Revision + 1,
            UpdatedAtUtc = at,
            OutboxMessageId = Guid.NewGuid()
        };
        updated = updated with { NextAction = BookCreationJourneyPlanner.Plan(updated) };
        _journeys[(command.WorkspaceId, command.JourneyId)] = updated;
        _commands[requestKey] = command.RequestFingerprint;
        return ValueTask.FromResult(updated);
    }

    public ValueTask<BookCreationJourney?> GetAsync(string workspaceId, Guid journeyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _journeys.TryGetValue((workspaceId, journeyId), out var journey);
        return ValueTask.FromResult(journey);
    }

    private static BookCreationJourney Apply(BookCreationJourney journey, BookCreationJourneyCommand command, DateTimeOffset at)
    {
        return command.Kind switch
        {
            JourneyCommandKind.RecordAuthority => RecordAuthority(journey, command.Authority ?? throw new BookCreationJourneyValidationException("Authority is required."), at),
            JourneyCommandKind.OpenDecision => OpenDecision(journey, command, at),
            JourneyCommandKind.ResolveDecision => ResolveDecision(journey, command, at),
            JourneyCommandKind.StartRepair => StartRepair(journey, command),
            JourneyCommandKind.CompleteRepair => CompleteRepair(journey, command),
            JourneyCommandKind.Pause => journey with { Status = JourneyStatus.Paused },
            JourneyCommandKind.Resume => journey with { Status = JourneyStatus.Active },
            JourneyCommandKind.Cancel => journey with { Status = JourneyStatus.Cancelled },
            JourneyCommandKind.Advance => journey,
            _ => throw new BookCreationJourneyValidationException("Unsupported journey command.")
        };
    }

    private static BookCreationJourney RecordAuthority(BookCreationJourney journey, JourneyAuthorityReference authority, DateTimeOffset at)
    {
        if (authority.Phase != journey.CurrentPhase || !authority.Approved || !authority.Current || string.IsNullOrWhiteSpace(authority.AuthorityDigest))
            throw new BookCreationJourneyValidationException("Only approved, current authority for the active phase may be recorded.");
        var progress = journey.Progress.Select(x => x.Phase == authority.Phase
            ? x with { Status = JourneyPhaseStatus.Approved, CompletedUnits = x.TotalUnits, Authority = authority, CompletedAtUtc = at }
            : x).ToArray();
        var next = progress.FirstOrDefault(x => x.Status == JourneyPhaseStatus.Pending);
        if (next is not null)
            progress = progress.Select(x => x.Phase == next.Phase ? x with { Status = JourneyPhaseStatus.Ready } : x).ToArray();
        return journey with
        {
            Progress = progress,
            CurrentPhase = next?.Phase ?? JourneyPhase.ReleaseReady,
            Status = next is null ? JourneyStatus.Completed : JourneyStatus.Active
        };
    }

    private static BookCreationJourney OpenDecision(BookCreationJourney journey, BookCreationJourneyCommand command, DateTimeOffset at)
    {
        if (journey.Decisions.Any(x => x.Status == JourneyDecisionStatus.Open))
            throw new BookCreationJourneyConflictException("A blocking decision is already open.");
        var decision = BookCreationJourneyPlanner.CreateDecision(
            command.DecisionId ?? Guid.NewGuid(),
            JourneyDecisionKind.CreativeChoice,
            journey.CurrentPhase,
            "Decision required",
            "Choose how the journey should continue.",
            [new JourneyDecisionOption("continue", "Continue", "Continue with the recommended path.", true)],
            at);
        return journey with { Decisions = journey.Decisions.Append(decision).ToArray(), Status = JourneyStatus.WaitingForDecision };
    }

    private static BookCreationJourney ResolveDecision(BookCreationJourney journey, BookCreationJourneyCommand command, DateTimeOffset at)
    {
        var found = false;
        var decisions = journey.Decisions.Select(x =>
        {
            if (x.DecisionId != command.DecisionId || x.Status != JourneyDecisionStatus.Open) return x;
            if (!x.Options.Any(o => o.OptionId == command.SelectedOptionId))
                throw new BookCreationJourneyValidationException("Selected decision option is invalid.");
            found = true;
            return x with { Status = JourneyDecisionStatus.Resolved, SelectedOptionId = command.SelectedOptionId, ResolvedAtUtc = at };
        }).ToArray();
        if (!found) throw new BookCreationJourneyValidationException("Open decision was not found.");
        return journey with { Decisions = decisions, Status = JourneyStatus.Active };
    }

    private static BookCreationJourney StartRepair(BookCreationJourney journey, BookCreationJourneyCommand command)
    {
        var previous = journey.Repairs.LastOrDefault(x => x.Phase == journey.CurrentPhase);
        var attempt = (previous?.Attempt ?? 0) + 1;
        if (attempt > journey.Autonomy.MaximumAutomaticRepairAttempts)
            throw new BookCreationJourneyValidationException("Automatic repair budget is exhausted.");
        var repair = new JourneyRepairState(journey.CurrentPhase, command.RepairScope ?? "current phase", attempt,
            journey.Autonomy.MaximumAutomaticRepairAttempts, command.RequestFingerprint, JourneyRepairStatus.Running, null);
        return journey with { Repairs = journey.Repairs.Append(repair).ToArray() };
    }

    private static BookCreationJourney CompleteRepair(BookCreationJourney journey, BookCreationJourneyCommand command)
    {
        var repairs = journey.Repairs.ToArray();
        var index = Array.FindLastIndex(repairs, x => x.Status == JourneyRepairStatus.Running);
        if (index < 0) throw new BookCreationJourneyValidationException("No running repair exists.");
        repairs[index] = repairs[index] with { Status = JourneyRepairStatus.Accepted };
        return journey with { Repairs = repairs };
    }

    private static void ValidateDraft(BookCreationJourneyDraft draft)
    {
        if (draft.JourneyId == Guid.Empty || draft.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(draft.WorkspaceId) ||
            string.IsNullOrWhiteSpace(draft.RequestFingerprint) || string.IsNullOrWhiteSpace(draft.Actor))
            throw new BookCreationJourneyValidationException("Journey identity, workspace, actor and fingerprint are required.");
    }
}