using Workbench.Samples.Rates;

namespace Workbench.Web;

/// <summary>
/// The Rates configuration (section <c>Rates</c>). Convention profiles are configuration, never code: until the
/// owner confirms them none is configured by default, and a line on an unconfigured index is refused on its row.
/// </summary>
public sealed class RatesSettings
{
    public List<ProfileSetting> Conventions { get; set; } = [];
    public MarketSetting SyntheticMarket { get; set; } = new();
    /// <summary>Advance the synthetic market on a clock. Off for tests that advance it themselves.</summary>
    public bool Ticker { get; set; } = true;
    /// <summary>How long a live subscription runs with no page watching it before it stops (owner, 2026-10-27).</summary>
    public int LiveIdleStopSeconds { get; set; } = 300;

    public ConventionRegistry Registry()
    {
        var calendars = new IBusinessCalendar[] { new WeekendsOnlyCalendar() };
        var profiles = Conventions.Select(p =>
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Version) || string.IsNullOrWhiteSpace(p.Currency)
                || string.IsNullOrWhiteSpace(p.Index) || string.IsNullOrWhiteSpace(p.CalendarId) || p.DayCount is null || p.Adjustment is null
                || p.SpotLagDays is null || p.PaymentLagDays is null || p.Stub is null || p.EndOfMonth is null || p.Overnight is null)
                throw new InvalidOperationException($"Rates convention profile '{p.Id}' leaves a field unset; every field is required.");
            if (calendars.All(c => c.Id != p.CalendarId))
                throw new InvalidOperationException($"Rates convention profile '{p.Id}' names calendar '{p.CalendarId}', which is not available.");
            return new ConventionProfile(p.Id, p.Version, p.Currency, p.Index, p.DayCount.Value, p.CalendarId, p.Adjustment.Value,
                p.SpotLagDays.Value, p.PaymentLagDays.Value, p.Stub.Value, p.EndOfMonth.Value, p.Overnight.Value);
        }).ToList();
        return new ConventionRegistry(profiles, calendars);
    }
}

public sealed class ProfileSetting
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Currency { get; set; } = "";
    public string Index { get; set; } = "";
    public DayCountBasis? DayCount { get; set; }
    public string CalendarId { get; set; } = "";
    public BusinessDayAdjustment? Adjustment { get; set; }
    public int? SpotLagDays { get; set; }
    public int? PaymentLagDays { get; set; }
    public StubRule? Stub { get; set; }
    public bool? EndOfMonth { get; set; }
    public bool? Overnight { get; set; }
}

/// <summary>A synthetic market's starting point. Its levels are inputs to a toy curve, never quotes of any market.</summary>
public sealed class MarketSetting
{
    public string ScenarioVersion { get; set; } = "empty-1";
    public int Seed { get; set; } = 1;
    public DateTimeOffset Start { get; set; } = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    public int StepMilliseconds { get; set; } = 1000;
    public double MaxLevelStepBp { get; set; } = 0.5;
    public double MaxSlopeStepBp { get; set; } = 0.2;
    public double MaxFxLogStep { get; set; } = 0.0005;
    public List<CurveSetting> Curves { get; set; } = [];
    /// <summary>Units of each currency per unit of a common base.</summary>
    public Dictionary<string, double> Fx { get; set; } = [];

    public SyntheticMarketConfig Config()
    {
        if (StepMilliseconds < 50) throw new InvalidOperationException("The synthetic market steps at most every 50 ms.");
        return new(ScenarioVersion, Seed, Start, TimeSpan.FromMilliseconds(StepMilliseconds),
            [.. Curves.Select(c => new CurveQuotes(c.Currency, c.Index, [.. c.Pillars.Select(p => (Tenor.Parse(p.Key), p.Value)).OrderBy(p => SyntheticMarket.YearsOf(p.Item1))]))],
            Fx, MaxLevelStepBp, MaxSlopeStepBp, MaxFxLogStep);
    }
}

public sealed class CurveSetting
{
    public string Currency { get; set; } = "";
    public string Index { get; set; } = "";
    /// <summary>Pillar levels in percent by tenor, such as <c>"1y": 0.5</c>.</summary>
    public Dictionary<string, double> Pillars { get; set; } = [];
}
