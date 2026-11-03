namespace Workbench.Samples.Rates;

/// <summary>A curve's pillar levels in percent, by tenor. Synthetic levels are inputs to a toy curve, not quotes of any market.</summary>
public sealed record CurveQuotes(string Currency, string Index, IReadOnlyList<(Tenor Tenor, double LevelPercent)> Pillars);

/// <summary>
/// A fixed market snapshot (docs/07 §7): everything needed to compute with it again, under one identifier. The
/// origin says whether it is synthetic; a seed and a scenario version make a synthetic one reproducible.
/// </summary>
public sealed record MarketSnapshot(
    string Id,
    long Sequence,
    DateTimeOffset AsOf,
    string Origin,
    int Seed,
    string ScenarioVersion,
    IReadOnlyDictionary<string, CurveQuotes> Curves,
    IReadOnlyDictionary<string, double> Fx)
{
    public static string CurveKey(string currency, string index) => $"{currency}/{index}";

    public MarketUpdate Update => new(Sequence, Id, AsOf, Origin);
}

/// <summary>The starting point of a synthetic market: its curves, FX rates against a base currency, and step bounds.</summary>
public sealed record SyntheticMarketConfig(
    string ScenarioVersion,
    int Seed,
    DateTimeOffset Start,
    TimeSpan Step,
    IReadOnlyList<CurveQuotes> Curves,
    IReadOnlyDictionary<string, double> Fx,
    double MaxLevelStepBp,
    double MaxSlopeStepBp,
    double MaxFxLogStep);

/// <summary>
/// A repeatable synthetic market (docs/07 §7): snapshot n is a pure function of the configuration and n. Each
/// step moves every curve by a bounded level and slope change shared within a currency, and every FX rate by a
/// bounded change in log space. Nothing here claims to be arbitrage-free or realistic; every snapshot says
/// synthetic.
/// </summary>
public sealed class SyntheticMarket(SyntheticMarketConfig config)
{
    public const string Origin = "synthetic";

    readonly Lock gate = new();
    readonly List<MarketSnapshot> snapshots = [];

    public SyntheticMarketConfig Config => config;

    public MarketSnapshot Snapshot(long sequence)
    {
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        lock (gate)
        {
            while (snapshots.Count <= sequence) snapshots.Add(snapshots.Count == 0 ? First() : Next(snapshots[^1]));
            return snapshots[(int)sequence];
        }
    }

    /// <summary>Resolves an identifier this market issued, or null: an unknown identifier is never taken as the latest.</summary>
    public MarketSnapshot? Resolve(string id)
    {
        var prefix = $"{Origin}:{config.ScenarioVersion}:{config.Seed}:";
        return id.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(id[prefix.Length..], out var n) && n >= 0 ? Snapshot(n) : null;
    }

    MarketSnapshot First() => Make(0, config.Curves.ToDictionary(c => MarketSnapshot.CurveKey(c.Currency, c.Index)), config.Fx);

    MarketSnapshot Next(MarketSnapshot previous)
    {
        var n = previous.Sequence + 1;
        // HashCode is salted per process, so the step's seed is mixed by hand to stay the same across restarts.
        var random = new Random(unchecked((int)(config.Seed * 1_000_003L + n * 7_919L)));
        double Bounded(double max) => (random.NextDouble() * 2 - 1) * max;
        var shocks = previous.Curves.Values.Select(c => c.Currency).Distinct().Order(StringComparer.Ordinal)
            .ToDictionary(c => c, _ => (Level: Bounded(config.MaxLevelStepBp) / 100, Slope: Bounded(config.MaxSlopeStepBp) / 100));
        var curves = previous.Curves.ToDictionary(p => p.Key, p =>
        {
            var (level, slope) = shocks[p.Value.Currency];
            var longest = p.Value.Pillars.Max(x => YearsOf(x.Tenor));
            return p.Value with { Pillars = [.. p.Value.Pillars.Select(x => (x.Tenor, x.LevelPercent + level + slope * YearsOf(x.Tenor) / longest))] };
        });
        var fx = previous.Fx.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value * Math.Exp(Bounded(config.MaxFxLogStep)));
        return Make(n, curves, fx);
    }

    MarketSnapshot Make(long n, IReadOnlyDictionary<string, CurveQuotes> curves, IReadOnlyDictionary<string, double> fx) =>
        new($"{Origin}:{config.ScenarioVersion}:{config.Seed}:{n}", n, config.Start + config.Step * n, Origin, config.Seed, config.ScenarioVersion, curves, fx);

    public static double YearsOf(Tenor tenor) => tenor.Unit switch
    {
        TenorUnit.Day => tenor.Count / 365.0,
        TenorUnit.Week => tenor.Count * 7 / 365.0,
        TenorUnit.Month => tenor.Count / 12.0,
        _ => tenor.Count,
    };
}
