using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class RightsLicenseJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "rights-license.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 38, "Rights license migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 18, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var bibliography = Guid.NewGuid();
        const long authorityRevision = 3;
        var authorityDigest = Hash($"{workspace}:{bibliography:D}:{authorityRevision}:APPROVED");
        SeedBibliography(factory, workspace, project, bibliography, authorityRevision, now);

        var caseId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteRightsLicenseStore(factory))
        {
            var draft = new RightsLicenseDraft(caseId, project, workspace, bibliography, authorityRevision, authorityDigest, assetId, AssetKind.Illustration, "cover/front", Hash("asset-v1"), 1, "Rights Holder Ltd", "rights-editor", "{\"asset\":\"cover/front\"}", "create-rights-case");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.LicenseCase.Status == RightsLicenseStatus.Proposed && created.LicenseCase.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<RightsLicenseConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var scope = new LicenseScope("exclusive-publishing", ["WORLD"], ["en", "es"], ["print", "ebook", "marketing"], true, true, true);
            var validate = new RightsLicenseValidateCommand(Guid.NewGuid(), workspace, caseId, 1, scope, now, now.AddYears(5), ["Attribution in colophon", "No sublicensing"], "Signed license agreement LIC-2026-0042 verified.", "rights-editor", "validate-rights");
            var validated = await store.ValidateAsync(validate, now.AddMinutes(3));
            Require(validated.Status == RightsLicenseStatus.Validated && validated.Revision == 2 && validated.Scope is not null && validated.Restrictions.Count == 2, "Validation failed.");
            Require((await store.ValidateAsync(validate, now.AddMinutes(4))).Revision == 2, "Validation replay changed state.");

            var approve = new RightsLicenseDecisionCommand(Guid.NewGuid(), workspace, caseId, 2, RightsLicenseDecision.Approve, "Scope, holder, asset digest and evidence are complete and current.", "rights-editor", "approve-rights");
            var approved = await store.DecideAsync(approve, now.AddMinutes(5));
            Require(approved.Status == RightsLicenseStatus.Approved && approved.Revision == 3, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approved event message missing.");
            Require((await store.DecideAsync(approve, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", caseId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteRightsLicenseStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, caseId) ?? throw new InvalidOperationException("Rights case missing after restart.");
            Require(durable.Status == RightsLicenseStatus.Approved && durable.Revision == 3 && durable.Scope?.Territories.Contains("WORLD") == true, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM rights_license_history WHERE workspace_id=$w AND case_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", caseId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("rights-worker", 80, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "rights.license.approved") == 1, "Approved event was not exactly once.");
    }

    private static void SeedBibliography(SqliteConnectionFactory factory, string workspace, Guid project, Guid bibliography, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO citation_bibliographies(workspace_id,bibliography_id,project_id,claim_verification_id,expected_claim_verification_revision,expected_claim_verification_digest,version,citation_style,locale,actor,snapshot_json,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$verification,4,'claim-digest',1,'APA-7','en-US','research-editor','{}',$r,'APPROVED','APPROVE','Verified bibliography.',NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace);
        cmd.Parameters.AddWithValue("$id", bibliography.ToString("D"));
        cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        cmd.Parameters.AddWithValue("$verification", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$r", revision);
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
