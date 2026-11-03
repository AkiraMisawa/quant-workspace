using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The synthetic market and the fake provider (docs/07 §5, §7). Every figure is simulated over synthetic curves.
public class RatesPricingTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();
    static readonly DateOnly ValuationDate = new(2026, 10, 27);

    static ConventionProfile Profile(string ccy, string index, bool overnight) =>
        new($"test.{ccy}.{index}", "1", ccy, index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            2, 2, StubRule.ShortFront, false, overnight);

    static readonly ConventionRegistry Conventions = new(
        [Profile("TST", "FIXED", false), Profile("TST", "TEST-ON", true), Profile("TS2", "TEST2-ON", true)], [Calendar]);

    static SyntheticMarketConfig Config(int seed = 7) => new("test-1", seed, new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1),
        [
            new("TST", "TEST-ON", [(Tenor.Parse("1y"), 1.0), (Tenor.Parse("5y"), 1.5), (Tenor.Parse("10y"), 2.0)]),
            new("TS2", "TEST2-ON", [(Tenor.Parse("1y"), 3.0), (Tenor.Parse("10y"), 3.5)]),
        ],
        new Dictionary<string, double> { ["TST"] = 1, ["TS2"] = 0.01 },
        MaxLevelStepBp: 0.5, MaxSlopeStepBp: 0.2, MaxFxLogStep: 0.0005);

    static EffectiveLine Line(string end = "5y", bool risk = true, double? fixedRate = null, string index = "TEST-ON")
    {
        var service = new WorkspaceService(new Dictionary<string, IEditRules> { [RatesEditRules.App] = new RatesEditRules(Conventions) }, TimeProvider.System);
        var ws = service.Create("akira", RatesEditRules.App, new JsonObject { ["valuationDate"] = "2026-10-27" }, new EmptySource("none"));
        var fields = LineFields.Template(RatesProduct.Irs);
        foreach (var (k, v) in new (string, JsonNode?)[]
        {
            ("end", end), ("risk", risk),
            ("leg1.currency", "TST"), ("leg1.index", "FIXED"), ("leg1.notional", 10_000_000), ("leg1.paymentFrequency", "12m"),
            ("leg1.quoteKind", fixedRate is null ? null : "FixedRatePercent"), ("leg1.quote", fixedRate),
            ("leg2.currency", "TST"), ("leg2.index", index), ("leg2.notional", 10_000_000), ("leg2.paymentFrequency", "12m"),
            ("leg2.quoteKind", "FloatSpreadPercent"), ("leg2.quote", 0),
        })
            fields[k] = v;
        ws = service.Create(ws.WorkspaceId, ws.Revision, "akira", RatesEditRules.LinesScope, "L1", fields);
        return RatesMaterialiser.Lines(ws, Conventions).Single();
    }

    static RowResult Price(EffectiveLine line, MarketSnapshot? market = null) =>
        new FakeRatesPricing().PriceBatchAsync(new RatesBatchInput(1, ValuationDate, market ?? new SyntheticMarket(Config()).Snapshot(0),
            [PricingLines.From(line, 1)]), CancellationToken.None).Result.Rows.Single();

    [Fact] // docs/07 §7: snapshot n is the same from the same seed, after a restart too, and resolves by its identifier
    public void The_synthetic_market_is_repeatable_from_its_seed()
    {
        var (a, b) = (new SyntheticMarket(Config()), new SyntheticMarket(Config()));

        Assert.Equal(a.Snapshot(40).Curves["TST/TEST-ON"].Pillars, b.Snapshot(40).Curves["TST/TEST-ON"].Pillars);
        Assert.NotEqual(a.Snapshot(40).Curves["TST/TEST-ON"].Pillars, new SyntheticMarket(Config(8)).Snapshot(40).Curves["TST/TEST-ON"].Pillars);
        Assert.Same(a.Snapshot(40), a.Resolve(a.Snapshot(40).Id));
        Assert.Null(a.Resolve("synthetic:other:7:40"));
        Assert.Equal("synthetic", a.Snapshot(3).Origin);
    }

    [Fact] // docs/07 §7: each step is bounded
    public void Market_steps_are_bounded()
    {
        var market = new SyntheticMarket(Config());
        for (var n = 1; n <= 200; n++)
        {
            var (before, after) = (market.Snapshot(n - 1).Curves["TST/TEST-ON"].Pillars[0].LevelPercent, market.Snapshot(n).Curves["TST/TEST-ON"].Pillars[0].LevelPercent);
            Assert.InRange(Math.Abs(after - before), 0, (0.5 + 0.2) / 100 + 1e-12);
        }
    }

    [Fact] // C38: an empty fixed rate is solved: at the par rate the PV is zero
    public void At_the_par_rate_the_pv_is_zero()
    {
        var row = Price(Line());

        Assert.Null(row.Failure);
        Assert.Equal("%/leg1", row.Measures[Measures.ParRate].Unit);
        Assert.Equal(0, row.Measures[Measures.Pv].Value, 6);
        var above = Price(Line(fixedRate: row.Measures[Measures.ParRate].Value + 1));
        Assert.True(above.Measures[Measures.Pv].Value < 0, "Paying a fixed rate above par is worth less than nothing.");
        Assert.False(above.Measures.ContainsKey(Measures.ParRate));
    }

    [Fact] // C38: risk is reported only for a line that asks for it, per pillar of the curves it uses
    public void Risk_follows_the_line_flag()
    {
        Assert.Equal(3, Price(Line()).Measures.Keys.Count(k => k.StartsWith("delta:", StringComparison.Ordinal)));
        Assert.DoesNotContain(Price(Line(risk: false)).Measures.Keys, k => k.StartsWith("delta:", StringComparison.Ordinal));
    }

    [Fact] // docs/07 §9.6: a row that cannot be priced is a failure on that row, never a zero
    public void A_missing_curve_is_a_row_failure_not_a_zero()
    {
        var line = Line(index: "TEST-ON");
        var market = new SyntheticMarket(Config()).Snapshot(0);
        var without = market with { Curves = market.Curves.Where(c => c.Key != "TST/TEST-ON").ToDictionary() };

        var row = Price(line, without);

        Assert.NotNull(row.Failure);
        Assert.Empty(row.Measures);
    }

    [Fact] // docs/07 §9.4: the same input against the same snapshot gives the same figures
    public void Pricing_is_deterministic()
    {
        var market = new SyntheticMarket(Config()).Snapshot(12);

        Assert.Equal(Price(Line(), market).Measures, Price(Line(), market).Measures);
    }
}
