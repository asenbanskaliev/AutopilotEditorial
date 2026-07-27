using BookStudio.Application.OpenCode;
using BookStudio.Tests.OpenCodeSseReconciliation;

try
{
    var report = await new OpenCodeSseReconciliationJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"OPENCODE_SSE_RECONCILIATION_PASS scenarios={report.Scenarios} requests={report.Requests} events={report.Events} gate={report.MutationGate} tasks={report.TaskGate}");
    return 0;
}
catch (Exception exception)
{
    var code = exception is OpenCodeEventReconciliationException reconciliation
        ? reconciliation.Code
        : "none";
    Console.Error.WriteLine(
        $"OPENCODE_SSE_RECONCILIATION_FAIL type={exception.GetType().Name} code={code}");
    return 1;
}
