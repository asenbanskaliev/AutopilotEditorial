namespace BookStudio.Application.Authoring;

public interface ITimelinePlotStore
{
    ValueTask<TimelineEventCreateResult> CreateEventAsync(TimelineEventDraft draft, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<TimelineEventEntry> ActivateEventAsync(TimelineEventControl command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<PlotThreadCreateResult> CreateThreadAsync(PlotThreadDraft draft, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<PlotThreadEntry> AdvanceThreadAsync(PlotThreadAdvance command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<TimelineEventEntry?> GetEventAsync(string workspaceId, Guid eventId, CancellationToken cancellationToken = default);
    ValueTask<PlotThreadEntry?> GetThreadAsync(string workspaceId, Guid threadId, CancellationToken cancellationToken = default);
}

public sealed record TimelineEventDraft(Guid EventId, Guid ProjectId, Guid KnowledgeEntryId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, string EventKey, long NarrativeOrder, DateTimeOffset OccursAtUtc, IReadOnlyList<Guid> DependsOnEventIds, string Summary, string Actor, string RequestFingerprint);
public sealed record TimelineEventControl(Guid RequestId, string WorkspaceId, Guid EventId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record TimelineEventEntry(Guid EventId, Guid ProjectId, Guid KnowledgeEntryId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, string EventKey, long NarrativeOrder, DateTimeOffset OccursAtUtc, IReadOnlyList<Guid> DependsOnEventIds, string Summary, string Actor, long Revision, TimelineEventStatus Status, Guid? ActivationMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record TimelineEventCreateResult(TimelineEventEntry Entry, bool Replayed);

public sealed record PlotThreadDraft(Guid ThreadId, Guid ProjectId, string WorkspaceId, string ThreadKey, string Title, IReadOnlyList<Guid> RequiredEventIds, string Actor, string RequestFingerprint);
public sealed record PlotThreadAdvance(Guid RequestId, string WorkspaceId, Guid ThreadId, long ExpectedRevision, PlotThreadStatus TargetStatus, Guid? MilestoneEventId, string Reason, string Actor, string RequestFingerprint);
public sealed record PlotThreadMilestone(Guid RequestId, Guid? EventId, PlotThreadStatus Status, string Reason, string Actor, DateTimeOffset AtUtc);
public sealed record PlotThreadEntry(Guid ThreadId, Guid ProjectId, string WorkspaceId, string ThreadKey, string Title, IReadOnlyList<Guid> RequiredEventIds, IReadOnlyList<PlotThreadMilestone> Milestones, string Actor, long Revision, PlotThreadStatus Status, Guid? LastMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record PlotThreadCreateResult(PlotThreadEntry Entry, bool Replayed);

public enum TimelineEventStatus { Draft, Active }
public enum PlotThreadStatus { Planned, Active, Resolved, Abandoned }
public sealed class TimelinePlotValidationException : Exception { public TimelinePlotValidationException(string message) : base(message) { } }
public sealed class TimelinePlotConflictException : Exception { public TimelinePlotConflictException(string message) : base(message) { } }
public sealed class TimelinePlotTransitionException : Exception { public TimelinePlotTransitionException(string message) : base(message) { } }
