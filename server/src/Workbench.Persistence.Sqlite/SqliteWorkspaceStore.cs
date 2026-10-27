using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Persistence.Sqlite;

/// <summary>
/// The local demonstrator's workspace store (D04): one SQLite file, one generic pair of tables for every domain.
/// <c>Workspace</c> holds each workspace's current state; <c>EditLog</c> is append-only and holds every
/// domain's edits, told apart by <c>domain_kind</c>. Domain payloads are JSON; the store never reads them.
/// </summary>
public sealed class SqliteWorkspaceStore : IWorkspaceStore
{
    // The store's own options: nulls are written, because an empty value and an absent one differ here.
    static readonly JsonSerializerOptions Json = CreateJson();

    readonly SqliteFile file;
    readonly IReadOnlyDictionary<string, SourceResolver> sources;

    /// <param name="path">The database file. It is created with its tables when missing.</param>
    /// <param name="sources">For each workspace app, how its source versions are acquired again on load.</param>
    public SqliteWorkspaceStore(string path, IReadOnlyDictionary<string, SourceResolver> sources)
    {
        file = new SqliteFile(path);
        this.sources = sources;
        using var connection = file.Open();
        SqliteFile.Execute(connection, Schema);
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS Workspace (
            workspace_id TEXT PRIMARY KEY,
            owner_id TEXT NOT NULL,
            app TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            context_json TEXT NOT NULL CHECK (json_valid(context_json)),
            source_json TEXT NOT NULL CHECK (json_valid(source_json)),
            state_json TEXT NOT NULL CHECK (json_valid(state_json)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            deleted_at TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_workspace_owner ON Workspace(owner_id, deleted_at, updated_at);
        CREATE TABLE IF NOT EXISTS EditLog (
            workspace_id TEXT NOT NULL REFERENCES Workspace(workspace_id),
            seq INTEGER NOT NULL CHECK (seq > 0),
            at TEXT NOT NULL,
            user_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            domain_kind TEXT,
            scope TEXT,
            entity TEXT,
            field TEXT,
            value_json TEXT CHECK (value_json IS NULL OR json_valid(value_json)),
            note TEXT,
            PRIMARY KEY (workspace_id, seq)
        );
        CREATE INDEX IF NOT EXISTS ix_editlog_domain ON EditLog(domain_kind, at);
        """;

    public void Insert(WorkspaceState state)
    {
        using var connection = file.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Workspace (workspace_id, owner_id, app, revision, context_json, source_json, state_json, created_at, updated_at)
                VALUES ($id, $owner, $app, $revision, $context, $source, $state, $at, $at)
                """;
            var at = state.Log.Count > 0 ? state.Log[0].At : throw new InvalidOperationException("A new workspace has its first entries.");
            Bind(command, state, at);
            command.Parameters.AddWithValue("$owner", state.Owner);
            command.Parameters.AddWithValue("$app", state.App);
            command.ExecuteNonQuery();
        }
        Append(connection, state.WorkspaceId, state.Log);
        transaction.Commit();
    }

    public void Commit(WorkspaceState previous, WorkspaceState next, DateTimeOffset at)
    {
        using var connection = file.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE Workspace SET revision = $revision, context_json = $context, source_json = $source, state_json = $state, updated_at = $at
                WHERE workspace_id = $id AND revision = $previous AND deleted_at IS NULL
                """;
            Bind(command, next, at);
            command.Parameters.AddWithValue("$previous", previous.Revision);
            if (command.ExecuteNonQuery() != 1)
                throw new WorkspaceRefused("RevisionMoved", $"Workspace {previous.WorkspaceId} is no longer at revision {previous.Revision}.");
        }
        Append(connection, next.WorkspaceId, next.Log.Where(e => e.Seq > previous.Revision));
        transaction.Commit();
    }

    public WorkspaceState? Load(string workspaceId)
    {
        using var connection = file.Open();
        string owner, app, contextJson, sourceJson, stateJson;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT owner_id, app, context_json, source_json, state_json FROM Workspace
                WHERE workspace_id = $id AND deleted_at IS NULL
                """;
            command.Parameters.AddWithValue("$id", workspaceId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            (owner, app, contextJson, sourceJson, stateJson) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }

        var stored = JsonNode.Parse(sourceJson)!.AsObject();
        var version = stored["version"]!.GetValue<string>();
        var resolve = sources.GetValueOrDefault(app) ?? throw new InvalidOperationException($"No source resolver is registered for {app}.");
        var source = resolve(stored["descriptor"]!.AsObject());
        // A source that answers another version would put the overrides on data the user never saw.
        if (source.Version != version)
            throw new InvalidOperationException($"Workspace {workspaceId} was on source {version}; its descriptor now acquires {source.Version}.");

        var state = JsonSerializer.Deserialize<StoredState>(stateJson, Json) ?? throw new InvalidOperationException($"Workspace {workspaceId} has no state.");
        return new WorkspaceState(workspaceId, owner, app, JsonNode.Parse(contextJson)!.AsObject(), source,
            state.Overrides.ToImmutableDictionary(o => o.Target),
            state.Conflicts.ToImmutableDictionary(c => c.Override.Target),
            state.Lost.ToImmutableDictionary(l => l.Override.Target),
            [.. ReadLog(connection, workspaceId)])
        {
            Created = (state.Created ?? []).ToImmutableDictionary(c => c.Entity),
        };
    }

    public IReadOnlyList<string> OwnedBy(string owner)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id FROM Workspace WHERE owner_id = $owner AND deleted_at IS NULL ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public void Delete(string workspaceId, DateTimeOffset at)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Workspace SET deleted_at = $at WHERE workspace_id = $id AND deleted_at IS NULL";
        command.Parameters.AddWithValue("$id", workspaceId);
        command.Parameters.AddWithValue("$at", SqliteFile.Text(at));
        if (command.ExecuteNonQuery() != 1) throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");
    }

