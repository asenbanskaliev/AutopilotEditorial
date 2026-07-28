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

    public ValueTask<ConcurrencyLimitDefinition> UpsertLimitAsync(ConcurrencyLimitDefinition definition, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateLimit(definition);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var read = connection.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT capacity,version FROM concurrency_limits WHERE scope_type=$type AND scope_key=$key;";
            read.Parameters.AddWithValue("$type", ScopeText(definition.ScopeType));
            read.Parameters.AddWithValue("$key", definition.ScopeKey);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                var currentVersion = reader.GetInt64(1);
                if (definition.Version != currentVersion + 1)
                    throw new ConcurrencyLimitConflictException($"Expected limit version {currentVersion + 1}.");
            }
            else if (definition.Version != 1)
            {
                throw new ConcurrencyLimitConflictException("A new limit must start at version 1.");
            }
            reader.Close();

            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO concurrency_limits(scope_type,scope_key,capacity,version,updated_by,updated_at_utc)
                VALUES($type,$key,$capacity,$version,$actor,$at)
                ON CONFLICT(scope_type,scope_key) DO UPDATE SET
                  capacity=excluded.capacity,version=excluded.version,updated_by=excluded.updated_by,updated_at_utc=excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$type", ScopeText(definition.ScopeType));
            command.Parameters.AddWithValue("$key", definition.ScopeKey);
            command.Parameters.AddWithValue("$capacity", definition.Capacity);
            command.Parameters.AddWithValue("$version", definition.Version);
            command.Parameters.AddWithValue("$actor", definition.UpdatedBy);
            command.Parameters.AddWithValue("$at", Text(updatedAtUtc));
            command.ExecuteNonQuery();
            return definition;
        }, cancellationToken);
    }

    public ValueTask<ConcurrencyAcquireResult> AcquireAsync(ConcurrencyAcquireCommand command, DateTimeOffset acquiredAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateAcquire(command);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            Expire(connection, tx, acquiredAtUtc);
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, "ACQUIRE", command.OwnerId, command.RequestFingerprint, null);
                var replayGrant = replay.GrantId is null ? null : ReadGrant(connection, tx, replay.GrantId.Value);
                return new ConcurrencyAcquireResult(
                    replayGrant is null ? ConcurrencyAcquireOutcome.CapacityUnavailable : ConcurrencyAcquireOutcome.Granted,
                    replayGrant,
                    true,
                    Availability(connection, tx, command.Scopes, acquiredAtUtc));
            }

            var availability = Availability(connection, tx, command.Scopes, acquiredAtUtc);
            if (availability.Any(item => item.Capacity - item.Used < item.Requested))
            {
                InsertRequest(connection, tx, command.RequestId, "ACQUIRE", null, command.OwnerId, command.RequestFingerprint, "CAPACITY_UNAVAILABLE", acquiredAtUtc);
                return new ConcurrencyAcquireResult(ConcurrencyAcquireOutcome.CapacityUnavailable, null, false, availability);
            }

            var grantId = DeterministicGrantId(command.RequestId);
            using (var grant = connection.CreateCommand())
            {
                grant.Transaction = tx;
                grant.CommandText = "INSERT INTO concurrency_grants(grant_id,acquire_request_id,owner_id,priority,generation,status,acquired_at_utc,lease_until_utc,updated_at_utc) VALUES($id,$request,$owner,$priority,1,'ACTIVE',$at,$until,$at);";
                grant.Parameters.AddWithValue("$id", grantId.ToString("D"));
                grant.Parameters.AddWithValue("$request", command.RequestId.ToString("D"));
                grant.Parameters.AddWithValue("$owner", command.OwnerId);
                grant.Parameters.AddWithValue("$priority", command.Priority);
                grant.Parameters.AddWithValue("$at", Text(acquiredAtUtc));
                grant.Parameters.AddWithValue("$until", Text(acquiredAtUtc.Add(command.LeaseDuration)));
                grant.ExecuteNonQuery();
            }
            foreach (var scope in Normalize(command.Scopes))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO concurrency_grant_scopes(grant_id,scope_type,scope_key,units) VALUES($grant,$type,$key,$units);";
                insert.Parameters.AddWithValue("$grant", grantId.ToString("D"));
                insert.Parameters.AddWithValue("$type", ScopeText(scope.ScopeType));
                insert.Parameters.AddWithValue("$key", scope.ScopeKey);
                insert.Parameters.AddWithValue("$units", scope.Units);
                insert.ExecuteNonQuery();
            }
            InsertRequest(connection, tx, command.RequestId, "ACQUIRE", grantId, command.OwnerId, command.RequestFingerprint, "GRANTED", acquiredAtUtc);
            return new ConcurrencyAcquireResult(ConcurrencyAcquireOutcome.Granted, RequireGrant(connection, tx, grantId), false, availability);
        }, cancellationToken);
    }

    public ValueTask<ConcurrencyGrant> RenewAsync(ConcurrencyRenewCommand command, DateTimeOffset renewedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateControl(command.RequestId, command.GrantId, command.Generation, command.OwnerId, command.LeaseDuration, command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            Expire(connection, tx, renewedAtUtc);
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, "RENEW", command.OwnerId, command.RequestFingerprint, command.GrantId);
                return RequireGrant(connection, tx, command.GrantId);
            }
            var current = RequireGrant(connection, tx, command.GrantId);
            RequireLive(current, command.OwnerId, command.Generation, renewedAtUtc);
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE concurrency_grants SET generation=generation+1,lease_until_utc=$until,updated_at_utc=$at WHERE grant_id=$id;";
            update.Parameters.AddWithValue("$until", Text(renewedAtUtc.Add(command.LeaseDuration)));
            update.Parameters.AddWithValue("$at", Text(renewedAtUtc));
            update.Parameters.AddWithValue("$id", command.GrantId.ToString("D"));
            update.ExecuteNonQuery();
            InsertRequest(connection, tx, command.RequestId, "RENEW", command.GrantId, command.OwnerId, command.RequestFingerprint, "ACTIVE", renewedAtUtc);
            return RequireGrant(connection, tx, command.GrantId);
        }, cancellationToken);
    }

    public ValueTask<ConcurrencyReleaseResult> ReleaseAsync(ConcurrencyReleaseCommand command, DateTimeOffset releasedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateControl(command.RequestId, command.GrantId, command.Generation, command.OwnerId, TimeSpan.FromSeconds(1), command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            Expire(connection, tx, releasedAtUtc);
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, "RELEASE", command.OwnerId, command.RequestFingerprint, command.GrantId);
                return new ConcurrencyReleaseResult(RequireGrant(connection, tx, command.GrantId), true);
            }
            var current = RequireGrant(connection, tx, command.GrantId);
            if (current.Status == ConcurrencyGrantStatus.Released)
            {
                InsertRequest(connection, tx, command.RequestId, "RELEASE", command.GrantId, command.OwnerId, command.RequestFingerprint, "RELEASED", releasedAtUtc);
                return new ConcurrencyReleaseResult(current, false);
            }
            RequireLive(current, command.OwnerId, command.Generation, releasedAtUtc);
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE concurrency_grants SET status='RELEASED',updated_at_utc=$at WHERE grant_id=$id;";
            update.Parameters.AddWithValue("$at", Text(releasedAtUtc));
            update.Parameters.AddWithValue("$id", command.GrantId.ToString("D"));
            update.ExecuteNonQuery();
            InsertRequest(connection, tx, command.RequestId, "RELEASE", command.GrantId, command.OwnerId, command.RequestFingerprint, "RELEASED", releasedAtUtc);
            return new ConcurrencyReleaseResult(RequireGrant(connection, tx, command.GrantId), false);
        }, cancellationToken);
    }

    public ValueTask<int> ReclaimExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Expire(connection, tx, nowUtc);
        }, cancellationToken);

    public async ValueTask<ConcurrencyGrant?> GetGrantAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(ReadGrant(connection, null, grantId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private static IReadOnlyList<ConcurrencyScopeAvailability> Availability(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<ConcurrencyScopeRequest> requested, DateTimeOffset now)
    {
        var result = new List<ConcurrencyScopeAvailability>();
        foreach (var scope in Normalize(requested))
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT l.capacity,COALESCE(SUM(CASE WHEN g.status='ACTIVE' AND g.lease_until_utc>$now THEN s.units ELSE 0 END),0)
                FROM concurrency_limits l
                LEFT JOIN concurrency_grant_scopes s ON s.scope_type=l.scope_type AND s.scope_key=l.scope_key
                LEFT JOIN concurrency_grants g ON g.grant_id=s.grant_id
                WHERE l.scope_type=$type AND l.scope_key=$key
                GROUP BY l.capacity;
                """;
            cmd.Parameters.AddWithValue("$now", Text(now));
            cmd.Parameters.AddWithValue("$type", ScopeText(scope.ScopeType));
            cmd.Parameters.AddWithValue("$key", scope.ScopeKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new ConcurrencyLimitConflictException($"No limit is configured for {scope.ScopeType}:{scope.ScopeKey}.");
            result.Add(new(scope.ScopeType, scope.ScopeKey, reader.GetInt32(0), Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture), scope.Units));
        }
        return result;
    }

    private static ConcurrencyGrant RequireGrant(SqliteConnection c, SqliteTransaction tx, Guid id) => ReadGrant(c, tx, id) ?? throw new KeyNotFoundException($"Grant '{id:D}' was not found.");
    private static ConcurrencyGrant? ReadGrant(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT grant_id,acquire_request_id,owner_id,priority,generation,status,acquired_at_utc,lease_until_utc,updated_at_utc FROM concurrency_grants WHERE grant_id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var grant = new ConcurrencyGrant(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4), ParseStatus(reader.GetString(5)), Array.Empty<ConcurrencyScopeRequest>(), ParseTime(reader.GetString(6)), ParseTime(reader.GetString(7)), ParseTime(reader.GetString(8)));
        reader.Close();
        using var scopes = c.CreateCommand(); scopes.Transaction = tx;
        scopes.CommandText = "SELECT scope_type,scope_key,units FROM concurrency_grant_scopes WHERE grant_id=$id ORDER BY scope_type,scope_key;";
        scopes.Parameters.AddWithValue("$id", id.ToString("D"));
        using var scopeReader = scopes.ExecuteReader();
        var list = new List<ConcurrencyScopeRequest>();
        while (scopeReader.Read()) list.Add(new(ParseScope(scopeReader.GetString(0)), scopeReader.GetString(1), scopeReader.GetInt32(2)));
        return grant with { Scopes = list };
    }

    private sealed record RequestRow(string Operation, Guid? GrantId, string OwnerId, string Fingerprint);
    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT operation,grant_id,owner_id,request_fingerprint FROM concurrency_requests WHERE request_id=$id;";
        cmd.Parameters.AddWithValue("$id", requestId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)) : null;
    }

    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid id, string operation, Guid? grantId, string owner, string fingerprint, string status, DateTimeOffset at)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO concurrency_requests(request_id,operation,grant_id,owner_id,request_fingerprint,result_status,created_at_utc) VALUES($id,$operation,$grant,$owner,$fingerprint,$status,$at);";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$grant", grantId is null ? DBNull.Value : grantId.Value.ToString("D"));
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$at", Text(at));
        cmd.ExecuteNonQuery();
    }

    private static int Expire(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE concurrency_grants SET status='EXPIRED',updated_at_utc=$now WHERE status='ACTIVE' AND lease_until_utc<=$now;";
        cmd.Parameters.AddWithValue("$now", Text(now));
        return cmd.ExecuteNonQuery();
    }

    private static void RequireLive(ConcurrencyGrant grant, string owner, long generation, DateTimeOffset now)
    {
        if (grant.Status != ConcurrencyGrantStatus.Active || grant.LeaseUntilUtc <= now || grant.OwnerId != owner || grant.Generation != generation)
            throw new ConcurrencyLeaseException(grant.GrantId, owner);
    }

    private static void RequireSame(RequestRow row, string operation, string owner, string fingerprint, Guid? grantId)
    {
        if (row.Operation != operation || row.OwnerId != owner || row.Fingerprint != fingerprint || row.GrantId != grantId)
            throw new ConcurrencyLimitConflictException("Request ID was reused with different immutable content.");
    }

    private static IReadOnlyList<ConcurrencyScopeRequest> Normalize(IReadOnlyList<ConcurrencyScopeRequest> scopes) => scopes.OrderBy(s => s.ScopeType).ThenBy(s => s.ScopeKey, StringComparer.Ordinal).ToArray();
    private static Guid DeterministicGrantId(Guid requestId) { var bytes=requestId.ToByteArray(); bytes[2]^=0x47; bytes[13]^=0x74; return new Guid(bytes); }
    private static void ValidateLimit(ConcurrencyLimitDefinition d) { if(d is null||string.IsNullOrWhiteSpace(d.ScopeKey)||d.Capacity<=0||d.Version<=0||string.IsNullOrWhiteSpace(d.UpdatedBy)) throw new ArgumentException("Concurrency limit is invalid."); }
    private static void ValidateAcquire(ConcurrencyAcquireCommand c) { if(c is null||c.RequestId==Guid.Empty||string.IsNullOrWhiteSpace(c.OwnerId)||c.LeaseDuration<=TimeSpan.Zero||c.LeaseDuration>TimeSpan.FromHours(24)||c.Scopes is null||c.Scopes.Count==0||string.IsNullOrWhiteSpace(c.RequestFingerprint)||c.Scopes.Any(s=>string.IsNullOrWhiteSpace(s.ScopeKey)||s.Units<=0)||Normalize(c.Scopes).GroupBy(s=>new{s.ScopeType,s.ScopeKey}).Any(g=>g.Count()>1)) throw new ArgumentException("Concurrency acquire command is invalid."); }
    private static void ValidateControl(Guid requestId,Guid grantId,long generation,string owner,TimeSpan lease,string fingerprint) { if(requestId==Guid.Empty||grantId==Guid.Empty||generation<=0||string.IsNullOrWhiteSpace(owner)||lease<=TimeSpan.Zero||lease>TimeSpan.FromHours(24)||string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Concurrency control command is invalid."); }
    private static string ScopeText(ConcurrencyScopeType value)=>value switch{ConcurrencyScopeType.Global=>"GLOBAL",ConcurrencyScopeType.Provider=>"PROVIDER",ConcurrencyScopeType.ModelRole=>"MODEL_ROLE",ConcurrencyScopeType.Workflow=>"WORKFLOW",ConcurrencyScopeType.Project=>"PROJECT",ConcurrencyScopeType.ToolProfile=>"TOOL_PROFILE",_=>throw new ArgumentOutOfRangeException(nameof(value))};
    private static ConcurrencyScopeType ParseScope(string value)=>value switch{"GLOBAL"=>ConcurrencyScopeType.Global,"PROVIDER"=>ConcurrencyScopeType.Provider,"MODEL_ROLE"=>ConcurrencyScopeType.ModelRole,"WORKFLOW"=>ConcurrencyScopeType.Workflow,"PROJECT"=>ConcurrencyScopeType.Project,"TOOL_PROFILE"=>ConcurrencyScopeType.ToolProfile,_=>throw new InvalidOperationException("Unknown concurrency scope.")};
    private static ConcurrencyGrantStatus ParseStatus(string value)=>value switch{"ACTIVE"=>ConcurrencyGrantStatus.Active,"RELEASED"=>ConcurrencyGrantStatus.Released,"EXPIRED"=>ConcurrencyGrantStatus.Expired,_=>throw new InvalidOperationException("Unknown concurrency grant status.")};
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}
