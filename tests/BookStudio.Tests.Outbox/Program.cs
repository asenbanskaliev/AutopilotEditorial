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
    await DiscoveryJourney.RunAsync(Path.Combine(workspaceRoot, "discovery-journey"));
    await EditorialProposalJourney.RunAsync(Path.Combine(workspaceRoot, "editorial-proposal"));
    await SpecificationJourney.RunAsync(Path.Combine(workspaceRoot, "specification-lifecycle"));
    await BookPlanJourney.RunAsync(Path.Combine(workspaceRoot, "book-planning"));
    Console.WriteLine("BOOK_PLANNING_PASS schema=PASS specification_link=PASS structure=PASS dag=PASS prepare=PASS commit=PASS approval=PASS versions=PASS idempotency=PASS outbox_once=PASS restart=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("BOOK_PLANNING_FAIL: " + exception);
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