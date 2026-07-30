using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class AiProvenanceJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "ai-provenance.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 39, "AI provenance migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 20, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var rightsCase = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var assetDigest = Hash("asset-v1");
        const long rightsRevision = 3;
        var rightsDigest = Hash($"{workspace}:{rightsCase:D}:{rightsRevision}:APPROVED");
        SeedRights(factory, workspace, project, rightsCase, asset, assetDigest, rightsRevision, now);

        var recordId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteAiProvenanceStore(factory))
        {
            var draft = new AiProvenanceDraft(recordId, project, workspace, rightsCase, rightsRevision, rightsDigest, asset, AssetKind.Illustration, "cover/front", assetDigest, 1, "provenance-editor", "{\"asset\":\"cover/front\"}", "create-provenance");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Record.Status == AiProvenanceStatus.Proposed && created.Record.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<AiProvenanceConflictException>(() => store.CreateAsync(draft with { Actor = "other" }, now.AddMinutes(2)).AsTask());

            var disclosures = new[]
            {
                new AiDisclosureDraft(Guid.NewGuid(), "KDP", "en-US", "metadata", "2026-01", "AI-assisted illustration; final composition and editing performed by a human.", true, "Policy matrix reviewed."),
                new AiDisclosureDraft(Guid.NewGuid(), "COLOPHON", "en-US", "text", "2026-01", "Generative AI assisted the initial image concept.", true, "Editorial disclosure approved.")
            };
            var evaluate = new AiProvenanceEvaluateCommand(Guid.NewGuid(), workspace, recordId, 1, AiProvenanceClassification.AiAssisted, "OpenAI", "image-model", "2026-07", "prompt://cover/front/v1", "Human art direction, compositing, typography and final retouching.", 35m, disclosures, "Prompt, output digest, edit log and policy review retained.", "provenance-editor", "evaluate-provenance");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(3));
            Require(evaluated.Status == AiProvenanceStatus.Evaluated && evaluated.Revision == 2 && evaluated.Disclosures.Count == 2, "Evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(4))).Revision == 2, "Evaluation replay changed state.");

            var approve = new AiProvenanceDecisionCommand(Guid.NewGuid(), workspace, recordId, 2, AiProvenanceDecision.Approve, "Classification, retained evidence and channel disclosures are complete and compliant.", "provenance-editor", "approve-provenance");
            var approved = await store.DecideAsync(approve, now.AddMinutes(5));
            Require(approved.Status == AiProvenanceStatus.Approved && approved.Revision == 3, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approved event missing.");
            Require((await store.DecideAsync(approve, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", recordId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteAiProvenanceStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, recordId) ?? throw new InvalidOperationException("Record missing after restart.");
            Require(durable.Status == AiProvenanceStatus.Approved && durable.Revision == 3 && durable.Disclosures.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM ai_provenance_history WHERE workspace_id=$w AND record_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", recordId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 3, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("provenance-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "ai.provenance.approved") == 1, "Approved event was not exactly once.");
    }

    private static void SeedRights(SqliteConnectionFactory factory, string workspace, Guid project, Guid caseId, Guid asset, string assetDigest, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO rights_license_cases(workspace_id,case_id,project_id,bibliography_id,expected_bibliography_revision,expected_bibliography_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,rights_holder,actor,snapshot_json,revision,status,scope_json,valid_from_utc,valid_until_utc,restrictions_json,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,3,'bibliography-digest',$asset,'ILLUSTRATION','cover/front',$digest,1,'Rights Holder Ltd','rights-editor','{}',$r,'APPROVED','{\"LicenseType\":\"exclusive\",\"Territories\":[\"WORLD\"],\"Languages\":[\"en\"],\"Channels\":[\"print\",\"ebook\"],\"CommercialUse\":true,\"DerivativesAllowed\":true,\"AttributionRequired\":true}',NULL,NULL,'[]','Signed agreement.','APPROVE','Approved rights.',NULL,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace);
        cmd.Parameters.AddWithValue("$id", caseId.ToString("D"));
        cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        cmd.Parameters.AddWithValue("$b", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$asset", asset.ToString("D"));
        cmd.Parameters.AddWithValue("$digest", assetDigest);
        cmd.Parameters.AddWithValue("$r", revision);
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
