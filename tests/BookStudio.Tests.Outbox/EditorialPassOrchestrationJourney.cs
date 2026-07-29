using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class EditorialPassOrchestrationJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "editorial-pass-orchestration.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 26, "Editorial pass orchestration migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 13, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var audit = Guid.NewGuid();
        const long auditRevision = 3;
        const string auditDigest = "approved-cross-chapter-audit-digest";
        SeedApprovedAudit(factory, workspace, project, audit, auditRevision, auditDigest, now);

        var planId = Guid.NewGuid();
        Guid completedMessage;
        await using (var store = new SqliteEditorialPassPlanStore(factory))
        {
            var draft = new EditorialPassPlanDraft(planId, project, workspace, audit, auditRevision, auditDigest, 1, "lead-editor", "create-editorial-plan");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Plan.Status == EditorialPlanStatus.Planned && created.Plan.Revision == 1, "Create failed.");
            Require(created.Plan.Passes.Count == 8 && created.Plan.Passes[0].Status == EditorialPassStatus.Ready, "Canonical pass DAG missing.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<EditorialPassConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var revision = 1L;
            foreach (var pass in Enum.GetValues<EditorialPassKind>())
            {
                var start = new EditorialPassCommand(Guid.NewGuid(), workspace, planId, revision, pass, "lead-editor", $"start-{pass}");
                var started = await store.StartPassAsync(start, now.AddMinutes(++revision));
                Require(started.Revision == revision && started.Passes.Single(x => x.Pass == pass).Status == EditorialPassStatus.InProgress, $"Start failed for {pass}.");
                Require((await store.StartPassAsync(start, now.AddMinutes(revision + 1))).Revision == revision, $"Start replay changed state for {pass}.");

                var gate = new EditorialPassGateCommand(Guid.NewGuid(), workspace, planId, revision, pass, EditorialGateResult.Pass, $"green evidence for {pass}", "lead-editor", $"gate-{pass}");
                var gated = await store.RecordGateAsync(gate, now.AddMinutes(++revision));
                Require(gated.Revision == revision && gated.Passes.Single(x => x.Pass == pass).Gate == EditorialGateResult.Pass, $"Gate failed for {pass}.");

                var complete = new EditorialPassCompleteCommand(Guid.NewGuid(), workspace, planId, revision, pass, $"completed {pass}", $"completion evidence for {pass}", "lead-editor", $"complete-{pass}");
                var completed = await store.CompletePassAsync(complete, now.AddMinutes(++revision));
                Require(completed.Revision == revision && completed.Passes.Single(x => x.Pass == pass).Status == EditorialPassStatus.Completed, $"Completion failed for {pass}.");
                if (pass == EditorialPassKind.OriginalityReadAloud)
                {
                    Require(completed.Status == EditorialPlanStatus.Completed, "Plan did not complete after final pass.");
                    completedMessage = completed.MessageId ?? throw new InvalidOperationException("Completion message missing.");
                }
            }

            Require(await store.GetAsync("workspace-b", planId) is null, "Workspace isolation failed.");
            var final = await store.GetAsync(workspace, planId) ?? throw new InvalidOperationException("Plan missing.");
            Require(final.Status == EditorialPlanStatus.Completed && final.Revision == 25 && final.Passes.All(x => x.Status == EditorialPassStatus.Completed && x.Gate == EditorialGateResult.Pass), "Final plan state invalid.");
            completedMessage = final.MessageId ?? throw new InvalidOperationException("Final message missing.");
        }

        await using (var restarted = new SqliteEditorialPassPlanStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, planId) ?? throw new InvalidOperationException("Plan missing after restart.");
            Require(durable.Status == EditorialPlanStatus.Completed && durable.Revision == 25, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM editorial_pass_history WHERE workspace_id=$w AND plan_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", planId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 25, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("editorial-pass-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(2));
        Require(messages.Count(x => x.MessageId == completedMessage && x.EventType == "editorial.pass-plan.completed") == 1, "Plan completion event was not exactly once.");
    }

    private static void SeedApprovedAudit(SqliteConnectionFactory factory, string workspace, Guid project, Guid audit, long revision, string digest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO cross_chapter_audits(workspace_id,audit_id,project_id,rule_set,chapters_json,findings_json,actor,evidence,payload_hash,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'global-v1','[]','[]','auditor','approved evidence',$d,$r,'APPROVED','APPROVE','approved',NULL,$at,$at);";
        command.Parameters.AddWithValue("$w", workspace);
        command.Parameters.AddWithValue("$a", audit.ToString("D"));
        command.Parameters.AddWithValue("$p", project.ToString("D"));
        command.Parameters.AddWithValue("$d", digest);
        command.Parameters.AddWithValue("$r", revision);
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.ExecuteNonQuery();
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
