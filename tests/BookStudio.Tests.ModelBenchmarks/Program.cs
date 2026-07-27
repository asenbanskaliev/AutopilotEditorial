using BookStudio.Tests.ModelBenchmarks;

try
{
    var report = await new ModelBenchmarksJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"OPENCODE_MODEL_BENCHMARKS_PASS scenarios={report.Scenarios} models={report.Models} roles={report.Roles} gate={report.Gate} mutation={report.Mutation}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        $"OPENCODE_MODEL_BENCHMARKS_FAIL type={exception.GetType().Name} message={exception.Message}");
    return 1;
}
