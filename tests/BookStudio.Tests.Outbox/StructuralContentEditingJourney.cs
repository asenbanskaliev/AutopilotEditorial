using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class StructuralContentEditingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "structural-content-editing.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 28, "Structural/content editing migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 18, 30, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var developmentalReview = Guid.NewGuid();
        const long developmentalRevision = 3;
        var developmentalDigest = Hash($"{workspace}:{developmentalReview:D}:{developmentalRevision}:APPROVED");
        SeedApprovedDevelopmentalReview(factory, workspace, project, plan, developmentalReview, developmentalRevision, now);

        var reviewId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteStructuralContentEditingStore(factory))
        {
            var draft = new StructuralContentReviewDraft(
                reviewId,
                project,
                workspace,
                plan,
                developmentalReview,
                developmentalRevision,
                developmentalDigest,
                1,
                "structural-content-v1",
                "structural-editor",
                "{\"chapters\":[1,2,3],\"scenes\":[\"1.1\",\"2.1\",\"3.1\"]}",
                "create-structural-content-review");

            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == StructuralContentReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<StructuralContentEditingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var findings = new[]
            {
                new StructuralContentFindingDraft(Guid.NewGuid(), StructuralContentFindingArea.ChapterOrder, StructuralContentSeverity.Major, "chapter-order", "chapters:1-3", new[] { 1, 2, 3 }, new[] { "1.1", "2.1", "3.1" }, "The causal chapter order is explicit and preserves escalation."),
                new StructuralContentFindingDraft(Guid.NewGuid(), StructuralContentFindingArea.ContentGap, StructuralContentSeverity.Minor, "objective-coverage", "chapter:2", new[] { 2 }, new[] { "2.1" }, "The chapter objective is covered without a blocking content gap.", false)
            };

            var evaluate = new StructuralContentEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, findings, "structural/content evaluation evidence", "structural-editor", "evaluate-structural-content-review");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(3));
            Require(evaluated.Status == StructuralContentReviewStatus.Evaluated && evaluated.Revision == 2 && evaluated.Findings.Count == 2, "Evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(4))).Revision == 2, "Evaluation replay changed state.");

            var decide = new StructuralContentDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, StructuralContentDecision.Approve, "Order, depth, continuity, coverage and gaps are acceptable.", null, "structural-editor", "approve-structural-content-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(5));
            Require(approved.Status == StructuralContentReviewStatus.Approved && approved.Revision == 3 && approved.Decision == StructuralContentDecision.Approve, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteStructuralContentEditingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == StructuralContentReviewStatus.Approved && durable.Revision == 3 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM structural_content_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("structural-content-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.structural-content.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedApprovedDevelopmentalReview(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO developmental_reviews(workspace_id,review_id,project_id,editorial_plan_id,expected_plan_revision,expected_plan_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,1,'plan-digest',1,'developmental-v1','lead-editor','{}',$r,'APPROVED','APPROVE','Approved for structural/content editing',NULL,NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace);
        cmd.Parameters.AddWithValue("$id", review.ToString("D"));
        cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        cmd.Parameters.AddWithValue("$plan", plan.ToString("D"));
        cmd.Parameters.AddWithValue("$r", revision);
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
