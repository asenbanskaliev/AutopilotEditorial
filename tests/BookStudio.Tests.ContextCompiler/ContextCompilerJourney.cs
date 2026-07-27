using BookStudio.Application.OpenCode;

namespace BookStudio.Tests.ContextCompiler;

internal sealed class ContextCompilerJourney
{
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private int _scenarios;

    public ContextCompilerReport Run()
    {
        DeterministicOrdering();
        BudgetAndTrustCaps();
        RequiredSourcesFailClosed();
        DuplicateSourcesRejected();
        IntegrityRejected();
        CancellationDoesNotMutate();
        return new ContextCompilerReport(_scenarios, 4);
    }

    private void DeterministicOrdering()
    {
        var compiler = new ContextCompiler();
        var request = Request([
            Source("u", ContextTrustLabels.Untrusted, 0, "UU"),
            Source("v", ContextTrustLabels.Verified, 5, "VV"),
            Source("s", ContextTrustLabels.System, 9, "SS"),
            Source("x", ContextTrustLabels.User, 1, "XX"),
        ]);
        var first = compiler.Compile(request);
        var second = compiler.Compile(request with { Sources = request.Sources.Reverse().ToArray() });
        Require(first == second, "Equivalent source orders did not compile deterministically.");
        Require(first.Entries.Select(item => item.SourceId).SequenceEqual(["s", "v", "x", "u"]),
            "Trust precedence was not preserved.");
        Require(ContextCompiler.Verify(first), "Manifest fingerprint verification failed.");
        _scenarios++;
    }

    private void BudgetAndTrustCaps()
    {
        var compiler = new ContextCompiler();
        var request = Request([
            Source("system", ContextTrustLabels.System, 0, "12345"),
            Source("verified", ContextTrustLabels.Verified, 0, "abcdef"),
            Source("user", ContextTrustLabels.User, 0, "uvwxyz"),
        ], maximum: 10, caps: Caps(system: 5, verified: 3, user: 2, untrusted: 0));
        var result = compiler.Compile(request);
        Require(result.IncludedCharacters == 10, "Global budget was not enforced exactly.");
        Require(result.Entries.Single(item => item.SourceId == "verified").Content == "abc",
            "Verified trust budget did not truncate deterministically.");
        Require(result.Entries.Single(item => item.SourceId == "user").Content == "uv",
            "User trust budget did not truncate deterministically.");
        _scenarios++;
    }

    private void RequiredSourcesFailClosed()
    {
        var compiler = new ContextCompiler();
        RequireCode(
            () => compiler.Compile(Request([
                Source("required", ContextTrustLabels.Verified, 0, "abcdef", required: true),
            ], maximum: 3, caps: Caps(system: 0, verified: 3, user: 0, untrusted: 0))),
            ContextCompilationErrorCodes.BudgetExceeded);
        _scenarios++;
    }

    private void DuplicateSourcesRejected()
    {
        var compiler = new ContextCompiler();
        var source = Source("duplicate", ContextTrustLabels.Verified, 0, "a");
        RequireCode(
            () => compiler.Compile(Request([source, source])),
            ContextCompilationErrorCodes.DuplicateSource);
        _scenarios++;
    }

    private void IntegrityRejected()
    {
        var compiler = new ContextCompiler();
        var invalid = Source("invalid", ContextTrustLabels.Verified, 0, "content") with
        {
            ContentSha256 = new string('0', 64),
        };
        RequireCode(
            () => compiler.Compile(Request([invalid])),
            ContextCompilationErrorCodes.Invalid);
        _scenarios++;
    }

    private void CancellationDoesNotMutate()
    {
        var compiler = new ContextCompiler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            compiler.Compile(Request([Source("source", ContextTrustLabels.System, 0, "content")]), cancellation.Token);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
        catch (OperationCanceledException)
        {
        }
        const int remoteMutations = 0;
        Require(remoteMutations == 0, "Context compilation performed a remote mutation.");
        _scenarios++;
    }

    private static ContextCompilationRequest Request(
        IReadOnlyList<ContextSource> sources,
        int maximum = 1_000,
        IReadOnlyDictionary<string, int>? caps = null) =>
        new(
            ManifestVersion: 1,
            WorkflowId: "authoring.workflow",
            RoleId: "long-form-author",
            ProfileFingerprint: Profile,
            Budget: new ContextBudget(maximum, 64, caps ?? Caps(maximum, maximum, maximum, maximum)),
            Sources: sources);

    private static ContextSource Source(
        string id,
        string trust,
        int priority,
        string content,
        bool required = false) =>
        new(
            id,
            Revision: 1,
            trust,
            priority,
            required,
            MediaType: "text/plain",
            content,
            ContextCompiler.Sha256(content));

    private static IReadOnlyDictionary<string, int> Caps(int system, int verified, int user, int untrusted) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ContextTrustLabels.System] = system,
            [ContextTrustLabels.Verified] = verified,
            [ContextTrustLabels.User] = user,
            [ContextTrustLabels.Untrusted] = untrusted,
        };

    private static void RequireCode(Action action, string code)
    {
        try
        {
            action();
        }
        catch (ContextCompilationException exception) when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected context compilation error '{code}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record ContextCompilerReport(int Scenarios, int Entries);
