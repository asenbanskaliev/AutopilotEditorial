using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ProjectJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "project-journey.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 9, "Project migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 11, 5, 0, TimeSpan.Zero);
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var command = new CreateEditorialProject(Guid.NewGuid(), workspaceId, projectId, "Atlas", EditorialProjectKind.Technical, "es-ES", "Equipos de ingeniería", "Crear un manual operativo verificable", "project-create-v1");

        Guid messageId;
        await using (var store = new SqliteEditorialProjectStore(factory))
        {
            var created = await store.CreateAsync(command, now);
            Require(!created.Replayed && created.Project.Status == EditorialProjectStatus.Active, "Project creation failed.");
            messageId = created.Project.CreatedMessageId;
            var replay = await store.CreateAsync(command, now.AddMinutes(1));
            Require(replay.Replayed && replay.Project.CreatedMessageId == messageId, "Project replay duplicated creation.");
            await RequireThrowsAsync<EditorialProjectConflictException>(() => store.CreateAsync(command with { Name = "Changed" }, now.AddMinutes(2)).AsTask());
            await RequireThrowsAsync<EditorialProjectConflictException>(() => store.CreateAsync(command with { RequestId = Guid.NewGuid(), Objective = "Changed objective" }, now.AddMinutes(2)).AsTask());
            Require(await store.GetAsync(workspaceId, projectId) is not null, "Read-after-write failed.");
            Require(await store.GetAsync(Guid.NewGuid(), projectId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteEditorialProjectStore(factory))
        {
            var durable = await restarted.GetAsync(workspaceId, projectId) ?? throw new InvalidOperationException("Project was not durable across restart.");
            Require(durable.CreatedMessageId == messageId && durable.Name == "Atlas", "Restart recovery failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("project-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(item => item.MessageId == messageId && item.EventType == "editorial.project.created") == 1, "Project-created event was not emitted exactly once.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
