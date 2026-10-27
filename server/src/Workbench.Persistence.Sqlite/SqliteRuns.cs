using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Persistence.Sqlite;

/// <summary>
/// Runs and retained values in the same SQLite file as the workspaces (D04, C35). A Run is one generic row for
/// every domain; its record is JSON, and the columns the history filters on are kept beside it.
/// </summary>
public sealed class SqliteRunStore : IRunStore, IDataStore
{
    // The wire vocabulary, with nulls written: an absent error and an empty one are both stored as they are.
    static readonly JsonSerializerOptions Json = new(WireJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    readonly SqliteFile file;

    public SqliteRunStore(string path)
    {
        file = new SqliteFile(path);
        using var connection = file.Open();
        SqliteFile.Execute(connection, """
            CREATE TABLE IF NOT EXISTS AnalysisRun (
                run_id TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL,
                origin TEXT NOT NULL,
                status TEXT NOT NULL,
                accepted_at TEXT NOT NULL,
                completed_at TEXT,
                workspace_id TEXT,
                workspace_revision INTEGER,
                parent_run_id TEXT,
                analysis_group_id TEXT,
                conversation_id TEXT,
                result_contracts TEXT NOT NULL,
                record_json TEXT NOT NULL CHECK (json_valid(record_json))
            );
            CREATE INDEX IF NOT EXISTS ix_run_owner ON AnalysisRun(owner_id, accepted_at);
            CREATE INDEX IF NOT EXISTS ix_run_accepted ON AnalysisRun(accepted_at);
            CREATE INDEX IF NOT EXISTS ix_run_workspace ON AnalysisRun(workspace_id);
            CREATE INDEX IF NOT EXISTS ix_run_group ON AnalysisRun(analysis_group_id);
            CREATE TABLE IF NOT EXISTS SubmissionKey (
                owner_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                run_id TEXT NOT NULL REFERENCES AnalysisRun(run_id),
                digest TEXT NOT NULL,
                PRIMARY KEY (owner_id, idempotency_key)
            );
            CREATE TABLE IF NOT EXISTS RetainedData (
                data_id TEXT NOT NULL,
                revision TEXT NOT NULL,
                contract_id TEXT NOT NULL,
                contract_version TEXT NOT NULL,
                content_json TEXT NOT NULL CHECK (json_valid(content_json)),
                expired INTEGER NOT NULL DEFAULT 0 CHECK (expired IN (0, 1)),
                PRIMARY KEY (data_id, revision)
            );
            """);
    }

    public bool Accept(RunRecord run, string idempotencyKey, string digest)
    {
        using var connection = file.Open();
        using var transaction = connection.BeginTransaction();
        using (var key = connection.CreateCommand())
        {
            key.CommandText = "SELECT 1 FROM SubmissionKey WHERE owner_id = $owner AND idempotency_key = $key";
            key.Parameters.AddWithValue("$owner", run.Owner);
            key.Parameters.AddWithValue("$key", idempotencyKey);
            if (key.ExecuteScalar() is not null) return false;
        }
        Upsert(connection, run);
        using (var key = connection.CreateCommand())
        {
            key.CommandText = "INSERT INTO SubmissionKey (owner_id, idempotency_key, run_id, digest) VALUES ($owner, $key, $run, $digest)";
            key.Parameters.AddWithValue("$owner", run.Owner);
            key.Parameters.AddWithValue("$key", idempotencyKey);
            key.Parameters.AddWithValue("$run", run.RunId);
            key.Parameters.AddWithValue("$digest", digest);
            key.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public void Complete(RunRecord run)
    {
        using var connection = file.Open();
        Upsert(connection, run);
    }

    public RunRecord? Find(string runId)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json FROM AnalysisRun WHERE run_id = $id";
        command.Parameters.AddWithValue("$id", runId);
        return command.ExecuteScalar() is string json ? Read(json) : null;
    }

    public (string RunId, string Digest)? FindKey(string owner, string idempotencyKey)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id, digest FROM SubmissionKey WHERE owner_id = $owner AND idempotency_key = $key";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public (IReadOnlyList<RunRecord> Runs, int Total) List(RunQuery query)
    {
        using var connection = file.Open();
        var conditions = new List<string>();
        if (query.Owner is not null) conditions.Add("owner_id = $owner");
        if (query.WorkspaceId is not null) conditions.Add("workspace_id = $workspace");
        // Stored space-separated with a space at each end, so a contract matches only as a whole word.
        if (query.ResultContracts is { } contracts)
            conditions.Add("(" + (contracts.Count == 0 ? "0" : string.Join(" OR ", contracts.Select((_, i) => $"instr(result_contracts, $contract{i}) > 0"))) + ")");
        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        void Bind(SqliteCommand c)
        {
            if (query.Owner is not null) c.Parameters.AddWithValue("$owner", query.Owner);
            if (query.WorkspaceId is not null) c.Parameters.AddWithValue("$workspace", query.WorkspaceId);
            for (var i = 0; i < (query.ResultContracts?.Count ?? 0); i++) c.Parameters.AddWithValue($"$contract{i}", $" {query.ResultContracts![i]} ");
        }
        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT count(*) FROM AnalysisRun {where}";
            Bind(count);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT record_json FROM AnalysisRun {where} ORDER BY accepted_at DESC, run_id DESC LIMIT $count OFFSET $start";
        Bind(command);
        command.Parameters.AddWithValue("$count", query.Count);
        command.Parameters.AddWithValue("$start", query.Start);
        using var reader = command.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read()) runs.Add(Read(reader.GetString(0)));
        return (runs, total);
    }

    public IReadOnlyList<RunRecord> Unfinished()
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json FROM AnalysisRun WHERE status = $running";
        command.Parameters.AddWithValue("$running", nameof(RunStatus.Running));
        using var reader = command.ExecuteReader();
        var runs = new List<RunRecord>();
        while (reader.Read()) runs.Add(Read(reader.GetString(0)));
        return runs;
    }

