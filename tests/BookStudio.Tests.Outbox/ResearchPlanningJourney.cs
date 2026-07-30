using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Research;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class ResearchPlanningJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "research-planning.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 35, "Research planning migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid(); var authority = Guid.NewGuid(); const long authorityRevision = 5;
        var authorityDigest = Hash($"{workspace}:{authority:D}:{authorityRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, authority, authorityRevision, now);
        var planId = Guid.NewGuid(); Guid approvalMessage;

        var q1 = new ResearchQuestionDraft(Guid.NewGuid(), ResearchQuestionType.HistoricalContext, ResearchPriority.High, "chapter-2/scene-3", new[] { "claim-17" }, Array.Empty<string>(), "What primary sources establish the date and local context?", "Primary archival source plus two independent scholarly sources.", "Named author, provenance, methodology and corroboration.", "Sources current to the manuscript cutoff; historical primary evidence retained.", "Date, place and causal context covered.", "Quoted source passages, bibliographic metadata and verification notes.", Array.Empty<Guid>(), "research-editor", ResearchQuestionStatus.Planned, 0);
        var q2 = new ResearchQuestionDraft(Guid.NewGuid(), ResearchQuestionType.ScientificTechnical, ResearchPriority.Critical, "chapter-4/p-8", new[] { "claim-31" }, new[] { "decision-9" }, "Is the described mechanism technically accurate under the stated conditions?", "Authoritative standard and peer-reviewed literature.", "Primary or official technical authority with reproducible reasoning.", "Latest applicable standard version.", "Mechanism, limits, exceptions and terminology covered.", "Exact standard clauses, calculations and conclusion.", new[] { q1.QuestionId }, "technical-reviewer", ResearchQuestionStatus.Blocked, 0);

        await using (var store = new SqliteResearchPlanningStore(factory))
        {
            var draft = new ResearchPlanDraft(planId, project, workspace, authority, authorityRevision, authorityDigest, 1, "research-lead", "Research scope derived from the approved originality review.", new[] { q1, q2 }, "create-research-plan");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Plan.Status == ResearchPlanStatus.Proposed && created.Plan.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<ResearchPlanningConflictException>(() => store.CreateAsync(draft with { Actor = "other-lead" }, now.AddMinutes(2)).AsTask());
            await Throws<ResearchPlanningTransitionException>(() => store.DecideAsync(new ResearchPlanDecisionCommand(Guid.NewGuid(), workspace, planId, 1, ResearchPlanDecision.Approve, "Approve incomplete plan.", "research-lead", "invalid-approval"), now.AddMinutes(3)).AsTask());

            var readyQuestions = new[]
            {
                q1 with { Status = ResearchQuestionStatus.Ready, Attempts = 1 },
                q2 with { Status = ResearchQuestionStatus.Ready, Attempts = 1 }
            };
            var update = new ResearchPlanUpdateCommand(Guid.NewGuid(), workspace, planId, 1, readyQuestions, "Source strategies and evidence requirements are complete.", "research-lead", "ready-research-plan");
            var ready = await store.UpdateAsync(update, now.AddMinutes(4));
            Require(ready.Status == ResearchPlanStatus.Ready && ready.Revision == 2 && ready.Questions.Count == 2, "Ready transition failed.");
            Require((await store.UpdateAsync(update, now.AddMinutes(5))).Revision == 2, "Update replay changed state.");

            var decide = new ResearchPlanDecisionCommand(Guid.NewGuid(), workspace, planId, 2, ResearchPlanDecision.Approve, "Research plan is complete, dependency-safe and evidence-ready.", "research-lead", "approve-research-plan");
            var approved = await store.DecideAsync(decide, now.AddMinutes(6));
            Require(approved.Status == ResearchPlanStatus.Approved && approved.Revision == 3, "Approval failed.");
            approvalMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(7))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", planId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteResearchPlanningStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, planId) ?? throw new InvalidOperationException("Research plan missing after restart.");
            Require(durable.Status == ResearchPlanStatus.Approved && durable.Revision == 3 && durable.Questions.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM research_plan_history WHERE workspace_id=$w AND plan_id=$id;";
            history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$id", planId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("research-worker", 40, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvalMessage && x.EventType == "research.plan.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO originality_read_aloud_reviews(workspace_id,review_id,project_id,editorial_plan_id,beta_reader_review_id,expected_beta_reader_revision,expected_beta_reader_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,4,'beta-digest',1,'originality-read-aloud-v1','quality-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", review.ToString("D")); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$plan", Guid.NewGuid().ToString("D")); cmd.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("D")); cmd.Parameters.AddWithValue("$r", revision); cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
