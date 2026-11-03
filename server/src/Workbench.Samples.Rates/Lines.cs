namespace Workbench.Samples.Rates;

public enum RatesProduct { Irs, Ccs }

/// <summary>Pay or receive leg 1 (an IRS's fixed leg); leg 2 is the other side (C40).</summary>
public enum Direction { Pay, Receive }

/// <summary>How a leg's rate or strike is quoted (C40). Strike and FX forward wait for products that use them.</summary>
public enum QuoteKind { FixedRatePercent, FloatSpreadPercent, StrikePercent, FxForwardPoints, AtmOffsetBp }

/// <summary>A leg's quote. A null value with no kind means "at par": the leg the pricer solves for.</summary>
public sealed record LegQuote(QuoteKind Kind, double Value);

/// <summary>A start or an end: a date, or a tenor (a start from spot, an end from the start).</summary>
public sealed record DateOrTenor(DateOnly? Date, Tenor? Tenor)
{
    public static DateOrTenor On(DateOnly date) => new(date, null);
    public static DateOrTenor After(Tenor tenor) => new(null, tenor);
}

public sealed record LegTerms(
    string Currency,
    string Index,
    double? Notional,
    LegQuote? Quote,
    Tenor PaymentFrequency,
    Tenor? ResetFrequency,
    bool Floating)
{
    /// <summary>The second level of the leg's terms (C43), collapsed by default; empty fields follow the profile.</summary>
    public LegDetail Detail { get; init; } = LegDetail.None;
}

/// <summary>
/// A leg's second-level terms (C43). Day count, accrual adjustment and payment delay override the convention
/// profile; the rest are terms the pricer reads. A null field is not set.
/// </summary>
public sealed record LegDetail(
    DayCountBasis? DayCount = null,
    BusinessDayAdjustment? Adjustment = null,
    bool? DailyAverage = null,
    int? ObservationShiftDays = null,
    int? LookbackDays = null,
    int? PaymentDelayDays = null,
    bool? InArrears = null,
    double? Gearing = null,
    double? CurrentResetPercent = null)
{
    public static LegDetail None { get; } = new();
}

/// <summary>The basic level of a row's terms (C40). One valuation date belongs to the grid, not the row.</summary>
public sealed record RatesLine(
    string RowKey,
    RatesProduct Product,
    DateOrTenor? Start,
    DateOrTenor End,
    Direction Direction,
    LegTerms Leg1,
    LegTerms Leg2,
    string? Collateral,
    DateOnly? MandatoryBreak,
    bool Risk);

public sealed record ResolvedLeg(LegTerms Terms, ConventionProfile Profile, LegSchedule Schedule);

/// <summary>
/// A row resolved for pricing: its legs with their profiles and generated schedules, and the leg whose quote the
/// pricer solves (the par leg), or an explicit list of reasons it cannot be priced. Never both.
/// </summary>
public sealed record LineResolution(RatesLine Line, DateOnly Spot, IReadOnlyList<ResolvedLeg> Legs, int? ParLeg, IReadOnlyList<string> Issues)
{
    public bool Priceable => Issues.Count == 0;
}

/// <summary>The confirmed convention profiles and the calendars they name (C41). Empty until the owner confirms them.</summary>
public sealed class ConventionRegistry(IEnumerable<ConventionProfile> profiles, IEnumerable<IBusinessCalendar> calendars)
{
    readonly Dictionary<(string, string), ConventionProfile> byIndex = profiles.ToDictionary(p => (p.Currency, p.Index));
    readonly Dictionary<string, IBusinessCalendar> calendarsById = calendars.ToDictionary(c => c.Id);

    public ConventionProfile? Find(string currency, string index) => byIndex.GetValueOrDefault((currency, index));

    /// <summary>Every confirmed profile, by currency and index: what a line's legs may name.</summary>
    public IEnumerable<ConventionProfile> Profiles => byIndex.Values.OrderBy(p => p.Currency, StringComparer.Ordinal).ThenBy(p => p.Index, StringComparer.Ordinal);
    public IBusinessCalendar? Calendar(string id) => calendarsById.GetValueOrDefault(id);
}

