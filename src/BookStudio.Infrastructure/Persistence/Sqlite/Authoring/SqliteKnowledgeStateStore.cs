using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteKnowledgeStateStore : IKnowledgeStateStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteKnowledgeStateStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<KnowledgeCreateResult> CreateAsync(KnowledgeDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(connection, transaction, draft.WorkspaceId, draft.EntryId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(connection, transaction, draft.EntryId);
                if (receipt is null)
                {
                    throw new KnowledgeConflictException("Existing knowledge entry lacks its create receipt.");
                }

                RequireReceipt(receipt, "CREATE", draft.WorkspaceId, draft.EntryId, draft.RequestFingerprint);
                if (!MatchesDraft(existing, draft))
                {
                    throw new KnowledgeConflictException("Entry identity already exists with different immutable content.");
                }

                return new KnowledgeCreateResult(existing, true);
            }

            ValidateAuthority(connection, transaction, draft);
            ValidateFactContradiction(connection, transaction, draft.WorkspaceId, draft.ProjectId, draft.EntryId, draft.Kind, draft.Subject, draft.Object, draft.Statement, draft.ValidFromUtc, draft.ValidToUtc);

            Execute(connection, transaction,
                "INSERT INTO knowledge_entries(workspace_id,entry_id,project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$t,$m,$k,$s,$o,$st,$e,$kn,$ex,'[]',$vf,$vt,$actor,1,'DRAFT',NULL,$at,$at);",
                ("$w", draft.WorkspaceId),
                ("$id", draft.EntryId.ToString("D")),
                ("$p", draft.ProjectId.ToString("D")),
                ("$t", draft.TransitionAuditId.ToString("D")),
                ("$m", draft.TransitionClosedMessageId.ToString("D")),
                ("$k", draft.Kind.ToString().ToUpperInvariant()),
                ("$s", draft.Subject),
                ("$o", draft.Object),
                ("$st", draft.Statement),
                ("$e", draft.Evidence),
                ("$kn", JsonSerializer.Serialize(Normalize(draft.Knowners))),
                ("$ex", JsonSerializer.Serialize(Normalize(draft.Excluded))),
                ("$vf", Text(draft.ValidFromUtc)),
                ("$vt", draft.ValidToUtc is null ? DBNull.Value : Text(draft.ValidToUtc.Value)),
                ("$actor", draft.Actor),
                ("$at", Text(createdAtUtc)));

            InsertReceipt(connection, transaction, draft.EntryId, draft.WorkspaceId, draft.EntryId, "CREATE", draft.RequestFingerprint, 1, null, createdAtUtc);
            return new KnowledgeCreateResult(Require(connection, transaction, draft.WorkspaceId, draft.EntryId), false);
        }, cancellationToken);
    }

    public ValueTask<KnowledgeEntry> ActivateAsync(KnowledgeControlCommand command, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.EntryId, "ACTIVATE", command.RequestFingerprint, command.ExpectedRevision, activatedAtUtc,
            (connection, transaction, entry, knowners, excluded, disclosures) =>
            {
                if (entry.Status != KnowledgeStatus.Draft)
                {
                    throw new KnowledgeTransitionException("Only draft knowledge can activate.");
                }

                ValidateFactContradiction(connection, transaction, entry.WorkspaceId, entry.ProjectId, entry.EntryId, entry.Kind, entry.Subject, entry.Object, entry.Statement, entry.ValidFromUtc, entry.ValidToUtc);
                var messageId = MessageId(command.RequestId, 0x3C);
                InsertOutbox(connection, transaction, messageId, "editorial.knowledge-state.activated", new
                {
                    command.WorkspaceId,
                    command.EntryId,
                    entry.ProjectId,
                    entry.Kind,
                    entry.Subject,
                    entry.Object,
                    activatedBy = command.Actor
                }, activatedAtUtc);

                return new MutationState(KnowledgeStatus.Active, knowners, excluded, disclosures, messageId, messageId);
            }, cancellationToken);

    public ValueTask<KnowledgeEntry> DiscloseAsync(KnowledgeDisclosureCommand command, DateTimeOffset disclosedAtUtc, CancellationToken cancellationToken = default)
    {
        if (command.AddKnowners.Count == 0 || string.IsNullOrWhiteSpace(command.Evidence) || string.IsNullOrWhiteSpace(command.Actor))
        {
            throw new KnowledgeValidationException("Disclosure knowners, evidence and actor are required.");
        }

        return Mutate(command.RequestId, command.WorkspaceId, command.EntryId, "DISCLOSE", command.RequestFingerprint, command.ExpectedRevision, disclosedAtUtc,
            (connection, transaction, entry, knowners, excluded, disclosures) =>
            {
                if (entry.Status != KnowledgeStatus.Active || entry.Kind == KnowledgeKind.Fact)
                {
                    throw new KnowledgeTransitionException("Only active beliefs or secrets can be disclosed.");
                }

                var addedKnowners = Normalize(command.AddKnowners);
                if (addedKnowners.Any(value => excluded.Contains(value, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new KnowledgeValidationException("Excluded actors cannot receive disclosure.");
                }

                var mergedKnowners = Normalize(knowners.Concat(addedKnowners));
                disclosures.Add(new KnowledgeDisclosure(command.RequestId, addedKnowners, command.Evidence, command.Actor, disclosedAtUtc));

                var messageId = MessageId(command.RequestId, 0x5A);
                InsertOutbox(connection, transaction, messageId, "editorial.knowledge-state.disclosed", new
                {
                    command.WorkspaceId,
                    command.EntryId,
                    entry.ProjectId,
                    entry.Kind,
                    addedKnowners,
                    command.Evidence,
                    disclosedBy = command.Actor
                }, disclosedAtUtc);

                return new MutationState(entry.Status, mergedKnowners, excluded, disclosures, entry.ActivationMessageId, messageId);
            }, cancellationToken);
    }

    public ValueTask<KnowledgeEntry> SupersedeAsync(KnowledgeTerminalCommand command, DateTimeOffset supersededAtUtc, CancellationToken cancellationToken = default) =>
        Terminal(command, supersededAtUtc, KnowledgeStatus.Superseded, "SUPERSEDE", cancellationToken);

    public ValueTask<KnowledgeEntry> RetractAsync(KnowledgeTerminalCommand command, DateTimeOffset retractedAtUtc, CancellationToken cancellationToken = default) =>
        Terminal(command, retractedAtUtc, KnowledgeStatus.Retracted, "RETRACT", cancellationToken);

    public async ValueTask<KnowledgeEntry?> GetAsync(string workspaceId, Guid entryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(Read(connection, null, workspaceId, entryId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _queue.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ValueTask<KnowledgeEntry> Terminal(KnowledgeTerminalCommand command, DateTimeOffset atUtc, KnowledgeStatus targetStatus, string operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.Actor))
        {
            throw new KnowledgeValidationException("Terminal reason and actor are required.");
        }

        return Mutate(command.RequestId, command.WorkspaceId, command.EntryId, operation, command.RequestFingerprint, command.ExpectedRevision, atUtc,
            (_, _, entry, knowners, excluded, disclosures) =>
            {
                if (entry.Status != KnowledgeStatus.Active)
                {
                    throw new KnowledgeTransitionException("Only active knowledge can terminate.");
                }

                return new MutationState(targetStatus, knowners, excluded, disclosures, entry.ActivationMessageId, null);
            }, cancellationToken);
    }

    private ValueTask<KnowledgeEntry> Mutate(
        Guid requestId,
        string workspaceId,
        Guid entryId,
        string operation,
        string requestFingerprint,
        long expectedRevision,
        DateTimeOffset atUtc,
        Func<SqliteConnection, SqliteTransaction, KnowledgeEntry, List<string>, List<string>, List<KnowledgeDisclosure>, MutationState> mutation,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty || entryId == Guid.Empty || string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(requestFingerprint))
        {
            throw new KnowledgeValidationException("Request identity is required.");
        }

        return _queue.ExecuteInTransactionAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(connection, transaction, requestId);
            if (receipt is not null)
            {
                RequireReceipt(receipt, operation, workspaceId, entryId, requestFingerprint);
                return Require(connection, transaction, workspaceId, entryId);
            }

            var entry = Require(connection, transaction, workspaceId, entryId);
            if (entry.Revision != expectedRevision)
            {
                throw new KnowledgeConflictException($"Expected revision {expectedRevision}, actual {entry.Revision}.");
            }

            var result = mutation(connection, transaction, entry, entry.Knowners.ToList(), entry.Excluded.ToList(), entry.Disclosures.ToList());
            Execute(connection, transaction,
                "UPDATE knowledge_entries SET knowners_json=$k,excluded_json=$x,disclosures_json=$d,status=$s,revision=$r,activation_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND entry_id=$id;",
                ("$k", JsonSerializer.Serialize(result.Knowners)),
                ("$x", JsonSerializer.Serialize(result.Excluded)),
                ("$d", JsonSerializer.Serialize(result.Disclosures)),
                ("$s", result.Status.ToString().ToUpperInvariant()),
                ("$r", entry.Revision + 1),
                ("$m", result.ActivationMessageId is null ? DBNull.Value : result.ActivationMessageId.Value.ToString("D")),
                ("$at", Text(atUtc)),
                ("$w", workspaceId),
                ("$id", entryId.ToString("D")));

            InsertReceipt(connection, transaction, requestId, workspaceId, entryId, operation, requestFingerprint, entry.Revision + 1, result.ReceiptMessageId, atUtc);
            return Require(connection, transaction, workspaceId, entryId);
        }, cancellationToken);
    }

    private static bool MatchesDraft(KnowledgeEntry entry, KnowledgeDraft draft) =>
        entry.ProjectId == draft.ProjectId &&
        entry.TransitionAuditId == draft.TransitionAuditId &&
        entry.TransitionClosedMessageId == draft.TransitionClosedMessageId &&
        entry.Kind == draft.Kind &&
        entry.Subject == draft.Subject &&
        entry.Object == draft.Object &&
        entry.Statement == draft.Statement &&
        entry.Evidence == draft.Evidence &&
        entry.Knowners.SequenceEqual(Normalize(draft.Knowners), StringComparer.Ordinal) &&
        entry.Excluded.SequenceEqual(Normalize(draft.Excluded), StringComparer.Ordinal) &&
        entry.ValidFromUtc == draft.ValidFromUtc.ToUniversalTime() &&
        entry.ValidToUtc == draft.ValidToUtc?.ToUniversalTime() &&
        entry.Actor == draft.Actor;

    private static void ValidateAuthority(SqliteConnection connection, SqliteTransaction transaction, KnowledgeDraft draft)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM transition_audits WHERE workspace_id=$w AND audit_id=$a AND project_id=$p AND status='CLOSED' AND closed_message_id=$m;";
        command.Parameters.AddWithValue("$w", draft.WorkspaceId);
        command.Parameters.AddWithValue("$a", draft.TransitionAuditId.ToString("D"));
        command.Parameters.AddWithValue("$p", draft.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$m", draft.TransitionClosedMessageId.ToString("D"));
        if (command.ExecuteScalar() is null)
        {
            throw new KnowledgeValidationException("Exact closed transition authority was not found.");
        }
    }

    private static void ValidateFactContradiction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        Guid projectId,
        Guid entryId,
        KnowledgeKind kind,
        string subject,
        string objectText,
        string statement,
        DateTimeOffset validFromUtc,
        DateTimeOffset? validToUtc)
    {
        if (kind != KnowledgeKind.Fact)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT statement FROM knowledge_entries WHERE workspace_id=$w AND project_id=$p AND entry_id<>$id AND kind='FACT' AND subject=$s AND object_text=$o AND status='ACTIVE' AND (valid_to_utc IS NULL OR valid_to_utc>$vf) AND ($vt IS NULL OR valid_from_utc<$vt);";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$p", projectId.ToString("D"));
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        command.Parameters.AddWithValue("$s", subject);
        command.Parameters.AddWithValue("$o", objectText);
        command.Parameters.AddWithValue("$vf", Text(validFromUtc));
        command.Parameters.AddWithValue("$vt", validToUtc is null ? DBNull.Value : Text(validToUtc.Value));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), statement, StringComparison.Ordinal))
            {
                throw new KnowledgeConflictException("Contradictory active fact exists for the same subject, object and validity window.");
            }
        }
    }

    private static KnowledgeEntry? Read(SqliteConnection connection, SqliteTransaction? transaction, string workspaceId, Guid entryId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc FROM knowledge_entries WHERE workspace_id=$w AND entry_id=$id;";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new KnowledgeEntry(
            entryId,
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            workspaceId,
            Enum.Parse<KnowledgeKind>(reader.GetString(3), true),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [],
            JsonSerializer.Deserialize<List<KnowledgeDisclosure>>(reader.GetString(10)) ?? [],
            Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : Parse(reader.GetString(12)),
            reader.GetString(13),
            reader.GetInt64(14),
            Enum.Parse<KnowledgeStatus>(reader.GetString(15), true),
            reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
            Parse(reader.GetString(17)),
            Parse(reader.GetString(18)));
    }

    private static KnowledgeEntry Require(SqliteConnection connection, SqliteTransaction transaction, string workspaceId, Guid entryId) =>
        Read(connection, transaction, workspaceId, entryId) ?? throw new KeyNotFoundException("Knowledge entry not found.");

    private static Receipt? ReadReceipt(SqliteConnection connection, SqliteTransaction transaction, Guid requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT workspace_id,entry_id,operation,request_fingerprint,message_id FROM knowledge_requests WHERE request_id=$id;";
        command.Parameters.AddWithValue("$id", requestId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Receipt(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)))
            : null;
    }

    private static void InsertReceipt(SqliteConnection connection, SqliteTransaction transaction, Guid requestId, string workspaceId, Guid entryId, string operation, string fingerprint, long revision, Guid? messageId, DateTimeOffset atUtc) =>
        Execute(connection, transaction,
            "INSERT INTO knowledge_requests(request_id,workspace_id,entry_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($q,$w,$id,$o,$f,$r,$m,$at);",
            ("$q", requestId.ToString("D")),
            ("$w", workspaceId),
            ("$id", entryId.ToString("D")),
            ("$o", operation),
            ("$f", fingerprint),
            ("$r", revision),
            ("$m", messageId is null ? DBNull.Value : messageId.Value.ToString("D")),
            ("$at", Text(atUtc)));

    private static void InsertOutbox(SqliteConnection connection, SqliteTransaction transaction, Guid messageId, string eventType, object payload, DateTimeOffset atUtc) =>
        Execute(connection, transaction,
            "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$e,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
            ("$m", messageId.ToString("D")),
            ("$e", eventType),
            ("$p", JsonSerializer.Serialize(payload)),
            ("$at", Text(atUtc)));

    private static void RequireReceipt(Receipt receipt, string operation, string workspaceId, Guid entryId, string fingerprint)
    {
        if (receipt.Operation != operation || receipt.Workspace != workspaceId || receipt.EntryId != entryId || receipt.Fingerprint != fingerprint)
        {
            throw new KnowledgeConflictException("Request id was reused with different immutable content.");
        }
    }

    private static void ValidateDraft(KnowledgeDraft draft)
    {
        if (draft.EntryId == Guid.Empty ||
            draft.ProjectId == Guid.Empty ||
            draft.TransitionAuditId == Guid.Empty ||
            draft.TransitionClosedMessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(draft.WorkspaceId) ||
            string.IsNullOrWhiteSpace(draft.Subject) ||
            string.IsNullOrWhiteSpace(draft.Object) ||
            string.IsNullOrWhiteSpace(draft.Statement) ||
            string.IsNullOrWhiteSpace(draft.Evidence) ||
            string.IsNullOrWhiteSpace(draft.Actor) ||
            string.IsNullOrWhiteSpace(draft.RequestFingerprint) ||
            draft.ValidToUtc <= draft.ValidFromUtc)
        {
            throw new KnowledgeValidationException("Complete valid knowledge authority and attribution are required.");
        }

        var knowners = Normalize(draft.Knowners);
        var excluded = Normalize(draft.Excluded);
        if (knowners.Intersect(excluded, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new KnowledgeValidationException("Knowers and excluded actors must be disjoint.");
        }

        if (draft.Kind == KnowledgeKind.Secret && knowners.Count == 0)
        {
            throw new KnowledgeValidationException("Secrets require at least one knower.");
        }
    }

    private static List<string> Normalize(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

    private static Guid MessageId(Guid requestId, byte salt) =>
        new(requestId.ToByteArray().Select((value, index) => (byte)(value ^ (index % 2 == 0 ? salt : (byte)~salt))).ToArray());

    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        command.ExecuteNonQuery();
    }

    private sealed record MutationState(KnowledgeStatus Status, List<string> Knowners, List<string> Excluded, List<KnowledgeDisclosure> Disclosures, Guid? ActivationMessageId, Guid? ReceiptMessageId);
    private sealed record Receipt(string Workspace, Guid EntryId, string Operation, string Fingerprint, Guid? MessageId);
}
