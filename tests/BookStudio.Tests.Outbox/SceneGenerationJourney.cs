using System.Text.Json;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class SceneGenerationJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "scene-generation.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 15, "Scene generation migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 17, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var scenePlanId = Guid.NewGuid();
        var scenePlanApproval = Guid.NewGuid();
        const string scenePlanDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        SeedApprovedScenePlan(factory, workspace, projectId, scenePlanId, scenePlanApproval, scenePlanDigest, now);

        var generationId = Guid.NewGuid();
        var brief = new SceneGenerationBrief("scene-1", "chapter-1", 1, "Opening", "Establish the promise", "A governed opening scene", new[] { "Hook", "Promise" }, new[] { "Approved plan" }, new[] { "No unsupported claims" }, new[] { "Hook is explicit", "Promise is explicit" });
        var draft = new SceneGenerationDraft(generationId, projectId, scenePlanId, 1, scenePlanApproval, scenePlanDigest, workspace, "1.0.0", brief, "author", "scene-create");
        var invocation = new SceneInvocation("openai", "gpt-test", "scene-v1", "context-digest", "{\"temperature\":0.2}", "authoring-safe");
        Guid approvalMessage;

        await using (var store = new SqliteSceneGenerationStore(factory))
        {
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Generation.Status == SceneGenerationStatus.Planned, "Scene generation creation failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Scene generation create replay failed.");
            await RequireThrowsAsync<SceneGenerationConflictException>(() => store.CreateAsync(draft with { SchemaVersion = "2.0.0" }, now.AddMinutes(2)).AsTask());
            await RequireThrowsAsync<SceneGenerationValidationException>(() => store.CreateAsync(draft with { GenerationId = Guid.NewGuid(), ScenePlanApprovalMessageId = Guid.NewGuid() }, now.AddMinutes(2)).AsTask());

            var firstStart = new SceneGenerationStartCommand(Guid.NewGuid(), workspace, generationId, 1, invocation, "author", "start-1");
            var running = await store.StartAttemptAsync(firstStart, now.AddMinutes(3));
            Require(running.Status == SceneGenerationStatus.Generating && running.Attempts.Count == 1, "First attempt did not start.");
            Require((await store.StartAttemptAsync(firstStart, now.AddMinutes(4))).Attempts.Count == 1, "Start replay duplicated attempt.");

            var failed = await store.FailAttemptAsync(new SceneGenerationFailCommand(Guid.NewGuid(), workspace, generationId, 2, 1, "PROVIDER_TIMEOUT", "Provider timed out", true, "worker", "fail-1"), now.AddMinutes(5));
            Require(failed.Status == SceneGenerationStatus.Failed && failed.Attempts[0].Retryable == true, "Retryable failure was not recorded.");

            var secondStart = await store.StartAttemptAsync(new SceneGenerationStartCommand(Guid.NewGuid(), workspace, generationId, 3, invocation with { Model = "gpt-test-2" }, "author", "start-2"), now.AddMinutes(6));
            Require(secondStart.Attempts.Count == 2 && secondStart.Attempts[1].Status == SceneAttemptStatus.Running, "Second attempt did not start.");
            await RequireThrowsAsync<SceneGenerationConflictException>(() => store.CompleteAttemptAsync(new SceneGenerationCompleteCommand(Guid.NewGuid(), workspace, generationId, 3, 2, "Text", Array.Empty<AcceptanceEvidence>(), "worker", "stale-complete"), now.AddMinutes(6)).AsTask());
            await RequireThrowsAsync<SceneGenerationValidationException>(() => store.CompleteAttemptAsync(new SceneGenerationCompleteCommand(Guid.NewGuid(), workspace, generationId, 4, 2, "Text", new[] { new AcceptanceEvidence("Hook is explicit", "Paragraph 1") }, "worker", "missing-evidence"), now.AddMinutes(7)).AsTask());

            var evidence = new[] { new AcceptanceEvidence("Hook is explicit", "Opening sentence"), new AcceptanceEvidence("Promise is explicit", "Closing sentence") };
            var complete = new SceneGenerationCompleteCommand(Guid.NewGuid(), workspace, generationId, 4, 2, "A precise opening. The promise is explicit.", evidence, "worker", "complete-2");
            var generated = await store.CompleteAttemptAsync(complete, now.AddMinutes(8));
            Require(generated.Status == SceneGenerationStatus.Generated && generated.Attempts[1].ContentDigest?.Length == 64, "Generated scene was not hashed.");
            Require((await store.CompleteAttemptAsync(complete, now.AddMinutes(9))).Revision == 5, "Completion replay changed revision.");

            var submitted = await store.SubmitAsync(new SceneGenerationSubmitCommand(Guid.NewGuid(), workspace, generationId, 5, "editor", "submit"), now.AddMinutes(10));
            Require(submitted.Status == SceneGenerationStatus.Submitted, "Scene submission failed.");
            var approve = new SceneGenerationApprovalCommand(Guid.NewGuid(), workspace, generationId, 6, "publisher", "Accepted", "approve");
            var approved = await store.ApproveAsync(approve, now.AddMinutes(11));
            approvalMessage = approved.ApprovalMessageId;
            Require(!approved.Replayed && approved.Generation.Status == SceneGenerationStatus.Approved, "Scene approval failed.");
            Require((await store.ApproveAsync(approve, now.AddMinutes(12))).Replayed, "Scene approval replay failed.");
            await RequireThrowsAsync<SceneGenerationConflictException>(() => store.ApproveAsync(approve with { RequestFingerprint = "approve-conflict" }, now.AddMinutes(12)).AsTask());
            Require(await store.GetAsync("workspace-b", generationId) is null, "Scene generation workspace isolation failed.");
        }

        await using (var restarted = new SqliteSceneGenerationStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, generationId) ?? throw new InvalidOperationException("Scene generation missing after restart.");
            Require(durable.Status == SceneGenerationStatus.Approved && durable.Attempts.Count == 2 && durable.Attempts[0].GeneratedText is null, "Restart recovery or failed-attempt isolation failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("scene-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessage && x.EventType == "editorial.scene.approved") == 1, "Scene-approved event was not emitted exactly once.");
    }

    private static void SeedApprovedScenePlan(SqliteConnectionFactory factory, string workspace, Guid projectId, Guid planId, Guid approval, string digest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection(); using var tx = c.BeginTransaction();
        var content = new ScenePlanContent(new[] { new PlannedScene("scene-1", "chapter-1", 1, "Opening", "Establish", "Summary", new[] { "Hook" }, Array.Empty<string>(), Array.Empty<string>(), new[] { "Hook is explicit" }, Array.Empty<string>()) }, Array.Empty<string>(), new[] { "Complete" });
        using var p=c.CreateCommand();p.Transaction=tx;p.CommandText="INSERT INTO scene_plans(workspace_id,scene_plan_id,project_id,book_plan_id,book_plan_version,book_plan_approval_message_id,book_plan_content_digest,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,1,$bm,$bd,'1.0.0',1,$m,$at,$at);";p.Parameters.AddWithValue("$w",workspace);p.Parameters.AddWithValue("$id",planId.ToString("D"));p.Parameters.AddWithValue("$p",projectId.ToString("D"));p.Parameters.AddWithValue("$b",Guid.NewGuid().ToString("D"));p.Parameters.AddWithValue("$bm",Guid.NewGuid().ToString("D"));p.Parameters.AddWithValue("$bd",digest);p.Parameters.AddWithValue("$m",approval.ToString("D"));p.Parameters.AddWithValue("$at",at.ToString("O"));p.ExecuteNonQuery();
        using var v=c.CreateCommand();v.Transaction=tx;v.CommandText="INSERT INTO scene_plan_versions(workspace_id,scene_plan_id,version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc) VALUES($w,$id,1,4,'APPROVED',$c,$d,'publisher','seed',$at,$at);";v.Parameters.AddWithValue("$w",workspace);v.Parameters.AddWithValue("$id",planId.ToString("D"));v.Parameters.AddWithValue("$c",JsonSerializer.Serialize(content));v.Parameters.AddWithValue("$d",digest);v.Parameters.AddWithValue("$at",at.ToString("O"));v.ExecuteNonQuery();tx.Commit();
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
