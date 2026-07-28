using System.Globalization;
using BookStudio.Application.Autopilot;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

public sealed class SqliteConcurrencyLimitStore : IConcurrencyLimitStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteConcurrencyLimitStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<ConcurrencyLimitDefinition> UpsertLimitAsync(ConcurrencyLimitDefinition definition, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateLimit(definition);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var read = c.CreateCommand(); read.Transaction = tx;
            read.CommandText = "SELECT version FROM concurrency_limits WHERE scope_type=$t AND scope_key=$k;";
            read.Parameters.AddWithValue("$t", ScopeText(definition.ScopeType)); read.Parameters.AddWithValue("$k", definition.ScopeKey);
            var current = read.ExecuteScalar();
            if (current is null && definition.Version != 1) throw new ConcurrencyLimitConflictException("A new limit must start at version 1.");
            if (current is not null && definition.Version != Convert.ToInt64(current, CultureInfo.InvariantCulture) + 1) throw new ConcurrencyLimitConflictException("Limit version conflict.");
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO concurrency_limits(scope_type,scope_key,capacity,version,updated_by,updated_at_utc) VALUES($t,$k,$c,$v,$u,$a) ON CONFLICT(scope_type,scope_key) DO UPDATE SET capacity=excluded.capacity,version=excluded.version,updated_by=excluded.updated_by,updated_at_utc=excluded.updated_at_utc;";
            cmd.Parameters.AddWithValue("$t", ScopeText(definition.ScopeType)); cmd.Parameters.AddWithValue("$k", definition.ScopeKey); cmd.Parameters.AddWithValue("$c", definition.Capacity); cmd.Parameters.AddWithValue("$v", definition.Version); cmd.Parameters.AddWithValue("$u", definition.UpdatedBy); cmd.Parameters.AddWithValue("$a", Text(at)); cmd.ExecuteNonQuery();
            return definition;
        }, ct);
    }

    public ValueTask<ConcurrencyAcquireResult> AcquireAsync(ConcurrencyAcquireCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateAcquire(command);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested(); Expire(c, tx, at);
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireSame(prior, "ACQUIRE", command.OwnerId, command.RequestFingerprint, expectedGrantId: null, compareGrant: false);
                var grant = prior.GrantId is null ? null : ReadGrant(c, tx, prior.GrantId.Value);
                return new(grant is null ? ConcurrencyAcquireOutcome.CapacityUnavailable : ConcurrencyAcquireOutcome.Granted, grant, true, Availability(c, tx, command.Scopes, at));
            }
            var availability = Availability(c, tx, command.Scopes, at);
            if (availability.Any(x => x.Capacity - x.Used < x.Requested))
            {
                InsertRequest(c, tx, command.RequestId, "ACQUIRE", null, command.OwnerId, command.RequestFingerprint, "CAPACITY_UNAVAILABLE", at);
                return new(ConcurrencyAcquireOutcome.CapacityUnavailable, null, false, availability);
            }
            var grantId = DeterministicGrantId(command.RequestId);
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx; cmd.CommandText = "INSERT INTO concurrency_grants(grant_id,acquire_request_id,owner_id,priority,generation,status,acquired_at_utc,lease_until_utc,updated_at_utc) VALUES($g,$r,$o,$p,1,'ACTIVE',$a,$l,$a);";
                cmd.Parameters.AddWithValue("$g", grantId.ToString("D")); cmd.Parameters.AddWithValue("$r", command.RequestId.ToString("D")); cmd.Parameters.AddWithValue("$o", command.OwnerId); cmd.Parameters.AddWithValue("$p", command.Priority); cmd.Parameters.AddWithValue("$a", Text(at)); cmd.Parameters.AddWithValue("$l", Text(at.Add(command.LeaseDuration))); cmd.ExecuteNonQuery();
            }
            foreach (var scope in Normalize(command.Scopes))
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO concurrency_grant_scopes(grant_id,scope_type,scope_key,units) VALUES($g,$t,$k,$u);";
                cmd.Parameters.AddWithValue("$g", grantId.ToString("D")); cmd.Parameters.AddWithValue("$t", ScopeText(scope.ScopeType)); cmd.Parameters.AddWithValue("$k", scope.ScopeKey); cmd.Parameters.AddWithValue("$u", scope.Units); cmd.ExecuteNonQuery();
            }
            InsertRequest(c, tx, command.RequestId, "ACQUIRE", grantId, command.OwnerId, command.RequestFingerprint, "GRANTED", at);
            return new(ConcurrencyAcquireOutcome.Granted, RequireGrant(c, tx, grantId), false, availability);
        }, ct);
    }

    public ValueTask<ConcurrencyGrant> RenewAsync(ConcurrencyRenewCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateControl(command.RequestId, command.GrantId, command.Generation, command.OwnerId, command.LeaseDuration, command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested(); Expire(c, tx, at);
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null) { RequireSame(prior, "RENEW", command.OwnerId, command.RequestFingerprint, command.GrantId, true); return RequireGrant(c, tx, command.GrantId); }
            var current = RequireGrant(c, tx, command.GrantId); RequireLive(current, command.OwnerId, command.Generation, at);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE concurrency_grants SET generation=generation+1,lease_until_utc=$l,updated_at_utc=$a WHERE grant_id=$g;";
            cmd.Parameters.AddWithValue("$l", Text(at.Add(command.LeaseDuration))); cmd.Parameters.AddWithValue("$a", Text(at)); cmd.Parameters.AddWithValue("$g", command.GrantId.ToString("D")); cmd.ExecuteNonQuery();
            InsertRequest(c, tx, command.RequestId, "RENEW", command.GrantId, command.OwnerId, command.RequestFingerprint, "ACTIVE", at);
            return RequireGrant(c, tx, command.GrantId);
        }, ct);
    }

    public ValueTask<ConcurrencyReleaseResult> ReleaseAsync(ConcurrencyReleaseCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateControl(command.RequestId, command.GrantId, command.Generation, command.OwnerId, TimeSpan.FromSeconds(1), command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested(); Expire(c, tx, at);
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null) { RequireSame(prior, "RELEASE", command.OwnerId, command.RequestFingerprint, command.GrantId, true); return new(RequireGrant(c, tx, command.GrantId), true); }
            var current = RequireGrant(c, tx, command.GrantId);
            if (current.Status != ConcurrencyGrantStatus.Released)
            {
                RequireLive(current, command.OwnerId, command.Generation, at);
                using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE concurrency_grants SET status='RELEASED',updated_at_utc=$a WHERE grant_id=$g;";
                cmd.Parameters.AddWithValue("$a", Text(at)); cmd.Parameters.AddWithValue("$g", command.GrantId.ToString("D")); cmd.ExecuteNonQuery();
            }
            InsertRequest(c, tx, command.RequestId, "RELEASE", command.GrantId, command.OwnerId, command.RequestFingerprint, "RELEASED", at);
            return new(RequireGrant(c, tx, command.GrantId), false);
        }, ct);
    }

    public ValueTask<int> ReclaimExpiredAsync(DateTimeOffset at, CancellationToken ct = default) => _queue.ExecuteInTransactionAsync((c, tx, token) => { token.ThrowIfCancellationRequested(); return Expire(c, tx, at); }, ct);

    public async ValueTask<ConcurrencyGrant?> GetGrantAsync(Guid grantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(ReadGrant(c, null, grantId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private static IReadOnlyList<ConcurrencyScopeAvailability> Availability(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<ConcurrencyScopeRequest> scopes, DateTimeOffset at)
    {
        var result = new List<ConcurrencyScopeAvailability>();
        foreach (var scope in Normalize(scopes))
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT l.capacity,COALESCE(SUM(CASE WHEN g.status='ACTIVE' AND g.lease_until_utc>$a THEN s.units ELSE 0 END),0) FROM concurrency_limits l LEFT JOIN concurrency_grant_scopes s ON s.scope_type=l.scope_type AND s.scope_key=l.scope_key LEFT JOIN concurrency_grants g ON g.grant_id=s.grant_id WHERE l.scope_type=$t AND l.scope_key=$k GROUP BY l.capacity;";
            cmd.Parameters.AddWithValue("$a", Text(at)); cmd.Parameters.AddWithValue("$t", ScopeText(scope.ScopeType)); cmd.Parameters.AddWithValue("$k", scope.ScopeKey);
            using var r = cmd.ExecuteReader(); if (!r.Read()) throw new ConcurrencyLimitConflictException($"No limit is configured for {scope.ScopeType}:{scope.ScopeKey}.");
            result.Add(new(scope.ScopeType, scope.ScopeKey, r.GetInt32(0), Convert.ToInt32(r.GetInt64(1), CultureInfo.InvariantCulture), scope.Units));
        }
        return result;
    }

    private static ConcurrencyGrant RequireGrant(SqliteConnection c, SqliteTransaction tx, Guid id) => ReadGrant(c, tx, id) ?? throw new KeyNotFoundException($"Grant '{id:D}' was not found.");
    private static ConcurrencyGrant? ReadGrant(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT grant_id,acquire_request_id,owner_id,priority,generation,status,acquired_at_utc,lease_until_utc,updated_at_utc FROM concurrency_grants WHERE grant_id=$g;"; cmd.Parameters.AddWithValue("$g", id.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        var grant = new ConcurrencyGrant(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetInt32(3), r.GetInt64(4), ParseStatus(r.GetString(5)), Array.Empty<ConcurrencyScopeRequest>(), ParseTime(r.GetString(6)), ParseTime(r.GetString(7)), ParseTime(r.GetString(8))); r.Close();
        using var scopes = c.CreateCommand(); scopes.Transaction = tx; scopes.CommandText = "SELECT scope_type,scope_key,units FROM concurrency_grant_scopes WHERE grant_id=$g ORDER BY scope_type,scope_key;"; scopes.Parameters.AddWithValue("$g", id.ToString("D"));
        using var sr = scopes.ExecuteReader(); var list = new List<ConcurrencyScopeRequest>(); while (sr.Read()) list.Add(new(ParseScope(sr.GetString(0)), sr.GetString(1), sr.GetInt32(2)));
        return grant with { Scopes = list };
    }

    private sealed record RequestRow(string Operation, Guid? GrantId, string OwnerId, string Fingerprint);
    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT operation,grant_id,owner_id,request_fingerprint FROM concurrency_requests WHERE request_id=$r;"; cmd.Parameters.AddWithValue("$r", id.ToString("D")); using var rd = cmd.ExecuteReader();
        return rd.Read() ? new(rd.GetString(0), rd.IsDBNull(1) ? null : Guid.Parse(rd.GetString(1)), rd.GetString(2), rd.GetString(3)) : null;
    }

    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid id, string operation, Guid? grantId, string owner, string fingerprint, string status, DateTimeOffset at)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO concurrency_requests(request_id,operation,grant_id,owner_id,request_fingerprint,result_status,created_at_utc) VALUES($r,$op,$g,$o,$f,$s,$a);";
        cmd.Parameters.AddWithValue("$r", id.ToString("D")); cmd.Parameters.AddWithValue("$op", operation); cmd.Parameters.AddWithValue("$g", grantId is null ? DBNull.Value : grantId.Value.ToString("D")); cmd.Parameters.AddWithValue("$o", owner); cmd.Parameters.AddWithValue("$f", fingerprint); cmd.Parameters.AddWithValue("$s", status); cmd.Parameters.AddWithValue("$a", Text(at)); cmd.ExecuteNonQuery();
    }

    private static int Expire(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE concurrency_grants SET status='EXPIRED',updated_at_utc=$a WHERE status='ACTIVE' AND lease_until_utc<=$a;"; cmd.Parameters.AddWithValue("$a",Text(at)); return cmd.ExecuteNonQuery(); }
    private static void RequireLive(ConcurrencyGrant g,string owner,long generation,DateTimeOffset at) { if(g.Status!=ConcurrencyGrantStatus.Active||g.LeaseUntilUtc<=at||g.OwnerId!=owner||g.Generation!=generation) throw new ConcurrencyLeaseException(g.GrantId,owner); }
    private static void RequireSame(RequestRow row,string operation,string owner,string fingerprint,Guid? expectedGrantId,bool compareGrant) { if(row.Operation!=operation||row.OwnerId!=owner||row.Fingerprint!=fingerprint||(compareGrant&&row.GrantId!=expectedGrantId)) throw new ConcurrencyLimitConflictException("Request ID was reused with different immutable content."); }
    private static IReadOnlyList<ConcurrencyScopeRequest> Normalize(IReadOnlyList<ConcurrencyScopeRequest> scopes)=>scopes.OrderBy(x=>x.ScopeType).ThenBy(x=>x.ScopeKey,StringComparer.Ordinal).ToArray();
    private static Guid DeterministicGrantId(Guid id) { var b=id.ToByteArray(); b[2]^=0x47; b[13]^=0x74; return new Guid(b); }
    private static void ValidateLimit(ConcurrencyLimitDefinition d) { if(d is null||string.IsNullOrWhiteSpace(d.ScopeKey)||d.Capacity<=0||d.Version<=0||string.IsNullOrWhiteSpace(d.UpdatedBy)) throw new ArgumentException("Concurrency limit is invalid."); }
    private static void ValidateAcquire(ConcurrencyAcquireCommand c) { if(c is null||c.RequestId==Guid.Empty||string.IsNullOrWhiteSpace(c.OwnerId)||c.LeaseDuration<=TimeSpan.Zero||c.LeaseDuration>TimeSpan.FromHours(24)||c.Scopes is null||c.Scopes.Count==0||string.IsNullOrWhiteSpace(c.RequestFingerprint)||c.Scopes.Any(s=>string.IsNullOrWhiteSpace(s.ScopeKey)||s.Units<=0)||Normalize(c.Scopes).GroupBy(s=>new{s.ScopeType,s.ScopeKey}).Any(g=>g.Count()>1)) throw new ArgumentException("Concurrency acquire command is invalid."); }
    private static void ValidateControl(Guid requestId,Guid grantId,long generation,string owner,TimeSpan lease,string fingerprint) { if(requestId==Guid.Empty||grantId==Guid.Empty||generation<=0||string.IsNullOrWhiteSpace(owner)||lease<=TimeSpan.Zero||lease>TimeSpan.FromHours(24)||string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Concurrency control command is invalid."); }
    private static string ScopeText(ConcurrencyScopeType v)=>v switch{ConcurrencyScopeType.Global=>"GLOBAL",ConcurrencyScopeType.Provider=>"PROVIDER",ConcurrencyScopeType.ModelRole=>"MODEL_ROLE",ConcurrencyScopeType.Workflow=>"WORKFLOW",ConcurrencyScopeType.Project=>"PROJECT",ConcurrencyScopeType.ToolProfile=>"TOOL_PROFILE",_=>throw new ArgumentOutOfRangeException(nameof(v))};
    private static ConcurrencyScopeType ParseScope(string v)=>v switch{"GLOBAL"=>ConcurrencyScopeType.Global,"PROVIDER"=>ConcurrencyScopeType.Provider,"MODEL_ROLE"=>ConcurrencyScopeType.ModelRole,"WORKFLOW"=>ConcurrencyScopeType.Workflow,"PROJECT"=>ConcurrencyScopeType.Project,"TOOL_PROFILE"=>ConcurrencyScopeType.ToolProfile,_=>throw new InvalidOperationException("Unknown scope.")};
    private static ConcurrencyGrantStatus ParseStatus(string v)=>v switch{"ACTIVE"=>ConcurrencyGrantStatus.Active,"RELEASED"=>ConcurrencyGrantStatus.Released,"EXPIRED"=>ConcurrencyGrantStatus.Expired,_=>throw new InvalidOperationException("Unknown status.")};
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}
