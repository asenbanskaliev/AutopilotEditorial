using BookStudio.Tests.AgentToolProfiles;

try
{
    var report = await new AgentToolProfilesJourney().RunAsync().ConfigureAwait(false);
    Console.WriteLine(
        $"OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios={report.Scenarios} profiles={report.Profiles} fingerprints={report.Fingerprints} gate={report.Gate} mutation={report.Mutation}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"OPENCODE_AGENT_TOOL_PROFILES_FAIL type={exception.GetType().Name} message={exception.Message}");
    return 1;
}
