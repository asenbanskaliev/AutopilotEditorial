using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteVisualBriefStore : IVisualBriefStore, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, VisualBrief> Briefs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Receipts = new(StringComparer.Ordinal);
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteVisualBriefStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<VisualBriefCreateResult> CreateAsync(VisualBriefDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        await _gate.WaitAsync(ct);
        try
        {
            var key = Key(draft.WorkspaceId, draft.BriefId);
            var payload = Hash(JsonSerializer.Serialize(draft));
            if (Briefs.TryGetValue(key, out var existing))
            {
                RequireReceipt(draft.WorkspaceId, draft.BriefId, draft.BriefId, "CREATE", draft.RequestFingerprint, payload);
                return new(existing, true);
            }

            RequireLegalAuthority(draft);
            var messageId = MessageId(draft.BriefId);
            var brief = new VisualBrief(
                draft.BriefId, draft.ProjectId, draft.WorkspaceId,
                draft.LegalRiskCaseId, draft.ExpectedLegalRiskRevision, draft.ExpectedLegalRiskDigest,
                draft.SubjectId, draft.SubjectReference, draft.SubjectDigest, draft.SubjectVersion,
                draft.BriefType, draft.TargetChannel, draft.Width, draft.Height,
                draft.CropMode, draft.SafeZoneJson, draft.ArtDirection, draft.Composition,
                draft.SubjectIdentity, draft.ContinuityConstraints, draft.Style, draft.Palette,
                draft.TypographyIntent, draft.AccessibilityIntent, draft.ProhibitedElements,
                draft.ContinuityReferences.Select(x => new VisualContinuityReference(x.ReferenceId, x.Kind, x.AuthorityKey, x.Digest, x.Version, x.Evidence)).ToArray(),
                [], 1, VisualBriefStatus.Proposed, null, messageId, at, at);

            PersistCreate(draft, brief, payload, at);
            Briefs[key] = brief;
            return new(brief, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<VisualBrief> ReviseAsync(VisualBriefReviseCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "REVISE", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item =>
            {
                if (item.Status is VisualBriefStatus.Approved or VisualBriefStatus.Revoked)
                    throw new VisualBriefTransitionException("Approved or revoked briefs must be reopened before revision.");
                RequireText(command.ArtDirection, command.Composition, command.ContinuityConstraints, command.Style,
                    command.Palette, command.TypographyIntent, command.AccessibilityIntent, command.Reason, command.Actor);
                return item with
                {
                    ArtDirection = command.ArtDirection, Composition = command.Composition,
                    ContinuityConstraints = command.ContinuityConstraints, Style = command.Style,
                    Palette = command.Palette, TypographyIntent = command.TypographyIntent,
                    AccessibilityIntent = command.AccessibilityIntent,
                    ProhibitedElements = command.ProhibitedElements,
                    Reviews = [], Revision = item.Revision + 1, Status = VisualBriefStatus.Proposed,
                    DecisionReason = command.Reason, MessageId = MessageId(command.RequestId), UpdatedAtUtc = at
                };
            }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief> ReviewAsync(VisualBriefReviewCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "REVIEW", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item =>
            {
                if (item.Status != VisualBriefStatus.InReview) throw new VisualBriefTransitionException("Brief is not in review.");
                if (command.ReviewId == Guid.Empty || string.IsNullOrWhiteSpace(command.ReviewerIdentity) ||
                    string.IsNullOrWhiteSpace(command.Scope) || string.IsNullOrWhiteSpace(command.Rationale) ||
                    string.IsNullOrWhiteSpace(command.Evidence))
                    throw new VisualBriefValidationException("Complete review evidence is required.");
                var review = new VisualBriefReview(command.ReviewId, command.ReviewerIdentity, command.Scope,
                    command.Decision, command.Rationale, command.Evidence, command.BlockingFindings, at);
                var status = command.Decision is VisualBriefReviewDecision.Reject or VisualBriefReviewDecision.RequireRepair
                    ? VisualBriefStatus.RepairRequired : VisualBriefStatus.InReview;
                return item with { Reviews = item.Reviews.Append(review).ToArray(), Revision = item.Revision + 1,
                    Status = status, MessageId = MessageId(command.RequestId), UpdatedAtUtc = at };
            }, command.Actor, command.Rationale, ct);

    public ValueTask<VisualBrief> DecideAsync(VisualBriefDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "DECIDE", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item =>
            {
                RequireText(command.Reason, command.Actor);
                if (!AuthorityMatches(item) && command.Decision != VisualBriefDecision.Revoke)
                    return item with { Revision = item.Revision + 1, Status = VisualBriefStatus.Stale,
                        DecisionReason = "Legal-risk authority drift.", MessageId = MessageId(command.RequestId), UpdatedAtUtc = at };
                var status = command.Decision switch
                {
                    VisualBriefDecision.SubmitForReview when item.Status is VisualBriefStatus.Proposed or VisualBriefStatus.RepairRequired => VisualBriefStatus.InReview,
                    VisualBriefDecision.Approve when item.Status == VisualBriefStatus.InReview && CanApprove(item) => VisualBriefStatus.Approved,
                    VisualBriefDecision.ReturnToRepair when item.Status == VisualBriefStatus.InReview => VisualBriefStatus.RepairRequired,
                    VisualBriefDecision.Reopen when item.Status is VisualBriefStatus.Approved or VisualBriefStatus.Revoked or VisualBriefStatus.Stale => VisualBriefStatus.Proposed,
                    VisualBriefDecision.Revoke when item.Status == VisualBriefStatus.Approved => VisualBriefStatus.Revoked,
                    _ => throw new VisualBriefTransitionException("Decision is not valid for current state.")
                };
                return item with { Revision = item.Revision + 1, Status = status, DecisionReason = command.Reason,
                    Reviews = command.Decision == VisualBriefDecision.Reopen ? [] : item.Reviews,
                    MessageId = MessageId(command.RequestId), UpdatedAtUtc = at };
            }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief> MarkStaleAsync(VisualBriefStaleCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "STALE", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item => item with { Revision = item.Revision + 1,
                Status = VisualBriefStatus.Stale, DecisionReason = command.Reason,
                MessageId = MessageId(command.RequestId), UpdatedAtUtc = at }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief?> GetAsync(string workspaceId, Guid briefId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Briefs.TryGetValue(Key(workspaceId, briefId), out var item);
        return ValueTask.FromResult(item);
    }

    private async ValueTask<VisualBrief> Mutate(Guid requestId, string workspaceId, Guid briefId, long expectedRevision,
        string operation, string fingerprint, string payload, DateTimeOffset at, Func<VisualBrief, VisualBrief> mutation,
        string actor, string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var receiptKey = ReceiptKey(workspaceId, requestId);
            var receipt = $"{operation}|{briefId:D}|{fingerprint}|{payload}";
            if (Receipts.TryGetValue(receiptKey, out var existingReceipt))
            {
                if (existingReceipt != receipt) throw new VisualBriefConflictException("Request reused with different payload.");
                return Require(workspaceId, briefId);
            }
            var item = Require(workspaceId, briefId);
            if (item.Revision != expectedRevision) throw new VisualBriefConflictException("Stale revision.");
            var next = mutation(item);
            PersistTransition(next, operation, actor, reason, requestId, fingerprint, payload, at);
            Briefs[Key(workspaceId, briefId)] = next;
            Receipts[receiptKey] = receipt;
            return next;
        }
        finally { _gate.Release(); }
    }

    private void PersistCreate(VisualBriefDraft draft, VisualBrief item, string payload, DateTimeOffset at)
    {
        using var connection = _factory.OpenConnection(); using var tx = connection.BeginTransaction();
        Exec(connection, tx, "INSERT INTO visual_briefs(workspace_id,brief_id,project_id,legal_risk_case_id,expected_legal_risk_revision,expected_legal_risk_digest,subject_id,subject_reference,subject_digest,subject_version,brief_type,target_channel,width,height,crop_mode,safe_zone_json,art_direction,composition,subject_identity,continuity_constraints,style,palette,typography_intent,accessibility_intent,prohibited_elements_json,snapshot_json,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$l,$lr,$ld,$s,$sr,$sd,$sv,$bt,$tc,$wi,$he,$cm,$sz,$ad,$co,$si,$cc,$st,$pa,$ti,$ai,$pe,$sn,1,'PROPOSED',$m,$at,$at)",
            ("$w",draft.WorkspaceId),("$id",draft.BriefId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$l",draft.LegalRiskCaseId.ToString("D")),("$lr",draft.ExpectedLegalRiskRevision),("$ld",draft.ExpectedLegalRiskDigest),("$s",draft.SubjectId.ToString("D")),("$sr",draft.SubjectReference),("$sd",draft.SubjectDigest),("$sv",draft.SubjectVersion),("$bt",draft.BriefType.ToString().ToUpperInvariant()),("$tc",draft.TargetChannel),("$wi",draft.Width),("$he",draft.Height),("$cm",draft.CropMode),("$sz",draft.SafeZoneJson),("$ad",draft.ArtDirection),("$co",draft.Composition),("$si",draft.SubjectIdentity),("$cc",draft.ContinuityConstraints),("$st",draft.Style),("$pa",draft.Palette),("$ti",draft.TypographyIntent),("$ai",draft.AccessibilityIntent),("$pe",JsonSerializer.Serialize(draft.ProhibitedElements)),("$sn",draft.SnapshotJson),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")));
        foreach (var reference in draft.ContinuityReferences)
            Exec(connection, tx, "INSERT INTO visual_continuity_references(workspace_id,brief_id,reference_id,kind,authority_key,digest,version,evidence,created_at_utc) VALUES($w,$b,$id,$k,$a,$d,$v,$e,$at)",("$w",draft.WorkspaceId),("$b",draft.BriefId.ToString("D")),("$id",reference.ReferenceId.ToString("D")),("$k",reference.Kind.ToString().ToUpperInvariant()),("$a",reference.AuthorityKey),("$d",reference.Digest),("$v",reference.Version),("$e",reference.Evidence),("$at",at.ToString("O")));
        History(connection, tx, item, "CREATE", draft.Actor, null, at); Outbox(connection, tx, item, "visual.brief.proposed", at); tx.Commit();
        Receipts[ReceiptKey(draft.WorkspaceId, draft.BriefId)] = $"CREATE|{draft.BriefId:D}|{draft.RequestFingerprint}|{payload}";
    }

    private void PersistTransition(VisualBrief item, string operation, string actor, string reason, Guid requestId, string fingerprint, string payload, DateTimeOffset at)
    {
        using var connection = _factory.OpenConnection(); using var tx = connection.BeginTransaction();
        Exec(connection, tx, "UPDATE visual_briefs SET revision=$r,status=$s,decision_reason=$dr,message_id=$m,art_direction=$ad,composition=$co,continuity_constraints=$cc,style=$st,palette=$pa,typography_intent=$ti,accessibility_intent=$ai,prohibited_elements_json=$pe,updated_at_utc=$at WHERE workspace_id=$w AND brief_id=$id",("$r",item.Revision),("$s",item.Status.ToString().ToUpperInvariant()),("$dr",Db(item.DecisionReason)),("$m",item.MessageId!.Value.ToString("D")),("$ad",item.ArtDirection),("$co",item.Composition),("$cc",item.ContinuityConstraints),("$st",item.Style),("$pa",item.Palette),("$ti",item.TypographyIntent),("$ai",item.AccessibilityIntent),("$pe",JsonSerializer.Serialize(item.ProhibitedElements)),("$at",at.ToString("O")),("$w",item.WorkspaceId),("$id",item.BriefId.ToString("D")));
        History(connection, tx, item, operation, actor, reason, at); Outbox(connection, tx, item, $"visual.brief.{item.Status.ToString().ToLowerInvariant()}", at);
        Exec(connection, tx, "INSERT INTO visual_brief_receipts(workspace_id,request_id,brief_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($w,$r,$b,$o,$f,$p,$v,$m,$at)",("$w",item.WorkspaceId),("$r",requestId.ToString("D")),("$b",item.BriefId.ToString("D")),("$o",operation),("$f",fingerprint),("$p",payload),("$v",item.Revision),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O"))); tx.Commit();
    }

    private void RequireLegalAuthority(VisualBriefDraft draft)
    {
        using var connection = _factory.OpenConnection(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id,revision,status,subject_id,subject_digest,subject_version FROM legal_risk_cases WHERE workspace_id=$w AND case_id=$id";
        cmd.Parameters.AddWithValue("$w", draft.WorkspaceId); cmd.Parameters.AddWithValue("$id", draft.LegalRiskCaseId.ToString("D"));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) throw new VisualBriefValidationException("Approved legal-risk authority not found.");
        var status = reader.GetString(2); var digest = Hash($"{draft.WorkspaceId}:{draft.LegalRiskCaseId:D}:{reader.GetInt64(1)}:{status}");
        if (Guid.Parse(reader.GetString(0)) != draft.ProjectId || reader.GetInt64(1) != draft.ExpectedLegalRiskRevision || status != "APPROVED" || digest != draft.ExpectedLegalRiskDigest || Guid.Parse(reader.GetString(3)) != draft.SubjectId || reader.GetString(4) != draft.SubjectDigest || reader.GetInt32(5) != draft.SubjectVersion)
            throw new VisualBriefValidationException("Legal-risk authority is not exact, current and approved.");
    }

    private bool AuthorityMatches(VisualBrief item)
    {
        try { RequireLegalAuthority(new VisualBriefDraft(item.BriefId,item.ProjectId,item.WorkspaceId,item.LegalRiskCaseId,item.ExpectedLegalRiskRevision,item.ExpectedLegalRiskDigest,item.SubjectId,item.SubjectReference,item.SubjectDigest,item.SubjectVersion,item.BriefType,item.TargetChannel,item.Width,item.Height,item.CropMode,item.SafeZoneJson,item.ArtDirection,item.Composition,item.SubjectIdentity,item.ContinuityConstraints,item.Style,item.Palette,item.TypographyIntent,item.AccessibilityIntent,item.ProhibitedElements,item.ContinuityReferences.Select(x=>new VisualContinuityReferenceDraft(x.ReferenceId,x.Kind,x.AuthorityKey,x.Digest,x.Version,x.Evidence)).ToArray(),"system","{}","check")); return true; }
        catch (VisualBriefValidationException) { return false; }
    }

    private static bool CanApprove(VisualBrief item) => item.Reviews.Any(r => r.Decision == VisualBriefReviewDecision.Approve) &&
        !item.Reviews.Any(r => r.Decision != VisualBriefReviewDecision.Approve || r.BlockingFindings.Count > 0) &&
        !string.IsNullOrWhiteSpace(item.AccessibilityIntent) && !string.IsNullOrWhiteSpace(item.TypographyIntent) && item.ContinuityReferences.Count > 0;

    private static void ValidateDraft(VisualBriefDraft d)
    {
        if (d.BriefId == Guid.Empty || d.ProjectId == Guid.Empty || d.LegalRiskCaseId == Guid.Empty || d.SubjectId == Guid.Empty ||
            d.ExpectedLegalRiskRevision < 1 || d.SubjectVersion < 1 || d.Width < 1 || d.Height < 1 || d.ContinuityReferences.Count == 0 ||
            d.ContinuityReferences.Any(x => x.ReferenceId == Guid.Empty || x.Version < 1 || string.IsNullOrWhiteSpace(x.AuthorityKey) || string.IsNullOrWhiteSpace(x.Digest) || string.IsNullOrWhiteSpace(x.Evidence)))
            throw new VisualBriefValidationException("Complete visual brief and continuity evidence are required.");
        RequireText(d.WorkspaceId,d.ExpectedLegalRiskDigest,d.SubjectReference,d.SubjectDigest,d.TargetChannel,d.CropMode,d.SafeZoneJson,d.ArtDirection,d.Composition,d.SubjectIdentity,d.ContinuityConstraints,d.Style,d.Palette,d.TypographyIntent,d.AccessibilityIntent,d.Actor,d.SnapshotJson,d.RequestFingerprint);
    }

    private VisualBrief Require(string workspaceId, Guid briefId) => Briefs.TryGetValue(Key(workspaceId, briefId), out var item) ? item : throw new VisualBriefValidationException("Visual brief not found.");
    private static void RequireReceipt(string workspaceId, Guid requestId, Guid briefId, string operation, string fingerprint, string payload) { if (!Receipts.TryGetValue(ReceiptKey(workspaceId, requestId), out var value) || value != $"{operation}|{briefId:D}|{fingerprint}|{payload}") throw new VisualBriefConflictException("Request reused with different payload."); }
    private static void RequireText(params string[] values) { if (values.Any(string.IsNullOrWhiteSpace)) throw new VisualBriefValidationException("Required visual brief text is missing."); }
    private static void History(SqliteConnection c, SqliteTransaction tx, VisualBrief i, string op, string actor, string? reason, DateTimeOffset at) => Exec(c,tx,"INSERT INTO visual_brief_history(workspace_id,brief_id,revision,transition,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$o,$a,$reason,$p,$at)",("$w",i.WorkspaceId),("$id",i.BriefId.ToString("D")),("$r",i.Revision),("$o",op),("$a",actor),("$reason",Db(reason)),("$p",JsonSerializer.Serialize(new{i.Status,i.SubjectId,i.SubjectVersion})),("$at",at.ToString("O")));
    private static void Outbox(SqliteConnection c, SqliteTransaction tx, VisualBrief i, string type, DateTimeOffset at) => Exec(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",("$id",i.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new{i.WorkspaceId,i.BriefId,i.ProjectId,i.SubjectId})),("$at",at.ToString("O")));
    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] values) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=sql; foreach(var value in values) cmd.Parameters.AddWithValue(value.Item1,value.Item2); cmd.ExecuteNonQuery(); }
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string Key(string workspaceId, Guid id) => workspaceId + ":" + id.ToString("D");
    private static string ReceiptKey(string workspaceId, Guid id) => workspaceId + ":" + id.ToString("D");
    private static Guid MessageId(Guid id) { var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("visual-brief:"+id.ToString("D"))); return new Guid(bytes[..16]); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}
