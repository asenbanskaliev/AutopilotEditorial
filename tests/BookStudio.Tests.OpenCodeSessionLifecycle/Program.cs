using BookStudio.Tests.OpenCodeSessionLifecycle;

try
{
    var report = await new OpenCodeSessionLifecycleJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"OPENCODE_SESSION_LIFECYCLE_PASS scenarios={report.Scenarios} requests={report.Requests} mutations={report.Mutations} gate={report.MutationGate}");
    return 0;
}
catch
{
    Console.Error.WriteLine("OPENCODE_SESSION_LIFECYCLE_FAIL");
    return 1;
}
