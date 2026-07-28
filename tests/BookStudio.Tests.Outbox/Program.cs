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
    Console.WriteLine("HUMAN_GATE_INBOX_PASS schema=PASS claim=PASS decision=PASS idempotency=PASS expiry=PASS cancel=PASS resume_once=PASS restart=PASS audit=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("HUMAN_GATE_INBOX_FAIL: " + exception);
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
