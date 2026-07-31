using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteAssetRegistryStore : IAssetRegistryStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAssetRegistryStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<AssetRegistrationResult> RegisterAsync(AssetRegistrationDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Hash(JsonSerializer.Serialize(draft));
            var existing = Load(draft.WorkspaceId, draft.AssetId);
            if (existing is not null)
            {
                RequireReceipt(draft.WorkspaceId, draft.AssetId, draft.AssetId, draft.RequestFingerprint, payload);
                return new(existing, true);
            }

            RequireBriefAuthority(draft.WorkspaceId, draft.ProjectId, draft.VisualBriefId,
                draft.ExpectedVisualBriefRevision, draft.ExpectedVisualBriefDigest);
            var canonical = CanonicalStorageIdentity(draft.StorageRoot, draft.RelativePath);
            var item = new VisualAsset(
                draft.AssetId, draft.ProjectId, draft.WorkspaceId, draft.VisualBriefId,
                draft.ExpectedVisualBriefRevision, draft.ExpectedVisualBriefDigest, draft.AssetType,
                draft.SourceAdapter, draft.StorageRoot, draft.RelativePath, draft.MediaFormat,
                draft.Width, draft.Height, draft.ColorProfile, draft.ContentDigest,
                draft.CausalSnapshotJson, draft.GenerationParametersJson, draft.Provenance,
                draft.Rights, draft.Accessibility, [],
                draft.Relationships.Select(x => new AssetRelationship(x.RelationshipId, x.Kind, x.RelatedAssetId, x.Evidence)).ToArray(),
                null, 1, VisualAssetStatus.Registered, null, MessageId(draft.AssetId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, "INSERT INTO visual_assets(workspace_id,asset_id,project_id,visual_brief_id,expected_visual_brief_revision,expected_visual_brief_digest,asset_type,source_adapter,storage_root,relative_path,canonical_storage_identity,media_format,width,height,color_profile,content_digest,causal_snapshot_json,generation_parameters_json,status,decision_reason,superseded_by_asset_id,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,$br,$bd,$t,$sa,$sr,$rp,$cs,$mf,$wi,$he,$cp,$cd,$cj,$gj,'REGISTERED',NULL,NULL,1,$m,$at,$at)",
                ("$w",draft.WorkspaceId),("$id",draft.AssetId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$b",draft.VisualBriefId.ToString("D")),("$br",draft.ExpectedVisualBriefRevision),("$bd",draft.ExpectedVisualBriefDigest),("$t",EnumText(draft.AssetType)),("$sa",draft.SourceAdapter),("$sr",draft.StorageRoot),("$rp",draft.RelativePath),("$cs",canonical),("$mf",draft.MediaFormat),("$wi",draft.Width),("$he",draft.Height),("$cp",draft.ColorProfile),("$cd",draft.ContentDigest),("$cj",draft.CausalSnapshotJson),("$gj",draft.GenerationParametersJson),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")));
            PersistEvidence(connection, tx, item, at);
            History(connection, tx, item, "REGISTER", draft.Actor, "asset registered", at);
            Outbox(connection, tx, item, "visual.asset.registered", at);
            Receipt(connection, tx, draft.WorkspaceId, draft.AssetId, draft.AssetId, draft.RequestFingerprint, payload, item, at);
            tx.Commit();
            return new(item, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<VisualAsset> ValidateAsync(AssetValidationCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "VALIDATE", command.Actor, "technical validation", at, item =>
            {
                RequireText(command.ValidatorIdentity, command.PolicyVersion, command.ArtifactDigest, command.Actor, command.RequestFingerprint);
                if (!StringComparer.OrdinalIgnoreCase.Equals(command.ArtifactDigest, item.ContentDigest))
                    throw new AssetRegistryValidationException("Artifact digest does not match the immutable registered digest.");
                if (command.Validations.Count == 0 || command.Validations.Any(v => v.ValidationId == Guid.Empty || string.IsNullOrWhiteSpace(v.PolicyVersion) || string.IsNullOrWhiteSpace(v.Evidence) || string.IsNullOrWhiteSpace(v.EvidenceDigest)))
                    throw new AssetRegistryValidationException("Complete technical validation evidence is required.");
                var pass = command.Validations.All(v => v.Outcome == AssetValidationOutcome.Pass);
                return item with { Validations = command.Validations.ToArray(), Revision = item.Revision + 1,
                    Status = pass ? VisualAssetStatus.Validated : VisualAssetStatus.RepairRequired,
                    DecisionReason = pass ? null : "Technical validation failed.", MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset> DecideAsync(AssetDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "DECIDE", command.Actor, command.Reason, at, item =>
            {
                RequireText(command.Reason, command.Actor, command.RequestFingerprint);
                var current = AuthorityMatches(item);
                var next = command.Decision switch
                {
                    AssetDecision.Approve when item.Status == VisualAssetStatus.Validated && current && CanApprove(item, at) => VisualAssetStatus.Approved,
                    AssetDecision.ReturnToRepair when item.Status is VisualAssetStatus.Validated or VisualAssetStatus.Approved => VisualAssetStatus.RepairRequired,
                    AssetDecision.Reopen when item.Status is VisualAssetStatus.RepairRequired or VisualAssetStatus.Quarantined or VisualAssetStatus.Revoked or VisualAssetStatus.Stale => VisualAssetStatus.Registered,
                    AssetDecision.Revoke when item.Status == VisualAssetStatus.Approved => VisualAssetStatus.Revoked,
                    AssetDecision.Approve when !current => VisualAssetStatus.Stale,
                    _ => throw new AssetRegistryTransitionException("Decision is not valid for the current asset state.")
                };
                return item with { Revision = item.Revision + 1, Status = next,
                    DecisionReason = current ? command.Reason : "Visual brief authority drift.",
                    MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset> QuarantineAsync(AssetQuarantineCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "QUARANTINE", command.Actor, command.Reason, at, item =>
            {
                RequireText(command.Reason, command.Evidence, command.Actor, command.RequestFingerprint);
                if (item.Status is VisualAssetStatus.Superseded or VisualAssetStatus.Revoked)
                    throw new AssetRegistryTransitionException("Terminal assets cannot be quarantined.");
                return item with { Revision = item.Revision + 1, Status = VisualAssetStatus.Quarantined,
                    DecisionReason = command.Reason, MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset> RepairAsync(AssetRepairCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "REPAIR", command.Actor, command.Reason, at, item =>
            {
                if (item.Status is not (VisualAssetStatus.RepairRequired or VisualAssetStatus.Quarantined))
                    throw new AssetRegistryTransitionException("Only repair-required or quarantined assets can be repaired.");
                RequireText(command.StorageRoot, command.RelativePath, command.MediaFormat, command.ColorProfile, command.ContentDigest, command.Reason, command.Actor, command.RequestFingerprint);
                ValidatePath(command.StorageRoot, command.RelativePath);
                if (command.Width < 1 || command.Height < 1 || command.Validations.Count == 0)
                    throw new AssetRegistryValidationException("Complete repaired artifact metadata and validations are required.");
                return item with { StorageRoot = command.StorageRoot, RelativePath = command.RelativePath,
                    MediaFormat = command.MediaFormat, Width = command.Width, Height = command.Height,
                    ColorProfile = command.ColorProfile, ContentDigest = command.ContentDigest,
                    Provenance = command.Provenance, Rights = command.Rights, Accessibility = command.Accessibility,
                    Validations = command.Validations.ToArray(), Revision = item.Revision + 1,
                    Status = VisualAssetStatus.Validated, DecisionReason = command.Reason,
                    MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset> SupersedeAsync(AssetSupersedeCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "SUPERSEDE", command.Actor, command.Reason, at, item =>
            {
                RequireText(command.Reason, command.Actor, command.RequestFingerprint);
                var successor = Require(command.WorkspaceId, command.SuccessorAssetId);
                if (successor.ProjectId != item.ProjectId || successor.VisualBriefId != item.VisualBriefId || successor.Status != VisualAssetStatus.Approved)
                    throw new AssetRegistryValidationException("Successor must be an approved asset under the same project and visual brief.");
                return item with { SupersededByAssetId = successor.AssetId, Revision = item.Revision + 1,
                    Status = VisualAssetStatus.Superseded, DecisionReason = command.Reason,
                    MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset> MarkStaleAsync(AssetStaleCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.AssetId, command.ExpectedRevision, command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), "STALE", command.Actor, command.Reason, at, item =>
            {
                RequireText(command.Reason, command.Actor, command.RequestFingerprint);
                return item with { Revision = item.Revision + 1, Status = VisualAssetStatus.Stale,
                    DecisionReason = $"{command.DriftKind}: {command.Reason}", MessageId = MessageId(command.RequestId, item.Revision + 1), UpdatedAtUtc = at };
            }, ct);

    public ValueTask<VisualAsset?> GetAsync(string workspaceId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, assetId));
    }

    private async ValueTask<VisualAsset> Mutate(Guid requestId, string workspaceId, Guid assetId, long expectedRevision,
        string fingerprint, string payload, string operation, string actor, string reason, DateTimeOffset at,
        Func<VisualAsset, VisualAsset> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var receipt = LoadReceipt(workspaceId, requestId);
            if (receipt is not null)
            {
                var expected = $"{assetId:D}|{fingerprint}|{payload}";
                if (!StringComparer.Ordinal.Equals(receipt, expected))
                    throw new AssetRegistryConflictException("Request reused with a different payload.");
                return Require(workspaceId, assetId);
            }
            var item = Require(workspaceId, assetId);
            if (item.Revision != expectedRevision) throw new AssetRegistryConflictException("Stale revision.");
            var next = mutation(item);
            PersistTransition(next, operation, actor, reason, requestId, fingerprint, payload, at);
            return next;
        }
        finally { _gate.Release(); }
    }

    private void PersistTransition(VisualAsset item, string operation, string actor, string reason, Guid requestId,
        string fingerprint, string payload, DateTimeOffset at)
    {
        using var connection = _factory.OpenConnection();
        using var tx = connection.BeginTransaction();
        var canonical = CanonicalStorageIdentity(item.StorageRoot, item.RelativePath);
        var affected = Exec(connection, tx, "UPDATE visual_assets SET storage_root=$sr,relative_path=$rp,canonical_storage_identity=$cs,media_format=$mf,width=$wi,height=$he,color_profile=$cp,content_digest=$cd,status=$s,decision_reason=$dr,superseded_by_asset_id=$sb,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND asset_id=$id AND revision=$expected",
            ("$sr",item.StorageRoot),("$rp",item.RelativePath),("$cs",canonical),("$mf",item.MediaFormat),("$wi",item.Width),("$he",item.Height),("$cp",item.ColorProfile),("$cd",item.ContentDigest),("$s",EnumText(item.Status)),("$dr",Db(item.DecisionReason)),("$sb",item.SupersededByAssetId is null ? DBNull.Value : item.SupersededByAssetId.Value.ToString("D")),("$r",item.Revision),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")),("$w",item.WorkspaceId),("$id",item.AssetId.ToString("D")),("$expected",item.Revision - 1));
        if (affected != 1) throw new AssetRegistryConflictException("Stale revision.");
        DeleteEvidence(connection, tx, item.WorkspaceId, item.AssetId);
        PersistEvidence(connection, tx, item, at);
        History(connection, tx, item, operation, actor, reason, at);
        Outbox(connection, tx, item, $"visual.asset.{item.Status.ToString().ToLowerInvariant()}", at);
        Receipt(connection, tx, item.WorkspaceId, requestId, item.AssetId, fingerprint, payload, item, at);
        tx.Commit();
    }

    private VisualAsset? Load(string workspaceId, Guid assetId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id,visual_brief_id,expected_visual_brief_revision,expected_visual_brief_digest,asset_type,source_adapter,storage_root,relative_path,media_format,width,height,color_profile,content_digest,causal_snapshot_json,generation_parameters_json,status,decision_reason,superseded_by_asset_id,revision,message_id,created_at_utc,updated_at_utc FROM visual_assets WHERE workspace_id=$w AND asset_id=$id";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", assetId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var projectId=Guid.Parse(reader.GetString(0)); var briefId=Guid.Parse(reader.GetString(1));
        var briefRevision=reader.GetInt64(2); var briefDigest=reader.GetString(3);
        var type=Enum.Parse<VisualAssetType>(reader.GetString(4), true); var source=reader.GetString(5);
        var root=reader.GetString(6); var path=reader.GetString(7); var format=reader.GetString(8);
        var width=reader.GetInt32(9); var height=reader.GetInt32(10); var color=reader.GetString(11);
        var digest=reader.GetString(12); var causal=reader.GetString(13); var generation=reader.GetString(14);
        var status=Enum.Parse<VisualAssetStatus>(reader.GetString(15), true);
        var decision=reader.IsDBNull(16)?null:reader.GetString(16); var superseded=reader.IsDBNull(17)?(Guid?)null:Guid.Parse(reader.GetString(17));
        var revision=reader.GetInt64(18); var message=reader.IsDBNull(19)?(Guid?)null:Guid.Parse(reader.GetString(19));
        var created=DateTimeOffset.Parse(reader.GetString(20)); var updated=DateTimeOffset.Parse(reader.GetString(21));
        reader.Close();
        return new VisualAsset(assetId,projectId,workspaceId,briefId,briefRevision,briefDigest,type,source,root,path,format,width,height,color,digest,causal,generation,
            LoadProvenance(connection,workspaceId,assetId),LoadRights(connection,workspaceId,assetId),LoadAccessibility(connection,workspaceId,assetId),
            LoadValidations(connection,workspaceId,assetId),LoadRelationships(connection,workspaceId,assetId),superseded,revision,status,decision,message,created,updated);
    }

    private AssetProvenanceEvidence LoadProvenance(SqliteConnection c,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT provider,model,source_uri,prompt_digest,input_lineage_json,evidence_digest,captured_at_utc FROM asset_provenance_evidence WHERE workspace_id=$w AND asset_id=$id"; cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read()) throw new AssetRegistryValidationException("Provenance evidence missing."); return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),DateTimeOffset.Parse(r.GetString(6)));
    }
    private AssetRightsEvidence LoadRights(SqliteConnection c,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT license_kind,license_reference,rights_holder,territory,valid_from_utc,valid_until_utc,evidence_digest FROM asset_rights_evidence WHERE workspace_id=$w AND asset_id=$id"; cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read()) throw new AssetRegistryValidationException("Rights evidence missing."); return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:DateTimeOffset.Parse(r.GetString(4)),r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5)),r.GetString(6));
    }
    private AssetAccessibilityEvidence LoadAccessibility(SqliteConnection c,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT alt_text,long_description,language,evidence_digest FROM asset_accessibility_evidence WHERE workspace_id=$w AND asset_id=$id"; cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read()) throw new AssetRegistryValidationException("Accessibility evidence missing."); return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3));
    }
    private IReadOnlyList<AssetTechnicalValidation> LoadValidations(SqliteConnection c,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT validation_id,validation_kind,outcome,policy_version,evidence,evidence_digest FROM asset_technical_validations WHERE workspace_id=$w AND asset_id=$id ORDER BY created_at_utc,validation_id";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();var list=new List<AssetTechnicalValidation>();while(r.Read())list.Add(new(Guid.Parse(r.GetString(0)),Enum.Parse<AssetValidationKind>(r.GetString(1),true),Enum.Parse<AssetValidationOutcome>(r.GetString(2),true),r.GetString(3),r.GetString(4),r.GetString(5)));return list;
    }
    private IReadOnlyList<AssetRelationship> LoadRelationships(SqliteConnection c,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.CommandText="SELECT relationship_id,relationship_kind,related_asset_id,evidence FROM asset_relationships WHERE workspace_id=$w AND asset_id=$id ORDER BY relationship_id";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();var list=new List<AssetRelationship>();while(r.Read())list.Add(new(Guid.Parse(r.GetString(0)),Enum.Parse<AssetRelationshipKind>(r.GetString(1),true),Guid.Parse(r.GetString(2)),r.GetString(3)));return list;
    }

    private void RequireBriefAuthority(string workspaceId, Guid projectId, Guid briefId, long revision, string digest)
    {
        using var connection=_factory.OpenConnection();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT project_id,revision,status FROM visual_briefs WHERE workspace_id=$w AND brief_id=$id";cmd.Parameters.AddWithValue("$w",workspaceId);cmd.Parameters.AddWithValue("$id",briefId.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())throw new AssetRegistryValidationException("Approved visual brief authority not found.");var status=r.GetString(2);var actual=Hash($"{workspaceId}:{briefId:D}:{r.GetInt64(1)}:{status}");if(Guid.Parse(r.GetString(0))!=projectId||r.GetInt64(1)!=revision||status!="APPROVED"||!StringComparer.Ordinal.Equals(actual,digest))throw new AssetRegistryValidationException("Visual brief authority is not exact, current and approved.");
    }
    private bool AuthorityMatches(VisualAsset item){try{RequireBriefAuthority(item.WorkspaceId,item.ProjectId,item.VisualBriefId,item.ExpectedVisualBriefRevision,item.ExpectedVisualBriefDigest);return true;}catch(AssetRegistryValidationException){return false;}}
    private static bool CanApprove(VisualAsset i,DateTimeOffset at)=>i.Validations.Count>0&&i.Validations.All(v=>v.Outcome==AssetValidationOutcome.Pass)&&!string.IsNullOrWhiteSpace(i.Provenance.EvidenceDigest)&&!string.IsNullOrWhiteSpace(i.Rights.EvidenceDigest)&&!string.IsNullOrWhiteSpace(i.Accessibility.AltText)&&!string.IsNullOrWhiteSpace(i.Accessibility.EvidenceDigest)&&(i.Rights.ValidFromUtc is null||i.Rights.ValidFromUtc<=at)&&(i.Rights.ValidUntilUtc is null||i.Rights.ValidUntilUtc>=at);

    private static void ValidateDraft(AssetRegistrationDraft d){if(d.AssetId==Guid.Empty||d.ProjectId==Guid.Empty||d.VisualBriefId==Guid.Empty||d.ExpectedVisualBriefRevision<1||d.Width<1||d.Height<1)throw new AssetRegistryValidationException("Complete asset identity and dimensions are required.");RequireText(d.WorkspaceId,d.ExpectedVisualBriefDigest,d.SourceAdapter,d.StorageRoot,d.RelativePath,d.MediaFormat,d.ColorProfile,d.ContentDigest,d.CausalSnapshotJson,d.GenerationParametersJson,d.Actor,d.RequestFingerprint,d.Provenance.Provider,d.Provenance.Model,d.Provenance.SourceUri,d.Provenance.PromptDigest,d.Provenance.InputLineageJson,d.Provenance.EvidenceDigest,d.Rights.LicenseKind,d.Rights.LicenseReference,d.Rights.RightsHolder,d.Rights.Territory,d.Rights.EvidenceDigest,d.Accessibility.AltText,d.Accessibility.Language,d.Accessibility.EvidenceDigest);ValidatePath(d.StorageRoot,d.RelativePath);}
    private static void ValidatePath(string root,string relative){if(Path.IsPathRooted(relative)||relative.Split('/', '\\').Any(x=>x=="..")||string.IsNullOrWhiteSpace(root))throw new AssetRegistryValidationException("Unsafe asset storage path.");}
    private static string CanonicalStorageIdentity(string root,string relative){ValidatePath(root,relative);return Path.GetFullPath(Path.Combine(root,relative)).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();}
    private VisualAsset Require(string w,Guid id)=>Load(w,id)??throw new AssetRegistryValidationException("Asset not found.");
    private string? LoadReceipt(string w,Guid requestId){using var c=_factory.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT asset_id,request_fingerprint,response_json FROM asset_registry_receipts WHERE workspace_id=$w AND request_id=$r";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$r",requestId.ToString("D"));using var reader=cmd.ExecuteReader();if(!reader.Read())return null;var payload=JsonSerializer.Deserialize<ReceiptPayload>(reader.GetString(2))??throw new AssetRegistryConflictException("Invalid receipt payload.");return $"{reader.GetString(0)}|{reader.GetString(1)}|{payload.PayloadHash}";}
    private void RequireReceipt(string w,Guid requestId,Guid assetId,string fingerprint,string payload){var actual=LoadReceipt(w,requestId);if(!StringComparer.Ordinal.Equals(actual,$"{assetId:D}|{fingerprint}|{payload}"))throw new AssetRegistryConflictException("Request reused with a different payload.");}

    private static void PersistEvidence(SqliteConnection c,SqliteTransaction tx,VisualAsset i,DateTimeOffset at){var p=i.Provenance;Exec(c,tx,"INSERT INTO asset_provenance_evidence VALUES($w,$id,$p,$m,$u,$pd,$l,$e,$at)",("$w",i.WorkspaceId),("$id",i.AssetId.ToString("D")),("$p",p.Provider),("$m",p.Model),("$u",p.SourceUri),("$pd",p.PromptDigest),("$l",p.InputLineageJson),("$e",p.EvidenceDigest),("$at",p.CapturedAtUtc.ToString("O")));var r=i.Rights;Exec(c,tx,"INSERT INTO asset_rights_evidence VALUES($w,$id,$k,$lr,$h,$t,$vf,$vu,$e)",("$w",i.WorkspaceId),("$id",i.AssetId.ToString("D")),("$k",r.LicenseKind),("$lr",r.LicenseReference),("$h",r.RightsHolder),("$t",r.Territory),("$vf",r.ValidFromUtc is null?DBNull.Value:r.ValidFromUtc.Value.ToString("O")),("$vu",r.ValidUntilUtc is null?DBNull.Value:r.ValidUntilUtc.Value.ToString("O")),("$e",r.EvidenceDigest));var a=i.Accessibility;Exec(c,tx,"INSERT INTO asset_accessibility_evidence VALUES($w,$id,$a,$l,$g,$e)",("$w",i.WorkspaceId),("$id",i.AssetId.ToString("D")),("$a",a.AltText),("$l",a.LongDescription),("$g",a.Language),("$e",a.EvidenceDigest));foreach(var v in i.Validations)Exec(c,tx,"INSERT INTO asset_technical_validations VALUES($w,$id,$v,$k,$o,$p,$e,$d,$at)",("$w",i.WorkspaceId),("$id",i.AssetId.ToString("D")),("$v",v.ValidationId.ToString("D")),("$k",EnumText(v.Kind)),("$o",EnumText(v.Outcome)),("$p",v.PolicyVersion),("$e",v.Evidence),("$d",v.EvidenceDigest),("$at",at.ToString("O")));foreach(var rel in i.Relationships)Exec(c,tx,"INSERT INTO asset_relationships VALUES($w,$id,$r,$k,$x,$e)",("$w",i.WorkspaceId),("$id",i.AssetId.ToString("D")),("$r",rel.RelationshipId.ToString("D")),("$k",EnumText(rel.Kind)),("$x",rel.RelatedAssetId.ToString("D")),("$e",rel.Evidence));}
    private static void DeleteEvidence(SqliteConnection c,SqliteTransaction tx,string w,Guid id){foreach(var table in new[]{"asset_provenance_evidence","asset_rights_evidence","asset_accessibility_evidence","asset_technical_validations","asset_relationships"})Exec(c,tx,$"DELETE FROM {table} WHERE workspace_id=$w AND asset_id=$id",("$w",w),("$id",id.ToString("D")));}
    private static void History(SqliteConnection c,SqliteTransaction tx,VisualAsset i,string op,string actor,string reason,DateTimeOffset at)=>Exec(c,tx,"INSERT INTO asset_registry_history(workspace_id,history_id,asset_id,revision,event_type,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$h,$id,$r,$e,$a,$reason,$s,$at)",("$w",i.WorkspaceId),("$h",Guid.NewGuid().ToString("D")),("$id",i.AssetId.ToString("D")),("$r",i.Revision),("$e",op),("$a",actor),("$reason",reason),("$s",JsonSerializer.Serialize(i)),("$at",at.ToString("O")));
    private static void Outbox(SqliteConnection c,SqliteTransaction tx,VisualAsset i,string type,DateTimeOffset at)=>Exec(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",("$id",i.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new{i.WorkspaceId,i.AssetId,i.ProjectId,i.VisualBriefId,i.Revision,i.Status})),("$at",at.ToString("O")));
    private static void Receipt(SqliteConnection c,SqliteTransaction tx,string w,Guid requestId,Guid assetId,string fingerprint,string payload,VisualAsset i,DateTimeOffset at)=>Exec(c,tx,"INSERT INTO asset_registry_receipts(workspace_id,request_id,request_fingerprint,asset_id,revision,response_json,created_at_utc) VALUES($w,$r,$f,$id,$v,$j,$at)",("$w",w),("$r",requestId.ToString("D")),("$f",fingerprint),("$id",assetId.ToString("D")),("$v",i.Revision),("$j",JsonSerializer.Serialize(new ReceiptPayload(payload,i.Revision))), ("$at",at.ToString("O")));
    private static int Exec(SqliteConnection c,SqliteTransaction tx,string sql,params (string,object)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var value in values)cmd.Parameters.AddWithValue(value.Item1,value.Item2);return cmd.ExecuteNonQuery();}
    private static object Db(string? value)=>value is null?DBNull.Value:value;
    private static void RequireText(params string[] values){if(values.Any(string.IsNullOrWhiteSpace))throw new AssetRegistryValidationException("Required asset evidence is missing.");}
    private static string EnumText<T>(T value) where T:struct,Enum=>value.ToString().ToUpperInvariant();
    private static Guid MessageId(Guid id,long revision){var bytes=SHA256.HashData(Encoding.UTF8.GetBytes($"visual-asset:{id:D}:{revision}"));return new Guid(bytes[..16]);}
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record ReceiptPayload(string PayloadHash,long Revision);
    public ValueTask DisposeAsync(){_gate.Dispose();return ValueTask.CompletedTask;}
}
