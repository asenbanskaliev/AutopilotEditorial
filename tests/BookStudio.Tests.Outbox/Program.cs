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
    Console.WriteLine("WORKFLOW_CATALOG_PASS schema=PASS references=PASS acyclic=PASS exact_version=PASS fingerprint=PASS immutable=PASS audit=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("WORKFLOW_CATALOG_FAIL: " + exception);
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
