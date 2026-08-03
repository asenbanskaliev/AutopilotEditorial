namespace BookStudio.Autopilot.EditorialJourney;

public sealed record LongRunningModelAcceptanceEvidence(
    string ProjectId,
    bool ActualOpenCode,
    bool ActualProductionOrchestrator,
    TimeSpan Elapsed,
    int GeneratedWords,
    int ChapterCount,
    int CompletedChapterReviews,
    bool WholeBookReviewPassed,
    int RestartCount,
    int ProviderTimeouts,
    int ProviderFallbacks,
    int ContextCompactions,
    bool KdpEpubCreated,
    bool KdpInteriorPdfCreated,
    bool KdpCoverPdfCreated,
    bool PublicationPackageVerified,
    int DuplicateChapters,
    int DegradedChapters,
    decimal EstimatedCostUsd,
    long TotalModelLatencyMilliseconds,
    bool SecretLeakageDetected,
    bool CredentialPersisted,
    string EvidenceSha256);

public sealed record LongRunningAcceptancePolicy(
    TimeSpan MinimumElapsed,
    int MinimumWords,
    int MinimumChapters,
    int MinimumRestarts,
    int MinimumFallbacks,
    int MinimumContextCompactions)
{
    public static LongRunningAcceptancePolicy Production =>
        new(TimeSpan.FromHours(2), 30_000, 10, 3, 1, 1);
}

public sealed record LongRunningAcceptanceResult(bool Passed, IReadOnlyList<string> BlockingReasons);

public static class RealLongRunningAcceptanceGate
{
    public static LongRunningAcceptanceResult Evaluate(
        LongRunningModelAcceptanceEvidence evidence,
        LongRunningAcceptancePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        policy ??= LongRunningAcceptancePolicy.Production;
        var blockers = new List<string>();
        if (!evidence.ActualOpenCode) blockers.Add("actual_opencode_missing");
        if (!evidence.ActualProductionOrchestrator) blockers.Add("actual_orchestrator_missing");
        if (evidence.Elapsed < policy.MinimumElapsed) blockers.Add("elapsed_below_full_scale");
        if (evidence.GeneratedWords < policy.MinimumWords) blockers.Add("word_count_below_full_scale");
        if (evidence.ChapterCount < policy.MinimumChapters) blockers.Add("chapter_count_below_full_scale");
        if (evidence.CompletedChapterReviews != evidence.ChapterCount) blockers.Add("chapter_reviews_incomplete");
        if (!evidence.WholeBookReviewPassed) blockers.Add("whole_book_review_failed");
        if (evidence.RestartCount < policy.MinimumRestarts) blockers.Add("restart_coverage_insufficient");
        if (evidence.ProviderFallbacks < policy.MinimumFallbacks) blockers.Add("fallback_coverage_insufficient");
        if (evidence.ProviderTimeouts < 1) blockers.Add("timeout_coverage_missing");
        if (evidence.ContextCompactions < policy.MinimumContextCompactions) blockers.Add("context_compaction_missing");
        if (!evidence.KdpEpubCreated || !evidence.KdpInteriorPdfCreated || !evidence.KdpCoverPdfCreated)
            blockers.Add("kdp_exports_incomplete");
        if (!evidence.PublicationPackageVerified) blockers.Add("publication_package_unverified");
        if (evidence.DuplicateChapters != 0) blockers.Add("duplicate_content_detected");
        if (evidence.DegradedChapters != 0) blockers.Add("degraded_content_detected");
        if (evidence.TotalModelLatencyMilliseconds <= 0) blockers.Add("latency_evidence_missing");
        if (evidence.EstimatedCostUsd < 0) blockers.Add("cost_evidence_invalid");
        if (evidence.SecretLeakageDetected) blockers.Add("secret_leakage_detected");
        if (evidence.CredentialPersisted) blockers.Add("credential_persisted");
        if (string.IsNullOrWhiteSpace(evidence.EvidenceSha256) || evidence.EvidenceSha256.Length != 64)
            blockers.Add("evidence_hash_invalid");
        return new(blockers.Count == 0, blockers);
    }
}
