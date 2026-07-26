namespace BookStudio.Application.Quality;

/// <summary>Provider-neutral deterministic audit and gate evaluation for immutable draft artifacts.</summary>
public interface IQualityAssessmentService
{
    ValueTask<QualityAuditResult> RunAuditAsync(
        QualityAuditQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<QualityGateResult> EvaluateGateAsync(
        QualityGateQuery query,
        CancellationToken cancellationToken = default);
}
