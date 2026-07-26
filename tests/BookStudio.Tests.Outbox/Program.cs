using BookStudio.Tests.Integration;
using Microsoft.Data.Sqlite;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Outbox",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

try
{
    await OutboxJourney.RunAsync(workspaceRoot);
    Console.WriteLine("Outbox integration PASS: idempotency, leases, retry, reclaim and restart verified.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Outbox integration FAIL: " + exception);
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
