using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class DeadLetterRecoveryJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "dead-letters.db", 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 7, "Dead-letter migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
        var draft = new DeadLetterDraft(Guid.NewGuid(), DeadLetterSourceKind.SchedulerJob, Guid.NewGuid(), "autopilot.book.generate", "1.0.0", "{\"chapter\":1}", 5, DeadLetterFailureClass.TransientExhausted, "provider timeout", "failure-v1");

        Guid recoveryMessageId;
        await using (var store = new SqliteDeadLetterStore(factory))
        {
            var captured = await store.CaptureAsync(draft, now);
            Require(!captured.AlreadyExists && captured.Record.Status == DeadLetterStatus.Quarantined, "Capture failed.");
            Require((await store.CaptureAsync(draft, now.AddMinutes(1))).AlreadyExists, "Capture replay was not idempotent.");
            await RequireThrowsAsync<DeadLetterConflictException>(() => store.CaptureAsync(draft with { Error = "different" }, now).AsTask());

            var repair = new DeadLetterRepairCommand(Guid.NewGuid(), draft.DeadLetterId, "operator-1", "fix schema", "{\"chapter\":1,\"safe\":true}", "1.1.0", "repair-v1");
            var repaired = await store.RepairAsync(repair, now.AddMinutes(2));
            Require(!repaired.Replayed && repaired.Record.Status == DeadLetterStatus.ReadyForRetry, "Repair failed.");
            Require((await store.RepairAsync(repair, now.AddMinutes(3))).Replayed, "Repair replay was not idempotent.");
            await RequireThrowsAsync<DeadLetterConflictException>(() => store.RepairAsync(repair with { Reason = "different" }, now.AddMinutes(3)).AsTask());

            var recovery = new DeadLetterRecoveryCommand(Guid.NewGuid(), draft.DeadLetterId, "operator-1", "retry repaired payload", "requeue-v1");
            var requeued = await store.RequeueAsync(recovery, now.AddMinutes(4));
            recoveryMessageId = requeued.RecoveryMessageId;
            Require(!requeued.Replayed && requeued.Record.Status == DeadLetterStatus.Requeued, "Requeue failed.");
            var replay = await store.RequeueAsync(recovery, now.AddMinutes(5));
            Require(replay.Replayed && replay.RecoveryMessageId == recoveryMessageId, "Requeue replay duplicated identity.");
            await RequireThrowsAsync<DeadLetterTransitionException>(() => store.DiscardAsync(new DeadLetterDiscardCommand(Guid.NewGuid(), draft.DeadLetterId, "operator-1", "too late", "discard-late"), now.AddMinutes(6)).AsTask());

            var discardDraft = draft with { DeadLetterId = Guid.NewGuid(), SourceId = Guid.NewGuid(), FailureFingerprint = "failure-v2" };
            _ = await store.CaptureAsync(discardDraft, now);
            var discarded = await store.DiscardAsync(new DeadLetterDiscardCommand(Guid.NewGuid(), discardDraft.DeadLetterId, "operator-2", "known poison", "discard-v1"), now.AddMinutes(2));
            Require(discarded.Status == DeadLetterStatus.Discarded && discarded.OriginalPayloadJson == discardDraft.PayloadJson, "Discard did not preserve evidence.");
        }

        await using var restarted = new SqliteDeadLetterStore(factory);
        var durable = await restarted.GetAsync(draft.DeadLetterId) ?? throw new InvalidOperationException("Dead letter was not durable across restart.");
        Require(durable.Status == DeadLetterStatus.Requeued && durable.RecoveryMessageId == recoveryMessageId, "Recovery state was not durable.");
        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("dead-letter-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(item => item.MessageId == recoveryMessageId) == 1, "Recovery event was not emitted exactly once.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
