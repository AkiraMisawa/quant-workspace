using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The schedule generator (C39, C40) over an explicit test profile. The profile and its weekends-only calendar are
// synthetic: they test the generator's rules, not any market's conventions (C41 is still open).
public class RatesScheduleTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();

    static ConventionProfile Profile(StubRule stub = StubRule.ShortFront, int paymentLag = 2, bool endOfMonth = false) =>
        new("test.act360", "1", "TST", "TEST-ON", DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            SpotLagDays: 2, PaymentLagDays: paymentLag, stub, endOfMonth);

    static LegSchedule Generate(DateOnly start, DateOnly end, string pay, string? reset = null, bool exchange = false, ConventionProfile? profile = null) =>
        ScheduleGenerator.Generate(new LegScheduleRequest(start, end, Tenor.Parse(pay), reset is null ? null : Tenor.Parse(reset), 1_000_000, exchange),
            profile ?? Profile(), Calendar);

    [Fact] // C40: whole periods rolled from the end; accrual adjusted, payment after its lag in business days
    public void A_two_year_semiannual_leg_has_four_adjusted_periods()
    {
        var leg = Generate(new(2026, 10, 29), new(2028, 10, 29), "6m");

        Assert.Equal(4, leg.Periods.Count);
        Assert.Equal(new DateOnly(2027, 4, 29), leg.Periods[0].AccrualEnd);
        // 2028-04-29 is a Saturday; the following Monday is in May, so Modified Following takes Friday 2028-04-28.
        Assert.Equal(new DateOnly(2028, 4, 28), leg.Periods[2].AccrualEnd);
        Assert.Equal(new DateOnly(2028, 5, 2), leg.Periods[2].PaymentDate);
        // 2028-10-29 is a Sunday and moves to Monday 2028-10-30.
        Assert.Equal(new DateOnly(2028, 10, 30), leg.Periods[3].AccrualEnd);
        Assert.Equal(leg.Periods[2].AccrualEnd, leg.Periods[3].AccrualStart);
        Assert.Equal(182 / 360.0, leg.Periods[0].YearFraction, 12);
    }

    [Fact] // Modified Following stays in the month: a month-end Saturday goes back to Friday
    public void Modified_following_does_not_cross_the_month_end()
    {
        var saturday = new DateOnly(2027, 7, 31);

        Assert.Equal(new DateOnly(2027, 7, 30), DateRules.Adjust(saturday, BusinessDayAdjustment.ModifiedFollowing, Calendar));
        Assert.Equal(new DateOnly(2027, 8, 2), DateRules.Adjust(saturday, BusinessDayAdjustment.Following, Calendar));
    }

    [Fact] // a broken term takes a short first period when the profile allows it, and is refused when it does not
    public void A_broken_term_is_a_short_front_stub_or_a_refusal()
    {
        var leg = Generate(new(2026, 12, 15), new(2028, 3, 15), "6m");

        Assert.Equal(new DateOnly(2027, 3, 15), leg.Periods[0].UnadjustedEnd);
        Assert.Equal(3, leg.Periods.Count);
        Assert.Throws<ScheduleRefused>(() => Generate(new(2026, 12, 15), new(2028, 3, 15), "6m", profile: Profile(StubRule.None)));
    }

    [Fact] // C40: the reset frequency defaults to the payment frequency; a shorter one splits each period
    public void Resets_follow_the_payment_frequency_unless_set()
    {
        var single = Generate(new(2026, 10, 29), new(2027, 10, 29), "12m");
        var quarterly = Generate(new(2026, 10, 29), new(2027, 10, 29), "12m", "3m");

        Assert.Single(single.Periods[0].Resets);
        Assert.Equal(4, quarterly.Periods[0].Resets.Count);
        Assert.Equal(quarterly.Periods[0].AccrualEnd, quarterly.Periods[0].Resets[^1].End);
        Assert.Throws<ScheduleRefused>(() => Generate(new(2026, 10, 29), new(2027, 10, 29), "6m", "4m"));
    }

    [Fact] // C42: a cross-currency leg exchanges its notional at the start and with the last coupon
    public void A_cross_currency_leg_exchanges_notional_at_both_ends()
    {
        var leg = Generate(new(2026, 10, 29), new(2028, 10, 29), "3m", exchange: true);

        Assert.Equal((ExchangeKind.Initial, leg.Periods[0].AccrualStart), (leg.Exchanges[0].Kind, leg.Exchanges[0].PaymentDate));
        Assert.Equal((ExchangeKind.Final, leg.Periods[^1].PaymentDate), (leg.Exchanges[1].Kind, leg.Exchanges[1].PaymentDate));
    }

    [Fact] // the spine: a profile for another calendar is refused, never quietly used
    public void A_calendar_that_does_not_match_the_profile_is_refused()
    {
        var profile = Profile() with { CalendarId = "somewhere-else" };

        Assert.Throws<ScheduleRefused>(() => Generate(new(2026, 10, 29), new(2027, 10, 29), "6m", profile: profile));
    }

    [Theory]
    [InlineData("3m", 3)]
    [InlineData("10Y", 120)]
    [InlineData("12m", 12)]
    public void Tenors_parse_to_months(string text, int months) => Assert.Equal(months, Tenor.Parse(text).Months);

    [Theory]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("3q")]
    [InlineData("m3")]
    public void Malformed_tenors_are_refused(string text) => Assert.False(Tenor.TryParse(text, out _));
}
