using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteCoverWorkflowStore : ICoverWorkflowStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteCoverWorkflowStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<CoverSubmissionResult> SubmitAsync(CoverProjectDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Hash(JsonSerializer.Serialize(draft));
            var existing = Load(draft.WorkspaceId, draft.CoverProjectId);
            if (existing is not null)
            {
                RequireReceipt(draft.WorkspaceId, draft.RequestId, draft.CoverProjectId, draft.RequestFingerprint, payload);
                return new(existing, true);
            }

            RequireAuthorities(draft.WorkspaceId, draft.ProjectId, draft.Authority);
            var state = new CoverProjectState(draft.CoverProjectId, draft.ProjectId, draft.WorkspaceId,
                draft.Authority, draft.RequiredChannels, [], null, CoverProjectStatus.Draft, 1,
                MessageId(draft.CoverProjectId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, "INSERT INTO cover_projects(workspace_id,cover_project_id,project_id,authority_json,required_channels_json,title,subtitle,author,series,imprint,blurb,isbn,selected_variant_id,status,request_fingerprint,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$c,$t,$st,$au,$s,$i,$b,$isbn,NULL,'DRAFT',$f,1,$m,$at,$at)",
                ("$w",draft.WorkspaceId),("$id",draft.CoverProjectId.ToString("D")),("$p",draft.ProjectId.ToString("D")),
                ("$a",JsonSerializer.Serialize(draft.Authority)),("$c",JsonSerializer.Serialize(draft.RequiredChannels)),
                ("$t",draft.Title),("$st",Db(draft.Subtitle)),("$au",draft.Author),("$s",Db(draft.Series)),
                ("$i",Db(draft.Imprint)),("$b",Db(draft.Blurb)),("$isbn",Db(draft.Isbn)),("$f",draft.RequestFingerprint),
                ("$m",state.MessageId!.Value.ToString("D")),("$at",at.ToString("O")));
            History(connection, tx, state, "SUBMIT", draft.Actor, "cover project submitted", at);
            Receipt(connection, tx, draft.WorkspaceId, draft.RequestId, draft.CoverProjectId, draft.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "cover.workflow.submitted", at);
            tx.Commit();
            return new(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<CoverProjectState> AddVariantAsync(CoverVariantCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.CoverProjectId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Hash(JsonSerializer.Serialize(command)), "ADD_VARIANT", command.Actor,
            "cover variant added", at, state =>
            {
                if (state.Variants.Any(x => x.Draft.VariantId == command.Variant.VariantId))
                    throw new CoverWorkflowConflictException("Variant identity already exists.");
                var variant = new CoverVariant(command.Variant, CoverVariantStatus.Validated, null, 1, at, at);
                return state with { Variants = state.Variants.Append(variant).ToArray(),
                    Status = CoverProjectStatus.CandidateReady, Revision = state.Revision + 1,
                    MessageId = MessageId(state.CoverProjectId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistVariant(connection, tx, command, next.Variants[^1]), ct);

    public ValueTask<CoverProjectState> DecideAsync(CoverDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.CoverProjectId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Hash(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state =>
            {
                var variants = state.Variants.ToArray();
                var index = Array.FindIndex(variants, x => x.Draft.VariantId == command.VariantId);
                if (index < 0) throw new CoverWorkflowValidationException("Cover variant not found.");
                var current = variants[index];
                var status = command.Decision switch
                {
                    CoverDecision.Select => CoverVariantStatus.Selected,
                    CoverDecision.Approve => CoverVariantStatus.Approved,
                    CoverDecision.ReturnToRepair => CoverVariantStatus.RepairRequired,
                    CoverDecision.Reject => CoverVariantStatus.Rejected,
                    CoverDecision.Supersede => CoverVariantStatus.Superseded,
                    _ => throw new CoverWorkflowValidationException("Unsupported cover decision.")
                };
                variants[index] = current with { Status = status, DecisionReason = command.Reason,
                    Revision = current.Revision + 1, UpdatedAtUtc = at };
                var projectStatus = command.Decision switch
                {
                    CoverDecision.Select => CoverProjectStatus.Selected,
                    CoverDecision.Approve => CoverProjectStatus.Approved,
                    CoverDecision.ReturnToRepair => CoverProjectStatus.RepairRequired,
                    CoverDecision.Reject => CoverProjectStatus.Rejected,
                    CoverDecision.Supersede => CoverProjectStatus.Superseded,
                    _ => state.Status
                };
                return state with { Variants = variants,
                    SelectedVariantId = command.Decision is CoverDecision.Select or CoverDecision.Approve ? command.VariantId : state.SelectedVariantId,
                    Status = projectStatus, Revision = state.Revision + 1,
                    MessageId = MessageId(state.CoverProjectId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<CoverProjectState?> GetAsync(string workspaceId, Guid coverProjectId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, coverProjectId));
    }

    private async ValueTask<CoverProjectState> Mutate(string workspaceId, Guid coverProjectId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason, DateTimeOffset at,
        Func<CoverProjectState, CoverProjectState> mutation,
        Action<SqliteConnection, SqliteTransaction, CoverProjectState>? sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint) ||
                    !StringComparer.Ordinal.Equals(replay.Value.Payload, payload))
                    throw new CoverWorkflowConflictException("Operation reused with a different payload.");
                return replay.Value.State;
            }

            var state = Load(workspaceId, coverProjectId)
                ?? throw new CoverWorkflowValidationException("Cover project not found.");
            if (state.Revision != expectedRevision) throw new CoverWorkflowConflictException("Stale revision.");
            RequireAuthorities(state.WorkspaceId, state.ProjectId, state.Authority);
            var next = mutation(state);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx, "UPDATE cover_projects SET selected_variant_id=$v,status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND cover_project_id=$id AND revision=$expected",
                ("$v",next.SelectedVariantId is null ? DBNull.Value : next.SelectedVariantId.Value.ToString("D")),
                ("$s",EnumText(next.Status)),("$r",next.Revision),("$m",next.MessageId!.Value.ToString("D")),
                ("$at",at.ToString("O")),("$w",workspaceId),("$id",coverProjectId.ToString("D")),("$expected",expectedRevision));
            if (affected != 1) throw new CoverWorkflowConflictException("Stale revision.");
            sideEffect?.Invoke(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, coverProjectId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"cover.workflow.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private CoverProjectState? Load(string workspaceId, Guid coverProjectId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_json FROM cover_workflow_history WHERE workspace_id=$w AND cover_project_id=$id ORDER BY revision DESC,occurred_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        cmd.Parameters.AddWithValue("$id", coverProjectId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<CoverProjectState>(json)
            ?? throw new CoverWorkflowConflictException("Invalid persisted cover state.");
    }

    private (string Fingerprint, string Payload, CoverProjectState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT request_fingerprint,payload_digest,response_json FROM cover_workflow_receipts WHERE workspace_id=$w AND operation_id=$o";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        cmd.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<CoverProjectState>(reader.GetString(2))
            ?? throw new CoverWorkflowConflictException("Invalid persisted cover receipt.");
        return (reader.GetString(0), reader.GetString(1), state);
    }

    private void RequireReceipt(string workspaceId, Guid operationId, Guid coverProjectId, string fingerprint, string payload)
    {
        var receipt = LoadReceipt(workspaceId, operationId);
        if (receipt is null || receipt.Value.State.CoverProjectId != coverProjectId ||
            !StringComparer.Ordinal.Equals(receipt.Value.Fingerprint, fingerprint) ||
            !StringComparer.Ordinal.Equals(receipt.Value.Payload, payload))
            throw new CoverWorkflowConflictException("Request reused with a different payload.");
    }

    private void RequireAuthorities(string workspaceId, Guid projectId, CoverAuthorityReference authority)
    {
        using var connection = _factory.OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id,revision,status FROM visual_briefs WHERE workspace_id=$w AND brief_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId);
            cmd.Parameters.AddWithValue("$id", authority.VisualBriefId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new CoverWorkflowValidationException("Visual brief authority not found.");
            var status = reader.GetString(2);
            var digest = Hash($"{workspaceId}:{authority.VisualBriefId:D}:{reader.GetInt64(1)}:{status}");
            if (Guid.Parse(reader.GetString(0)) != projectId || reader.GetInt64(1) != authority.VisualBriefRevision ||
                status != "APPROVED" || !StringComparer.Ordinal.Equals(digest, authority.VisualBriefDigest))
                throw new CoverWorkflowValidationException("Visual brief authority is not exact and current.");
        }

        foreach (var asset in authority.Assets)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT project_id,revision,content_digest,status FROM visual_assets WHERE workspace_id=$w AND asset_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId);
            cmd.Parameters.AddWithValue("$id", asset.AssetId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || Guid.Parse(reader.GetString(0)) != projectId || reader.GetInt64(1) != asset.Revision ||
                !StringComparer.Ordinal.Equals(reader.GetString(2), asset.ContentDigest) || reader.GetString(3) != "APPROVED")
                throw new CoverWorkflowValidationException("Cover asset authority is not exact and approved.");
        }

        foreach (var audit in authority.VisualAudits)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT revision,outcome,status FROM visual_audits WHERE workspace_id=$w AND audit_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId);
            cmd.Parameters.AddWithValue("$id", audit.AuditId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != audit.Revision || reader.GetString(1) != audit.Outcome ||
                reader.GetString(2) != "COMPLETED")
                throw new CoverWorkflowValidationException("Visual audit authority is not exact and passing.");
        }
    }

    private static void PersistVariant(SqliteConnection connection, SqliteTransaction tx, CoverVariantCommand command, CoverVariant variant)
    {
        var draft = variant.Draft;
        Exec(connection, tx, "INSERT INTO cover_variants(workspace_id,cover_project_id,variant_id,channel,variant_kind,source_variant_id,geometry_json,typography_json,export_profile,artifact_digest,status,decision_reason,revision,created_at_utc,updated_at_utc) VALUES($w,$p,$id,$c,$k,$s,$g,$t,$e,$d,$st,NULL,1,$at,$at)",
            ("$w",command.WorkspaceId),("$p",command.CoverProjectId.ToString("D")),("$id",draft.VariantId.ToString("D")),
            ("$c",EnumText(draft.Channel)),("$k",EnumText(draft.Kind)),
            ("$s",draft.SourceVariantId is null ? DBNull.Value : draft.SourceVariantId.Value.ToString("D")),
            ("$g",JsonSerializer.Serialize(draft.Geometry)),("$t",JsonSerializer.Serialize(draft.Typography)),
            ("$e",draft.ExportProfile),("$d",draft.ArtifactDigest),("$st",EnumText(variant.Status)),
            ("$at",variant.CreatedAtUtc.ToString("O")));
        foreach (var placement in draft.Placements)
            Exec(connection, tx, "INSERT INTO cover_placements VALUES($w,$p,$v,$id,$a,$ar,$ad,$r,$b,$c,$l)",
                ("$w",command.WorkspaceId),("$p",command.CoverProjectId.ToString("D")),("$v",draft.VariantId.ToString("D")),
                ("$id",placement.PlacementId.ToString("D")),("$a",placement.AssetId.ToString("D")),("$ar",placement.AssetRevision),
                ("$ad",placement.AssetDigest),("$r",EnumText(placement.Role)),("$b",JsonSerializer.Serialize(placement.Bounds)),
                ("$c",placement.CropMode),("$l",placement.LineageEvidenceDigest));
        foreach (var validation in draft.Validations)
            Exec(connection, tx, "INSERT INTO cover_validations VALUES($w,$p,$v,$id,$k,$o,$pv,$e,$d)",
                ("$w",command.WorkspaceId),("$p",command.CoverProjectId.ToString("D")),("$v",draft.VariantId.ToString("D")),
                ("$id",validation.ValidationId.ToString("D")),("$k",EnumText(validation.Kind)),("$o",EnumText(validation.Outcome)),
                ("$pv",validation.PolicyVersion),("$e",validation.Evidence),("$d",validation.EvidenceDigest));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, CoverDecisionCommand command, CoverProjectState state, DateTimeOffset at)
    {
        var variant = state.Variants.Single(x => x.Draft.VariantId == command.VariantId);
        Exec(connection, tx, "UPDATE cover_variants SET status=$s,decision_reason=$r,revision=$v,updated_at_utc=$at WHERE workspace_id=$w AND variant_id=$id",
            ("$s",EnumText(variant.Status)),("$r",command.Reason),("$v",variant.Revision),("$at",at.ToString("O")),
            ("$w",command.WorkspaceId),("$id",command.VariantId.ToString("D")));
        Exec(connection, tx, "INSERT INTO cover_decisions VALUES($w,$p,$r,$v,$d,$reason,$e,$ed,$a,$at)",
            ("$w",command.WorkspaceId),("$p",command.CoverProjectId.ToString("D")),("$r",command.RequestId.ToString("D")),
            ("$v",command.VariantId.ToString("D")),("$d",EnumText(command.Decision)),("$reason",command.Reason),
            ("$e",command.Evidence),("$ed",command.EvidenceDigest),("$a",command.Actor),("$at",at.ToString("O")));
    }

    private static void History(SqliteConnection connection, SqliteTransaction tx, CoverProjectState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(connection, tx, "INSERT INTO cover_workflow_history VALUES($w,$h,$p,$r,$e,$a,$reason,$s,$at)",
            ("$w",state.WorkspaceId),("$h",Guid.NewGuid().ToString("D")),("$p",state.CoverProjectId.ToString("D")),
            ("$r",state.Revision),("$e",operation),("$a",actor),("$reason",reason),
            ("$s",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId, Guid operationId,
        Guid coverProjectId, string fingerprint, string payload, CoverProjectState state, DateTimeOffset at) =>
        Exec(connection, tx, "INSERT INTO cover_workflow_receipts VALUES($w,$o,$p,$f,$d,$s,$at)",
            ("$w",workspaceId),("$o",operationId.ToString("D")),("$p",coverProjectId.ToString("D")),
            ("$f",fingerprint),("$d",payload),("$s",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, CoverProjectState state, string type, DateTimeOffset at) =>
        Exec(connection, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",
            ("$id",state.MessageId!.Value.ToString("D")),("$t",type),
            ("$p",JsonSerializer.Serialize(new { state.WorkspaceId, state.CoverProjectId, state.ProjectId, state.Revision, state.Status })),
            ("$at",at.ToString("O")));

    private static void ValidateDraft(CoverProjectDraft draft)
    {
        if (draft.RequestId == Guid.Empty || draft.CoverProjectId == Guid.Empty || draft.ProjectId == Guid.Empty ||
            draft.RequiredChannels.Count == 0 || draft.Authority.Assets.Count == 0)
            throw new CoverWorkflowValidationException("Complete cover project identity and authority are required.");
        RequireText(draft.WorkspaceId, draft.Title, draft.Author, draft.Actor, draft.RequestFingerprint,
            draft.Authority.VisualBriefDigest);
    }

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql, params (string, object)[] values)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var value in values) cmd.Parameters.AddWithValue(value.Item1, value.Item2);
        return cmd.ExecuteNonQuery();
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new CoverWorkflowValidationException("Required cover evidence is missing.");
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid MessageId(Guid id, long revision) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"cover-workflow:{id:D}:{revision}"))[..16]);

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
