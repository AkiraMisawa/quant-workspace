using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Persistence.Sqlite;

namespace Workbench.Composition.Tests;

// ADR-0004 on the generic workspace, with a toy module: an item the user creates has a count and a size, and the
// module derives that many parts of that size. Nothing here is financial.
public sealed class DerivedWorkspaceTests : IDisposable
{
    const string App = "parts";
    readonly string path = Path.Combine(Path.GetTempPath(), $"workbench-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    sealed class NoSource : ISourceVersion
    {
        public string Version => "none";
        public JsonObject Descriptor => [];
        public SourceValue? Read(string entity, string field) => null;
    }

    sealed class Parts(int count, JsonNode? size, string version) : ISourceVersion
    {
        public string Version => version;
        public JsonObject Descriptor => [];
        public SourceValue? Read(string entity, string field) =>
            field == "size" && entity.Split("/p") is [_, var k] && int.Parse(k) is var n && n >= 1 && n <= count ? new SourceValue(size?.DeepClone()) : null;
    }

    sealed class PartRules : IEditRules
    {
        public string KindOf(EditTarget target) => target.Scope.StartsWith("parts:", StringComparison.Ordinal) ? "parts.part" : "parts.item";
        public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];

        public IReadOnlyDictionary<string, ISourceVersion> Derive(WorkspaceState state) =>
            state.Created.Values.ToDictionary(c => $"parts:{c.Entity}", c =>
            {
                JsonNode? Effective(string field) =>
                    state.Overrides.TryGetValue(new EditTarget(c.Scope, c.Entity, field), out var o) ? o.Value : c.Fields[field];
                var (count, size) = (Effective("count")!.GetValue<int>(), Effective("size"));
                return (ISourceVersion)new Parts(count, size, $"{count}:{size?.ToJsonString()}");
            });
    }

    static readonly IReadOnlyDictionary<string, IEditRules> Rules = new Dictionary<string, IEditRules> { [App] = new PartRules() };

    WorkspaceService Service(IWorkspaceStore? store = null) => new(Rules, TimeProvider.System, store);

    SqliteWorkspaceStore Store() => new(path, new Dictionary<string, SourceResolver> { [App] = _ => new NoSource() });

    static EditTarget Item(string field) => new("book", "item-1", field);
    static EditTarget Part(int k) => new("parts:item-1", $"item-1/p{k}", "size");

    static WorkspaceState WithItem(WorkspaceService service, int count = 3)
    {
        var ws = service.Create("akira", App, new JsonObject { ["valueDate"] = "2026-10-27" }, new NoSource());
        return service.Create(ws.WorkspaceId, ws.Revision, "akira", "book", "item-1", new JsonObject { ["count"] = count, ["size"] = "M" });
    }

    [Fact] // ADR-0004: a created entity takes edits like an acquired one, its Create values as the base
    public void A_created_entity_takes_overrides_against_its_initial_values()
    {
        var service = Service();
        var ws = WithItem(service);

        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("size"), "L");

        Assert.Equal("M", ws.Overrides[Item("size")].Base["size"]!.GetValue<string>());
        Assert.StartsWith("created:", ws.Overrides[Item("size")].BaseVersion);
        Assert.Throws<WorkspaceRefused>(() => service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("colour"), "red"));
    }

    [Fact] // ADR-0004: derived edits carry when their part stays, conflict when its base moves, and are lost when it goes
    public void A_derived_edit_is_checked_when_its_scope_is_derived_again()
    {
        var service = Service();
        var ws = WithItem(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(2), "S");
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(3), "XS");

        var more = service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("count"), 4);
        Assert.Equal(new CarrySummary(2, 0, 0), more.Rederived);

        var fewer = service.Set(more.WorkspaceId, more.Revision, "akira", Item("count"), 2);
        Assert.Equal(new CarrySummary(1, 0, 1), fewer.Rederived);
        Assert.True(fewer.Lost.ContainsKey(Part(3)));

        var resized = service.Set(fewer.WorkspaceId, fewer.Revision, "akira", Item("size"), "L");
        // Part 3's edit is still checked, and still has no target.
        Assert.Equal(new CarrySummary(0, 1, 1), resized.Rederived);
        Assert.True(resized.Conflicts.ContainsKey(Part(2)));
        Assert.Contains(resized.Log, e => e.Kind == EditKind.Derive && e.Value!["scope"]!.GetValue<string>() == "parts:item-1");
    }

    [Fact] // ADR-0004: a command that moves no derived scope reports nothing derived
    public void An_unrelated_edit_derives_nothing_new()
    {
        var service = Service();
        var ws = WithItem(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(1), "S");

        var again = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(1), "XL");

        Assert.Null(again.Rederived);
        Assert.Equal("XL", again.Overrides[Part(1)].Value!.GetValue<string>());
    }

    [Fact] // ADR-0004: removing a created entity ends its edits without asking the user to acknowledge them
    public void Removing_an_entity_ends_its_edits_and_loses_what_was_derived()
    {
        var service = Service();
        var ws = WithItem(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("size"), "L");
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(1), "S");

        ws = service.Remove(ws.WorkspaceId, ws.Revision, "akira", "item-1");

        Assert.Empty(ws.Created);
        Assert.Empty(ws.Overrides);
        Assert.False(ws.Lost.ContainsKey(Item("size")));
        Assert.True(ws.Lost.ContainsKey(Part(1)));
        Assert.Contains(ws.Log, e => e.Kind == EditKind.Remove && e.Note == "1 edits");
    }

    [Fact] // ADR-0004 with ADR-0003 §6: dropping a scope counts the entities created in it, so none goes unseen
    public void Dropping_a_scope_counts_the_entities_created_in_it()
    {
        var service = Service();
        var ws = WithItem(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("count"), 5);

        Assert.Throws<WorkspaceRefused>(() => service.DropScope(ws.WorkspaceId, ws.Revision, "akira", "book", 1));
        ws = service.DropScope(ws.WorkspaceId, ws.Revision, "akira", "book", 2);

        Assert.Empty(ws.Created);
        Assert.Empty(ws.Derived);
    }

    [Fact] // ADR-0004 with ADR-0003 §6: created entities are stored, and derived sources come back by deriving again
    public void Created_and_derived_state_survives_a_restart()
    {
        var service = Service(Store());
        var ws = WithItem(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Part(2), "S");
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Item("count"), 1);

        var restored = Service(Store()).Find(ws.WorkspaceId)!;

        Assert.Equal(ws.Created.Keys, restored.Created.Keys);
        Assert.Equal(ws.Derived["parts:item-1"].Version, restored.Derived["parts:item-1"].Version);
        Assert.True(restored.Lost.ContainsKey(Part(2)));
        Assert.Equal(ws.Log.Select(e => (e.Seq, e.Kind)), restored.Log.Select(e => (e.Seq, e.Kind)));
        var next = Service(Store()).Set(ws.WorkspaceId, ws.Revision, "akira", Part(1), "XS");
        Assert.Equal("XS", next.Overrides[Part(1)].Value!.GetValue<string>());
    }
}
