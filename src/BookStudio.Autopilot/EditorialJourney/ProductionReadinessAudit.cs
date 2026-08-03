namespace BookStudio.Autopilot.EditorialJourney;

public enum ProductionReadinessDimension
{
    Security,
    Privacy,
    CopyrightAndLicensing,
    DependencyRisk,
    SecretHandling,
    Accessibility,
    UserExperience,
    Installation,
    Observability,
    Recovery,
    Documentation,
    KdpCompliance,
    ReleaseEvidence,
}

public enum AuditFindingSeverity { Informational, Low, Medium, High, Critical }

public sealed record ProductionReadinessFinding(
    ProductionReadinessDimension Dimension,
    AuditFindingSeverity Severity,
    string Code,
    string Summary,
    string EvidenceReference,
    bool Resolved);

public sealed record ProductionReadinessDimensionResult(
    ProductionReadinessDimension Dimension,
    bool Passed,
    IReadOnlyList<string> EvidenceReferences);

public sealed record ProductionReadinessAuditReport(
    string ReleaseId,
    string AuditorId,
    bool AuditorIndependent,
    IReadOnlyList<ProductionReadinessDimensionResult> Dimensions,
    IReadOnlyList<ProductionReadinessFinding> Findings,
    string ResidualRiskStatement,
    bool ResidualRiskAccepted,
    string ReleaseEvidenceSha256,
    DateTimeOffset CompletedAtUtc);

public sealed record ProductionReadinessDecision(bool Passed, IReadOnlyList<string> BlockingReasons);

public static class ProductionReadinessAuditGate
{
    public static ProductionReadinessDecision Evaluate(ProductionReadinessAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(report.ReleaseId)) blockers.Add("release_id_missing");
        if (string.IsNullOrWhiteSpace(report.AuditorId)) blockers.Add("auditor_id_missing");
        if (!report.AuditorIndependent) blockers.Add("independent_auditor_missing");

        var expected = Enum.GetValues<ProductionReadinessDimension>();
        var duplicateDimensions = report.Dimensions.GroupBy(x => x.Dimension).Where(x => x.Count() != 1).Select(x => x.Key).ToArray();
        if (duplicateDimensions.Length > 0) blockers.Add("dimension_results_duplicated");
        foreach (var dimension in expected)
        {
            var result = report.Dimensions.SingleOrDefault(x => x.Dimension == dimension);
            if (result is null) blockers.Add($"dimension_missing:{dimension}");
            else
            {
                if (!result.Passed) blockers.Add($"dimension_failed:{dimension}");
                if (result.EvidenceReferences.Count == 0 || result.EvidenceReferences.Any(string.IsNullOrWhiteSpace))
                    blockers.Add($"dimension_evidence_missing:{dimension}");
            }
        }

        foreach (var finding in report.Findings.Where(x => !x.Resolved && x.Severity >= AuditFindingSeverity.High))
            blockers.Add($"unresolved_{finding.Severity.ToString().ToLowerInvariant()}:{finding.Code}");
        if (string.IsNullOrWhiteSpace(report.ResidualRiskStatement)) blockers.Add("residual_risk_statement_missing");
        if (!report.ResidualRiskAccepted) blockers.Add("residual_risk_not_accepted");
        if (string.IsNullOrWhiteSpace(report.ReleaseEvidenceSha256) || report.ReleaseEvidenceSha256.Length != 64)
            blockers.Add("release_evidence_hash_invalid");
        return new(blockers.Count == 0, blockers);
    }
}
