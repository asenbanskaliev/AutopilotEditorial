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
    var detail = exception is InvalidOperationException
        ? exception.Message.Replace('\r', ' ').Replace('\n', ' ')
        : "none";
    var scenario = "unknown";
    foreach (var line in (exception.StackTrace ?? string.Empty).Split('\n'))
    {
        if (!line.Contains("OpenCodeSseReconciliationJourney.", StringComparison.Ordinal))
        {
            continue;
        }
        var start = line.IndexOf("OpenCodeSseReconciliationJourney.", StringComparison.Ordinal);
        var end = line.IndexOf('(', start);
        scenario = end > start ? line[start..end] : "journey";
        break;
    }
    Console.Error.WriteLine(
        $"OPENCODE_SSE_RECONCILIATION_FAIL type={exception.GetType().Name} code={code} detail={detail} scenario={scenario}");
    return 1;
}
