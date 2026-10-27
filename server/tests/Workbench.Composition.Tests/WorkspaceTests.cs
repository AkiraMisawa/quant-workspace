using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Composition.Tests;

// ADR-0003 §6 and §7 on the generic workspace. Entities, fields and scopes here are illustrative.
public class WorkspaceTests
{
    sealed class Source(string version, Dictionary<(string, string), JsonNode?> values) : ISourceVersion
    {
        public string Version => version;
        public SourceValue? Read(string entity, string field) =>
            values.TryGetValue((entity, field), out var v) ? new SourceValue(v?.DeepClone()) : null;
    }

    sealed class LegRules : IEditRules
    {
        public string KindOf(EditTarget target) => "test.trade";
        // The fixed rate is compared together with the schedule it applies to.
        public IReadOnlyList<string> ConflictGroup(EditTarget target) =>
            target.Field is "fixedRate" or "schedule" ? ["fixedRate", "schedule"] : [target.Field];
    }

    static readonly EditTarget Rate = new("cp:ABC/LE-01", "T1", "fixedRate");
    static readonly EditTarget Notional = new("cp:ABC/LE-01", "T1", "notional");
    static readonly EditTarget Csa = new("cp:ABC/LE-01", "T2", "csa");

    static Source Day1() => new("d1", new()
    {
        [("T1", "fixedRate")] = 0.01, [("T1", "schedule")] = "6M", [("T1", "notional")] = 100,
        [("T1", "fixing")] = null, [("T2", "csa")] = "CSA-A",
    });

    static WorkspaceService Service() => new(new Dictionary<string, IEditRules> { ["test"] = new LegRules() }, TimeProvider.System);

    static WorkspaceState Created(WorkspaceService service) =>
        service.Create("akira", "test", new JsonObject { ["valueDate"] = "2026-10-26" }, Day1());

    [Fact] // ADR-0003 §7: an override whose base is unchanged carries to the new version
    public void An_override_with_an_unchanged_base_carries_over()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Notional, 150);
        var day2 = new Source("d2", new() { [("T1", "fixedRate")] = 0.01, [("T1", "schedule")] = "6M", [("T1", "notional")] = 100, [("T2", "csa")] = "CSA-A" });

        var (next, summary) = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject { ["valueDate"] = "2026-10-27" }, day2);

        Assert.Equal(new CarrySummary(1, 0, 0), summary);
        Assert.Equal(150, next.Overrides[Notional].Value!.GetValue<int>());
        Assert.Equal("d2", next.Overrides[Notional].BaseVersion);
    }

    [Fact] // ADR-0003 §7: a source change inside the conflict group holds the override back for the user
    public void A_change_to_a_grouped_field_is_a_conflict_not_a_silent_choice()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Rate, 0.02);
        var day2 = new Source("d2", new() { [("T1", "fixedRate")] = 0.01, [("T1", "schedule")] = "3M", [("T1", "notional")] = 100, [("T2", "csa")] = "CSA-A" });

        var (next, summary) = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject(), day2);

        Assert.Equal(new CarrySummary(0, 1, 0), summary);
        Assert.False(next.Overrides.ContainsKey(Rate));
        Assert.Equal("3M", next.Conflicts[Rate].Source["schedule"]!.GetValue<string>());
        var refused = Assert.Throws<WorkspaceRefused>(() => service.Freeze(next.WorkspaceId, next.Revision, _ => true));
        Assert.Equal("UnresolvedEdits", refused.Code);
        Assert.Equal([Rate], refused.Targets);
    }

    [Fact] // ADR-0003 §7: keeping the user's value moves its base, so the next refresh compares against what was seen
    public void Keeping_an_override_moves_its_base_to_the_new_version()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Rate, 0.02);
        var day2 = new Source("d2", new() { [("T1", "fixedRate")] = 0.015, [("T1", "schedule")] = "6M", [("T1", "notional")] = 100, [("T2", "csa")] = "CSA-A" });
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject(), day2).State;

        ws = service.KeepOverride(ws.WorkspaceId, ws.Revision, "akira", Rate);

        Assert.Equal(0.015, ws.Overrides[Rate].Base["fixedRate"]!.GetValue<double>());
        Assert.Equal(new CarrySummary(1, 0, 0), service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject(), day2).Summary);
    }

    [Fact] // ADR-0003 §7: an override whose trade is gone is listed, never moved, and blocks Price until seen
    public void An_override_on_a_vanished_trade_is_lost_and_must_be_acknowledged()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Csa, "CSA-B");
        var day2 = new Source("d2", new() { [("T1", "fixedRate")] = 0.01, [("T1", "schedule")] = "6M", [("T1", "notional")] = 100 });
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject(), day2).State;

        Assert.Throws<WorkspaceRefused>(() => service.Freeze(ws.WorkspaceId, ws.Revision, e => e == "T2"));
        Assert.Empty(service.Freeze(ws.WorkspaceId, ws.Revision, e => e == "T1").Overrides);
        ws = service.AcknowledgeLost(ws.WorkspaceId, ws.Revision, "akira", Csa);
        Assert.Empty(service.Freeze(ws.WorkspaceId, ws.Revision, _ => true).Overrides);
    }

    [Fact] // ADR-0003 §6: leaving a scope drops its edits only as many as the user confirmed
    public void Dropping_a_scope_needs_the_count_the_user_saw()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Csa, "CSA-B");
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Notional, 120);

        Assert.Equal("ConfirmationStale", Assert.Throws<WorkspaceRefused>(() => service.DropScope(ws.WorkspaceId, ws.Revision, "akira", "cp:ABC/LE-01", 1)).Code);
        ws = service.DropScope(ws.WorkspaceId, ws.Revision, "akira", "cp:ABC/LE-01", 2);

        Assert.Empty(ws.Overrides);
        Assert.Equal(EditKind.DropScope, ws.Log[^1].Kind);
        Assert.Contains(ws.Log, e => e.Kind == EditKind.Set && e.Target == Csa);
    }

    [Fact] // Spine 6: a command taken against an older revision is refused, whatever the timing
    public void A_command_against_a_moved_revision_is_refused()
    {
        var service = Service();
        var ws = Created(service);
        var stale = ws.Revision;
        service.Set(ws.WorkspaceId, stale, "akira", Notional, 120);

        Assert.Equal("RevisionMoved", Assert.Throws<WorkspaceRefused>(() => service.Set(ws.WorkspaceId, stale, "akira", Csa, "CSA-B")).Code);
    }

    [Fact] // ADR-0003 §6: the frozen copy outlives the workspace, and the log keeps every edit with its module kind
    public void A_freeze_is_a_copy_the_workspace_can_be_deleted_under()
    {
        var service = Service();
        var ws = Created(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Notional, 120);
        ws = service.Reset(ws.WorkspaceId, ws.Revision, "akira", Notional);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Notional, 130);
        var frozen = service.Freeze(ws.WorkspaceId, ws.Revision, _ => true);

        service.Delete(ws.WorkspaceId, "akira");

        Assert.Null(service.Find(ws.WorkspaceId));
        Assert.Equal(130, frozen.Overrides.Single().Value!.GetValue<int>());
        Assert.Equal([EditKind.Context, EditKind.Acquire, EditKind.Set, EditKind.Reset, EditKind.Set], ws.Log.Select(e => e.Kind));
        Assert.All(ws.Log.Where(e => e.Target is not null), e => Assert.Equal("test.trade", e.DomainKind));
    }
}
