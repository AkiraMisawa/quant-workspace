using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The Rates application service (docs/07 §3, §4): Price, live and capture over one workspace. Profiles, curves
// and figures are synthetic test ones; the provider is the fake one.
public class RatesApplicationTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();

    static ConventionProfile Profile(string index, bool overnight) =>
        new($"test.{index}", "1", "TST", index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            2, 2, StubRule.ShortFront, false, overnight);

    static readonly ConventionRegistry Conventions = new([Profile("FIXED", false), Profile("TEST-ON", true)], [Calendar]);

    static SyntheticMarketConfig MarketConfig => new("test-1", 7, new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1),
        [new("TST", "TEST-ON", [(Tenor.Parse("1y"), 1.0), (Tenor.Parse("5y"), 1.5), (Tenor.Parse("10y"), 2.0)])],
        new Dictionary<string, double> { ["TST"] = 1 }, 0.5, 0.2, 0.0005);

    sealed record Rig(RatesWorkspaces Service, RunService Runs, IDataStore Store, MarketFeed Market, RatesLiveService Live);

    static Rig Build()
    {
        var market = new SyntheticMarket(MarketConfig);
        var provider = new FakeRatesPricing();
        var live = new RatesLiveService(provider, market.Resolve, TimeProvider.System, TimeSpan.FromMinutes(5));
        var feed = new MarketFeed(market, live);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new RatesModule(provider, market.Resolve)]), store, TimeProvider.System);
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [RatesEditRules.App] = new RatesEditRules(Conventions) }, TimeProvider.System);
        return new(new RatesWorkspaces(workspaces, store, runs, live, feed, Conventions), runs, store, feed, live);
    }

    /// <summary>A workspace with one complete five-year IRS paying par fixed against the test overnight index, and one empty line.</summary>
    static (string Id, long Revision, string Line, string Empty) Workspace(Rig rig)
    {
        var ws = rig.Service.Create("akira", new DateOnly(2026, 10, 27));
        var (state, line) = rig.Service.AddLine(ws.WorkspaceId, ws.Revision, "akira", RatesProduct.Irs);
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("end", "5y"), ("risk", true),
            ("leg1.currency", "TST"), ("leg1.index", "FIXED"), ("leg1.notional", 10_000_000), ("leg1.paymentFrequency", "12m"),
            ("leg2.currency", "TST"), ("leg2.index", "TEST-ON"), ("leg2.notional", 10_000_000), ("leg2.paymentFrequency", "12m"),
            ("leg2.quoteKind", "FloatSpreadPercent"), ("leg2.quote", 0),
        })
            state = rig.Service.EditLine(ws.WorkspaceId, state.Revision, "akira", line, field, value);
        (state, var empty) = rig.Service.AddLine(ws.WorkspaceId, state.Revision, "akira", RatesProduct.Irs);
        return (ws.WorkspaceId, state.Revision, line, empty);
    }

    static async Task<(RunRecord Run, JsonNode Lines, JsonNode Risk)> Completed(Rig rig, SubmissionResult submitted)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(submitted);
        await rig.Runs.Completion(accepted.Run.RunId);
        var run = rig.Runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        return (run, rig.Store.ReadRequired(run.Outputs["result"]), rig.Store.ReadRequired(run.Outputs["risk"]));
    }

    static JsonNode Row(JsonNode dataset, string line) => dataset["rows"]!.AsArray().Single(r => r!["line"]!.GetValue<string>() == line)!;

    // One page per subscription in these tests: attached once, read for every frame after.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Channels.ChannelReader<LiveDelivery>> Attached = new();

    static async Task<LiveDelivery> NextFrame(Rig rig, string subscription)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!Attached.TryGetValue(subscription, out var frames))
            Attached[subscription] = frames = rig.Live.Attach("akira", subscription).Frames;
        return await frames.ReadAsync(timeout.Token);
    }

    [Fact] // docs/07 §3: Price pins a snapshot and prices the priceable lines; a line that is not is listed with why, never a zero
    public async Task Price_prices_the_priceable_lines_and_lists_the_rest()
    {
        var rig = Build();
        var (id, revision, line, empty) = Workspace(rig);
        var snapshot = await rig.Market.AdvanceAsync();

        var (run, lines, risk) = await Completed(rig, rig.Service.Price(id, revision, "akira", "k1", snapshot.Id));

        Assert.Equal("priced", Row(lines, line)["status"]!.GetValue<string>());
        Assert.Equal("parRate", Row(lines, line)["parMeasure"]!.GetValue<string>());
        var left = Row(lines, empty);
        Assert.Equal("notPriced", left["status"]!.GetValue<string>());
        Assert.Null(left["pv"]);
        Assert.Contains("empty", left["message"]!.GetValue<string>());
        Assert.NotEmpty(risk["rows"]!.AsArray());
        Assert.Equal("simulated", lines["provenance"]!["executionMode"]!.GetValue<string>());
        Assert.Equal(snapshot.Id, run.Input["marketSnapshotId"]!.GetValue<string>());
        Assert.Equal(revision, run.Relations.WorkspaceRevision);
    }

    [Fact] // Spine 1: a market the server cannot resolve is refused, never replaced with the latest
    public async Task Price_refuses_an_unknown_snapshot()
    {
        var rig = Build();
        var (id, revision, line, empty) = Workspace(rig);
        await rig.Market.AdvanceAsync();

        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Service.Price(id, revision, "akira", "k1", "synthetic:test-1:7:999x"));

        Assert.Equal("UnknownMarket", refused.Code);
    }

    [Fact] // docs/07 acceptance 4: a manual price and a live frame on the same input and market give the same figures
    public async Task Manual_and_live_agree_on_the_same_input_and_market()
    {
        var rig = Build();
        var (id, revision, line, empty) = Workspace(rig);
        var subscription = rig.Service.StartLive(id, "akira");
        var snapshot = await rig.Market.AdvanceAsync();
        var frame = await NextFrame(rig, subscription);

        var (_, lines, _) = await Completed(rig, rig.Service.Price(id, revision, "akira", "k1", snapshot.Id));

        Assert.Equal(snapshot.Id, frame.Market.SnapshotId);
        Assert.Equal(frame.Rows.Single(r => r.RowKey == line).Measures[Measures.Pv].Value, Row(lines, line)["pv"]!.GetValue<double>());
    }

    [Fact] // docs/07 §6: an accepted edit moves a running subscription to its revision, and frames say which
    public async Task An_edit_moves_the_live_subscription_to_its_revision()
    {
        var rig = Build();
        var (id, revision, line, empty) = Workspace(rig);
        var subscription = rig.Service.StartLive(id, "akira");
        await rig.Market.AdvanceAsync();
        Assert.Equal(revision, (await NextFrame(rig, subscription)).InputRevision);

        var edited = rig.Service.EditLine(id, revision, "akira", line, "leg1.notional", 20_000_000);
        var frame = await NextFrame(rig, subscription);

        Assert.Equal(edited.Revision, frame.InputRevision);
    }

    [Fact] // docs/07 §3: capture saves the frame the user saw, its own market on it, after the market moved on
    public async Task Capture_saves_the_shown_frame_as_it_was()
    {
        var rig = Build();
        var (id, _, line, _) = Workspace(rig);
        var subscription = rig.Service.StartLive(id, "akira");
        await rig.Market.AdvanceAsync();
        var seen = await NextFrame(rig, subscription);

        var (run, lines, _) = await Completed(rig, rig.Service.Capture(subscription, "akira",
            new FrameIdentity(seen.SubscriptionRevision, seen.InputRevision, seen.Market.SnapshotId), "k1"));

        Assert.Equal(RatesModule.CaptureWorkflow, run.Workflow.DefinitionId);
        Assert.Equal(seen.Market.SnapshotId, run.Input["marketSnapshotId"]!.GetValue<string>());
        Assert.Equal(seen.Rows.Single(r => r.RowKey == line).Measures[Measures.Pv].Value, Row(lines, line)["pv"]!.GetValue<double>());
    }

    [Fact] // Spine 6: a capture names its frame; once another is shown, the capture is refused rather than saving the newer one
    public async Task Capture_of_a_frame_no_longer_shown_is_refused()
    {
        var rig = Build();
        var (id, _, line, _) = Workspace(rig);
        var subscription = rig.Service.StartLive(id, "akira");
        await rig.Market.AdvanceAsync();
        var seen = await NextFrame(rig, subscription);
        await rig.Market.AdvanceAsync();
        await NextFrame(rig, subscription);

        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Service.Capture(subscription, "akira",
            new FrameIdentity(seen.SubscriptionRevision, seen.InputRevision, seen.Market.SnapshotId), "k1"));

        Assert.Equal("FrameMoved", refused.Code);
    }

    [Fact] // C39: a period's cashflow edit is checked by type and lands on the priced period
    public void A_cashflow_edit_is_typed_and_shown_on_its_period()
    {
        var rig = Build();
        var (id, revision, line, empty) = Workspace(rig);
        var period = rig.Service.Cashflows(id, "akira", line).Periods.First(p => p.Leg == 1);

        Assert.Equal("InvalidValue", Assert.Throws<WorkspaceRefused>(() =>
            rig.Service.EditCashflow(id, revision, "akira", line, period.Entity, "notional", "lots")).Code);
        rig.Service.EditCashflow(id, revision, "akira", line, period.Entity, "notional", 5_000_000);

        var edited = rig.Service.Cashflows(id, "akira", line).Periods.Single(p => p.Entity == period.Entity);
        Assert.Equal(5_000_000d, edited.Notional);
        Assert.Equal(["notional"], edited.UserFields);
    }

    [Fact] // docs/07 §9.3: deleting the workspace stops its live subscriptions
    public void Deleting_a_workspace_stops_its_subscriptions()
    {
        var rig = Build();
        var (id, _, line, _) = Workspace(rig);
        rig.Service.StartLive(id, "akira");

        rig.Service.Delete(id, "akira");

        Assert.Equal(0, rig.Live.Count);
    }
}
