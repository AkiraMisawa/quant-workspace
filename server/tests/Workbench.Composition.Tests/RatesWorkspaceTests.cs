using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The Rates lines on the generic workspace (ADR-0004, C39, C40). Profiles and currencies are synthetic test ones.
public class RatesWorkspaceTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();

    static ConventionProfile Profile(string index, bool overnight) =>
        new($"test.{index}", "1", "TST", index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            2, 2, StubRule.ShortFront, false, overnight);

    static readonly ConventionRegistry Conventions = new([Profile("FIXED", false), Profile("TEST-ON", true)], [Calendar]);

    sealed class NoSource : ISourceVersion
    {
        public string Version => "none";
        public JsonObject Descriptor => [];
        public SourceValue? Read(string entity, string field) => null;
    }

    static WorkspaceService Service() =>
        new(new Dictionary<string, IEditRules> { [RatesEditRules.App] = new RatesEditRules(Conventions) }, TimeProvider.System);

    static EditTarget Line(string field) => new(RatesEditRules.LinesScope, "L1", field);

    static WorkspaceState Set(WorkspaceService service, WorkspaceState ws, EditTarget target, JsonNode? value) =>
        service.Set(ws.WorkspaceId, ws.Revision, "akira", target, value);

    /// <summary>A two-year IRS paying par fixed annually against the test overnight index, from spot.</summary>
    static WorkspaceState TwoYearIrs(WorkspaceService service)
    {
        var ws = service.Create("akira", RatesEditRules.App, new JsonObject { ["valuationDate"] = "2026-10-27" }, new NoSource());
        ws = service.Create(ws.WorkspaceId, ws.Revision, "akira", RatesEditRules.LinesScope, "L1", LineFields.Template(RatesProduct.Irs));
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("end", "2y"),
            ("leg1.currency", "TST"), ("leg1.index", "FIXED"), ("leg1.notional", 1_000_000), ("leg1.paymentFrequency", "12m"),
            ("leg2.currency", "TST"), ("leg2.index", "TEST-ON"), ("leg2.notional", 1_000_000), ("leg2.paymentFrequency", "12m"),
            ("leg2.quoteKind", "FloatSpreadPercent"), ("leg2.quote", 0),
        })
            ws = Set(service, ws, Line(field), value);
        return ws;
    }

    static EditTarget Period(WorkspaceState ws, int leg, int index, string field)
    {
        var source = ws.Derived[RatesEditRules.SchedulePrefix + "L1"];
        var (resolution, _) = RatesEditRules.Resolve(ws, "L1", RatesEditRules.ValuationDate(ws), Conventions);
        var p = resolution!.Legs[leg - 1].Schedule.Periods[index];
        var entity = RatesEditRules.PeriodEntity("L1", leg, p);
        Assert.NotNull(source.Read(entity, field));
        return new EditTarget(RatesEditRules.SchedulePrefix + "L1", entity, field);
    }

    [Fact] // C39: a line's sparse terms derive its standard schedule, which the user can then customise
    public void A_line_derives_a_schedule_that_takes_cashflow_edits()
    {
        var service = Service();
        var ws = TwoYearIrs(service);

        var notional = Period(ws, 1, 1, "notional");
        ws = Set(service, ws, notional, 500_000);

        Assert.Equal(1_000_000, ws.Overrides[notional].Base["notional"]!.GetValue<double>());
        Assert.StartsWith("L1:", ws.Overrides[notional].BaseVersion);
    }

    [Fact] // ADR-0004: a longer maturity keeps every period's identity, so a cashflow edit carries
    public void Extending_the_maturity_carries_cashflow_edits()
    {
        var service = Service();
        var ws = TwoYearIrs(service);
        var notional = Period(ws, 1, 1, "notional");
        ws = Set(service, ws, notional, 500_000);

        ws = Set(service, ws, Line("end"), "3y");

        Assert.Equal(new CarrySummary(1, 0, 0), ws.Rederived);
        Assert.True(ws.Overrides.ContainsKey(notional));
    }

    [Fact] // C39: a changed notional under an edited period's notional is a conflict for the user, not a silent choice
    public void A_new_notional_under_an_edited_period_is_a_conflict()
    {
        var service = Service();
        var ws = TwoYearIrs(service);
        var notional = Period(ws, 1, 1, "notional");
        ws = Set(service, ws, notional, 500_000);

        // An IRS has one notional, so the line is whole again only once both legs say 2,000,000.
        ws = Set(service, ws, Line("leg1.notional"), 2_000_000);
        ws = Set(service, ws, Line("leg2.notional"), 2_000_000);

        Assert.True(ws.Conflicts.ContainsKey(notional));
    }

    [Fact] // ADR-0004: a moved start makes the old periods lost targets, never re-pointed at new ones
    public void A_moved_start_loses_the_old_periods_edits()
    {
        var service = Service();
        var ws = TwoYearIrs(service);
        var rate = Period(ws, 2, 0, "rate");
        ws = Set(service, ws, rate, 0.1);

        ws = Set(service, ws, Line("start"), "1m");

        Assert.True(ws.Lost.ContainsKey(rate));
    }

    [Fact] // C40: a line left incomplete derives nothing, and its edits come back once it is whole again
    public void An_incomplete_line_holds_its_cashflow_edits_until_it_is_whole()
    {
        var service = Service();
        var ws = TwoYearIrs(service);
        var notional = Period(ws, 1, 0, "notional");
        ws = Set(service, ws, notional, 750_000);

        ws = Set(service, ws, Line("leg2.index"), null);
        Assert.True(ws.Lost.ContainsKey(notional));
        Assert.Contains("Leg 2's index is empty.", RatesEditRules.Resolve(ws, "L1", RatesEditRules.ValuationDate(ws), Conventions).Issues);

        ws = Set(service, ws, Line("leg2.index"), "TEST-ON");
        Assert.True(ws.Overrides.ContainsKey(notional));
    }
}
