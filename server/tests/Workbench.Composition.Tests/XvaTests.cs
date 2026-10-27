using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// The synthetic XVA module on the generic workspace (docs/03, ADR-0003 §7). Names and figures are synthetic.
public class XvaTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static WorkspaceService Service() =>
        new(new Dictionary<string, IEditRules> { [XvaEditRules.App] = new XvaEditRules() }, TimeProvider.System);

    static JsonObject Context(DateOnly valueDate, string registration) =>
        new() { ["valueDate"] = valueDate.ToString("yyyy-MM-dd"), ["registration"] = registration };

    static WorkspaceState Open(WorkspaceService service, string registration = "ABC/LE-01", int existing = 40) =>
        service.Create("akira", XvaEditRules.App, Context(Day1, registration), new XvaSourceVersion(registration, Day1, existing));

    static WorkspaceState Set(WorkspaceService service, WorkspaceState ws, string entity, string field, JsonNode? value) =>
        service.Set(ws.WorkspaceId, ws.Revision, "akira", XvaEditRules.Target((XvaSourceVersion)ws.Source, entity, field), value);

    static EffectiveTrade Row(WorkspaceState ws, string tradeId) => XvaMaterialiser.Trades(ws).Single(t => t.TradeId == tradeId);

    [Fact] // docs/03: a new trade takes the main CSA automatically, and its curve follows that CSA
    public void A_new_trade_takes_the_main_csa_and_its_curve()
    {
        var ws = Open(Service());

        var draft = Row(ws, "DRAFT-1");

        Assert.Equal(("CSA-A", Ownership.Automatic, "jpy_ois", Ownership.Automatic), (draft.Csa, draft.CsaOwner, draft.Curve, draft.CurveOwner));
        Assert.False(draft.Before);
        Assert.True(draft.After);
    }

    [Fact] // docs/03: an uncollateralised trade is on nocsa_{ma id} and starts on the profile's curve
    public void An_uncollateralised_trade_uses_the_nocsa_sentinel_and_the_profile_curve()
    {
        var ws = Open(Service(), "XYZ/LE-03");

        Assert.All(XvaMaterialiser.Trades(ws), t => Assert.Equal(("nocsa_MA-003", "jpy_ois"), (t.Csa, t.Curve)));
    }

    [Fact] // Akira: changing CSA B's curve moves every trade on B, except one whose curve the user set
    public void A_csa_curve_edit_reaches_its_trades_but_not_a_manual_curve()
    {
        var service = Service();
        var ws = Open(service);
        ws = Set(service, ws, "trade:DRAFT-1", "csa", "CSA-B");
        ws = Set(service, ws, "trade:DRAFT-2", "csa", "CSA-B");
        ws = Set(service, ws, "trade:DRAFT-2", "curve", "jpy_ois");

        ws = Set(service, ws, "csa:CSA-B", "curve", "eur_estr");

        Assert.Equal(("eur_estr", Ownership.Automatic), (Row(ws, "DRAFT-1").Curve, Row(ws, "DRAFT-1").CurveOwner));
        Assert.Equal(("jpy_ois", Ownership.User), (Row(ws, "DRAFT-2").Curve, Row(ws, "DRAFT-2").CurveOwner));
        Assert.Equal("CSA-A", Row(ws, "DRAFT-3").Csa);
    }

    [Fact] // Spine: a CSA from another agreement is an issue on the row, and Price refuses rather than guess
    public void An_ineligible_csa_is_shown_and_price_refuses_it()
    {
        var service = Service();
        var ws = Open(service);
        ws = Set(service, ws, "trade:DRAFT-1", "csa", "CSA-C");

        Assert.NotEmpty(Row(ws, "DRAFT-1").Issues);
        var refused = Assert.Throws<WorkspaceRefused>(() => XvaPrice.Submission(service, new InMemoryDataStore(), ws.WorkspaceId, ws.Revision, "k1", "akira"));
        Assert.Equal("InvalidInput", refused.Code);
    }

    [Fact] // ADR-0003 §7: a new trade's own content belongs to that trade; its assignment belongs to the counterparty
    public void Scopes_are_decided_by_the_module()
    {
        var source = new XvaSourceVersion("ABC/LE-01", Day1, 5);
        var existing = source.Existing[0].TradeId;

        Assert.Equal("new:DRAFT-1", XvaEditRules.Target(source, "trade:DRAFT-1", "notional").Scope);
        Assert.Equal("cp:ABC/LE-01", XvaEditRules.Target(source, "trade:DRAFT-1", "csa").Scope);
        Assert.Equal("cp:ABC/LE-01", XvaEditRules.Target(source, $"trade:{existing}", "notional").Scope);
        Assert.Equal("xva.assignment", new XvaEditRules().KindOf(XvaEditRules.Target(source, "trade:DRAFT-1", "curve")));
    }

    [Fact] // Akira: another value date is another version; a trade that matured in between takes its edits with it, listed
    public void Moving_the_value_date_carries_edits_and_lists_those_on_matured_trades()
    {
        var service = Service();
        var ws = Open(service);
        var day2 = Day1.AddYears(1);
        var later = new XvaSourceVersion("ABC/LE-01", day2, 40);
        var matured = ws.Source is XvaSourceVersion s ? s.Existing.First(t => later.Trade(t.TradeId) is null) : throw new InvalidOperationException();
        var surviving = later.Existing[0];
        ws = Set(service, ws, $"trade:{matured.TradeId}", "notional", 1m);
        ws = Set(service, ws, $"trade:{surviving.TradeId}", "notional", 2m);

        var (next, summary) = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", Context(day2, "ABC/LE-01"), later);

        Assert.Equal(new CarrySummary(1, 0, 1), summary);
        Assert.Equal(2m, Row(next, surviving.TradeId).Notional);
        Assert.Contains(next.Lost.Keys, t => t.Entity == $"trade:{matured.TradeId}");
    }

    [Fact] // docs/03: the last fixing moves with the value date, so the same trade is another version
    public void A_passed_fixing_date_changes_the_trade()
    {
        var first = new XvaSourceVersion("ABC/LE-01", Day1, 40);
        var irs = first.Existing.First(t => t.Product == "IRS" && t.Maturity > Day1.AddYears(1));

        var later = new XvaSourceVersion("ABC/LE-01", Day1.AddYears(1), 40).Trade(irs.TradeId)!;

        Assert.NotEqual(irs.LastFixing, later.LastFixing);
        Assert.Equal((irs.FixedRate, irs.Schedule, irs.Notional), (later.FixedRate, later.Schedule, later.Notional));
    }

    static async Task<(JsonNode Result, RunRecord Run, IDataStore Store)> Price(WorkspaceService service, WorkspaceState ws, string key)
    {
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new XvaModule()]), store, TimeProvider.System);
        var accepted = Assert.IsType<SubmissionResult.Accepted>(runs.Submit(XvaPrice.Submission(service, store, ws.WorkspaceId, ws.Revision, key, "akira")));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        return (store.ReadRequired(run.Outputs["result"]), run, store);
    }

    static decimal Cell(JsonNode result, string measure, string column) =>
        result["rows"]!.AsArray().Single(r => r!["measure"]!.GetValue<string>() == measure)![column]!.GetValue<decimal>();

    [Fact] // CLAUDE.md: Before and After are independent; adding new trades leaves Before as it was
    public async Task Before_and_after_are_priced_independently()
    {
        var service = Service();
        var ws = Open(service);
        var (plain, _, _) = await Price(service, ws, "k1");
        ws = Set(service, ws, "trade:DRAFT-1", "notional", 9_000_000_000m);

        var (edited, _, _) = await Price(service, ws, "k2");

        Assert.Equal(Cell(plain, "CVA", "before"), Cell(edited, "CVA", "before"));
        Assert.NotEqual(Cell(plain, "CVA", "after"), Cell(edited, "CVA", "after"));
        Assert.Equal(Cell(edited, "CVA", "after") - Cell(edited, "CVA", "before"), Cell(edited, "CVA", "incremental"));
    }

    [Fact] // docs/03: an empty CVA recovery stays empty in the input, and analytics uses the CDS recovery
    public async Task An_empty_cva_recovery_prices_with_the_cds_recovery()
    {
        var service = Service();
        var ws = Open(service);
        var (empty, run, store) = await Price(service, ws, "k1");
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference);
        ws = Set(service, ws, "credit:ABC/LE-01", "cvaRecovery", 0.4m);

        var (explicitCds, _, _) = await Price(service, ws, "k2");

        Assert.Null(input["credit"]!["cvaRecovery"]);
        Assert.Equal(Cell(empty, "CVA", "after"), Cell(explicitCds, "CVA", "after"));
    }

    [Fact] // ADR-0003 §6: Price freezes the revision the user saw, and a moved workspace is refused
    public void Price_is_taken_against_the_revision_seen()
    {
        var service = Service();
        var ws = Open(service);
        var seen = ws.Revision;
        Set(service, ws, "trade:DRAFT-1", "notional", 1m);

        Assert.Equal("RevisionMoved", Assert.Throws<WorkspaceRefused>(() => XvaPrice.Submission(service, new InMemoryDataStore(), ws.WorkspaceId, seen, "k1", "akira")).Code);
    }
}
