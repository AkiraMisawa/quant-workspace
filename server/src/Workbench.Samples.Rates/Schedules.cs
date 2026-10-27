namespace Workbench.Samples.Rates;

/// <summary>One accrual period of a leg, as generated. The schedule is the immutable base user edits sit on (C39).</summary>
public sealed record CashflowPeriod(
    int Number,
    DateOnly UnadjustedStart,
    DateOnly UnadjustedEnd,
    DateOnly AccrualStart,
    DateOnly AccrualEnd,
    DateOnly PaymentDate,
    double YearFraction,
    double Notional,
    IReadOnlyList<ResetPeriod> Resets);

/// <summary>A compounding sub-period of a floating period; flat compounding means the spread is not compounded (C40).</summary>
public sealed record ResetPeriod(DateOnly Start, DateOnly End, double YearFraction);

public enum ExchangeKind { Initial, Final }

/// <summary>
/// A notional exchange of a cross-currency leg (C42). The amount is unsigned: the leg's direction decides who pays,
/// as for its coupons. The initial exchange is on the adjusted start, the final one with the last coupon.
/// </summary>
public sealed record NotionalExchange(ExchangeKind Kind, DateOnly PaymentDate, double Amount);

public sealed record LegScheduleRequest(
    DateOnly Start,
    DateOnly End,
    Tenor PaymentFrequency,
    Tenor? ResetFrequency,
    double Notional,
    bool ExchangeNotional);

public sealed record LegSchedule(
    string ProfileId,
    string ProfileVersion,
    string GeneratorVersion,
    IReadOnlyList<CashflowPeriod> Periods,
    IReadOnlyList<NotionalExchange> Exchanges);

/// <summary>A schedule the generator will not build, with the reason shown on the row. Never replaced by a guess.</summary>
public sealed class ScheduleRefused(string message) : Exception(message);

/// <summary>
/// Generates a leg's standard schedule from the row's sparse terms and an explicit convention profile.
/// Unadjusted dates roll back from the unadjusted end in whole payment periods; accrual dates are adjusted by
/// the profile's rule, and payment follows the adjusted accrual end by the profile's lag in business days.
/// A reset frequency must divide the payment frequency; its sub-periods run forward from the period start.
/// </summary>
public static class ScheduleGenerator
{
    public const string Version = "1";

    public static LegSchedule Generate(LegScheduleRequest request, ConventionProfile profile, IBusinessCalendar calendar)
    {
        if (calendar.Id != profile.CalendarId)
            throw new ScheduleRefused($"Profile {profile.Id} needs calendar {profile.CalendarId}, not {calendar.Id}.");
        if (request.End <= request.Start)
            throw new ScheduleRefused($"The end {request.End:yyyy-MM-dd} is not after the start {request.Start:yyyy-MM-dd}.");
        if (request.Notional <= 0)
            throw new ScheduleRefused("The notional must be positive; the direction says who pays.");
        var paymentMonths = request.PaymentFrequency.Months
            ?? throw new ScheduleRefused($"Payment frequency {request.PaymentFrequency} is not a whole number of months.");
        var resetMonths = paymentMonths;
        if (request.ResetFrequency is { } reset)
        {
            resetMonths = reset.Months ?? throw new ScheduleRefused($"Reset frequency {reset} is not a whole number of months.");
            if (resetMonths > paymentMonths || paymentMonths % resetMonths != 0)
                throw new ScheduleRefused($"Reset frequency {reset} does not divide payment frequency {request.PaymentFrequency}.");
        }

        var boundaries = new List<DateOnly> { request.End };
        for (var k = 1; ; k++)
        {
            var date = Roll(request.End, -k * paymentMonths, profile.EndOfMonth);
            if (date <= request.Start) break;
            boundaries.Add(date);
        }
        boundaries.Add(request.Start);
        boundaries.Reverse();
        var whole = Roll(request.End, -(boundaries.Count - 1) * paymentMonths, profile.EndOfMonth) == request.Start;
        if (!whole && profile.Stub == StubRule.None)
            throw new ScheduleRefused($"{request.Start:yyyy-MM-dd} to {request.End:yyyy-MM-dd} is not a whole number of {request.PaymentFrequency} periods, and profile {profile.Id} allows no stub.");

        var periods = new List<CashflowPeriod>();
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var (us, ue) = (boundaries[i], boundaries[i + 1]);
            var (start, end) = (DateRules.Adjust(us, profile.Adjustment, calendar), DateRules.Adjust(ue, profile.Adjustment, calendar));
            var payment = DateRules.AddBusinessDays(end, profile.PaymentLagDays, calendar);
            periods.Add(new CashflowPeriod(i + 1, us, ue, start, end, payment,
                DateRules.YearFraction(start, end, profile.DayCount), request.Notional, Resets(us, ue, resetMonths, profile, calendar)));
        }

        IReadOnlyList<NotionalExchange> exchanges = request.ExchangeNotional
            ?
            [
                new(ExchangeKind.Initial, periods[0].AccrualStart, request.Notional),
                new(ExchangeKind.Final, periods[^1].PaymentDate, request.Notional),
            ]
            : [];
        return new LegSchedule(profile.Id, profile.Version, Version, periods, exchanges);
    }

    static IReadOnlyList<ResetPeriod> Resets(DateOnly start, DateOnly end, int months, ConventionProfile profile, IBusinessCalendar calendar)
    {
        var resets = new List<ResetPeriod>();
        for (var j = 0; ; j++)
        {
            var from = start.AddMonths(j * months);
            if (from >= end) break;
            var to = start.AddMonths((j + 1) * months);
            if (to > end) to = end;
            var (s, e) = (DateRules.Adjust(from, profile.Adjustment, calendar), DateRules.Adjust(to, profile.Adjustment, calendar));
            resets.Add(new ResetPeriod(s, e, DateRules.YearFraction(s, e, profile.DayCount)));
        }
        return resets;
    }

    static DateOnly Roll(DateOnly end, int months, bool endOfMonth)
    {
        var date = end.AddMonths(months);
        return endOfMonth && end.Day == DateTime.DaysInMonth(end.Year, end.Month)
            ? new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))
            : date;
    }
}
