using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class EditorialProposalJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "editorial-proposal.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 11, "Editorial proposal migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 13, 15, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var discoveryId = Guid.NewGuid();

        await using (var discovery = new SqliteDiscoverySessionStore(factory))
        {
            var draft = new DiscoverySessionDraft(discoveryId, projectId, workspace, "1.0.0", new[] { new DiscoveryQuestion("premise", 1, DiscoveryQuestionType.Text, true, "Premise") }, "proposal-discovery");
            await discovery.CreateAsync(draft, now);
            await discovery.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, discoveryId, "premise", "\"A durable editorial system\"", "editor", "proposal-answer"), now.AddMinutes(1));
            await discovery.CompleteAsync(new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, discoveryId, "editor", "proposal-discovery-complete"), now.AddMinutes(2));
        }

        var proposalId = Guid.NewGuid();
        var content = Content("Initial");
        var evidence = new[] { new ProposalEvidenceReference("DISCOVERY_ANSWER", "premise", $"discovery:{discoveryId:D}:premise:v1") };
        var draftProposal = new EditorialProposalDraft(proposalId, projectId, discoveryId, workspace, "1.0.0", content, evidence, "editor", "proposal-create-v1");
        Guid approvalMessageId;

        await using (var store = new SqliteEditorialProposalStore(factory))
        {
            var created = await store.CreateAsync(draftProposal, now.AddMinutes(3));
            Require(!created.Replayed && created.Proposal.Status == EditorialProposalStatus.Draft && created.Proposal.Revision == 1, "Proposal creation failed.");
            Require((await store.CreateAsync(draftProposal, now.AddMinutes(4))).Replayed, "Proposal create replay failed.");
            await RequireThrowsAsync<EditorialProposalConflictException>(() => store.CreateAsync(draftProposal with { SchemaVersion = "2.0.0" }, now.AddMinutes(4)).AsTask());
            await RequireThrowsAsync<EditorialProposalValidationException>(() => store.CreateAsync(draftProposal with { ProposalId = Guid.NewGuid(), DiscoverySessionId = Guid.NewGuid() }, now.AddMinutes(4)).AsTask());

            var reviseCommand = new EditorialProposalRevisionCommand(Guid.NewGuid(), workspace, proposalId, 1, Content("Revised"), evidence, "editor", "Strengthen market promise", "proposal-revise-v2");
            var revised = await store.ReviseAsync(reviseCommand, now.AddMinutes(5));
            Require(revised.Revision == 2 && revised.Content.Premise.StartsWith("Revised", StringComparison.Ordinal), "Proposal revision failed.");
            Require((await store.ReviseAsync(reviseCommand, now.AddMinutes(6))).Revision == 2, "Revision replay duplicated history.");
            await RequireThrowsAsync<EditorialProposalConflictException>(() => store.ReviseAsync(reviseCommand with { Reason = "conflict", RequestFingerprint = "proposal-revise-conflict" }, now.AddMinutes(6)).AsTask());
            await RequireThrowsAsync<EditorialProposalConflictException>(() => store.SubmitAsync(new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, proposalId, 1, "editor", "submit-stale"), now.AddMinutes(7)).AsTask());

            var submit = new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, proposalId, 2, "editor", "proposal-submit-v2");
            var submitted = await store.SubmitAsync(submit, now.AddMinutes(8));
            Require(submitted.Status == EditorialProposalStatus.Submitted, "Proposal submit failed.");
            Require((await store.SubmitAsync(submit, now.AddMinutes(9))).Status == EditorialProposalStatus.Submitted, "Submit replay failed.");
            await RequireThrowsAsync<EditorialProposalTransitionException>(() => store.ReviseAsync(new EditorialProposalRevisionCommand(Guid.NewGuid(), workspace, proposalId, 2, Content("Illegal"), evidence, "editor", "illegal", "illegal-revise"), now.AddMinutes(9)).AsTask());

            var approve = new EditorialProposalDecisionCommand(Guid.NewGuid(), workspace, proposalId, 2, EditorialProposalDecision.Approve, "publisher", "Approved for specification", "proposal-approve-v2");
            var approved = await store.DecideAsync(approve, now.AddMinutes(10));
            Require(!approved.Replayed && approved.Proposal.Status == EditorialProposalStatus.Approved && approved.ApprovalMessageId is not null, "Proposal approval failed.");
            approvalMessageId = approved.ApprovalMessageId!.Value;
            var replay = await store.DecideAsync(approve, now.AddMinutes(11));
            Require(replay.Replayed && replay.ApprovalMessageId == approvalMessageId, "Approval replay duplicated event.");
            await RequireThrowsAsync<EditorialProposalConflictException>(() => store.DecideAsync(approve with { Reason = "changed", RequestFingerprint = "approve-conflict" }, now.AddMinutes(11)).AsTask());

            var rejectedId = Guid.NewGuid();
            var rejectedDraft = draftProposal with { ProposalId = rejectedId, RequestFingerprint = "reject-create" };
            await store.CreateAsync(rejectedDraft, now.AddMinutes(12));
            await store.SubmitAsync(new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, rejectedId, 1, "editor", "reject-submit"), now.AddMinutes(13));
            var rejected = await store.DecideAsync(new EditorialProposalDecisionCommand(Guid.NewGuid(), workspace, rejectedId, 1, EditorialProposalDecision.Reject, "publisher", "Needs narrower scope", "reject-decision"), now.AddMinutes(14));
            Require(rejected.Proposal.Status == EditorialProposalStatus.Rejected && rejected.ApprovalMessageId is null, "Proposal rejection failed.");
            var redraft = await store.ReviseAsync(new EditorialProposalRevisionCommand(Guid.NewGuid(), workspace, rejectedId, 1, Content("Narrowed"), evidence, "editor", "Address rejection", "reject-revise"), now.AddMinutes(15));
            Require(redraft.Status == EditorialProposalStatus.Draft && redraft.Revision == 2, "Rejected proposal could not return to draft.");
            Require(await store.GetAsync("workspace-b", proposalId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteEditorialProposalStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, proposalId) ?? throw new InvalidOperationException("Proposal missing after restart.");
            Require(durable.Status == EditorialProposalStatus.Approved && durable.Revision == 2 && durable.ApprovalMessageId == approvalMessageId, "Proposal restart recovery failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("proposal-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessageId && x.EventType == "editorial.proposal.approved") == 1, "Proposal-approved event was not emitted exactly once.");
    }

    private static EditorialProposalContent Content(string prefix) => new(
        $"{prefix} premise", "Professional authors", "Produce a coherent book", "Full manuscript", "Evidence-linked workflow", "Model drift", "Approved discovery", "All gates pass", "Prepare specification");
    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
