using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Persistence.Sqlite;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// ADR-0003 §6 on the SQLite store (D04, C35): every edit is on disk before it is answered.
public sealed class SqliteWorkspaceStoreTests : IDisposable
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    readonly string path = Path.Combine(Path.GetTempPath(), $"workbench-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    SqliteWorkspaceStore Store() =>
        new(path, new Dictionary<string, SourceResolver> { [XvaEditRules.App] = XvaSourceVersion.Resolve });

    WorkspaceService Service(IWorkspaceStore store) =>
        new(new Dictionary<string, IEditRules> { [XvaEditRules.App] = new XvaEditRules() }, TimeProvider.System, store);

    static JsonObject Context(DateOnly valueDate) => new() { ["valueDate"] = valueDate.ToString("yyyy-MM-dd"), ["registration"] = "ABC/LE-01" };

    static WorkspaceState Set(WorkspaceService service, WorkspaceState ws, string entity, string field, JsonNode? value) =>
        service.Set(ws.WorkspaceId, ws.Revision, "akira", XvaEditRules.Target((XvaSourceVersion)ws.Source, entity, field), value);

    static string Picture(WorkspaceState ws) => new JsonObject
    {
        ["revision"] = ws.Revision,
        ["source"] = ws.Source.Version,
        ["context"] = ws.Context.DeepClone(),
        ["rows"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(XvaMaterialiser.Trades(ws))),
        ["conflicts"] = new JsonArray([.. ws.Conflicts.Keys.Select(k => JsonValue.Create($"{k.Entity}.{k.Field}"))]),
        ["lost"] = new JsonArray([.. ws.Lost.Select(l => JsonValue.Create($"{l.Key.Entity}.{l.Key.Field}:{l.Value.Acknowledged}"))]),
        ["log"] = new JsonArray([.. ws.Log.Select(e => JsonValue.Create($"{e.Seq}:{e.Kind}:{e.DomainKind}:{e.Target}:{e.Value?.ToJsonString()}"))]),
    }.ToJsonString();

    [Fact] // ADR-0003 §6: the workspace survives a restart as it stood, held conflicts and lost edits included
    public void A_workspace_is_restored_after_a_restart()
    {
        var service = Service(Store());
        var ws = service.Create("akira", XvaEditRules.App, Context(Day1), new XvaSourceVersion("ABC/LE-01", Day1, 40));
        var later = new XvaSourceVersion("ABC/LE-01", Day1.AddYears(1), 40);
        var matured = ((XvaSourceVersion)ws.Source).Existing.First(t => later.Trade(t.TradeId) is null);
        ws = Set(service, ws, "trade:DRAFT-1", "csa", "CSA-B");
        ws = Set(service, ws, "csa:CSA-B", "curve", "eur_estr");
        ws = Set(service, ws, "credit:ABC/LE-01", "cvaRecovery", null);
        ws = Set(service, ws, $"trade:{matured.TradeId}", "notional", 5m);
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", Context(Day1.AddYears(1)), later).State;

        var restored = Service(Store()).Find(ws.WorkspaceId);

        Assert.NotNull(restored);
        Assert.Equal(Picture(ws), Picture(restored));
        Assert.Equal("eur_estr", XvaMaterialiser.Trades(restored).Single(t => t.TradeId == "DRAFT-1").Curve);
    }

    [Fact] // Spine 6: two servers on one file cannot both commit against the same revision
    public void A_second_writer_on_a_moved_revision_is_refused()
    {
        var first = Service(Store());
        var ws = first.Create("akira", XvaEditRules.App, Context(Day1), new XvaSourceVersion("ABC/LE-01", Day1, 5));
        var second = Service(Store());
        var seenBySecond = second.Find(ws.WorkspaceId)!;
        Set(first, ws, "trade:DRAFT-1", "notional", 1m);

        var refused = Assert.Throws<WorkspaceRefused>(() => Set(second, seenBySecond, "trade:DRAFT-2", "notional", 2m));

        Assert.Equal("RevisionMoved", refused.Code);
        Assert.Equal(3, Service(Store()).Find(ws.WorkspaceId)!.Revision);
    }

    [Fact] // ADR-0003 §6, C33: a deleted workspace is gone from use, and its log still explains what was priced
    public void Deleting_keeps_the_log()
    {
        var store = Store();
        var service = Service(store);
        var ws = service.Create("akira", XvaEditRules.App, Context(Day1), new XvaSourceVersion("ABC/LE-01", Day1, 5));
        ws = Set(service, ws, "trade:DRAFT-1", "notional", 1m);

        service.Delete(ws.WorkspaceId, "akira");

        Assert.Null(Service(Store()).Find(ws.WorkspaceId));
        Assert.Empty(store.OwnedBy("akira"));
        Assert.Equal(["Context", "Acquire", "Set"], store.Log(ws.WorkspaceId).Select(e => e.Kind.ToString()));
        Assert.Equal("xva.trade", store.Log(ws.WorkspaceId)[^1].DomainKind);
    }

    [Fact] // docs/03: an edit to empty is kept as empty, not as "no edit"
    public void An_edit_to_empty_survives_as_empty()
    {
        var service = Service(Store());
        var ws = service.Create("akira", XvaEditRules.App, Context(Day1), new XvaSourceVersion("ABC/LE-02", Day1, 5));
        ws = Set(service, ws, "credit:ABC/LE-02", "cvaRecovery", null);

        var restored = Service(Store()).Find(ws.WorkspaceId)!;

        Assert.Equal((null, Ownership.User), (XvaMaterialiser.Credit(restored).CvaRecovery, XvaMaterialiser.Credit(restored).CvaRecoveryOwner));
    }
}
