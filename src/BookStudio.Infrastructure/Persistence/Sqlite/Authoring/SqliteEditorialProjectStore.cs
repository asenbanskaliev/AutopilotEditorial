using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteEditorialProjectStore : IEditorialProjectStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteEditorialProjectStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<ProjectCreateResult> CreateAsync(CreateEditorialProject command, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var byRequest = ReadByRequest(connection, tx, command.RequestId);
            if (byRequest is not null)
            {
                if (Matches(byRequest, command)) return new ProjectCreateResult(byRequest, true);
                throw new EditorialProjectConflictException("Create request ID was reused with different immutable content.");
            }
            var byProject = Read(connection, tx, command.WorkspaceId, command.ProjectId);
            if (byProject is not null)
            {
                if (Matches(byProject, command)) return new ProjectCreateResult(byProject, true);
                throw new EditorialProjectConflictException("Project identity already exists with different immutable content.");
            }

            var messageId = DeterministicMessageId(command.RequestId);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO editorial_projects(workspace_id,project_id,create_request_id,name,project_kind,language_tag,audience,objective,request_fingerprint,status,created_message_id,created_at_utc,updated_at_utc)
                    VALUES($workspace,$project,$request,$name,$kind,$language,$audience,$objective,$fingerprint,'ACTIVE',$message,$at,$at);
                    """;
                insert.Parameters.AddWithValue("$workspace", command.WorkspaceId.ToString("D"));
                insert.Parameters.AddWithValue("$project", command.ProjectId.ToString("D"));
                insert.Parameters.AddWithValue("$request", command.RequestId.ToString("D"));
                insert.Parameters.AddWithValue("$name", command.Name.Trim());
                insert.Parameters.AddWithValue("$kind", KindText(command.Kind));
                insert.Parameters.AddWithValue("$language", command.LanguageTag.Trim());
                insert.Parameters.AddWithValue("$audience", command.Audience.Trim());
                insert.Parameters.AddWithValue("$objective", command.Objective.Trim());
                insert.Parameters.AddWithValue("$fingerprint", command.RequestFingerprint);
                insert.Parameters.AddWithValue("$message", messageId.ToString("D"));
                insert.Parameters.AddWithValue("$at", Text(createdAtUtc));
                insert.ExecuteNonQuery();
            }
            using (var outbox = connection.CreateCommand())
            {
                outbox.Transaction = tx;
                outbox.CommandText = """
                    INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc)
                    VALUES($message,'editorial.project.created','1.0.0',$payload,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);
                    """;
                outbox.Parameters.AddWithValue("$message", messageId.ToString("D"));
                outbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { command.WorkspaceId, command.ProjectId, name = command.Name.Trim(), kind = command.Kind.ToString(), languageTag = command.LanguageTag.Trim() }));
                outbox.Parameters.AddWithValue("$at", Text(createdAtUtc));
                outbox.ExecuteNonQuery();
            }
            return new ProjectCreateResult(Require(connection, tx, command.WorkspaceId, command.ProjectId), false);
        }, cancellationToken);
    }

    public async ValueTask<EditorialProject?> GetAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(Read(connection, null, workspaceId, projectId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private static EditorialProject Require(SqliteConnection c, SqliteTransaction tx, Guid workspaceId, Guid projectId) => Read(c, tx, workspaceId, projectId) ?? throw new KeyNotFoundException("Project was not found.");
    private static EditorialProject? ReadByRequest(SqliteConnection c, SqliteTransaction tx, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_id,project_id,name,project_kind,language_tag,audience,objective,status,created_message_id,created_at_utc,updated_at_utc FROM editorial_projects WHERE create_request_id=$request;";
        cmd.Parameters.AddWithValue("$request", requestId.ToString("D"));
        return ReadRow(cmd);
    }
    private static EditorialProject? Read(SqliteConnection c, SqliteTransaction? tx, Guid workspaceId, Guid projectId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_id,project_id,name,project_kind,language_tag,audience,objective,status,created_message_id,created_at_utc,updated_at_utc FROM editorial_projects WHERE workspace_id=$workspace AND project_id=$project;";
        cmd.Parameters.AddWithValue("$workspace", workspaceId.ToString("D")); cmd.Parameters.AddWithValue("$project", projectId.ToString("D"));
        return ReadRow(cmd);
    }
    private static EditorialProject? ReadRow(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new(Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),r.GetString(2),ParseKind(r.GetString(3)),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)=="ACTIVE"?EditorialProjectStatus.Active:EditorialProjectStatus.Archived,Guid.Parse(r.GetString(8)),ParseTime(r.GetString(9)),ParseTime(r.GetString(10)));
    }
    private static bool Matches(EditorialProject project, CreateEditorialProject command) => project.WorkspaceId==command.WorkspaceId && project.ProjectId==command.ProjectId && project.Name==command.Name.Trim() && project.Kind==command.Kind && project.LanguageTag==command.LanguageTag.Trim() && project.Audience==command.Audience.Trim() && project.Objective==command.Objective.Trim();
    private static void Validate(CreateEditorialProject c) { if(c is null||c.RequestId==Guid.Empty||c.WorkspaceId==Guid.Empty||c.ProjectId==Guid.Empty||string.IsNullOrWhiteSpace(c.Name)||c.Name.Length>256||string.IsNullOrWhiteSpace(c.LanguageTag)||c.LanguageTag.Length>32||string.IsNullOrWhiteSpace(c.Audience)||c.Audience.Length>2048||string.IsNullOrWhiteSpace(c.Objective)||c.Objective.Length>8192||string.IsNullOrWhiteSpace(c.RequestFingerprint)||c.RequestFingerprint.Length>256) throw new ArgumentException("Editorial project command is invalid."); }
    private static Guid DeterministicMessageId(Guid id) { var bytes=id.ToByteArray(); bytes[1]^=0x50; bytes[14]^=0x05; return new Guid(bytes); }
    private static string KindText(EditorialProjectKind value)=>value switch{EditorialProjectKind.Fiction=>"FICTION",EditorialProjectKind.NonFiction=>"NON_FICTION",EditorialProjectKind.Technical=>"TECHNICAL",EditorialProjectKind.Educational=>"EDUCATIONAL",EditorialProjectKind.Other=>"OTHER",_=>throw new ArgumentOutOfRangeException(nameof(value))};
    private static EditorialProjectKind ParseKind(string value)=>value switch{"FICTION"=>EditorialProjectKind.Fiction,"NON_FICTION"=>EditorialProjectKind.NonFiction,"TECHNICAL"=>EditorialProjectKind.Technical,"EDUCATIONAL"=>EditorialProjectKind.Educational,"OTHER"=>EditorialProjectKind.Other,_=>throw new InvalidOperationException("Unknown project kind.")};
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}
