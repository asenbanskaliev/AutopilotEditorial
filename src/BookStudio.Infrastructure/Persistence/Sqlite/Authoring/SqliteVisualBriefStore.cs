using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteVisualBriefStore : IVisualBriefStore, IAsyncDisposable
{
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
            var payload = Hash(JsonSerializer.Serialize(draft));
            var existing = LoadBrief(draft.WorkspaceId, draft.BriefId);
            if (existing is not null)
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
                    ArtDirection = command.ArtDirection,
                    Composition = command.Composition,
                    ContinuityConstraints = command.ContinuityConstraints,
                    Style = command.Style,
                    Palette = command.Palette,
                    TypographyIntent = command.TypographyIntent,
                    AccessibilityIntent = command.AccessibilityIntent,
                    ProhibitedElements = command.ProhibitedElements,
                    Reviews = [],
                    Revision = item.Revision + 1,
                    Status = VisualBriefStatus.Proposed,
                    DecisionReason = command.Reason,
                    MessageId = MessageId(command.RequestId),
                    UpdatedAtUtc = at
                };
            }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief> ReviewAsync(VisualBriefReviewCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "REVIEW", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item =>
            {
                if (item.Status != VisualBriefStatus.InReview)
                    throw new VisualBriefTransitionException("Brief is not in review.");
                if (command.ReviewId == Guid.Empty || string.IsNullOrWhiteSpace(command.ReviewerIdentity) ||
                    string.IsNullOrWhiteSpace(command.Scope) || string.IsNullOrWhiteSpace(command.Rationale) ||
                    string.IsNullOrWhiteSpace(command.Evidence))
                    throw new VisualBriefValidationException("Complete review evidence is required.");
                var review = new VisualBriefReview(command.ReviewId, command.ReviewerIdentity, command.Scope,
                    command.Decision, command.Rationale, command.Evidence, command.BlockingFindings, at);
                var status = command.Decision is VisualBriefReviewDecision.Reject or VisualBriefReviewDecision.RequireRepair
                    ? VisualBriefStatus.RepairRequired : VisualBriefStatus.InReview;
                return item with
                {
                    Reviews = item.Reviews.Append(review).ToArray(),
                    Revision = item.Revision + 1,
                    Status = status,
                    MessageId = MessageId(command.RequestId),
                    UpdatedAtUtc = at
                };
            }, command.Actor, command.Rationale, ct);

    public ValueTask<VisualBrief> DecideAsync(VisualBriefDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "DECIDE", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item =>
            {
                RequireText(command.Reason, command.Actor);
                if (!AuthorityMatches(item) && command.Decision != VisualBriefDecision.Revoke)
                    return item with
                    {
                        Revision = item.Revision + 1,
                        Status = VisualBriefStatus.Stale,
                        DecisionReason = "Legal-risk authority drift.",
                        MessageId = MessageId(command.RequestId),
                        UpdatedAtUtc = at
                    };
                var status = command.Decision switch
                {
                    VisualBriefDecision.SubmitForReview when item.Status is VisualBriefStatus.Proposed or VisualBriefStatus.RepairRequired => VisualBriefStatus.InReview,
                    VisualBriefDecision.Approve when item.Status == VisualBriefStatus.InReview && CanApprove(item) => VisualBriefStatus.Approved,
                    VisualBriefDecision.ReturnToRepair when item.Status == VisualBriefStatus.InReview => VisualBriefStatus.RepairRequired,
                    VisualBriefDecision.Reopen when item.Status is VisualBriefStatus.Approved or VisualBriefStatus.Revoked or VisualBriefStatus.Stale => VisualBriefStatus.Proposed,
                    VisualBriefDecision.Revoke when item.Status == VisualBriefStatus.Approved => VisualBriefStatus.Revoked,
                    _ => throw new VisualBriefTransitionException("Decision is not valid for current state.")
                };
                return item with
                {
                    Revision = item.Revision + 1,
                    Status = status,
                    DecisionReason = command.Reason,
                    Reviews = command.Decision == VisualBriefDecision.Reopen ? [] : item.Reviews,
                    MessageId = MessageId(command.RequestId),
                    UpdatedAtUtc = at
                };
            }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief> MarkStaleAsync(VisualBriefStaleCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.BriefId, command.ExpectedRevision, "STALE", command.RequestFingerprint,
            Hash(JsonSerializer.Serialize(command)), at, item => item with
            {
                Revision = item.Revision + 1,
                Status = VisualBriefStatus.Stale,
                DecisionReason = command.Reason,
                MessageId = MessageId(command.RequestId),
                UpdatedAtUtc = at
            }, command.Actor, command.Reason, ct);

    public ValueTask<VisualBrief?> GetAsync(string workspaceId, Guid briefId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(LoadBrief(workspaceId, briefId));
    }

    private async ValueTask<VisualBrief> Mutate(Guid requestId, string workspaceId, Guid briefId, long expectedRevision,
        string operation, string fingerprint, string payload, DateTimeOffset at, Func<VisualBrief, VisualBrief> mutation,
        string actor, string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existingReceipt = LoadReceipt(workspaceId, requestId);
            if (existingReceipt is not null)
            {
                var expected = $"{operation}|{briefId:D}|{fingerprint}|{payload}";
                if (!StringComparer.Ordinal.Equals(existingReceipt, expected))
                    throw new VisualBriefConflictException("Request reused with different payload.");
                return Require(workspaceId, briefId);
            }

            var item = Require(workspaceId, briefId);
            if (item.Revision != expectedRevision)
                throw new VisualBriefConflictException("Stale revision.");
            var next = mutation(item);
            PersistTransition(next, operation, actor, reason, requestId, fingerprint, payload, at);
            return next;
        }
        finally { _gate.Release(); }
    }

    private void PersistCreate(VisualBriefDraft draft, VisualBrief item, string payload, DateTimeOffset at)
    {
        using var connection = _factory.OpenConnection();
        using var tx = connection.BeginTransaction();
        Exec(connection, tx, "INSERT INTO visual_briefs(workspace_id,brief_id,project_id,legal_risk_case_id,expected_legal_risk_revision,expected_legal_risk_digest,subject_id,subject_reference,subject_digest,subject_version,brief_type,target_channel,width,height,crop_mode,safe_zone_json,art_direction,composition,subject_identity,continuity_constraints,style,palette,typography_intent,accessibility_intent,prohibited_elements_json,snapshot_json,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$l,$lr,$ld,$s,$sr,$sd,$sv,$bt,$tc,$wi,$he,$cm,$sz,$ad,$co,$si,$cc,$st,$pa,$ti,$ai,$pe,$sn,1,'PROPOSED',$m,$at,$at)",
            ("$w",draft.WorkspaceId),("$id",draft.BriefId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$l",draft.LegalRiskCaseId.ToString("D")),("$lr",draft.ExpectedLegalRiskRevision),("$ld",draft.ExpectedLegalRiskDigest),("$s",draft.SubjectId.ToString("D")),("$sr",draft.SubjectReference),("$sd",draft.SubjectDigest),("$sv",draft.SubjectVersion),("$bt",draft.BriefType.ToString().ToUpperInvariant()),("$tc",draft.TargetChannel),("$wi",draft.Width),("$he",draft.Height),("$cm",draft.CropMode),("$sz",draft.SafeZoneJson),("$ad",draft.ArtDirection),("$co",draft.Composition),("$si",draft.SubjectIdentity),("$cc",draft.ContinuityConstraints),("$st",draft.Style),("$pa",draft.Palette),("$ti",draft.TypographyIntent),("$ai",draft.AccessibilityIntent),("$pe",JsonSerializer.Serialize(draft.ProhibitedElements)),("$sn",draft.SnapshotJson),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")));
        foreach (var reference in draft.ContinuityReferences)
            Exec(connection, tx, "INSERT INTO visual_continuity_references(workspace_id,brief_id,reference_id,kind,authority_key,digest,version,evidence,created_at_utc) VALUES($w,$b,$id,$k,$a,$d,$v,$e,$at)",
                ("$w",draft.WorkspaceId),("$b",draft.BriefId.ToString("D")),("$id",reference.ReferenceId.ToString("D")),("$k",reference.Kind.ToString().ToUpperInvariant()),("$a",reference.AuthorityKey),("$d",reference.Digest),("$v",reference.Version),("$e",reference.Evidence),("$at",at.ToString("O")));
        History(connection, tx, item, "CREATE", draft.Actor, null, at);
        Outbox(connection, tx, item, "visual.brief.proposed", at);
        Receipt(connection, tx, draft.WorkspaceId, draft.BriefId, draft.BriefId, "CREATE", draft.RequestFingerprint, payload, item.Revision, item.MessageId, at);
        tx.Commit();
    }

    private void PersistTransition(VisualBrief item, string operation, string actor, string reason, Guid requestId, string fingerprint, string payload, DateTimeOffset at)
    {
        using var connection = _factory.OpenConnection();
        using var tx = connection.BeginTransaction();
        var affected = Exec(connection, tx, "UPDATE visual_briefs SET revision=$r,status=$s,decision_reason=$dr,message_id=$m,art_direction=$ad,composition=$co,continuity_constraints=$cc,style=$st,palette=$pa,typography_intent=$ti,accessibility_intent=$ai,prohibited_elements_json=$pe,updated_at_utc=$at WHERE workspace_id=$w AND brief_id=$id AND revision=$expected",
            ("$r",item.Revision),("$s",item.Status.ToString().ToUpperInvariant()),("$dr",Db(item.DecisionReason)),("$m",item.MessageId!.Value.ToString("D")),("$ad",item.ArtDirection),("$co",item.Composition),("$cc",item.ContinuityConstraints),("$st",item.Style),("$pa",item.Palette),("$ti",item.TypographyIntent),("$ai",item.AccessibilityIntent),("$pe",JsonSerializer.Serialize(item.ProhibitedElements)),("$at",at.ToString("O")),("$w",item.WorkspaceId),("$id",item.BriefId.ToString("D")),("$expected",item.Revision - 1));
        if (affected != 1)
            throw new VisualBriefConflictException("Stale revision.");

        Exec(connection, tx, "DELETE FROM visual_brief_reviews WHERE workspace_id=$w AND brief_id=$b", ("$w",item.WorkspaceId),("$b",item.BriefId.ToString("D")));
        foreach (var review in item.Reviews)
            Exec(connection, tx, "INSERT INTO visual_brief_reviews(workspace_id,brief_id,review_id,reviewer_identity,scope,decision,rationale,evidence,blocking_findings_json,reviewed_at_utc) VALUES($w,$b,$id,$ri,$sc,$d,$ra,$e,$bf,$at)",
                ("$w",item.WorkspaceId),("$b",item.BriefId.ToString("D")),("$id",review.ReviewId.ToString("D")),("$ri",review.ReviewerIdentity),("$sc",review.Scope),("$d",review.Decision.ToString().ToUpperInvariant()),("$ra",review.Rationale),("$e",review.Evidence),("$bf",JsonSerializer.Serialize(review.BlockingFindings)),("$at",review.ReviewedAtUtc.ToString("O")));
        History(connection, tx, item, operation, actor, reason, at);
        Outbox(connection, tx, item, $"visual.brief.{item.Status.ToString().ToLowerInvariant()}", at);
        Receipt(connection, tx, item.WorkspaceId, requestId, item.BriefId, operation, fingerprint, payload, item.Revision, item.MessageId, at);
        tx.Commit();
    }

    private VisualBrief? LoadBrief(string workspaceId, Guid briefId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id,legal_risk_case_id,expected_legal_risk_revision,expected_legal_risk_digest,subject_id,subject_reference,subject_digest,subject_version,brief_type,target_channel,width,height,crop_mode,safe_zone_json,art_direction,composition,subject_identity,continuity_constraints,style,palette,typography_intent,accessibility_intent,prohibited_elements_json,revision,status,decision_reason,message_id,created_at_utc,updated_at_utc FROM visual_briefs WHERE workspace_id=$w AND brief_id=$id";
        cmd.Parameters.AddWithValue("$w", workspaceId);
        cmd.Parameters.AddWithValue("$id", briefId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var references = LoadReferences(connection, workspaceId, briefId);
        var reviews = LoadReviews(connection, workspaceId, briefId);
        return new VisualBrief(
            briefId, Guid.Parse(reader.GetString(0)), workspaceId,
            Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3),
            Guid.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
            Enum.Parse<VisualBriefType>(reader.GetString(8), true), reader.GetString(9), reader.GetInt32(10), reader.GetInt32(11),
            reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17),
            reader.GetString(18), reader.GetString(19), reader.GetString(20), reader.GetString(21),
            JsonSerializer.Deserialize<string[]>(reader.GetString(22)) ?? [], references, reviews,
            reader.GetInt64(23), Enum.Parse<VisualBriefStatus>(reader.GetString(24), true), reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : Guid.Parse(reader.GetString(26)), DateTimeOffset.Parse(reader.GetString(27)), DateTimeOffset.Parse(reader.GetString(28)));
    }

    private static IReadOnlyList<VisualContinuityReference> LoadReferences(SqliteConnection connection, string workspaceId, Guid briefId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT reference_id,kind,authority_key,digest,version,evidence FROM visual_continuity_references WHERE workspace_id=$w AND brief_id=$b ORDER BY reference_id";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$b", briefId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var result = new List<VisualContinuityReference>();
        while (reader.Read()) result.Add(new VisualContinuityReference(Guid.Parse(reader.GetString(0)), Enum.Parse<VisualContinuityKind>(reader.GetString(1), true), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
        return result;
    }

    private static IReadOnlyList<VisualBriefReview> LoadReviews(SqliteConnection connection, string workspaceId, Guid briefId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT review_id,reviewer_identity,scope,decision,rationale,evidence,blocking_findings_json,reviewed_at_utc FROM visual_brief_reviews WHERE workspace_id=$w AND brief_id=$b ORDER BY reviewed_at_utc,review_id";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$b", briefId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        var result = new List<VisualBriefReview>();
        while (reader.Read()) result.Add(new VisualBriefReview(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), Enum.Parse<VisualBriefReviewDecision>(reader.GetString(3), true), reader.GetString(4), reader.GetString(5), JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [], DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

    private string? LoadReceipt(string workspaceId, Guid requestId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT operation,brief_id,request_fingerprint,payload_hash FROM visual_brief_receipts WHERE workspace_id=$w AND request_id=$r";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$r", requestId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}" : null;
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
        try
        {
            RequireLegalAuthority(new VisualBriefDraft(item.BriefId,item.ProjectId,item.WorkspaceId,item.LegalRiskCaseId,item.ExpectedLegalRiskRevision,item.ExpectedLegalRiskDigest,item.SubjectId,item.SubjectReference,item.SubjectDigest,item.SubjectVersion,item.BriefType,item.TargetChannel,item.Width,item.Height,item.CropMode,item.SafeZoneJson,item.ArtDirection,item.Composition,item.SubjectIdentity,item.ContinuityConstraints,item.Style,item.Palette,item.TypographyIntent,item.AccessibilityIntent,item.ProhibitedElements,item.ContinuityReferences.Select(x=>new VisualContinuityReferenceDraft(x.ReferenceId,x.Kind,x.AuthorityKey,x.Digest,x.Version,x.Evidence)).ToArray(),"system","{}","check"));
            return true;
        }
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

    private VisualBrief Require(string workspaceId, Guid briefId) => LoadBrief(workspaceId, briefId) ?? throw new VisualBriefValidationException("Visual brief not found.");
    private void RequireReceipt(string workspaceId, Guid requestId, Guid briefId, string operation, string fingerprint, string payload)
    {
        var value = LoadReceipt(workspaceId, requestId);
        if (!StringComparer.Ordinal.Equals(value, $"{operation}|{briefId:D}|{fingerprint}|{payload}"))
            throw new VisualBriefConflictException("Request reused with different payload.");
    }
    private static void RequireText(params string[] values) { if (values.Any(string.IsNullOrWhiteSpace)) throw new VisualBriefValidationException("Required visual brief text is missing."); }
    private static void History(SqliteConnection c, SqliteTransaction tx, VisualBrief i, string op, string actor, string? reason, DateTimeOffset at) => Exec(c,tx,"INSERT INTO visual_brief_history(workspace_id,brief_id,revision,transition,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$o,$a,$reason,$p,$at)",("$w",i.WorkspaceId),("$id",i.BriefId.ToString("D")),("$r",i.Revision),("$o",op),("$a",actor),("$reason",Db(reason)),("$p",JsonSerializer.Serialize(new{i.Status,i.SubjectId,i.SubjectVersion})),("$at",at.ToString("O")));
    private static void Outbox(SqliteConnection c, SqliteTransaction tx, VisualBrief i, string type, DateTimeOffset at) => Exec(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",("$id",i.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new{i.WorkspaceId,i.BriefId,i.ProjectId,i.SubjectId})),("$at",at.ToString("O")));
    private static void Receipt(SqliteConnection c, SqliteTransaction tx, string workspaceId, Guid requestId, Guid briefId, string operation, string fingerprint, string payload, long revision, Guid? messageId, DateTimeOffset at) => Exec(c,tx,"INSERT INTO visual_brief_receipts(workspace_id,request_id,brief_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($w,$r,$b,$o,$f,$p,$v,$m,$at)",("$w",workspaceId),("$r",requestId.ToString("D")),("$b",briefId.ToString("D")),("$o",operation),("$f",fingerprint),("$p",payload),("$v",revision),("$m",messageId is null ? DBNull.Value : messageId.Value.ToString("D")),("$at",at.ToString("O")));
    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] values) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=sql; foreach(var value in values) cmd.Parameters.AddWithValue(value.Item1,value.Item2); return cmd.ExecuteNonQuery(); }
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static Guid MessageId(Guid id) { var bytes=SHA256.HashData(Encoding.UTF8.GetBytes("visual-brief:"+id.ToString("D"))); return new Guid(bytes[..16]); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}
