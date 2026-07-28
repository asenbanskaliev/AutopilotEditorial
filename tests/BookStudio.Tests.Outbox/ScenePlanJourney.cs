using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ScenePlanJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "scene-planning.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 14, "Scene planning migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 14, 30, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var discoveryId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var specificationId = Guid.NewGuid();
        var bookPlanId = Guid.NewGuid();

        Guid proposalApproval;
        await using (var discovery = new SqliteDiscoverySessionStore(factory))
        {
            await discovery.CreateAsync(new DiscoverySessionDraft(discoveryId, projectId, workspace, "1.0.0", new[] { new DiscoveryQuestion("premise", 1, DiscoveryQuestionType.Text, true, "Premise") }, "scene-discovery"), now);
            await discovery.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, discoveryId, "premise", "\"A scene-planned book\"", "editor", "scene-answer"), now.AddMinutes(1));
            await discovery.CompleteAsync(new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, discoveryId, "editor", "scene-complete"), now.AddMinutes(2));
        }
        await using (var proposals = new SqliteEditorialProposalStore(factory))
        {
            var evidence = new[] { new ProposalEvidenceReference("DISCOVERY_ANSWER", "premise", $"discovery:{discoveryId:D}:premise:v1") };
            await proposals.CreateAsync(new EditorialProposalDraft(proposalId, projectId, discoveryId, workspace, "1.0.0", ProposalContent(), evidence, "editor", "scene-proposal"), now.AddMinutes(3));
            await proposals.SubmitAsync(new EditorialProposalSubmitCommand(Guid.NewGuid(), workspace, proposalId, 1, "editor", "scene-submit"), now.AddMinutes(4));
            proposalApproval = (await proposals.DecideAsync(new EditorialProposalDecisionCommand(Guid.NewGuid(), workspace, proposalId, 1, EditorialProposalDecision.Approve, "publisher", "Authorize", "scene-proposal-approve"), now.AddMinutes(5))).ApprovalMessageId!.Value;
        }

        Guid specificationApproval;
        await using (var specifications = new SqliteSpecificationStore(factory))
        {
            await specifications.CreateAsync(new SpecificationDraft(specificationId, projectId, proposalId, 1, proposalApproval, workspace, "1.0.0", SpecificationContent(), "architect", "scene-spec-create"), now.AddMinutes(6));
            var prepared = await specifications.PrepareAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, 1, "architect", "scene-spec-prepare"), now.AddMinutes(7));
            var committed = await specifications.CommitAsync(new SpecificationControlCommand(Guid.NewGuid(), workspace, specificationId, 1, prepared.Current.Revision, "architect", "scene-spec-commit"), now.AddMinutes(8));
            specificationApproval = (await specifications.ApproveAsync(new SpecificationApprovalCommand(Guid.NewGuid(), workspace, specificationId, 1, committed.Current.Revision, "publisher", "Authorize plan", "scene-spec-approve"), now.AddMinutes(9))).ApprovalMessageId;
        }

        Guid bookPlanApproval;
        string bookPlanDigest;
        await using (var plans = new SqliteBookPlanStore(factory))
        {
            await plans.CreateAsync(new BookPlanDraft(bookPlanId, projectId, specificationId, 1, specificationApproval, workspace, "1.0.0", BookContent("v1"), "planner", "scene-book-create"), now.AddMinutes(10));
            var prepared = await plans.PrepareAsync(new BookPlanControlCommand(Guid.NewGuid(), workspace, bookPlanId, 1, 1, "planner", "scene-book-prepare"), now.AddMinutes(11));
            var committed = await plans.CommitAsync(new BookPlanControlCommand(Guid.NewGuid(), workspace, bookPlanId, 1, prepared.Current.Revision, "planner", "scene-book-commit"), now.AddMinutes(12));
            bookPlanDigest = committed.Current.ContentDigest!;
            bookPlanApproval = (await plans.ApproveAsync(new BookPlanApprovalCommand(Guid.NewGuid(), workspace, bookPlanId, 1, committed.Current.Revision, "publisher", "Authorize scenes", "scene-book-approve"), now.AddMinutes(13))).ApprovalMessageId;
        }

        var scenePlanId = Guid.NewGuid();
        var content = Content("v1");
        var draft = new ScenePlanDraft(scenePlanId, projectId, bookPlanId, 1, bookPlanApproval, bookPlanDigest, workspace, "1.0.0", content, "scene-architect", "scene-create-v1");
        Guid approvalMessage;
        await using (var store = new SqliteScenePlanStore(factory))
        {
            var created = await store.CreateAsync(draft, now.AddMinutes(14));
            Require(!created.Replayed && created.ScenePlan.Current.Status == ScenePlanStatus.Draft, "Scene plan creation failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(15))).Replayed, "Scene plan create replay failed.");
            await RequireThrowsAsync<ScenePlanConflictException>(() => store.CreateAsync(draft with { SchemaVersion = "2.0.0" }, now.AddMinutes(15)).AsTask());
            await RequireThrowsAsync<ScenePlanValidationException>(() => store.CreateAsync(draft with { ScenePlanId = Guid.NewGuid(), BookPlanApprovalMessageId = Guid.NewGuid() }, now.AddMinutes(15)).AsTask());
            await RequireThrowsAsync<ScenePlanValidationException>(() => store.ReviseAsync(new ScenePlanRevisionCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 1, MissingChapterContent(), "scene-architect", "missing chapter", "scene-missing"), now.AddMinutes(16)).AsTask());
            await RequireThrowsAsync<ScenePlanValidationException>(() => store.ReviseAsync(new ScenePlanRevisionCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 1, CyclicContent(), "scene-architect", "bad graph", "scene-cycle"), now.AddMinutes(16)).AsTask());

            var revise = new ScenePlanRevisionCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 1, Content("v1-revised"), "scene-architect", "Clarify beats", "scene-revise-r2");
            var revised = await store.ReviseAsync(revise, now.AddMinutes(17));
            Require(revised.Current.Revision == 2 && revised.Current.Content.Scenes.Count == 3, "Scene plan revision failed.");
            Require((await store.ReviseAsync(revise, now.AddMinutes(18))).Current.Revision == 2, "Scene revision replay duplicated history.");
            await RequireThrowsAsync<ScenePlanConflictException>(() => store.PrepareAsync(new ScenePlanControlCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 1, "scene-architect", "scene-stale"), now.AddMinutes(18)).AsTask());

            var prepared = await store.PrepareAsync(new ScenePlanControlCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 2, "scene-architect", "scene-prepare"), now.AddMinutes(19));
            Require(prepared.Current.Status == ScenePlanStatus.Prepared && prepared.Current.Revision == 3, "Scene plan prepare failed.");
            await RequireThrowsAsync<ScenePlanTransitionException>(() => store.ReviseAsync(new ScenePlanRevisionCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 3, Content("illegal"), "scene-architect", "illegal", "scene-illegal"), now.AddMinutes(19)).AsTask());

            var committed = await store.CommitAsync(new ScenePlanControlCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 3, "scene-architect", "scene-commit"), now.AddMinutes(20));
            Require(committed.Current.Status == ScenePlanStatus.Committed && committed.Current.ContentDigest?.Length == 64, "Scene plan commit failed.");

            var approve = new ScenePlanApprovalCommand(Guid.NewGuid(), workspace, scenePlanId, 1, 4, "publisher", "Approved for drafting", "scene-approve");
            var approved = await store.ApproveAsync(approve, now.AddMinutes(21));
            Require(!approved.Replayed && approved.ScenePlan.Current.Status == ScenePlanStatus.Approved, "Scene plan approval failed.");
            approvalMessage = approved.ApprovalMessageId;
            Require((await store.ApproveAsync(approve, now.AddMinutes(22))).Replayed, "Scene plan approval replay failed.");
            await RequireThrowsAsync<ScenePlanConflictException>(() => store.ApproveAsync(approve with { RequestFingerprint = "scene-approve-conflict" }, now.AddMinutes(22)).AsTask());

            var next = await store.OpenNextVersionAsync(new ScenePlanNextVersionCommand(Guid.NewGuid(), workspace, scenePlanId, 1, Content("v2"), "scene-architect", "Add examples", "scene-next-v2"), now.AddMinutes(23));
            Require(next.CurrentVersion == 2 && next.Current.Status == ScenePlanStatus.Draft && next.Versions.Count == 2, "Scene plan next version failed.");
            Require(next.Versions.Single(x => x.Version == 1).Status == ScenePlanStatus.Approved, "Approved scene plan version was mutated.");
            Require(await store.GetAsync("workspace-b", scenePlanId) is null, "Scene plan workspace isolation failed.");
        }

        await using (var restarted = new SqliteScenePlanStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, scenePlanId) ?? throw new InvalidOperationException("Scene plan missing after restart.");
            Require(durable.CurrentVersion == 2 && durable.Versions.Single(x => x.Version == 1).ContentDigest?.Length == 64, "Scene plan restart recovery failed.");
        }

        using (var connection = factory.OpenConnection())
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM scene_plan_versions WHERE workspace_id=$w AND scene_plan_id=$id AND version=1;";
            count.Parameters.AddWithValue("$w", workspace); count.Parameters.AddWithValue("$id", scenePlanId.ToString("D"));
            Require(Convert.ToInt32(count.ExecuteScalar()) == 5, "Scene plan history is not append-only.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("scene-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessage && x.EventType == "editorial.scene-plan.approved") == 1, "Scene-plan-approved event was not emitted exactly once.");
    }

    private static EditorialProposalContent ProposalContent() => new("Premise", "Audience", "Promise", "Scope", "Differentiators", "Risks", "Assumptions", "Success", "Prepare specification");
    private static SpecificationContent SpecificationContent() => new("Plan scenes", "Professional readers", "Two chapters", "Acyclic dependencies", "All gates", "Scene plan", "Only approved scene plans authorize drafting");
    private static BookPlanContent BookContent(string prefix) => new(
        new[] { new BookPart("part-1", 1, $"{prefix} Foundations", "Establish the model") },
        new[] {
            new BookChapter("chapter-1", "part-1", 1, $"{prefix} Introduction", "Explain the promise", "Professional readers", new[] { "Draft" }, Array.Empty<string>(), new[] { "Promise explicit" }, Array.Empty<string>()),
            new BookChapter("chapter-2", "part-1", 2, $"{prefix} Application", "Apply the model", "Professional readers", new[] { "Example" }, Array.Empty<string>(), new[] { "Example reproducible" }, new[] { "chapter-1" }) },
        Array.Empty<string>(), new[] { "All chapters planned" });
    private static ScenePlanContent Content(string prefix) => new(
        new[] {
            new PlannedScene("scene-1", "chapter-1", 1, $"{prefix} Opening", "Establish promise", "Open the argument", new[] { "Hook", "Promise" }, new[] { "Source A" }, Array.Empty<string>(), new[] { "Promise visible" }, Array.Empty<string>()),
            new PlannedScene("scene-2", "chapter-1", 2, $"{prefix} Model", "Explain model", "Define the framework", new[] { "Definition", "Example" }, new[] { "Source B" }, Array.Empty<string>(), new[] { "Model understood" }, new[] { "scene-1" }),
            new PlannedScene("scene-3", "chapter-2", 1, $"{prefix} Application", "Apply model", "Work the example", new[] { "Setup", "Result" }, new[] { "Dataset" }, Array.Empty<string>(), new[] { "Result reproducible" }, new[] { "scene-2" }) },
        new[] { "No orphan scenes" }, new[] { "Every chapter covered" });
    private static ScenePlanContent MissingChapterContent() => new(
        new[] { new PlannedScene("scene-1", "chapter-1", 1, "Only", "Purpose", "Summary", new[] { "Beat" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "Accepted" }, Array.Empty<string>()) },
        Array.Empty<string>(), new[] { "Valid" });
    private static ScenePlanContent CyclicContent() => new(
        new[] {
            new PlannedScene("a", "chapter-1", 1, "A", "A", "A", new[] { "A" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "A" }, new[] { "b" }),
            new PlannedScene("b", "chapter-2", 1, "B", "B", "B", new[] { "B" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "B" }, new[] { "a" }) },
        Array.Empty<string>(), new[] { "Valid" });
    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
