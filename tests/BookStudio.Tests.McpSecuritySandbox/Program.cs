using BookStudio.Tests.McpSecuritySandbox;

try
{
    var report = await new SandboxSecurityJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"MCP_SECURITY_SANDBOX_PASS servers={report.Servers} invalidStarts={report.InvalidStarts} policyReads={report.PolicyReads} quotaChecks={report.QuotaChecks}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("MCP_SECURITY_SANDBOX_FAIL " + exception);
    return 1;
}
