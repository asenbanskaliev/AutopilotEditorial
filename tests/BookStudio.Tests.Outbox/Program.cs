using BookStudio.Tests.Integration;
using BookStudio.Tests.Outbox;
using Microsoft.Data.Sqlite;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Outbox",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

try
{
    await OutboxJourney.RunAsync(Path.Combine(workspaceRoot, "legacy"));
    await TransactionalOutboxJourney.RunAsync(Path.Combine(workspaceRoot, "transactional"));
    await SchedulerJourney.RunAsync(Path.Combine(workspaceRoot, "scheduler"));
    await WorkerExecutionJourney.RunAsync(Path.Combine(workspaceRoot, "worker"));
    WorkflowCatalogJourney.Run(Directory.GetCurrentDirectory());
    await HumanGateJourney.RunAsync(Path.Combine(workspaceRoot, "human-gates"));
    await ExecutionControlJourney.RunAsync(Path.Combine(workspaceRoot, "execution-control"));
    await DeadLetterRecoveryJourney.RunAsync(Path.Combine(workspaceRoot, "dead-letter-recovery"));
    await ConcurrencyLimitJourney.RunAsync(Path.Combine(workspaceRoot, "concurrency-limits"));
    await ProjectJourney.RunAsync(Path.Combine(workspaceRoot, "project-journey"));
    Console.WriteLine("PROJECT_JOURNEY_PASS schema=PASS create=PASS idempotency=PASS identity_conflict=PASS workspace_isolation=PASS outbox_once=PASS read_after_write=PASS restart=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("PROJECT_JOURNEY_FAIL: " + exception);
    return 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    TryDelete(workspaceRoot);
}

static void TryDelete(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        // Integration cleanup is best effort.
    }
    catch (UnauthorizedAccessException)
    {
        // Integration cleanup is best effort.
    }
}
