using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class DevelopmentalEditingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "developmental-editing.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 27, "Developmental editing migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 16, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var plan = Guid.NewGuid();
        const long planRevision = 2;
        const string planDigest = "active-developmental-plan-digest";
        SeedActiveDevelopmentalPlan(factory, workspace, project, plan, planRevision, now);

        var reviewId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteDevelopmentalEditingStore(factory))
        {
            var draft = new DevelopmentalReviewDraft(reviewId, project, workspace, plan, planRevision, planDigest, 1, "developmental-v1", "lead-editor", "{\"chapters\":[1,2,3]}", "create-developmental-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == DevelopmentalReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<DevelopmentalEditingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var findings = new[]
            {
                new DevelopmentalFindingDraft(Guid.NewGuid(), DevelopmentalFindingArea.EditorialPromise, DevelopmentalSeverity.Major, "promise-kept", "book", new[] { 1, 3 }, "The opening promise is paid off in chapter 3."),
                new DevelopmentalFindingDraft(Guid.NewGuid(), DevelopmentalFindingArea.CharacterArc, DevelopmentalSeverity.Minor, "arc-progression", "protagonist", new[] { 2 }, "Motivation transition is explicit and coherent.", false)
            };
            var evaluate = new DevelopmentalEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, findings, "developmental evaluation evidence", "lead-editor", "evaluate-developmental-review");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(3));
            Require(evaluated.Status == DevelopmentalReviewStatus.Evaluated && evaluated.Revision == 2 && evaluated.Findings.Count == 2, "Evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(4))).Revision == 2, "Evaluation replay changed state.");

            var decide = new DevelopmentalDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, DevelopmentalDecision.Approve, "Developmental promise, macro structure and arcs are coherent.", null, "lead-editor", "approve-developmental-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(5));
            Require(approved.Status == DevelopmentalReviewStatus.Approved && approved.Revision == 3 && approved.Decision == DevelopmentalDecision.Approve, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteDevelopmentalEditingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == DevelopmentalReviewStatus.Approved && durable.Revision == 3 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM developmental_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("developmental-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.developmental.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedActiveDevelopmentalPlan(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',$r,'IN_PROGRESS',NULL,$at,$at);";
            p.Parameters.AddWithValue("$w", workspace); p.Parameters.AddWithValue("$id", plan.ToString("D")); p.Parameters.AddWithValue("$p", project.ToString("D")); p.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); p.Parameters.AddWithValue("$r", revision); p.Parameters.AddWithValue("$at", at.ToString("O"));
            p.ExecuteNonQuery();
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = tx;
            n.CommandText = "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'DEVELOPMENTAL',0,'[]','IN_PROGRESS',1,NULL,NULL,NULL,'lead-editor',$at,NULL);";
            n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", plan.ToString("D")); n.Parameters.AddWithValue("$at", at.ToString("O"));
            n.ExecuteNonQuery();
        }
        tx.Commit();
    }

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
