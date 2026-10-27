using System.Globalization;
using System.Text.RegularExpressions;

namespace Workbench.Samples.Rates;

public enum TenorUnit { Day, Week, Month, Year }

/// <summary>A period such as 3m or 10y, as the row's terms write it (C40).</summary>
public readonly partial record struct Tenor(int Count, TenorUnit Unit)
{
    /// <summary>Months in this tenor, or null for days and weeks, which are not a whole number of months.</summary>
    public int? Months => Unit switch { TenorUnit.Month => Count, TenorUnit.Year => Count * 12, _ => null };

    public DateOnly AddTo(DateOnly date) => Unit switch
    {
        TenorUnit.Day => date.AddDays(Count),
        TenorUnit.Week => date.AddDays(7 * Count),
        TenorUnit.Month => date.AddMonths(Count),
        _ => date.AddYears(Count),
    };

    public static Tenor Parse(string text) => TryParse(text, out var tenor)
        ? tenor
        : throw new FormatException($"'{text}' is not a tenor such as 3m or 10y.");

    public static bool TryParse(string? text, out Tenor tenor)
    {
        tenor = default;
        var match = text is null ? null : Pattern().Match(text.Trim());
        if (match is null || !match.Success) return false;
        var count = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (count <= 0) return false;
        tenor = new Tenor(count, char.ToLowerInvariant(match.Groups[2].Value[0]) switch
        {
            'd' => TenorUnit.Day, 'w' => TenorUnit.Week, 'm' => TenorUnit.Month, _ => TenorUnit.Year,
        });
        return true;
    }

    public override string ToString() => $"{Count}{Unit switch { TenorUnit.Day => "d", TenorUnit.Week => "w", TenorUnit.Month => "m", _ => "y" }}";

    [GeneratedRegex("^([0-9]{1,3})([dDwWmMyY])$")]
    private static partial Regex Pattern();
}

public enum BusinessDayAdjustment { Unadjusted, Following, ModifiedFollowing, Preceding }

public enum DayCountBasis { Act360, Act365Fixed }

/// <summary>Business days of one calendar. A real calendar comes from a confirmed holiday source, never from this code.</summary>
public interface IBusinessCalendar
{
    string Id { get; }
    bool IsBusinessDay(DateOnly date);
}

/// <summary>Saturdays and Sundays only. For tests and synthetic profiles; it is no market's calendar.</summary>
public sealed class WeekendsOnlyCalendar : IBusinessCalendar
{
    public string Id => "weekends-only";
    public bool IsBusinessDay(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}

public static class DateRules
{
    public static DateOnly Adjust(DateOnly date, BusinessDayAdjustment rule, IBusinessCalendar calendar)
    {
        switch (rule)
        {
            case BusinessDayAdjustment.Unadjusted:
                return date;
            case BusinessDayAdjustment.Following:
                return Roll(date, 1, calendar);
            case BusinessDayAdjustment.Preceding:
                return Roll(date, -1, calendar);
            default:
                var following = Roll(date, 1, calendar);
                return following.Month == date.Month ? following : Roll(date, -1, calendar);
        }
    }

    /// <summary>Moves a number of business days; zero leaves the date as it is.</summary>
    public static DateOnly AddBusinessDays(DateOnly date, int days, IBusinessCalendar calendar)
    {
        var step = Math.Sign(days);
        for (var left = Math.Abs(days); left > 0; left--)
        {
            date = date.AddDays(step);
            while (!calendar.IsBusinessDay(date)) date = date.AddDays(step);
        }
        return date;
    }

    public static double YearFraction(DateOnly start, DateOnly end, DayCountBasis basis)
    {
        var days = end.DayNumber - start.DayNumber;
        return basis switch { DayCountBasis.Act360 => days / 360.0, _ => days / 365.0 };
    }

    static DateOnly Roll(DateOnly date, int step, IBusinessCalendar calendar)
    {
        while (!calendar.IsBusinessDay(date)) date = date.AddDays(step);
        return date;
    }
}

public enum StubRule
{
    /// <summary>A term that is not a whole number of periods is refused.</summary>
    None,
    /// <summary>Periods are rolled back from the end; a shorter first period takes the remainder.</summary>
    ShortFront,
}

/// <summary>
/// Every convention one leg's schedule needs, for one currency and index. Profiles are registered only once the
/// owner has confirmed them (C41); the generator never fills a missing convention with a default of its own.
/// </summary>
public sealed record ConventionProfile(
    string Id,
    string Version,
    string Currency,
    string Index,
    DayCountBasis DayCount,
    string CalendarId,
    BusinessDayAdjustment Adjustment,
    int SpotLagDays,
    int PaymentLagDays,
    StubRule Stub,
    bool EndOfMonth);
