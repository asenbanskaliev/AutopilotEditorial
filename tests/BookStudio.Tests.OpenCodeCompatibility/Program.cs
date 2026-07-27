using BookStudio.Tests.OpenCodeCompatibility;

try
{
    var report = await new OpenCodeCompatibilityJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"OPENCODE_COMPATIBILITY_PASS scenarios={report.Scenarios} requests={report.Requests} features={report.Features}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("OPENCODE_COMPATIBILITY_FAIL " + exception);
    return 1;
}
