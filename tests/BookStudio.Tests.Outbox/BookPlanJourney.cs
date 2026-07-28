using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class BookPlanJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "book-planning.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 13, "Book planning migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 14, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var discoveryId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var specificationId = Guid.NewGuid();
        Guid proposalApproval;

        await using (var discovery = new SqliteDiscoverySessionStore(factory))
        {
            await discovery.CreateAsync(new DiscoverySessionDraft(discoveryId, projectId, workspace, "1.0.0", new[] { new DiscoveryQuestion("premise", 1, DiscoveryQuestionType.Text, true, "Premise") }, "plan-discovery"), now);
            await discovery.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, discoveryId, "premise", "\"A planned book\"", "editor", "plan-answer"), now.AddMinutes(1));
            await discovery.CompleteAsync(new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, discoveryId, "editor", "plan-complete"), now.AddMinutes(2));
        }
        await using (var proposals = new SqliteEditorialProposalStore(factory))
        {
            var evidence = new[] { new ProposalEvidenceReference("DISCOVERY_ANSWER", "premise", $"discovery:{discoveryId:D}:premise:v1") };
            await proposals.CreateAsync(new EditorialProposalDraft(proposalId, projectId, discoveryId, workspace, "1.0.0", ProposalContent(), evidence, "editor", "plan-proposal"), now.AddMinutes(3));
            await proposals.SubmitAsync(new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, proposalId, 1, "editor", "plan-submit"), now.AddMinutes(4));
            proposalApproval = (await proposals.DecideAsync(new EditorialProposalDecisionCommand(Guid.NewGuid(), workspace, proposalId, 1, EditorialProposalDecision.Approve, "publisher", "Authorize", "plan-proposal-approve"), now.AddMinutes(5))).ApprovalMessageId!.Value;
        }

        Guid specificationApproval;
        await using (var specifications = new SqliteSpecificationStore(factory))
        {
            await specifications.CreateAsync(new SpecificationDraft(specificationId, projectId, proposalId, 1, proposalApproval, workspace, "1.0.0", SpecificationContent(), "architect", "plan-spec-create"), now.AddMinutes(6));
            var prepared = await specifications.PrepareAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 1, "architect", "plan-spec-prepare"), now.AddMinutes(7));
            var committed = await specifications.CommitAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, prepared.Current.Revision, "architect", "plan-spec-commit"), now.AddMinutes(8));
            specificationApproval = (await specifications.ApproveAsync(new SpecificationApprovalCommand(Guid.NewGuid(), workspace, specificationId, 1, committed.Current.Revision, "publisher", "Authorize plan", "plan-spec-approve"), now.AddMinutes(9))).ApprovalMessageId;
        }

        var planId = Guid.NewGuid();
        var content = Content("v1");
        var draft = new BookPlanDraft(planId, projectId, specificationId, 1, specificationApproval, workspace, "1.0.0", content, "planner", "plan-create-v1");
        Guid approvalMessage;
        await using (var store = new SqliteBookPlanStore(factory))
        {
            var created = await store.CreateAsync(draft, now.AddMinutes(10));
            Require(!created.Replayed && created.Plan.Current.Status == BookPlanStatus.Draft, "Book plan creation failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(11))).Replayed, "Book plan create replay failed.");
            await RequireThrowsAsync<BookPlanConflictException>(() => store.CreateAsync(draft with { SchemaVersion = "2.0.0" }, now.AddMinutes(11)).AsTask());
            await RequireThrowsAsync<BookPlanValidationException>(() => store.CreateAsync(draft with { PlanId = Guid.NewGuid(), SpecificationApprovalMessageId = Guid.NewGuid() }, now.AddMinutes(11)).AsTask());
            await RequireThrowsAsync<BookPlanValidationException>(() => store.ReviseAsync(new BookPlanRevisionCommand(Guid.NewGuid(), workspace, planId, 1, 1, CyclicContent(), "planner", "bad graph", "plan-cycle"), now.AddMinutes(12)).AsTask());

            var revise = new BookPlanRevisionCommand(Guid.NewGuid(), workspace, planId, 1, 1, Content("v1-revised"), "planner", "Clarify chapter outcomes", "plan-revise-v1-r2");
            var revised = await store.ReviseAsync(revise, now.AddMinutes(13));
            Require(revised.Current.Revision == 2 && revised.Current.Content.Chapters.Count == 2, "Book plan revision failed.");
            Require((await store.ReviseAsync(revise, now.AddMinutes(14))).Current.Revision == 2, "Book plan revision replay duplicated history.");
            await RequireThrowsAsync<BookPlanConflictException>(() => store.PrepareAsync(new BookPlanControlCommand(Guid.NewGuid(), workspace, planId, 1, 1, "planner", "stale-plan"), now.AddMinutes(14)).AsTask());

            var prepared = await store.PrepareAsync(new BookPlanControlCommand(Guid.NewGuid(), workspace, planId, 1, 2, "planner", "plan-prepare"), now.AddMinutes(15));
            Require(prepared.Current.Status == BookPlanStatus.Prepared && prepared.Current.Revision == 3, "Book plan prepare failed.");
            await RequireThrowsAsync<BookPlanTransitionException>(() => store.ReviseAsync(new BookPlanRevisionCommand(Guid.NewGuid(), workspace, planId, 1, 3, Content("illegal"), "planner", "illegal", "plan-illegal"), now.AddMinutes(15)).AsTask());

            var committed = await store.CommitAsync(new BookPlanControlCommand(Guid.NewGuid(), workspace, planId, 1, 3, "planner", "plan-commit"), now.AddMinutes(16));
            Require(committed.Current.Status == BookPlanStatus.Committed && committed.Current.ContentDigest?.Length == 64, "Book plan commit failed.");

            var approve = new BookPlanApprovalCommand(Guid.NewGuid(), workspace, planId, 1, 4, "publisher", "Approved for drafting", "plan-approve");
            var approved = await store.ApproveAsync(approve, now.AddMinutes(17));
            Require(!approved.Replayed && approved.Plan.Current.Status == BookPlanStatus.Approved, "Book plan approval failed.");
            approvalMessage = approved.ApprovalMessageId;
            Require((await store.ApproveAsync(approve, now.AddMinutes(18))).Replayed, "Book plan approval replay failed.");
            await RequireThrowsAsync<BookPlanConflictException>(() => store.ApproveAsync(approve with { RequestFingerprint = "plan-approve-conflict" }, now.AddMinutes(18)).AsTask());

            var next = await store.OpenNextVersionAsync(new BookPlanNextVersionCommand(Guid.NewGuid(), workspace, planId, 1, Content("v2"), "planner", "Add advanced part", "plan-next-v2"), now.AddMinutes(19));
            Require(next.CurrentVersion == 2 && next.Current.Status == BookPlanStatus.Draft && next.Versions.Count == 2, "Book plan next version failed.");
            Require(next.Versions.Single(x => x.Version == 1).Status == BookPlanStatus.Approved, "Approved book plan version was mutated.");
            Require(await store.GetAsync("workspace-b", planId) is null, "Book plan workspace isolation failed.");
        }

        await using (var restarted = new SqliteBookPlanStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, planId) ?? throw new InvalidOperationException("Book plan missing after restart.");
            Require(durable.CurrentVersion == 2 && durable.Versions.Single(x => x.Version == 1).ContentDigest?.Length == 64, "Book plan restart recovery failed.");
        }

        using (var connection = factory.OpenConnection())
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM book_plan_versions WHERE workspace_id=$w AND plan_id=$id AND version=1;";
            count.Parameters.AddWithValue("$w", workspace); count.Parameters.AddWithValue("$id", planId.ToString("D"));
            Require(Convert.ToInt32(count.ExecuteScalar()) == 5, "Book plan history is not append-only.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("plan-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessage && x.EventType == "editorial.book-plan.approved") == 1, "Book-plan-approved event was not emitted exactly once.");
    }

    private static EditorialProposalContent ProposalContent() => new("Premise", "Audience", "Promise", "Scope", "Differentiators", "Risks", "Assumptions", "Success", "Prepare specification");
    private static SpecificationContent SpecificationContent() => new("Plan a complete book", "Professional readers", "Two governed chapters", "Acyclic dependencies", "All gates", "Book plan", "Only approved plans authorize drafting");
    private static BookPlanContent Content(string prefix) => new(
        new[] { new BookPart("part-1", 1, $"{prefix} Foundations", "Establish the model") },
        new[] {
            new BookChapter("chapter-1", "part-1", 1, $"{prefix} Introduction", "Explain the promise", "Professional readers", new[] { "Draft" }, new[] { "Evidence required" }, new[] { "Promise is explicit" }, Array.Empty<string>()),
            new BookChapter("chapter-2", "part-1", 2, $"{prefix} Application", "Apply the model", "Professional readers", new[] { "Worked example" }, new[] { "Depends on foundations" }, new[] { "Example is reproducible" }, new[] { "chapter-1" }) },
        new[] { "No orphan chapters" }, new[] { "All chapters have measurable outcomes" });
    private static BookPlanContent CyclicContent() => new(
        new[] { new BookPart("part-1", 1, "Part", "Objective") },
        new[] {
            new BookChapter("a", "part-1", 1, "A", "A", "Readers", new[] { "A" }, Array.Empty<string>(), new[] { "A" }, new[] { "b" }),
            new BookChapter("b", "part-1", 2, "B", "B", "Readers", new[] { "B" }, Array.Empty<string>(), new[] { "B" }, new[] { "a" }) },
        Array.Empty<string>(), new[] { "Valid" });
    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}