    /// <summary>The log of one workspace, deleted or not, as it was appended.</summary>
    public IReadOnlyList<EditEntry> Log(string workspaceId)
    {
        using var connection = file.Open();
        return [.. ReadLog(connection, workspaceId)];
    }

    // Created is absent from states stored before ADR-0004, which created nothing.
    sealed record StoredState(IReadOnlyList<Override> Overrides, IReadOnlyList<Conflict> Conflicts, IReadOnlyList<LostTarget> Lost,
        IReadOnlyList<CreatedEntity>? Created = null);

    static void Bind(SqliteCommand command, WorkspaceState state, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("$id", state.WorkspaceId);
        command.Parameters.AddWithValue("$revision", state.Revision);
        command.Parameters.AddWithValue("$context", state.Context.ToJsonString());
        command.Parameters.AddWithValue("$source", new JsonObject { ["version"] = state.Source.Version, ["descriptor"] = state.Source.Descriptor.DeepClone() }.ToJsonString());
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(new StoredState(
            [.. state.Overrides.Values.OrderBy(o => o.Seq)],
            [.. state.Conflicts.Values.OrderBy(c => c.Override.Seq)],
            [.. state.Lost.Values.OrderBy(l => l.Override.Seq)],
            [.. state.Created.Values.OrderBy(c => c.Seq)]), Json));
        command.Parameters.AddWithValue("$at", SqliteFile.Text(at));
    }

    static void Append(SqliteConnection connection, string workspaceId, IEnumerable<EditEntry> entries)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO EditLog (workspace_id, seq, at, user_id, kind, domain_kind, scope, entity, field, value_json, note)
            VALUES ($id, $seq, $at, $user, $kind, $domain, $scope, $entity, $field, $value, $note)
            """;
        var parameters = new[] { "$id", "$seq", "$at", "$user", "$kind", "$domain", "$scope", "$entity", "$field", "$value", "$note" }
            .ToDictionary(n => n, n => command.Parameters.Add(n, SqliteType.Text));
        foreach (var e in entries)
        {
            parameters["$id"].Value = workspaceId;
            parameters["$seq"].Value = e.Seq;
            parameters["$at"].Value = SqliteFile.Text(e.At);
            parameters["$user"].Value = e.User;
            parameters["$kind"].Value = e.Kind.ToString();
            parameters["$domain"].Value = (object?)e.DomainKind ?? DBNull.Value;
            parameters["$scope"].Value = (object?)e.Target?.Scope ?? DBNull.Value;
            parameters["$entity"].Value = (object?)e.Target?.Entity ?? DBNull.Value;
            parameters["$field"].Value = (object?)e.Target?.Field ?? DBNull.Value;
            // A value set to empty is the JSON literal null; a NULL column means the entry carries no value.
            parameters["$value"].Value = e.Kind is EditKind.Set or EditKind.KeepOverride or EditKind.Create || e.Value is not null
                ? (e.Value?.ToJsonString() ?? "null") : DBNull.Value;
            parameters["$note"].Value = (object?)e.Note ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    static IEnumerable<EditEntry> ReadLog(SqliteConnection connection, string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, at, user_id, kind, domain_kind, scope, entity, field, value_json, note
            FROM EditLog WHERE workspace_id = $id ORDER BY seq
            """;
        command.Parameters.AddWithValue("$id", workspaceId);
        using var reader = command.ExecuteReader();
        var entries = new List<EditEntry>();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var target = Text(5) is { } scope ? new EditTarget(scope, reader.GetString(6), reader.GetString(7)) : null;
            entries.Add(new EditEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                Enum.Parse<EditKind>(reader.GetString(3)),
                Text(4),
                target,
                Text(8) is { } json ? JsonNode.Parse(json) : null,
                Text(9)));
        }
        return entries;
    }

    static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
