using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class ClaimVerificationJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "claim-verification.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 36, "Claim verification migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var researchPlan = Guid.NewGuid();
        const long authorityRevision = 3;
        var authorityDigest = Hash($"{workspace}:{researchPlan:D}:{authorityRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, researchPlan, authorityRevision, now);

        var verificationId = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        Guid verifiedMessage;

        await using (var store = new SqliteClaimVerificationStore(factory))
        {
            var draft = new ClaimVerificationDraft(verificationId, project, workspace, researchPlan, authorityRevision, authorityDigest, claimId, ClaimType.Historical, "chapter-3/scene-2/p-4", 1, "claim-verification-v1", "research-editor", "{\"claim\":\"event occurred in 1847\"}", "create-claim-verification");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Verification.Status == ClaimVerificationStatus.Proposed && created.Verification.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<ClaimVerificationConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var openEvidence = new[]
            {
                new ClaimEvidenceDraft(Guid.NewGuid(), EvidenceDisposition.Supports, "primary-archive", "archive://collection/box-4/item-7", now, null, EvidenceQuality.Primary, EvidenceCoverage.Complete, 0.95m, "chapter-3/scene-2/p-4", "Contemporaneous register records the event and date.", "archive checksum sha256:abc123; page 17", true)
            };
            var evaluated = await store.EvaluateAsync(new ClaimVerificationEvaluateCommand(Guid.NewGuid(), workspace, verificationId, 1, openEvidence, "Primary evidence captured but review remains open.", "research-editor", "evaluate-open-evidence"), now.AddMinutes(3));
            Require(evaluated.Status == ClaimVerificationStatus.Evaluated && evaluated.Revision == 2, "Evaluation failed.");
            await Throws<ClaimVerificationTransitionException>(() => store.DecideAsync(new ClaimVerificationDecisionCommand(Guid.NewGuid(), workspace, verificationId, 2, ClaimVerificationDecision.Verified, "Attempt invalid verification.", null, "research-editor", "invalid-verification"), now.AddMinutes(4)).AsTask());

            var closedEvidence = openEvidence.Select(x => x with { IsOpen = false }).ToArray();
            var reevaluate = new ClaimVerificationEvaluateCommand(Guid.NewGuid(), workspace, verificationId, 2, closedEvidence, "Evidence independently reviewed, reproducible and closed.", "research-editor", "evaluate-closed-evidence");
            var ready = await store.EvaluateAsync(reevaluate, now.AddMinutes(5));
            Require(ready.Status == ClaimVerificationStatus.Evaluated && ready.Revision == 3 && ready.Evidence.Count == 1 && !ready.Evidence[0].IsOpen, "Closed evidence evaluation failed.");
            Require((await store.EvaluateAsync(reevaluate, now.AddMinutes(6))).Revision == 3, "Evaluation replay changed state.");

            var decide = new ClaimVerificationDecisionCommand(Guid.NewGuid(), workspace, verificationId, 3, ClaimVerificationDecision.Verified, "Primary evidence completely and reproducibly supports the claim.", null, "research-editor", "verify-claim");
            var verified = await store.DecideAsync(decide, now.AddMinutes(7));
            Require(verified.Status == ClaimVerificationStatus.Verified && verified.Revision == 4, "Verification decision failed.");
            verifiedMessage = verified.MessageId ?? throw new InvalidOperationException("Verified event message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(8))).Revision == 4, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", verificationId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteClaimVerificationStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, verificationId) ?? throw new InvalidOperationException("Claim verification missing after restart.");
            Require(durable.Status == ClaimVerificationStatus.Verified && durable.Revision == 4 && durable.Evidence.Count == 1, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM claim_verification_history WHERE workspace_id=$w AND verification_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", verificationId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 4, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("claim-worker", 40, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == verifiedMessage && x.EventType == "claim.verification.verified") == 1, "Verified event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO research_plans(workspace_id,plan_id,project_id,originality_review_id,expected_originality_revision,expected_originality_digest,version,actor,evidence,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$review,2,'originality-digest',1,'research-lead','Approved research plan evidence.',$r,'APPROVED','APPROVE','Approved.',NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace);
        cmd.Parameters.AddWithValue("$id", plan.ToString("D"));
        cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        cmd.Parameters.AddWithValue("$review", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$r", revision);
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
