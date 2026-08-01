using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Publishing;

public sealed class ProofWorkflowOrchestrator
{
    private readonly IProofWorkflowStore _store;
    private readonly IProofPackageAuthorityReader _authority;
    private readonly IReadOnlyList<IProofChecklist> _checklists;

    public ProofWorkflowOrchestrator(IProofWorkflowStore store, IProofPackageAuthorityReader authority,
        IEnumerable<IProofChecklist> checklists)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _checklists = (checklists ?? throw new ArgumentNullException(nameof(checklists)))
            .OrderBy(x => x.ChecklistId, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<ProofState> SubmitAsync(ProofRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var authority = await _authority.RequireCurrentAsync(request.Authority, ct);
        RequireApprovedCurrent(authority, request.Authority, "VS-116 package authority is stale, mismatched or not approved.");
        return (await _store.SubmitAsync(request, at, ct)).State;
    }

    public async ValueTask<ProofState> EvaluateAsync(ProofEvaluationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        RequireText(command.WorkspaceId, command.Actor, command.RequestFingerprint);
        var state = await RequireAsync(command.WorkspaceId, command.ProofId, ct);
        RequireRevision(state, command.ExpectedRevision);
        var authority = await _authority.RequireCurrentAsync(state.Authority, ct);
        RequireApprovedCurrent(authority, state.Authority, "VS-116 package authority drifted before proof evaluation.");
        if (_checklists.Count == 0) throw new ProofValidationException("At least one versioned proof checklist is required.");

        var request = new ProofRequest(command.RequestId, state.ProofId, state.ProjectId, state.WorkspaceId,
            state.Authority, state.ProofType, state.Locale, state.Reviewer, command.Actor, command.RequestFingerprint,
            state.SupersedesProofId);
        var context = new ProofReviewContext(request, authority);
        var executions = new List<ProofChecklistExecution>(_checklists.Count);
        var findings = new List<ProofFinding>();

        foreach (var checklist in _checklists)
        {
            RequireText(checklist.ChecklistId, checklist.Version);
            var inputDigest = Hash($"{state.Authority.PackageEvidenceDigest}|{state.Authority.PackageDigest}|{state.ProofType}|{state.Locale}|{checklist.ChecklistId}|{checklist.Version}");
            var raw = await checklist.ExecuteAsync(context, ct);
            var normalized = raw.OrderBy(x => x.RuleId, StringComparer.Ordinal)
                .ThenBy(x => x.Location, StringComparer.Ordinal)
                .ThenBy(x => x.Description, StringComparer.Ordinal)
                .Select(x => NormalizeFinding(checklist, x)).ToArray();
            var outputDigest = Hash(string.Join("\n", normalized.Select(CanonicalFinding)));
            executions.Add(new ProofChecklistExecution(checklist.ChecklistId, checklist.Version, inputDigest, outputDigest, at));
            findings.AddRange(normalized);
        }

        var evidence = BuildEvidenceDigest(state, executions, findings);
        return await _store.EvaluateAsync(command, executions, findings, evidence, at, ct);
    }

    public async ValueTask<ProofState> RecordPhysicalReceiptAsync(PhysicalProofReceiptCommand command,
        DateTimeOffset at, CancellationToken ct = default)
    {
        RequireText(command.WorkspaceId, command.Provider, command.OrderReference, command.InspectedArtifactDigest,
            command.ReviewerAttestation, command.Actor, command.RequestFingerprint);
        var state = await RequireAsync(command.WorkspaceId, command.ProofId, ct);
        RequireRevision(state, command.ExpectedRevision);
        if (state.ProofType != ProofType.Physical)
            throw new ProofTransitionException("A physical receipt can only be recorded for a physical proof.");
        if (!StringComparer.Ordinal.Equals(command.InspectedArtifactDigest, state.Authority.PackageDigest))
            throw new ProofValidationException("Physical proof artifact digest does not match the approved package.");
        return await _store.RecordPhysicalReceiptAsync(command, at, ct);
    }

    public async ValueTask<ProofState> DecideAsync(ProofDecisionCommand command, DateTimeOffset at,
        CancellationToken ct = default)
    {
        RequireText(command.WorkspaceId, command.Reason, command.Evidence, command.EvidenceDigest,
            command.Actor, command.RequestFingerprint);
        var state = await RequireAsync(command.WorkspaceId, command.ProofId, ct);
        RequireRevision(state, command.ExpectedRevision);
        if (command.Decision == ProofDecision.Approve)
        {
            if (state.Status is not (ProofStatus.Evaluated or ProofStatus.AwaitingPhysicalReceipt) || string.IsNullOrWhiteSpace(state.EvidenceDigest))
                throw new ProofTransitionException("Only an evaluated proof with immutable evidence can be approved.");
            if (state.Findings.Any(x => x.Severity == ProofFindingSeverity.Blocking && x.Status == ProofFindingStatus.Open))
                throw new ProofTransitionException("Unresolved blocking proof findings prevent approval.");
            if (state.ProofType == ProofType.Physical && state.PhysicalReceipt is null)
                throw new ProofTransitionException("Physical proof approval requires a durable receipt and reviewer attestation.");
        }
        if (command.Decision == ProofDecision.Supersede && state.Status != ProofStatus.Approved)
            throw new ProofTransitionException("Only an approved proof can be superseded.");
        return await _store.DecideAsync(command, at, ct);
    }

    private static ProofFinding NormalizeFinding(IProofChecklist checklist, ProofFindingInput input)
    {
        RequireText(input.RuleId, input.Location, input.Description, input.Annotation);
        var annotationDigest = Hash(input.Annotation);
        var evidenceDigest = Hash($"{checklist.ChecklistId}|{checklist.Version}|{input.RuleId}|{input.Severity}|{input.Location}|{input.Description}|{annotationDigest}|{input.Status}|{input.Disposition}");
        return new ProofFinding(DeterministicGuid(evidenceDigest), checklist.ChecklistId, checklist.Version,
            input.RuleId, input.Severity, input.Location.Trim(), input.Description.Trim(), annotationDigest,
            evidenceDigest, input.Status, input.Disposition);
    }

    private static string CanonicalFinding(ProofFinding value) =>
        $"{value.FindingId:D}|{value.ChecklistId}|{value.ChecklistVersion}|{value.RuleId}|{value.Severity}|{value.Location}|{value.Description}|{value.AnnotationDigest}|{value.EvidenceDigest}|{value.Status}|{value.Disposition}";

    private static string BuildEvidenceDigest(ProofState state, IEnumerable<ProofChecklistExecution> executions,
        IEnumerable<ProofFinding> findings)
    {
        var executionText = string.Join("\n", executions.OrderBy(x => x.ChecklistId, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .Select(x => $"{x.ChecklistId}|{x.Version}|{x.InputDigest}|{x.OutputDigest}"));
        var findingText = string.Join("\n", findings.OrderBy(x => x.ChecklistId, StringComparer.Ordinal)
            .ThenBy(x => x.RuleId, StringComparer.Ordinal).ThenBy(x => x.Location, StringComparer.Ordinal)
            .Select(CanonicalFinding));
        return Hash($"{state.Authority.PackageEvidenceDigest}\n{state.Authority.PackageDigest}\n{state.ProofType}\n{state.Locale}\n{state.Reviewer}\n{executionText}\n{findingText}");
    }

    private async ValueTask<ProofState> RequireAsync(string workspaceId, Guid proofId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, proofId, ct) ?? throw new ProofValidationException("Proof workflow not found.");

    private static void ValidateRequest(ProofRequest request)
    {
        if (request.RequestId == Guid.Empty || request.ProofId == Guid.Empty || request.ProjectId == Guid.Empty ||
            request.Authority.PackageId == Guid.Empty || request.Authority.PackageRevision <= 0)
            throw new ProofValidationException("Stable proof and package identities are required.");
        RequireText(request.WorkspaceId, request.Authority.PackageEvidenceDigest, request.Authority.PackageDigest,
            request.Locale, request.Reviewer, request.Actor, request.RequestFingerprint);
        if (!StringComparer.Ordinal.Equals(request.WorkspaceId, request.Authority.WorkspaceId) ||
            request.ProjectId != request.Authority.ProjectId)
            throw new ProofValidationException("Cross-workspace or cross-project package authority is forbidden.");
        if (request.SupersedesProofId == request.ProofId)
            throw new ProofValidationException("A proof cannot supersede itself.");
    }

    private static void RequireApprovedCurrent(ProofPackageAuthoritySnapshot snapshot, ProofPackageAuthority requested,
        string message)
    {
        if (!snapshot.IsCurrent || requested.Status != ProofPackageAuthorityStatus.Approved ||
            snapshot.Authority.PackageId != requested.PackageId ||
            snapshot.Authority.PackageRevision != requested.PackageRevision ||
            !StringComparer.Ordinal.Equals(snapshot.Authority.PackageEvidenceDigest, requested.PackageEvidenceDigest) ||
            !StringComparer.Ordinal.Equals(snapshot.Authority.PackageDigest, requested.PackageDigest))
            throw new ProofValidationException(message);
    }

    private static void RequireRevision(ProofState state, long expected)
    {
        if (state.Revision != expected) throw new ProofConflictException("Stale proof workflow revision.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new ProofValidationException("Required proof workflow text is missing.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
