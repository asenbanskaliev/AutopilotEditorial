using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class KnowledgeStateJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "knowledge-state.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 19, "Knowledge state migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 20, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var transitionAuditId = Guid.NewGuid();
        var closedMessageId = Guid.NewGuid();
        SeedAuthority(factory, workspace, projectId, transitionAuditId, closedMessageId, now);

        var secretId = Guid.NewGuid();
        var secretDraft = new KnowledgeDraft(
            secretId,
            projectId,
            transitionAuditId,
            closedMessageId,
            workspace,
            KnowledgeKind.Secret,
            "Mara",
            "cabinet-route",
            "The cabinet reveals a hidden route.",
            "Closed transition proves discovery.",
            ["Mara"],
            ["Ivo"],
            now,
            null,
            "auditor",
            "create-secret");

        Guid activationMessageId;
        await using (var store = new SqliteKnowledgeStateStore(factory))
        {
            var created = await store.CreateAsync(secretDraft, now.AddMinutes(1));
            Require(!created.Replayed && created.Entry.Status == KnowledgeStatus.Draft && created.Entry.Actor == "auditor", "Create or actor persistence failed.");
            Require((await store.CreateAsync(secretDraft, now.AddMinutes(2))).Replayed, "Create replay failed.");

            await Throws<KnowledgeConflictException>(() => store.CreateAsync(secretDraft with { Evidence = "Different evidence." }, now.AddMinutes(2)).AsTask());
            await Throws<KnowledgeConflictException>(() => store.CreateAsync(secretDraft with { Knowners = ["Mara", "Nora"] }, now.AddMinutes(2)).AsTask());
            await Throws<KnowledgeConflictException>(() => store.CreateAsync(secretDraft with { Actor = "different-actor" }, now.AddMinutes(2)).AsTask());
            await Throws<KnowledgeValidationException>(() => store.CreateAsync(secretDraft with { EntryId = Guid.NewGuid(), TransitionClosedMessageId = Guid.NewGuid() }, now.AddMinutes(2)).AsTask());

            var activateSecret = new KnowledgeControlCommand(Guid.NewGuid(), workspace, secretId, 1, "editor", "activate-secret");
            var activeSecret = await store.ActivateAsync(activateSecret, now.AddMinutes(3));
            activationMessageId = activeSecret.ActivationMessageId ?? throw new InvalidOperationException("Activation message missing.");
            Require(activeSecret.Status == KnowledgeStatus.Active && activeSecret.Revision == 2, "Activation failed.");
            Require((await store.ActivateAsync(activateSecret, now.AddMinutes(4))).Revision == 2, "Activation replay changed revision.");
            await Throws<KnowledgeConflictException>(() => store.ActivateAsync(activateSecret with { RequestFingerprint = "different" }, now.AddMinutes(4)).AsTask());

            await Throws<KnowledgeValidationException>(() => store.DiscloseAsync(
                new KnowledgeDisclosureCommand(Guid.NewGuid(), workspace, secretId, 2, ["Ivo"], "bad", "editor", "bad-disclosure"),
                now.AddMinutes(5)).AsTask());

            var discloseSecret = new KnowledgeDisclosureCommand(
                Guid.NewGuid(),
                workspace,
                secretId,
                2,
                ["Nora"],
                "Mara tells Nora after trust is established.",
                "editor",
                "disclose-secret");

            var disclosed = await store.DiscloseAsync(discloseSecret, now.AddMinutes(6));
            Require(disclosed.Knowners.SequenceEqual(["Mara", "Nora"]) && disclosed.Disclosures.Count == 1 && disclosed.Revision == 3, "Disclosure failed.");
            var replayedDisclosure = await store.DiscloseAsync(discloseSecret, now.AddMinutes(7));
            Require(replayedDisclosure.Revision == 3 && replayedDisclosure.Disclosures.Count == 1, "Disclosure replay was not exactly once.");

            await Throws<KnowledgeConflictException>(() => store.RetractAsync(
                new KnowledgeTerminalCommand(Guid.NewGuid(), workspace, secretId, 2, "stale", "editor", "stale"),
                now.AddMinutes(8)).AsTask());

            var retract = new KnowledgeTerminalCommand(Guid.NewGuid(), workspace, secretId, 3, "Source was proven fabricated.", "editor", "retract-secret");
            var retracted = await store.RetractAsync(retract, now.AddMinutes(9));
            Require(retracted.Status == KnowledgeStatus.Retracted && retracted.Revision == 4, "Retraction failed.");
            Require(await store.GetAsync("workspace-b", secretId) is null, "Workspace isolation failed.");

            await VerifyFactAndBeliefSemanticsAsync(store, projectId, transitionAuditId, closedMessageId, workspace, now.AddMinutes(10));
        }

        await using (var restarted = new SqliteKnowledgeStateStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, secretId) ?? throw new InvalidOperationException("Entry missing after restart.");
            Require(durable.Status == KnowledgeStatus.Retracted && durable.Disclosures.Count == 1 && durable.Actor == "auditor", "Restart durability failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("knowledge-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(message => message.MessageId == activationMessageId && message.EventType == "editorial.knowledge-state.activated") == 1, "Activation event was not exactly once.");
        Require(messages.Count(message => message.EventType == "editorial.knowledge-state.disclosed" && message.PayloadJson.Contains(secretId.ToString("D"), StringComparison.OrdinalIgnoreCase)) == 1, "Disclosure event was not exactly once.");
    }

    private static async Task VerifyFactAndBeliefSemanticsAsync(
        SqliteKnowledgeStateStore store,
        Guid projectId,
        Guid transitionAuditId,
        Guid closedMessageId,
        string workspace,
        DateTimeOffset now)
    {
        var firstFactId = Guid.NewGuid();
        var secondFactId = Guid.NewGuid();
        var firstFact = new KnowledgeDraft(firstFactId, projectId, transitionAuditId, closedMessageId, workspace, KnowledgeKind.Fact, "cabinet", "route-state", "The route is open.", "Scene evidence A.", [], [], now, null, "auditor", "create-fact-a");
        var secondFact = new KnowledgeDraft(secondFactId, projectId, transitionAuditId, closedMessageId, workspace, KnowledgeKind.Fact, "cabinet", "route-state", "The route is sealed.", "Scene evidence B.", [], [], now, null, "auditor", "create-fact-b");

        await store.CreateAsync(firstFact, now);
        await store.CreateAsync(secondFact, now.AddSeconds(1));
        await store.ActivateAsync(new KnowledgeControlCommand(Guid.NewGuid(), workspace, firstFactId, 1, "editor", "activate-fact-a"), now.AddSeconds(2));
        await Throws<KnowledgeConflictException>(() => store.ActivateAsync(
            new KnowledgeControlCommand(Guid.NewGuid(), workspace, secondFactId, 1, "editor", "activate-fact-b"),
            now.AddSeconds(3)).AsTask());

        var beliefId = Guid.NewGuid();
        var belief = new KnowledgeDraft(beliefId, projectId, transitionAuditId, closedMessageId, workspace, KnowledgeKind.Belief, "cabinet", "route-state", "The route is sealed.", "Ivo has incomplete information.", ["Ivo"], [], now, null, "auditor", "create-belief");
        await store.CreateAsync(belief, now.AddSeconds(4));
        var activeBelief = await store.ActivateAsync(new KnowledgeControlCommand(Guid.NewGuid(), workspace, beliefId, 1, "editor", "activate-belief"), now.AddSeconds(5));
        Require(activeBelief.Status == KnowledgeStatus.Active, "Belief divergence from fact was incorrectly blocked.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid projectId, Guid auditId, Guid closedMessageId, DateTimeOffset atUtc)
    {
        using var connection = factory.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'SCENE','{}','{}','1.0','[]','[]',10,'CLOSED',$m,$at,$at);";
        command.Parameters.AddWithValue("$w", workspace);
        command.Parameters.AddWithValue("$a", auditId.ToString("D"));
        command.Parameters.AddWithValue("$p", projectId.ToString("D"));
        command.Parameters.AddWithValue("$m", closedMessageId.ToString("D"));
        command.Parameters.AddWithValue("$at", atUtc.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
