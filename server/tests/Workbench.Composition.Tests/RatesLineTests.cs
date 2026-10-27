using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// A row's basic terms (C40, C42) resolved over synthetic test profiles. "TST" and "TS2" are not currencies.
public class RatesLineTests
{
    static readonly DateOnly ValuationDate = new(2026, 10, 27);
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();

    static ConventionProfile Profile(string currency, string index) =>
        new($"test.{currency}.{index}", "1", currency, index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            SpotLagDays: 2, PaymentLagDays: 2, StubRule.ShortFront, EndOfMonth: false);

    static readonly ConventionRegistry Conventions = new(
        [Profile("TST", "FIXED"), Profile("TST", "TEST-ON"), Profile("TS2", "TEST2-ON")], [Calendar]);

    static LegTerms Fixed(LegQuote? quote = null) => new("TST", "FIXED", 10_000_000, quote, Tenor.Parse("12m"), null, Floating: false);
    static LegTerms Float(string ccy = "TST", string index = "TEST-ON", LegQuote? quote = null, double notional = 10_000_000) =>
        new(ccy, index, notional, quote ?? new LegQuote(QuoteKind.FloatSpreadPercent, 0), Tenor.Parse("3m"), null, Floating: true);

    static RatesLine Irs(LegTerms? leg1 = null, LegTerms? leg2 = null, string end = "5y") =>
        new("L1", RatesProduct.Irs, null, DateOrTenor.After(Tenor.Parse(end)), Direction.Pay, leg1 ?? Fixed(), leg2 ?? Float(), null, null, Risk: true);

    [Fact] // C40: an empty fixed rate is the par leg; the start defaults to spot and the end is a tenor from it
    public void An_irs_with_an_empty_rate_solves_its_fixed_leg_from_spot()
    {
        var resolved = LineResolver.Resolve(Irs(), ValuationDate, Conventions);

        Assert.True(resolved.Priceable, string.Join("; ", resolved.Issues));
        Assert.Equal(new DateOnly(2026, 10, 29), resolved.Spot);
        Assert.Equal(1, resolved.ParLeg);
        Assert.Equal((5, 20), (resolved.Legs[0].Schedule.Periods.Count, resolved.Legs[1].Schedule.Periods.Count));
        Assert.Empty(resolved.Legs[0].Schedule.Exchanges);
    }

    [Fact] // C41: no convention is assumed; an unconfirmed currency or index is an issue on the row
    public void A_leg_without_confirmed_conventions_is_refused()
    {
        var resolved = LineResolver.Resolve(Irs(leg2: Float(index: "SOMETHING")), ValuationDate, Conventions);

        Assert.False(resolved.Priceable);
        Assert.Contains("No confirmed conventions for TST SOMETHING.", resolved.Issues);
        Assert.Empty(resolved.Legs);
    }

    [Fact] // C42: the cross-currency swap is float against float, exchanging both notionals
    public void A_cross_currency_basis_swap_exchanges_notionals_on_both_legs()
    {
        var line = new RatesLine("L2", RatesProduct.Ccs, null, DateOrTenor.After(Tenor.Parse("3y")), Direction.Receive,
            Float() with { Quote = null }, Float("TS2", "TEST2-ON", notional: 65_000), null, null, false);

        var resolved = LineResolver.Resolve(line, ValuationDate, Conventions);

        Assert.True(resolved.Priceable, string.Join("; ", resolved.Issues));
        Assert.Equal(1, resolved.ParLeg);
        Assert.All(resolved.Legs, l => Assert.Equal(2, l.Schedule.Exchanges.Count));
        Assert.Equal(65_000, resolved.Legs[1].Schedule.Exchanges[0].Amount);
    }

    [Theory] // the spine: terms that do not fit the product are refused, not reinterpreted
    [InlineData("both-par")]
    [InlineData("strike")]
    [InlineData("fixed-ccs")]
    [InlineData("spread-on-fixed")]
    public void Terms_that_do_not_fit_are_issues(string kind)
    {
        var line = kind switch
        {
            "both-par" => Irs(leg2: Float() with { Quote = null }),
            "strike" => Irs(leg1: Fixed(new LegQuote(QuoteKind.StrikePercent, 1))),
            "fixed-ccs" => Irs() with { Product = RatesProduct.Ccs, Leg2 = Float("TS2", "TEST2-ON") },
            _ => Irs(leg1: Fixed(new LegQuote(QuoteKind.FloatSpreadPercent, 0.1))),
        };

        Assert.NotEmpty(LineResolver.Resolve(line, ValuationDate, Conventions).Issues);
    }

    [Fact] // C40: a mandatory break must fall inside the trade
    public void A_mandatory_break_outside_the_trade_is_an_issue()
    {
        var line = Irs() with { MandatoryBreak = new DateOnly(2040, 1, 1) };

        Assert.Contains(LineResolver.Resolve(line, ValuationDate, Conventions).Issues, i => i.Contains("mandatory break"));
    }
}
