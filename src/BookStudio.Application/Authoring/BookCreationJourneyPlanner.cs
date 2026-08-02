using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public static class BookCreationJourneyPlanner
{
    private static readonly JourneyPhase[] OrderedPhases =
    [
        JourneyPhase.Intake,
        JourneyPhase.EditorialProposal,
        JourneyPhase.BookPlan,
        JourneyPhase.Authoring,
        JourneyPhase.EditorialQuality,
        JourneyPhase.ReaderRetention,
        JourneyPhase.Visuals,
        JourneyPhase.ProductionPackage,
        JourneyPhase.Proof,
        JourneyPhase.ReleaseReady
    ];

    public static JourneyNextAction Plan(BookCreationJourney journey)
    {
        Validate(journey);

        if (journey.Status is JourneyStatus.Cancelled or JourneyStatus.Completed or JourneyStatus.Failed)
            return Action(JourneyActionKind.None, journey.CurrentPhase, "Journey is terminal.", false);

        if (journey.Status == JourneyStatus.Paused)
            return Action(JourneyActionKind.Pause, journey.CurrentPhase, "Journey is paused by policy or user request.", false);

        var openDecision = journey.Decisions.FirstOrDefault(x => x.Status == JourneyDecisionStatus.Open);
        if (openDecision is not null)
            return new JourneyNextAction(JourneyActionKind.RequestDecision, openDecision.Phase,
                openDecision.Explanation, false, Digest($"decision|{openDecision.DecisionId}|{openDecision.EvidenceDigest}"), openDecision.DecisionId);

        var activeRepair = journey.Repairs.LastOrDefault(x => x.Status is JourneyRepairStatus.Planned or JourneyRepairStatus.Running);
        if (activeRepair is not null)
        {
            if (activeRepair.Attempt > activeRepair.MaximumAttempts)
                throw new BookCreationJourneyValidationException("Repair attempt exceeds policy maximum.");

            return Action(JourneyActionKind.Repair, activeRepair.Phase,
                $"Repair the smallest safe scope '{activeRepair.Scope}' (attempt {activeRepair.Attempt}/{activeRepair.MaximumAttempts}).", true);
        }

        var exhausted = journey.Repairs.LastOrDefault(x => x.Status == JourneyRepairStatus.Exhausted);
        if (exhausted is not null && !journey.Decisions.Any(x => x.Kind == JourneyDecisionKind.RetryExhausted && x.Phase == exhausted.Phase))
            throw new BookCreationJourneyValidationException("Exhausted repair requires an escalation decision.");

        foreach (var phase in OrderedPhases)
        {
            var progress = journey.Progress.SingleOrDefault(x => x.Phase == phase)
                ?? throw new BookCreationJourneyValidationException($"Missing progress for phase {phase}.");

            if (progress.Status is JourneyPhaseStatus.Approved or JourneyPhaseStatus.Skipped)
                continue;

            if (!DependenciesApproved(journey, phase))
                return Action(JourneyActionKind.None, phase, "Waiting for exact approved upstream authority.", false);

            if (RequiresDecision(journey, phase))
                throw new BookCreationJourneyValidationException($"Phase {phase} requires an open user decision before continuation.");

            var kind = progress.Status == JourneyPhaseStatus.Running
                ? JourneyActionKind.ContinuePhase
                : JourneyActionKind.StartPhase;

            return Action(kind, phase, $"Automatically {kind.ToString().ToLowerInvariant()} {phase}.", true);
        }

        return Action(JourneyActionKind.Complete, JourneyPhase.ReleaseReady,
            "All required phases have exact approved and current authority.", true);
    }

    public static bool RequiresDecision(BookCreationJourney journey, JourneyPhase phase)
    {
        var policy = journey.Autonomy;
        return phase switch
        {
            JourneyPhase.BookPlan => policy.RequirePlanApproval && !HasResolved(journey, JourneyDecisionKind.PlanApproval),
            JourneyPhase.Visuals => journey.Brief.ImagesRequired && policy.RequireCoverApproval && !HasResolved(journey, JourneyDecisionKind.CoverApproval),
            JourneyPhase.ProductionPackage => policy.RequireManuscriptApproval && !HasResolved(journey, JourneyDecisionKind.ManuscriptApproval),
            JourneyPhase.Proof => policy.RequirePhysicalProof && !HasResolved(journey, JourneyDecisionKind.PhysicalProof),
            _ => false
        };
    }

    public static JourneyDecision CreateDecision(Guid id, JourneyDecisionKind kind, JourneyPhase phase,
        string title, string explanation, IReadOnlyList<JourneyDecisionOption> options, DateTimeOffset at)
    {
        if (options.Count == 0)
            throw new BookCreationJourneyValidationException("A user decision requires at least one option.");

        var recommended = options.Where(x => x.Recommended).Select(x => x.OptionId).ToArray();
        if (recommended.Length > 1)
            throw new BookCreationJourneyValidationException("A decision may have at most one recommended option.");

        var optionEvidence = string.Join(';', options.Select(x => $"{x.OptionId}:{x.Label}:{x.Consequence}:{x.Recommended}"));
        var evidence = Digest($"{id}|{kind}|{phase}|{title}|{explanation}|{optionEvidence}");

        return new JourneyDecision(id, kind, phase, title, explanation, options,
            recommended.SingleOrDefault(), JourneyDecisionStatus.Open, null, evidence, at, null);
    }

    private static bool DependenciesApproved(BookCreationJourney journey, JourneyPhase phase)
    {
        var index = Array.IndexOf(OrderedPhases, phase);
        if (index <= 0) return true;

        var previous = journey.Progress.Single(x => x.Phase == OrderedPhases[index - 1]);
        if (previous.Status is not (JourneyPhaseStatus.Approved or JourneyPhaseStatus.Skipped)) return false;
        if (previous.Status == JourneyPhaseStatus.Skipped) return true;

        return previous.Authority is { Approved: true, Current: true } authority
            && !string.IsNullOrWhiteSpace(authority.AuthorityDigest);
    }

    private static bool HasResolved(BookCreationJourney journey, JourneyDecisionKind kind) =>
        journey.Decisions.Any(x => x.Kind == kind && x.Status == JourneyDecisionStatus.Resolved && !string.IsNullOrWhiteSpace(x.SelectedOptionId));

    private static void Validate(BookCreationJourney journey)
    {
        if (string.IsNullOrWhiteSpace(journey.WorkspaceId) || string.IsNullOrWhiteSpace(journey.Brief.Idea))
            throw new BookCreationJourneyValidationException("Workspace and natural-language idea are required.");
        if (journey.Brief.TargetWordCount <= 0)
            throw new BookCreationJourneyValidationException("Target word count must be positive.");
        if (journey.Autonomy.MaximumAutomaticRepairAttempts < 0)
            throw new BookCreationJourneyValidationException("Repair budget cannot be negative.");
        if (journey.Progress.Select(x => x.Phase).Distinct().Count() != OrderedPhases.Length)
            throw new BookCreationJourneyValidationException("Progress must contain each canonical phase exactly once.");
        if (journey.Decisions.Count(x => x.Status == JourneyDecisionStatus.Open) > 1)
            throw new BookCreationJourneyValidationException("Only one blocking user decision may be active at a time.");
    }

    private static JourneyNextAction Action(JourneyActionKind kind, JourneyPhase phase, string reason, bool automatic) =>
        new(kind, phase, reason, automatic, Digest($"{kind}|{phase}|{reason}|{automatic}"));

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}