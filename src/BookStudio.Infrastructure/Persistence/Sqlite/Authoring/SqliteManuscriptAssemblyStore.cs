using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteManuscriptAssemblyStore : IManuscriptAssemblyStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteManuscriptAssemblyStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<ManuscriptAssemblySubmissionResult> SubmitAsync(
        ManuscriptAssemblyDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(draft));
            var replay = LoadReceipt(draft.WorkspaceId, draft.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, draft.AssemblyId, draft.RequestFingerprint, payload);
                return new ManuscriptAssemblySubmissionResult(replay.Value.State, true);
            }

            if (Load(draft.WorkspaceId, draft.AssemblyId) is not null)
                throw new ManuscriptAssemblyConflictException("Manuscript assembly already exists.");

            var state = new ManuscriptAssemblyState(
                draft.AssemblyId, draft.ProjectId, draft.WorkspaceId, draft.Locale,
                draft.TargetChannels, draft.Authority, draft.Sections, [], null,
                ManuscriptAssemblyStatus.Draft, 1, MessageId(draft.AssemblyId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO manuscript_assemblies(workspace_id,assembly_id,project_id,locale,target_channels_json,authority_json,sections_json,findings_json,manifest_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$l,$tc,$a,$s,$f,NULL,$st,1,$m,$at,$at)",
                ("$w", draft.WorkspaceId), ("$id", draft.AssemblyId.ToString("D")),
                ("$p", draft.ProjectId.ToString("D")), ("$l", draft.Locale),
                ("$tc", JsonSerializer.Serialize(draft.TargetChannels)),
                ("$a", JsonSerializer.Serialize(draft.Authority)),
                ("$s", JsonSerializer.Serialize(draft.Sections)), ("$f", "[]"),
                ("$st", EnumText(state.Status)), ("$m", state.MessageId!.Value.ToString("D")),
                ("$at", at.ToString("O")));
            PersistSources(connection, tx, draft);
            PersistSections(connection, tx, draft);
            History(connection, tx, state, "SUBMIT", draft.Actor, "canonical manuscript submitted", at);
            Receipt(connection, tx, draft.WorkspaceId, draft.RequestId, draft.AssemblyId,
                draft.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "manuscript.assembly.submitted", at);
            tx.Commit();
            return new ManuscriptAssemblySubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<ManuscriptAssemblyState> ValidateAsync(
        ManuscriptAssemblyValidationCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AssemblyId, command.ExpectedRevision,
            command.RequestId, command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)),
            "VALIDATE", command.Actor, "canonical manuscript validated", at,
            state =>
            {
                var draft = new ManuscriptAssemblyDraft(
                    command.RequestId, state.AssemblyId, state.ProjectId, state.WorkspaceId,
                    state.Locale, state.TargetChannels, state.Authority, state.Sections, [],
                    command.Actor, command.RequestFingerprint);
                var manifest = ManuscriptAssemblyOrchestrator.BuildManifest(draft);
                return state with
                {
                    Findings = [],
                    Manifest = manifest,
                    Status = ManuscriptAssemblyStatus.Validated,
                    Revision = state.Revision + 1,
                    MessageId = MessageId(state.AssemblyId, state.Revision + 1),
                    UpdatedAtUtc = at
                };
            },
            static (_, _, _) => { }, ct);

    public ValueTask<ManuscriptAssemblyState> DecideAsync(
        ManuscriptAssemblyDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AssemblyId, command.ExpectedRevision,
            command.RequestId, command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)),
            "DECIDE", command.Actor, command.Reason, at,
            state => state with
            {
                Status = command.Decision switch
                {
                    ManuscriptAssemblyDecision.Approve => ManuscriptAssemblyStatus.Approved,
                    ManuscriptAssemblyDecision.ReturnToRepair => ManuscriptAssemblyStatus.RepairRequired,
                    ManuscriptAssemblyDecision.Reject => ManuscriptAssemblyStatus.Rejected,
                    ManuscriptAssemblyDecision.Supersede => ManuscriptAssemblyStatus.Superseded,
                    _ => throw new ManuscriptAssemblyValidationException("Unsupported manuscript decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.AssemblyId, state.Revision + 1),
                UpdatedAtUtc = at
            },
            (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<ManuscriptAssemblyState?> GetAsync(
        string workspaceId, Guid assemblyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, assemblyId));
    }

    private async ValueTask<ManuscriptAssemblyState> Mutate(
        string workspaceId, Guid assemblyId, long expectedRevision, Guid operationId,
        string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<ManuscriptAssemblyState, ManuscriptAssemblyState> mutation,
        Action<SqliteConnection, SqliteTransaction, ManuscriptAssemblyState> sideEffect,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, assemblyId, fingerprint, payload);
                return replay.Value.State;
            }

            var current = Load(workspaceId, assemblyId)
                ?? throw new ManuscriptAssemblyValidationException("Manuscript assembly not found.");
            if (current.Revision != expectedRevision)
                throw new ManuscriptAssemblyConflictException("Stale manuscript assembly revision.");
            var next = mutation(current);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE manuscript_assemblies SET findings_json=$f,manifest_json=$manifest,status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND assembly_id=$id AND revision=$expected",
                ("$f", JsonSerializer.Serialize(next.Findings)),
                ("$manifest", next.Manifest is null ? DBNull.Value : JsonSerializer.Serialize(next.Manifest)),
                ("$s", EnumText(next.Status)), ("$r", next.Revision),
                ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", assemblyId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1)
                throw new ManuscriptAssemblyConflictException("Stale manuscript assembly revision.");
            sideEffect(connection, tx, next);
            PersistFindings(connection, tx, next, at);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, assemblyId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"manuscript.assembly.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private ManuscriptAssemblyState? Load(string workspaceId, Guid assemblyId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM manuscript_history WHERE workspace_id=$w AND assembly_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", assemblyId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<ManuscriptAssemblyState>(json)
            ?? throw new ManuscriptAssemblyConflictException("Invalid persisted manuscript state.");
    }

    private (Guid AssemblyId, string Fingerprint, string Payload, ManuscriptAssemblyState State)? LoadReceipt(
        string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT assembly_id,request_fingerprint,payload_digest,response_json FROM manuscript_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<ManuscriptAssemblyState>(reader.GetString(3))
            ?? throw new ManuscriptAssemblyConflictException("Invalid persisted manuscript receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay(
        (Guid AssemblyId, string Fingerprint, string Payload, ManuscriptAssemblyState State) replay,
        Guid assemblyId, string fingerprint, string payload)
    {
        if (replay.AssemblyId != assemblyId ||
            !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint) ||
            !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new ManuscriptAssemblyConflictException("Operation reused with a different payload.");
    }

    private static IEnumerable<ManuscriptSourceBinding> Included(ManuscriptAssemblyAuthority authority) =>
        authority.EditorialSources.Concat(authority.ResearchSources).Concat(authority.RightsSources)
            .Concat(authority.VisualSources).Concat(authority.AccessibilitySources)
            .Concat(authority.CoverSource is null ? [] : [authority.CoverSource]);

    private static void PersistSources(SqliteConnection connection, SqliteTransaction tx, ManuscriptAssemblyDraft draft)
    {
        foreach (var pair in Included(draft.Authority).Select(x => (Source: x, Included: 1))
                     .Concat(draft.ExcludedOptionalSources.Select(x => (Source: x, Included: 0))))
            Exec(connection, tx,
                "INSERT INTO manuscript_source_bindings(workspace_id,assembly_id,source_id,slice_id,revision,content_digest,evidence_digest,source_status,project_id,included) VALUES($w,$a,$id,$slice,$r,$c,$e,$s,$p,$i)",
                ("$w", draft.WorkspaceId), ("$a", draft.AssemblyId.ToString("D")),
                ("$id", pair.Source.SourceId.ToString("D")), ("$slice", pair.Source.SliceId),
                ("$r", pair.Source.Revision), ("$c", pair.Source.ContentDigest),
                ("$e", pair.Source.EvidenceDigest), ("$s", EnumText(pair.Source.Status)),
                ("$p", pair.Source.ProjectId.ToString("D")), ("$i", pair.Included));
    }

    private static void PersistSections(SqliteConnection connection, SqliteTransaction tx, ManuscriptAssemblyDraft draft)
    {
        foreach (var section in draft.Sections)
        {
            Exec(connection, tx,
                "INSERT INTO manuscript_sections(workspace_id,assembly_id,section_id,section_kind,section_order,section_json) VALUES($w,$a,$id,$k,$o,$j)",
                ("$w", draft.WorkspaceId), ("$a", draft.AssemblyId.ToString("D")),
                ("$id", section.SectionId.ToString("D")), ("$k", EnumText(section.Kind)),
                ("$o", section.Order), ("$j", JsonSerializer.Serialize(section)));
            foreach (var node in section.Nodes)
                Exec(connection, tx,
                    "INSERT INTO manuscript_nodes(workspace_id,assembly_id,section_id,node_id,node_kind,node_order,content_digest,node_json) VALUES($w,$a,$s,$id,$k,$o,$d,$j)",
                    ("$w", draft.WorkspaceId), ("$a", draft.AssemblyId.ToString("D")),
                    ("$s", section.SectionId.ToString("D")), ("$id", node.NodeId.ToString("D")),
                    ("$k", EnumText(node.Kind)), ("$o", node.Order),
                    ("$d", node.ContentDigest), ("$j", JsonSerializer.Serialize(node)));
        }
    }

    private static void PersistFindings(SqliteConnection connection, SqliteTransaction tx,
        ManuscriptAssemblyState state, DateTimeOffset at)
    {
        foreach (var finding in state.Findings)
            Exec(connection, tx,
                "INSERT OR REPLACE INTO manuscript_findings(workspace_id,assembly_id,finding_id,code,severity,evidence_digest,finding_json,created_at_utc) VALUES($w,$a,$id,$c,$s,$e,$j,$at)",
                ("$w", state.WorkspaceId), ("$a", state.AssemblyId.ToString("D")),
                ("$id", finding.FindingId.ToString("D")), ("$c", finding.Code),
                ("$s", EnumText(finding.Severity)), ("$e", finding.EvidenceDigest),
                ("$j", JsonSerializer.Serialize(finding)), ("$at", at.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx,
        ManuscriptAssemblyDecisionCommand command, ManuscriptAssemblyState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO manuscript_decisions(workspace_id,assembly_id,operation_id,decision,reason,evidence,evidence_digest,actor,revision,occurred_at_utc) VALUES($w,$a,$o,$d,$r,$e,$ed,$actor,$v,$at)",
            ("$w", command.WorkspaceId), ("$a", command.AssemblyId.ToString("D")),
            ("$o", command.RequestId.ToString("D")), ("$d", EnumText(command.Decision)),
            ("$r", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
            ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx,
        ManuscriptAssemblyState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO manuscript_history(workspace_id,assembly_id,revision,operation,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$a,$r,$o,$actor,$reason,$j,$at)",
            ("$w", state.WorkspaceId), ("$a", state.AssemblyId.ToString("D")),
            ("$r", state.Revision), ("$o", operation), ("$actor", actor),
            ("$reason", reason), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid assemblyId, string fingerprint, string payload,
        ManuscriptAssemblyState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO manuscript_receipts(workspace_id,operation_id,assembly_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$a,$f,$p,$j,$at)",
            ("$w", workspaceId), ("$o", operationId.ToString("D")),
            ("$a", assemblyId.ToString("D")), ("$f", fingerprint), ("$p", payload),
            ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx,
        ManuscriptAssemblyState state, string eventType, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO manuscript_outbox(message_id,workspace_id,assembly_id,revision,event_type,payload_json,created_at_utc) VALUES($m,$w,$a,$r,$e,$p,$at)",
            ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId),
            ("$a", state.AssemblyId.ToString("D")), ("$r", state.Revision),
            ("$e", eventType), ("$p", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();

    private static Guid MessageId(Guid assemblyId, long revision) =>
        DeterministicGuid($"manuscript-assembly:{assemblyId:D}:{revision}");

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
