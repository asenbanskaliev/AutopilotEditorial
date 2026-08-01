using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class LanguageGovernanceOrchestrator
{
    private const string ContractVersion = "language-contract/v1";
    private readonly ILanguageGovernanceStore _store;
    private readonly IProjectLanguageAuthorityReader _authorityReader;
    private readonly ILanguageDetector _detector;

    public LanguageGovernanceOrchestrator(
        ILanguageGovernanceStore store,
        IProjectLanguageAuthorityReader authorityReader,
        ILanguageDetector detector)
    {
        _store = store;
        _authorityReader = authorityReader;
        _detector = detector;
    }

    public async ValueTask<LanguagePolicySubmissionResult> SubmitAsync(
        LanguagePolicyRequest request,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var snapshot = await _authorityReader.RequireCurrentAsync(request.Authority, ct);
        RequireExactAuthority(request, snapshot);
        return await _store.SubmitAsync(request with
        {
            BookLanguageTag = CanonicalizeLanguageTag(request.BookLanguageTag),
            UiLanguageTag = CanonicalizeLanguageTag(request.UiLanguageTag),
            LocaleProfile = ResolveLocaleProfile(request.BookLanguageTag)
        }, at, ct);
    }

    public CompiledLanguageContract Compile(LanguagePolicyRequest request)
    {
        ValidateRequest(request);
        var tag = CanonicalizeLanguageTag(request.BookLanguageTag);
        var profile = ResolveLocaleProfile(tag);
        var outputLanguage = DescribeOutputLanguage(tag);
        var conventions = DescribeConventions(profile);
        var policyDigest = Hash(CanonicalPolicy(request, tag, profile));
        var instruction = $"LANGUAGE CONTRACT\nContract version: {ContractVersion}\nRequired output language: {outputLanguage} ({tag}).\nRegional conventions: {conventions}\nWrite all narrative, dialogue, headings, descriptions, editorial comments and metadata in the required language. Do not switch language unless the passage is covered by an explicitly approved bounded exception. Preserve quotations, proper nouns and citations only within their approved scope. A materially different language or incompatible regional variant is invalid and must be regenerated.";
        return new CompiledLanguageContract(tag, profile, outputLanguage, conventions, instruction,
            policyDigest, Hash(instruction), ContractVersion);
    }

    public async ValueTask<LanguageValidationState> ValidateGeneratedTextAsync(
        LanguagePolicyRequest policy,
        LanguageValidationCommand command,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        ValidateRequest(policy);
        ValidateCommand(policy, command);
        var compiled = Compile(policy);
        if (!FixedEquals(command.Invocation.PolicyDigest, compiled.PolicyDigest))
            throw new LanguageGovernanceConflictException("Invocation policy digest does not match the current language policy.");

        var inputDigest = Hash(command.GeneratedText);
        if (!FixedEquals(command.Invocation.InputDigest, inputDigest))
            throw new LanguageGovernanceConflictException("Generated text digest does not match the invocation input digest.");

        var detection = await _detector.DetectAsync(command.GeneratedText, ct);
        var findings = BuildFindings(compiled, detection, policy.AllowedSecondaryLanguageScopes, at);
        var blocking = findings.Any(x => x.Severity == LanguageFindingSeverity.Blocking && !x.CoveredByApprovedScope);
        var evidenceDigest = Hash(string.Join("\n", new[]
        {
            compiled.PolicyDigest,
            compiled.InstructionDigest,
            inputDigest,
            detection.OutputDigest,
            _detector.DetectorId,
            _detector.DetectorVersion,
            string.Join("|", findings.OrderBy(x => x.FindingId).Select(x => x.EvidenceDigest))
        }));

        var result = new LanguageValidationResult(
            compiled.BookLanguageTag,
            CanonicalizeLanguageTag(detection.DetectedLanguageTag),
            detection.Confidence,
            findings,
            !blocking,
            blocking,
            $"{_detector.DetectorId}@{_detector.DetectorVersion}",
            inputDigest,
            detection.OutputDigest,
            evidenceDigest);

        return await _store.RecordValidationAsync(command, compiled, result, at, ct);
    }

    private static IReadOnlyList<LanguageFinding> BuildFindings(
        CompiledLanguageContract contract,
        LanguageDetectionResult detection,
        IReadOnlyList<AllowedLanguageScope> scopes,
        DateTimeOffset at)
    {
        var findings = new List<LanguageFinding>();
        var detected = CanonicalizeLanguageTag(detection.DetectedLanguageTag);
        if (!SameBaseLanguage(contract.BookLanguageTag, detected))
        {
            findings.Add(CreateFinding("LANGUAGE_DRIFT", LanguageFindingSeverity.Blocking, null, null,
                contract.BookLanguageTag, detected, detection.Confidence,
                "Predominant generated language differs from the book language.", false));
        }

        foreach (var span in detection.Spans.OrderBy(x => x.Start).ThenBy(x => x.Length))
        {
            var spanTag = CanonicalizeLanguageTag(span.LanguageTag);
            if (SameBaseLanguage(contract.BookLanguageTag, spanTag))
                continue;

            var covered = scopes.Any(scope => IsActive(scope, at) &&
                SameBaseLanguage(scope.LanguageTag, spanTag) && ScopeCovers(scope.LocationPattern, span.Start, span.Length));
            findings.Add(CreateFinding("UNAUTHORIZED_LANGUAGE_SPAN",
                covered ? LanguageFindingSeverity.Info : LanguageFindingSeverity.Blocking,
                span.Start, span.Length, contract.BookLanguageTag, spanTag, span.Confidence,
                covered ? "Secondary-language span is covered by an approved scope." : "Secondary-language span is outside approved scopes.",
                covered));
        }

        return findings;
    }

    private static LanguageFinding CreateFinding(string rule, LanguageFindingSeverity severity, int? start, int? length,
        string expected, string detected, decimal confidence, string message, bool covered)
    {
        var evidence = Hash($"{rule}|{severity}|{start}|{length}|{expected}|{detected}|{confidence}|{covered}");
        return new LanguageFinding(Hash(evidence)[..24], rule, severity, start, length, expected, detected,
            confidence, message, evidence, covered);
    }

    private static void ValidateRequest(LanguagePolicyRequest request)
    {
        if (request.RequestId == Guid.Empty || request.PolicyId == Guid.Empty || request.ProjectId == Guid.Empty)
            throw new LanguageGovernanceValidationException("Request, policy and project identifiers are required.");
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.Actor) ||
            string.IsNullOrWhiteSpace(request.RequestFingerprint))
            throw new LanguageGovernanceValidationException("Workspace, actor and request fingerprint are required.");
        if (request.PolicyRevision <= 0)
            throw new LanguageGovernanceValidationException("Policy revision must be positive.");
        _ = CanonicalizeLanguageTag(request.BookLanguageTag);
        _ = CanonicalizeLanguageTag(request.UiLanguageTag);
        if (!string.Equals(request.Authority.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal) ||
            request.Authority.ProjectId != request.ProjectId)
            throw new LanguageGovernanceValidationException("Language authority must belong to the same workspace and project.");
    }

    private static void ValidateCommand(LanguagePolicyRequest policy, LanguageValidationCommand command)
    {
        if (command.RequestId == Guid.Empty || command.PolicyId != policy.PolicyId || command.ExpectedRevision <= 0)
            throw new LanguageGovernanceValidationException("Validation command identity or revision is invalid.");
        if (!string.Equals(command.WorkspaceId, policy.WorkspaceId, StringComparison.Ordinal) ||
            command.Invocation.ProjectId != policy.ProjectId || command.Invocation.PolicyId != policy.PolicyId ||
            command.Invocation.PolicyRevision != policy.PolicyRevision)
            throw new LanguageGovernanceConflictException("Validation command references stale or cross-workspace language authority.");
        if (string.IsNullOrWhiteSpace(command.GeneratedText))
            throw new LanguageGovernanceValidationException("Generated text is required.");
    }

    private static void RequireExactAuthority(LanguagePolicyRequest request, ProjectLanguageAuthoritySnapshot snapshot)
    {
        if (!snapshot.IsCurrent || snapshot.Authority.Status != ProjectLanguageAuthorityStatus.Active)
            throw new LanguageGovernanceConflictException("Project language authority is not current and active.");
        if (snapshot.Authority != request.Authority)
            throw new LanguageGovernanceConflictException("Project language authority does not exactly match the requested authority.");
        if (!FixedEquals(CanonicalizeLanguageTag(snapshot.Authority.BookLanguageTag), CanonicalizeLanguageTag(request.BookLanguageTag)))
            throw new LanguageGovernanceConflictException("Book language differs from the approved project authority.");
    }

    public static string CanonicalizeLanguageTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new LanguageGovernanceValidationException("A BCP-47 language tag is required.");
        try { return CultureInfo.GetCultureInfo(tag.Trim()).Name; }
        catch (CultureNotFoundException ex) { throw new LanguageGovernanceValidationException($"Invalid BCP-47 language tag: {ex.InvalidCultureName}"); }
    }

    private static string ResolveLocaleProfile(string tag) => CanonicalizeLanguageTag(tag) switch
    {
        "es-ES" => "spanish-spain/v1",
        "es-MX" => "spanish-mexico/v1",
        "en-US" => "english-united-states/v1",
        "en-GB" => "english-united-kingdom/v1",
        var canonical => $"bcp47/{canonical.ToLowerInvariant()}/v1"
    };

    private static string DescribeOutputLanguage(string tag) => CanonicalizeLanguageTag(tag) switch
    {
        "es-ES" => "Spanish as used in Spain",
        "es-MX" => "Spanish as used in Mexico",
        "en-US" => "English as used in the United States",
        "en-GB" => "English as used in the United Kingdom",
        var canonical => CultureInfo.GetCultureInfo(canonical).EnglishName
    };

    private static string DescribeConventions(string profile) => profile switch
    {
        "spanish-spain/v1" => "Spanish spelling and grammar, Spain vocabulary and punctuation conventions",
        "spanish-mexico/v1" => "Spanish spelling and grammar, Mexico vocabulary and punctuation conventions",
        "english-united-states/v1" => "United States spelling, grammar and punctuation conventions",
        "english-united-kingdom/v1" => "United Kingdom spelling, grammar and punctuation conventions",
        _ => "Conventions defined by the canonical BCP-47 locale profile"
    };

    private static string CanonicalPolicy(LanguagePolicyRequest request, string tag, string profile) => string.Join("\n", new[]
    {
        request.WorkspaceId,
        request.ProjectId.ToString("D"),
        request.PolicyId.ToString("D"),
        request.PolicyRevision.ToString(CultureInfo.InvariantCulture),
        tag,
        profile,
        request.Authority.ProjectDigest,
        string.Join("|", request.AllowedSecondaryLanguageScopes.OrderBy(x => x.ScopeId).Select(x =>
            $"{x.ScopeId}:{CanonicalizeLanguageTag(x.LanguageTag)}:{x.Kind}:{x.LocationPattern}:{x.EvidenceDigest}"))
    });

    private static bool SameBaseLanguage(string left, string right) =>
        string.Equals(CanonicalizeLanguageTag(left).Split('-')[0], CanonicalizeLanguageTag(right).Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(AllowedLanguageScope scope, DateTimeOffset at) =>
        !string.IsNullOrWhiteSpace(scope.ApprovedBy) && !string.IsNullOrWhiteSpace(scope.EvidenceDigest) &&
        (!scope.ExpiresAtUtc.HasValue || scope.ExpiresAtUtc.Value >= at);

    private static bool ScopeCovers(string locationPattern, int start, int length) =>
        locationPattern == "*" || string.Equals(locationPattern, $"{start}:{length}", StringComparison.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
