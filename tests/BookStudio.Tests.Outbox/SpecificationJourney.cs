using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class SpecificationJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "specification-lifecycle.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 12, "Specification migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 13, 40, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var discoveryId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        Guid proposalApproval;

        await using (var discovery = new SqliteDiscoverySessionStore(factory))
        {
            await discovery.CreateAsync(new DiscoverySessionDraft(discoveryId, projectId, workspace, "1.0.0", new[] { new DiscoveryQuestion("premise", 1, DiscoveryQuestionType.Text, true, "Premise") }, "spec-discovery"), now);
            await discovery.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, discoveryId, "premise", "\"A governed book\"", "editor", "spec-answer"), now.AddMinutes(1));
            await discovery.CompleteAsync(new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, discoveryId, "editor", "spec-discovery-complete"), now.AddMinutes(2));
        }
        await using (var proposals = new SqliteEditorialProposalStore(factory))
        {
            var evidence = new[] { new ProposalEvidenceReference("DISCOVERY_ANSWER", "premise", $"discovery:{discoveryId:D}:premise:v1") };
            await proposals.CreateAsync(new EditorialProposalDraft(proposalId, projectId, discoveryId, workspace, "1.0.0", ProposalContent(), evidence, "editor", "spec-proposal-create"), now.AddMinutes(3));
            await proposals.SubmitAsync(new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, proposalId, 1, "editor", "spec-proposal-submit"), now.AddMinutes(4));
            proposalApproval = (await proposals.DecideAsync(new EditorialProposalDecisionCommand(Guid.NewGuid(), workspace, proposalId, 1, EditorialProposalDecision.Approve, "publisher", "Authorize specification", "spec-proposal-approve"), now.AddMinutes(5))).ApprovalMessageId!.Value;
        }

        var specificationId = Guid.NewGuid();
        var draft = new SpecificationDraft(specificationId, projectId, proposalId, 1, proposalApproval, workspace, "1.0.0", Content("V1"), "architect", "spec-create-v1");
        Guid approvalMessage;
        await using (var store = new SqliteSpecificationStore(factory))
        {
            var created = await store.CreateAsync(draft, now.AddMinutes(6));
            Require(!created.Replayed && created.Specification.Current.Status == SpecificationStatus.Draft, "Specification creation failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(7))).Replayed, "Specification create replay failed.");
            await RequireThrowsAsync<SpecificationConflictException>(() => store.CreateAsync(draft with { SchemaVersion = "2.0.0" }, now.AddMinutes(7)).AsTask());
            await RequireThrowsAsync<SpecificationValidationException>(() => store.CreateAsync(draft with { SpecificationId = Guid.NewGuid(), ProposalApprovalMessageId = Guid.NewGuid() }, now.AddMinutes(7)).AsTask());

            var revise = new SpecificationRevisionCommand(Guid.NewGuid(), workspace, specificationId, 1, 1, Content("V1 revised"), "architect", "Clarify constraints", "spec-revise-v1-r2");
            var revised = await store.ReviseAsync(revise, now.AddMinutes(8));
            Require(revised.Current.Revision == 2 && revised.Current.Status == SpecificationStatus.Draft, "Specification revision failed.");
            Require((await store.ReviseAsync(revise, now.AddMinutes(9))).Current.Revision == 2, "Revision replay duplicated history.");
            await RequireThrowsAsync<SpecificationConflictException>(() => store.PrepareAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 1, "architect", "prepare-stale"), now.AddMinutes(9)).AsTask());

            var prepare = new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 2, "architect", "spec-prepare-v1");
            var prepared = await store.PrepareAsync(prepare, now.AddMinutes(10));
            Require(prepared.Current.Status == SpecificationStatus.Prepared && prepared.Current.Revision == 3, "Specification prepare failed.");
            await RequireThrowsAsync<SpecificationTransitionException>(() => store.ReviseAsync(new SpecificationRevisionCommand(Guid.NewGuid(), workspace, specificationId, 1, 3, Content("illegal"), "architect", "illegal", "illegal-revise"), now.AddMinutes(10)).AsTask());

            var commit = new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 3, "architect", "spec-commit-v1");
            var committed = await store.CommitAsync(commit, now.AddMinutes(11));
            Require(committed.Current.Status == SpecificationStatus.Committed && committed.Current.ContentDigest?.Length == 64, "Specification commit failed.");
            await RequireThrowsAsync<SpecificationTransitionException>(() => store.PrepareAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 4, "architect", "prepare-after-commit"), now.AddMinutes(11)).AsTask());

            var approve = new SpecificationApprovalCommand(Guid.NewGuid(), workspace, specificationId, 1, 4, "publisher", "Approved for planning", "spec-approve-v1");
            var approved = await store.ApproveAsync(approve, now.AddMinutes(12));
            Require(!approved.Replayed && approved.Specification.Current.Status == SpecificationStatus.Approved, "Specification approval failed.");
            approvalMessage = approved.ApprovalMessageId;
            Require((await store.ApproveAsync(approve, now.AddMinutes(13))).Replayed, "Specification approval replay failed.");
            await RequireThrowsAsync<SpecificationConflictException>(() => store.ApproveAsync(approve with { Reason = "changed", RequestFingerprint = "approve-conflict" }, now.AddMinutes(13)).AsTask());

            var next = new SpecificationNextVersionCommand(Guid.NewGuid(), workspace, specificationId, 1, Content("V2"), "architect", "New market requirement", "spec-next-v2");
            var version2 = await store.OpenNextVersionAsync(next, now.AddMinutes(14));
            Require(version2.CurrentVersion == 2 && version2.Current.Status == SpecificationStatus.Draft && version2.Versions.Count == 2, "Specification next version failed.");
            Require(version2.Versions.Single(x => x.Version == 1).Status == SpecificationStatus.Approved, "Approved version history was mutated.");
            Require(await store.GetAsync("workspace-b", specificationId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteSpecificationStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, specificationId) ?? throw new InvalidOperationException("Specification missing after restart.");
            Require(durable.CurrentVersion == 2 && durable.Versions.Single(x => x.Version == 1).ContentDigest?.Length == 64, "Specification restart recovery failed.");
        }

        using (var connection = factory.OpenConnection())
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM book_specification_versions WHERE workspace_id=$w AND specification_id=$id AND version=1;";
            count.Parameters.AddWithValue("$w", workspace);
            count.Parameters.AddWithValue("$id", specificationId.ToString("D"));
            Require(Convert.ToInt32(count.ExecuteScalar()) == 5, "Specification version history is not append-only.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("spec-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessage && x.EventType == "editorial.specification.approved") == 1, "Specification-approved event was not emitted exactly once.");
    }

    private static EditorialProposalContent ProposalContent() => new("Premise", "Audience", "Promise", "Scope", "Differentiators", "Risks", "Assumptions", "Success", "Prepare specification");
    private static SpecificationContent Content(string prefix) => new($"{prefix} goals", "Professional authors", "Complete book", "No orphan chapters", "All coherence gates", "Manuscript and evidence", "Planning accepts only approved specification");
    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
