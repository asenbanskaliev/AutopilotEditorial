namespace BookStudio.Autopilot.EditorialJourney;

public enum EditorialControlDecision { Approve, Revise, Block, Resume }
public enum EditorialControlStage { Briefing, Outline, Drafting, GlobalReview, Production, Publication }

public sealed record EditorialControlAction(
    string ProjectId,
    EditorialControlStage Stage,
    EditorialControlDecision Decision,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt,
    string EvidenceHash);

public sealed record EditorialControlSnapshot(
    string ProjectId,
    EditorialControlStage Stage,
    bool IsBlocked,
    bool IsApproved,
    string? BlockReason,
    IReadOnlyList<EditorialControlAction> History);

public interface IEditorialControlAuditStore
{
    ValueTask AppendAsync(EditorialControlAction action, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<EditorialControlAction>> ReadAsync(string projectId, CancellationToken cancellationToken);
}

public sealed class HumanEditorialControlCenter
{
    private readonly IEditorialControlAuditStore _store;
    public HumanEditorialControlCenter(IEditorialControlAuditStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<EditorialControlSnapshot> ApplyAsync(
        string projectId,
        EditorialControlStage stage,
        EditorialControlDecision decision,
        string actor,
        string reason,
        string evidenceHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceHash);

        var history = (await _store.ReadAsync(projectId, cancellationToken).ConfigureAwait(false)).ToList();
        var current = Build(projectId, history);
        ValidateTransition(current, stage, decision);
        var action = new EditorialControlAction(projectId, stage, decision, actor.Trim(), reason.Trim(), DateTimeOffset.UtcNow, evidenceHash.Trim());
        await _store.AppendAsync(action, cancellationToken).ConfigureAwait(false);
        history.Add(action);
        return Build(projectId, history);
    }

    public async ValueTask<EditorialControlSnapshot> GetAsync(string projectId, CancellationToken cancellationToken)
        => Build(projectId, await _store.ReadAsync(projectId, cancellationToken).ConfigureAwait(false));

    private static void ValidateTransition(EditorialControlSnapshot current, EditorialControlStage stage, EditorialControlDecision decision)
    {
        if (stage < current.Stage) throw new InvalidOperationException("Editorial control cannot move backwards.");
        if (current.IsBlocked && decision is not EditorialControlDecision.Resume)
            throw new InvalidOperationException("A blocked journey requires an explicit resume decision.");
        if (!current.IsBlocked && decision is EditorialControlDecision.Resume)
            throw new InvalidOperationException("Only blocked journeys can be resumed.");
        if (decision is EditorialControlDecision.Approve && string.IsNullOrWhiteSpace(current.ProjectId))
            throw new InvalidOperationException("Approval requires a project.");
    }

    private static EditorialControlSnapshot Build(string projectId, IReadOnlyList<EditorialControlAction> history)
    {
        if (history.Count == 0) return new(projectId, EditorialControlStage.Briefing, false, false, null, history);
        var last = history[^1];
        var blocked = last.Decision == EditorialControlDecision.Block;
        var approved = last.Decision == EditorialControlDecision.Approve;
        return new(projectId, last.Stage, blocked, approved, blocked ? last.Reason : null, history);
    }
}

public sealed class InMemoryEditorialControlAuditStore : IEditorialControlAuditStore
{
    private readonly Dictionary<string, List<EditorialControlAction>> _items = new(StringComparer.Ordinal);
    public ValueTask AppendAsync(EditorialControlAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(action.ProjectId, out var list)) _items[action.ProjectId] = list = [];
        list.Add(action);
        return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<EditorialControlAction>> ReadAsync(string projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EditorialControlAction> result = _items.TryGetValue(projectId, out var list) ? list.ToArray() : [];
        return ValueTask.FromResult(result);
    }
}
