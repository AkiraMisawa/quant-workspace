using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Persistence.Sqlite;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// docs/05 on the SQLite store (D04, C35): Runs, their keys and their results outlive the process.
public sealed class SqliteRunStoreTests : IDisposable
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    readonly string path = Path.Combine(Path.GetTempPath(), $"workbench-runs-{Guid.NewGuid():N}.db");
    readonly CompositionRegistry registry = new([new XvaModule()]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    (WorkspaceService Workspaces, SqliteRunStore Store, RunService Runs) Process()
    {
        var store = new SqliteRunStore(path);
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [XvaEditRules.App] = new XvaEditRules() }, TimeProvider.System,
            new SqliteWorkspaceStore(path, new Dictionary<string, SourceResolver> { [XvaEditRules.App] = XvaSourceVersion.Resolve }));
        return (workspaces, store, new RunService(registry, store, TimeProvider.System, store));
    }

    [Fact] // docs/05 §7: a priced Run, its links and its result are read back after a restart
    public async Task A_run_and_its_result_survive_a_restart()
    {
        var (workspaces, store, runs) = Process();
        var ws = workspaces.Create("akira", XvaEditRules.App, new JsonObject { ["valueDate"] = "2026-10-26" }, new XvaSourceVersion("ABC/LE-01", Day1, 20));
        var accepted = Assert.IsType<SubmissionResult.Accepted>(runs.Submit(XvaPrice.Submission(workspaces, store, ws.WorkspaceId, ws.Revision, "k1", "akira")));
        await runs.Completion(accepted.Run.RunId);

        var (_, restartedStore, restarted) = Process();
        var run = restarted.Find(accepted.Run.RunId)!;

        Assert.Equal((RunStatus.Succeeded, RunOrigin.Pricer, ws.WorkspaceId, ws.Revision), (run.Status, run.Origin, run.Relations.WorkspaceId, run.Relations.WorkspaceRevision));
        Assert.IsType<DataLookup.Found>(restartedStore.Read(run.Outputs["result"]));
        Assert.Equal([run.RunId], restarted.List(new RunQuery("akira", 0, 10, ws.WorkspaceId)).Runs.Select(r => r.RunId));
    }

    [Fact] // docs/05 §8.2: a retried key after a restart finds the same Run; another payload under it is refused
    public async Task A_retried_key_finds_its_run_after_a_restart()
    {
        var (workspaces, store, runs) = Process();
        var ws = workspaces.Create("akira", XvaEditRules.App, new JsonObject { ["valueDate"] = "2026-10-26" }, new XvaSourceVersion("ABC/LE-01", Day1, 20));
        var submission = XvaPrice.Submission(workspaces, store, ws.WorkspaceId, ws.Revision, "k1", "akira");
        var first = Assert.IsType<SubmissionResult.Accepted>(runs.Submit(submission));
        await runs.Completion(first.Run.RunId);

        var (_, _, restarted) = Process();

        Assert.Equal(first.Run.RunId, Assert.IsType<SubmissionResult.Accepted>(restarted.Submit(submission)).Run.RunId);
        var other = submission with { Relations = submission.Relations with { AnalysisGroupId = "study-1" } };
        Assert.Equal("IdempotencyConflict", Assert.IsType<SubmissionResult.Rejected>(restarted.Submit(other)).Code);
    }

    [Fact] // Spine 1: a Run the stopped process left running is not shown as still running, nor as a result
    public void A_run_left_running_is_recorded_as_interrupted()
    {
        var store = new SqliteRunStore(path);
        var workflow = XvaModule.Workflow(new TypedDataRef("x", XvaContracts.AcceptedInput, XvaContracts.Version, "0"));
        store.Accept(new RunRecord("run-left", workflow, new JsonObject(), new JsonObject(), RunStatus.Running, [], new Dictionary<string, TypedDataRef>(),
            null, DateTimeOffset.UtcNow, null, "akira", RunOrigin.Ai, new RunRelations(ConversationId: "c1"), [XvaContracts.CaseResult]), "k", "d");

        var (_, _, restarted) = Process();
        var run = restarted.Find("run-left")!;

        Assert.Equal((RunStatus.Failed, "Interrupted"), (run.Status, run.Error!.Code));
        Assert.Empty(run.Outputs);
    }
}
