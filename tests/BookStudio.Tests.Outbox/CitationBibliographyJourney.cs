using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class CitationBibliographyJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "citation-bibliography.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 37, "Citation bibliography migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var verification = Guid.NewGuid();
        const long authorityRevision = 4;
        var authorityDigest = Hash($"{workspace}:{verification:D}:{authorityRevision}:VERIFIED");
        SeedAuthority(factory, workspace, project, verification, authorityRevision, now);

        var bibliographyId = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        Guid approvedMessage;

        await using (var store = new SqliteCitationBibliographyStore(factory))
        {
            var draft = new CitationBibliographyDraft(bibliographyId, project, workspace, verification, authorityRevision, authorityDigest, 1, "APA-7", "en-US", "research-editor", "{\"chapter\":3}", "create-citation-bibliography");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Bibliography.Status == CitationBibliographyStatus.Proposed && created.Bibliography.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<CitationBibliographyConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var citations = new[]
            {
                new CitationDraft(Guid.NewGuid(), claimId, sourceId, CitationKind.Footnote, "chapter-3/scene-2/p-4", "p. 17", "Archive Register (1847), p. 17.", true, true, true, "Locator and rendering independently checked.")
            };
            var entries = new[]
            {
                new BibliographyEntryDraft(Guid.NewGuid(), sourceId, "archive-register-1847", "Archive Register", "Regional Archive", "Regional Archive", 1847, null, null, "archive://collection/box-4/item-7", "Regional Archive. (1847). Archive Register.", true, true, "Canonical metadata and source identity verified.")
            };
            var validate = new CitationBibliographyValidateCommand(Guid.NewGuid(), workspace, bibliographyId, 1, citations, entries, "Citation coverage, metadata, links, currency and canonical bibliography were validated.", "research-editor", "validate-citations");
            var validated = await store.ValidateAsync(validate, now.AddMinutes(3));
            Require(validated.Status == CitationBibliographyStatus.Validated && validated.Revision == 2 && validated.Citations.Count == 1 && validated.Entries.Count == 1, "Validation failed.");
            Require((await store.ValidateAsync(validate, now.AddMinutes(4))).Revision == 2, "Validation replay changed state.");

            var approve = new CitationBibliographyDecisionCommand(Guid.NewGuid(), workspace, bibliographyId, 2, CitationBibliographyDecision.Approve, "All citations resolve to current, supported and canonical bibliography entries.", "research-editor", "approve-bibliography");
            var approved = await store.DecideAsync(approve, now.AddMinutes(5));
            Require(approved.Status == CitationBibliographyStatus.Approved && approved.Revision == 3, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approved event message missing.");
            Require((await store.DecideAsync(approve, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", bibliographyId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteCitationBibliographyStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, bibliographyId) ?? throw new InvalidOperationException("Citation bibliography missing after restart.");
            Require(durable.Status == CitationBibliographyStatus.Approved && durable.Revision == 3 && durable.Citations.Count == 1 && durable.Entries.Count == 1, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM citation_bibliography_history WHERE workspace_id=$w AND bibliography_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", bibliographyId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("citation-worker", 40, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "citation.bibliography.approved") == 1, "Approved event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid verification, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO claim_verifications(workspace_id,verification_id,project_id,research_plan_id,expected_research_plan_revision,expected_research_plan_digest,claim_id,claim_type,location,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,3,'research-plan-digest',$claim,'HISTORICAL','chapter-3/scene-2/p-4',1,'standard-v1','research-editor','{\"claim\":\"event occurred in 1847\"}',$r,'VERIFIED','VERIFIED','Primary evidence supports the claim.',NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace);
        cmd.Parameters.AddWithValue("$id", verification.ToString("D"));
        cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        cmd.Parameters.AddWithValue("$plan", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$claim", Guid.NewGuid().ToString("D"));
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
