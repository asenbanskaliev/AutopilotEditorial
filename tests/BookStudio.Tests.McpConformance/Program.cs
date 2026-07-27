using BookStudio.Tests.McpConformance;

try
{
    var report = await new McpConformanceRunner().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"MCP_CONFORMANCE_PASS servers={report.Servers} corpus={report.CorpusCases} fuzz={report.FuzzCases} seed={report.Seed} sha256={report.Sha256}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("MCP_CONFORMANCE_FAIL " + exception);
    return 1;
}