/// <summary>Validates a row and generates its standard schedules. Unsupported terms are issues on the row, not defaults.</summary>
public static class LineResolver
{
    public static LineResolution Resolve(RatesLine line, DateOnly valuationDate, ConventionRegistry conventions)
    {
        var issues = new List<string>();
        Check(line, issues);
        var p1 = Profile(line.Leg1, conventions, issues);
        var p2 = Profile(line.Leg2, conventions, issues);
        if (issues.Count > 0) return new(line, valuationDate, [], null, issues);

        var c1 = conventions.Calendar(p1!.CalendarId)!;
        var spot = DateRules.AddBusinessDays(valuationDate, p1.SpotLagDays, c1);
        var start = line.Start switch
        {
            null => spot,
            { Date: { } date } => date,
            { Tenor: { } tenor } => tenor.AddTo(spot),
            _ => throw new ArgumentException("A start is a date or a tenor."),
        };
        var end = line.End.Date ?? line.End.Tenor!.Value.AddTo(start);
        if (line.MandatoryBreak is { } breakDate && (breakDate <= start || breakDate >= end))
            issues.Add($"The mandatory break {breakDate:yyyy-MM-dd} is not between the start and the end.");

        foreach (var (leg, n) in new[] { (line.Leg1, 1), (line.Leg2, 2) })
            if (leg.Detail.CurrentResetPercent is not null && start > spot)
                issues.Add($"Leg {n}: a current reset does not apply to a forward-starting trade.");

        var exchange = line.Product == RatesProduct.Ccs;
        var legs = new List<ResolvedLeg>();
        foreach (var (terms, profile) in new[] { (line.Leg1, p1), (line.Leg2, p2!) })
        {
            try
            {
                var calendar = conventions.Calendar(profile.CalendarId)!;
                // The leg's own second-level terms override its profile (C43); the schedule records the profile it used.
                var effective = profile with
                {
                    DayCount = terms.Detail.DayCount ?? profile.DayCount,
                    Adjustment = terms.Detail.Adjustment ?? profile.Adjustment,
                    PaymentLagDays = terms.Detail.PaymentDelayDays ?? profile.PaymentLagDays,
                };
                legs.Add(new(terms, effective, ScheduleGenerator.Generate(
                    new LegScheduleRequest(start, end, terms.PaymentFrequency, terms.ResetFrequency, terms.Notional!.Value, exchange), effective, calendar)));
            }
            catch (ScheduleRefused refused)
            {
                issues.Add($"{terms.Currency} leg: {refused.Message}");
            }
        }
        if (issues.Count > 0) return new(line, spot, [], null, issues);
        int? par = line.Leg1.Quote is null ? 1 : line.Leg2.Quote is null ? 2 : null;
        return new(line, spot, legs, par, []);
    }

    static void Check(RatesLine line, List<string> issues)
    {
        if (line.End.Date is null == line.End.Tenor is null) issues.Add("The end is a date or a tenor.");
        if (line.Start is { } s && s.Date is null == s.Tenor is null) issues.Add("The start is a date or a tenor.");
        foreach (var (leg, n) in new[] { (line.Leg1, 1), (line.Leg2, 2) })
        {
            if (leg.Notional is not > 0) issues.Add($"Leg {n} needs a positive notional.");
            if (leg.Quote is { Kind: QuoteKind.StrikePercent or QuoteKind.FxForwardPoints } q)
                issues.Add($"Leg {n}: a {q.Kind} quote has no product that uses it yet.");
            if (leg.Quote is { Kind: QuoteKind.FixedRatePercent } && leg.Floating) issues.Add($"Leg {n} is floating; a fixed rate does not apply.");
            if (leg.Quote is { Kind: QuoteKind.FloatSpreadPercent } && !leg.Floating) issues.Add($"Leg {n} is fixed; a floating spread does not apply.");
            if (!leg.Floating && leg.ResetFrequency is not null) issues.Add($"Leg {n} is fixed and has no resets.");
            var d = leg.Detail;
            if (!leg.Floating && (d.DailyAverage is not null || d.ObservationShiftDays is not null || d.LookbackDays is not null
                    || d.InArrears is not null || d.Gearing is not null || d.CurrentResetPercent is not null))
                issues.Add($"Leg {n} is fixed; floating-rate terms do not apply.");
            if (d.Gearing == 0) issues.Add($"Leg {n}: a gearing of 0 removes the floating rate; leave it empty for 1.");
            if (d.ObservationShiftDays < 0 || d.LookbackDays < 0 || d.PaymentDelayDays < 0) issues.Add($"Leg {n}: shifts, lookbacks and delays are not negative.");
        }
        if (line.Leg1.Quote is null && line.Leg2.Quote is null)
            issues.Add("Both legs are at par; give one leg's quote so the other can be solved.");
        switch (line.Product)
        {
            case RatesProduct.Irs:
                if (line.Leg1.Floating || !line.Leg2.Floating) issues.Add("An IRS has a fixed leg 1 and a floating leg 2.");
                if (line.Leg1.Currency != line.Leg2.Currency) issues.Add("An IRS has one currency.");
                if (line.Leg1.Notional != line.Leg2.Notional) issues.Add("An IRS has one notional.");
                break;
            case RatesProduct.Ccs:
                if (!line.Leg1.Floating || !line.Leg2.Floating) issues.Add("The supported cross-currency swap is floating against floating (C42).");
                if (line.Leg1.Currency == line.Leg2.Currency) issues.Add("A cross-currency swap has two currencies.");
                break;
        }
    }

    static ConventionProfile? Profile(LegTerms leg, ConventionRegistry conventions, List<string> issues)
    {
        var profile = conventions.Find(leg.Currency, leg.Index);
        if (profile is null) issues.Add($"No confirmed conventions for {leg.Currency} {leg.Index}.");
        else if (conventions.Calendar(profile.CalendarId) is null) issues.Add($"Calendar {profile.CalendarId} of {profile.Id} is not available.");
        else if (leg.Detail.DailyAverage is not null && !profile.Overnight)
            issues.Add($"{leg.Currency} {leg.Index}: daily averaging is for overnight rates only.");
        return profile;
    }
}
