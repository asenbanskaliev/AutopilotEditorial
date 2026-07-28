using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class DiscoveryJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "discovery-journey.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 10, "Discovery migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 11, 30, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var draft = new DiscoverySessionDraft(sessionId, projectId, workspace, "1.0.0", new[]
        {
            new DiscoveryQuestion("promise", 1, DiscoveryQuestionType.Text, true, "¿Cuál es la promesa del libro?"),
            new DiscoveryQuestion("chapters", 2, DiscoveryQuestionType.Number, true, "¿Cuántos capítulos?"),
            new DiscoveryQuestion("appendix", 3, DiscoveryQuestionType.Boolean, false, "¿Incluye anexos?")
        }, "discovery-create-v1");

        Guid messageId;
        await using (var store = new SqliteDiscoverySessionStore(factory))
        {
            var created = await store.CreateAsync(draft, now);
            Require(!created.Replayed && created.Session.Status == DiscoverySessionStatus.Open, "Discovery creation failed.");
            Require((await store.CreateAsync(draft, now.AddSeconds(1))).Replayed, "Discovery create replay failed.");
            await RequireThrowsAsync<DiscoveryConflictException>(() => store.CreateAsync(draft with { SchemaVersion = "2.0.0" }, now.AddSeconds(2)).AsTask());

            var blocked = new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, sessionId, "editor", "complete-blocked");
            await RequireThrowsAsync<DiscoveryCompletionException>(() => store.CompleteAsync(blocked, now.AddMinutes(1)).AsTask());

            var answer1 = new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, sessionId, "promise", "\"Una guía práctica\"", "editor", "answer-promise-v1");
            var answered = await store.AnswerAsync(answer1, now.AddMinutes(2));
            Require(answered.Answers.Single(a => a.QuestionKey == "promise").Version == 1, "First answer version missing.");
            var replayAnswer = await store.AnswerAsync(answer1, now.AddMinutes(3));
            Require(replayAnswer.Answers.Count(a => a.QuestionKey == "promise") == 1, "Answer replay duplicated history.");
            await RequireThrowsAsync<DiscoveryConflictException>(() => store.AnswerAsync(answer1 with { AnswerJson = "\"Changed\"", RequestFingerprint = "answer-promise-conflict" }, now.AddMinutes(3)).AsTask());
            var answer2 = await store.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, sessionId, "promise", "\"Una guía verificable\"", "editor", "answer-promise-v2"), now.AddMinutes(4));
            Require(answer2.Answers.Count(a => a.QuestionKey == "promise") == 2, "Answer history was not versioned.");
            await store.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, sessionId, "chapters", "12", "editor", "answer-chapters-v1"), now.AddMinutes(5));
            await RequireThrowsAsync<ArgumentException>(() => store.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, sessionId, "chapters", "\"twelve\"", "editor", "answer-invalid"), now.AddMinutes(5)).AsTask());

            await store.DecideAsync(new DiscoveryDecisionCommand(Guid.NewGuid(), workspace, sessionId, "tone", "professional", "Audience requires precision", "editor", "brief#tone", "decision-tone-v1"), now.AddMinutes(6));
            var open = await store.SetOpenItemAsync(new DiscoveryOpenItemCommand(Guid.NewGuid(), workspace, sessionId, "sources", "Confirm primary sources", true, false, "editor", "item-sources-open"), now.AddMinutes(7));
            Require(open.OpenItems.Single().Resolved is false, "Required open item missing.");
            await RequireThrowsAsync<DiscoveryCompletionException>(() => store.CompleteAsync(new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, sessionId, "editor", "complete-open-item"), now.AddMinutes(8)).AsTask());
            await store.SetOpenItemAsync(new DiscoveryOpenItemCommand(Guid.NewGuid(), workspace, sessionId, "sources", "Confirm primary sources", true, true, "editor", "item-sources-resolved"), now.AddMinutes(9));

            var completedCommand = new DiscoveryCompleteCommand(Guid.NewGuid(), workspace, sessionId, "editor", "complete-v1");
            var completed = await store.CompleteAsync(completedCommand, now.AddMinutes(10));
            Require(!completed.Replayed && completed.Session.Status == DiscoverySessionStatus.Completed, "Discovery completion failed.");
            messageId = completed.CompletionMessageId;
            var replayComplete = await store.CompleteAsync(completedCommand, now.AddMinutes(11));
            Require(replayComplete.Replayed && replayComplete.CompletionMessageId == messageId, "Completion replay duplicated event.");
            await RequireThrowsAsync<DiscoveryImmutableException>(() => store.AnswerAsync(new DiscoveryAnswerCommand(Guid.NewGuid(), workspace, sessionId, "appendix", "true", "editor", "after-complete"), now.AddMinutes(12)).AsTask());
            Require(await store.GetAsync("workspace-b", sessionId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteDiscoverySessionStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, sessionId) ?? throw new InvalidOperationException("Discovery session was not durable across restart.");
            Require(durable.Status == DiscoverySessionStatus.Completed && durable.CompletionMessageId == messageId && durable.Answers.Count == 3, "Discovery restart recovery failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("discovery-worker", 50, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(m => m.MessageId == messageId && m.EventType == "editorial.discovery.completed") == 1, "Discovery-completed event was not emitted exactly once.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
