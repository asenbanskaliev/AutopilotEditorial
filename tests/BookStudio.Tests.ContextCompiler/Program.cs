using BookStudio.Tests.ContextCompiler;

try
{
    var report = new ContextCompilerJourney().Run();
    Console.WriteLine($"OPENCODE_CONTEXT_COMPILER_PASS scenarios={report.Scenarios} entries={report.Entries} gate=TRUST_BUDGETS audit=PASS mutation=NONE");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"OPENCODE_CONTEXT_COMPILER_FAIL type={exception.GetType().Name} message={exception.Message}\n{exception.StackTrace}");
    return 1;
}