    public TypedDataRef Put(string dataId, string contractId, string contractVersion, JsonNode content)
    {
        var (reference, json) = RetainedData.Prepare(dataId, contractId, contractVersion, content);
        using var connection = file.Open();
        using var transaction = connection.BeginTransaction();
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT contract_id, contract_version FROM RetainedData WHERE data_id = $id AND revision = $revision";
            existing.Parameters.AddWithValue("$id", dataId);
            existing.Parameters.AddWithValue("$revision", reference.Revision);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != contractId || reader.GetString(1) != contractVersion)
                    throw new InvalidOperationException($"Data {dataId}@{reference.Revision} is already retained under another contract.");
                return reference;
            }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO RetainedData (data_id, revision, contract_id, contract_version, content_json)
                VALUES ($id, $revision, $contract, $version, $json)
                """;
            insert.Parameters.AddWithValue("$id", dataId);
            insert.Parameters.AddWithValue("$revision", reference.Revision);
            insert.Parameters.AddWithValue("$contract", contractId);
            insert.Parameters.AddWithValue("$version", contractVersion);
            insert.Parameters.AddWithValue("$json", json);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return reference;
    }

    public DataLookup Read(TypedDataRef reference)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT contract_id, contract_version, content_json, expired FROM RetainedData WHERE data_id = $id AND revision = $revision";
        command.Parameters.AddWithValue("$id", reference.DataId);
        command.Parameters.AddWithValue("$revision", reference.Revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != reference.ContractId || reader.GetString(1) != reference.ContractVersion)
            return new DataLookup.Unknown(reference);
        if (reader.GetInt32(3) == 1) return new DataLookup.Expired(reference);
        return new DataLookup.Found(JsonNode.Parse(reader.GetString(2)) ?? throw new InvalidOperationException("Retained JSON is null."));
    }

    public void Expire(TypedDataRef reference)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RetainedData SET expired = 1 WHERE data_id = $id AND revision = $revision";
        command.Parameters.AddWithValue("$id", reference.DataId);
        command.Parameters.AddWithValue("$revision", reference.Revision);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Cannot expire unknown data {reference.DataId}@{reference.Revision}.");
    }

    static void Upsert(SqliteConnection connection, RunRecord run)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AnalysisRun (run_id, owner_id, origin, status, accepted_at, completed_at, workspace_id, workspace_revision,
                parent_run_id, analysis_group_id, conversation_id, result_contracts, record_json)
            VALUES ($id, $owner, $origin, $status, $accepted, $completed, $workspace, $revision, $parent, $group, $conversation, $contracts, $json)
            ON CONFLICT (run_id) DO UPDATE SET status = excluded.status, completed_at = excluded.completed_at,
                record_json = excluded.record_json
            """;
        command.Parameters.AddWithValue("$id", run.RunId);
        command.Parameters.AddWithValue("$owner", run.Owner);
        command.Parameters.AddWithValue("$origin", run.Origin.ToString());
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$accepted", SqliteFile.Text(run.AcceptedAt));
        command.Parameters.AddWithValue("$completed", run.CompletedAt is { } done ? SqliteFile.Text(done) : DBNull.Value);
        command.Parameters.AddWithValue("$workspace", (object?)run.Relations.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)run.Relations.WorkspaceRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent", (object?)run.Relations.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$group", (object?)run.Relations.AnalysisGroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversation", (object?)run.Relations.ConversationId ?? DBNull.Value);
        // The result contracts tell a domain's history view which Runs are its own (docs/05 §7).
        command.Parameters.AddWithValue("$contracts", $" {string.Join(" ", run.ResultContracts)} ");
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(run, Json));
        command.ExecuteNonQuery();
    }

    static RunRecord Read(string json) =>
        JsonSerializer.Deserialize<RunRecord>(json, Json) ?? throw new InvalidOperationException("A stored Run is null.");
}

/// <summary>Connections to one SQLite file, opened per operation with the same pragmas.</summary>
sealed class SqliteFile(string path)
{
    readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string Text(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
