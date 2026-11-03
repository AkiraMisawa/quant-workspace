namespace Workbench.Samples.Rates;

/// <summary>What one line asks the provider for. The legs are the effective ones: the user's cashflow edits applied.</summary>
public sealed record PricingLine(string RowKey, long InputRevision, Direction Direction, IReadOnlyList<EffectiveLeg> Legs, int? ParLeg, bool Risk);

/// <summary>A fixed batch against a fixed snapshot (docs/07 §5). No mutable "latest market" is looked up inside.</summary>
public sealed record RatesBatchInput(long InputRevision, DateOnly ValuationDate, MarketSnapshot Market, IReadOnlyList<PricingLine> Lines);

public sealed record RatesBatchResult(string Provider, string CalculationMode, IReadOnlyList<RowResult> Rows);

/// <summary>The rates pricing port (docs/07 §4). A real worker and the fake implement the same contract.</summary>
public interface IRatesPricingProvider
{
    string Name { get; }
    Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken);
}

/// <summary>Measure names on the wire. A par rate and a fair spread are different measures (docs/08).</summary>
public static class Measures
{
    public const string Pv = "pv";
    public const string ParRate = "parRate";
    public const string FairSpread = "fairSpread";
    public static string Delta(string curve, Tenor pillar) => $"delta:{curve}:{pillar}";
}

/// <summary>
/// The deterministic fake provider. Its figures come from a toy curve (flat-forward between pillar levels,
/// continuously compounded) and say <c>simulated</c>: they move with edits and ticks and mean nothing
/// financially. Risk is a 1bp difference per pillar, standing in for the engine's automatic-differentiation
/// sensitivities (C38) until a real worker returns them.
/// </summary>
public sealed class FakeRatesPricing : IRatesPricingProvider
{
    public const string Mode = "simulated";
    public string Name => "fake-rates/1";

    public Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
    {
        var rows = input.Lines.Select(line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return Price(line, input); }
            catch (PricingFailure failure) { return new RowResult(line.RowKey, new Dictionary<string, Measure>(), failure.Message); }
        }).ToList();
        return Task.FromResult(new RatesBatchResult(Name, Mode, rows));
    }

    sealed class PricingFailure(string message) : Exception(message);

    static RowResult Price(PricingLine line, RatesBatchInput input)
    {
        var reporting = line.Legs[0].Terms.Currency;
        double Pv(MarketSnapshot market, double? parQuote) => Value(line, input.ValuationDate, market, reporting, parQuote);
        var measures = new Dictionary<string, Measure>();

        double? par = null;
        if (line.ParLeg is { } p)
        {
            // PV is linear in the solved leg's quote, so two evaluations give the quote at which it is zero.
            var (a, b) = (Pv(input.Market, 0), Pv(input.Market, 1));
            if (Math.Abs(b - a) < 1e-12) throw new PricingFailure("The solved leg's quote does not move the PV.");
            par = -a / (b - a);
            measures[line.Legs[p - 1].Terms.Floating ? Measures.FairSpread : Measures.ParRate] = new(par.Value, $"%/leg{p}");
        }
        var pv = Pv(input.Market, par);
        measures[Measures.Pv] = new(pv, reporting);

        if (line.Risk)
            foreach (var curve in line.Legs.Select(l => CurveOf(l, input.Market)).Select(c => MarketSnapshot.CurveKey(c.Currency, c.Index)).Distinct())
            {
                var quotes = input.Market.Curves[curve];
                foreach (var (tenor, _) in quotes.Pillars)
                {
                    var bumped = input.Market with
                    {
                        Curves = input.Market.Curves.ToDictionary(c => c.Key, c => c.Key != curve ? c.Value
                            : c.Value with { Pillars = [.. c.Value.Pillars.Select(x => (x.Tenor, x.Tenor == tenor ? x.LevelPercent + 0.01 : x.LevelPercent))] }),
                    };
                    measures[Measures.Delta(curve, tenor)] = new(Pv(bumped, par) - pv, $"{reporting}/bp");
                }
            }
        return new RowResult(line.RowKey, measures, null);
    }

    /// <summary>The line's PV in the reporting currency: receive leg 2 and pay leg 1 when paying, the reverse when receiving.</summary>
    static double Value(PricingLine line, DateOnly valuationDate, MarketSnapshot market, string reporting, double? parQuote)
    {
        double total = 0;
        for (var n = 1; n <= line.Legs.Count; n++)
        {
            var leg = line.Legs[n - 1];
            var discount = CurveOf(leg, market);
            double Df(DateOnly d) => d <= valuationDate ? 1 : Math.Exp(-Level(discount, Years(valuationDate, d)) / 100 * Years(valuationDate, d));
            var gearing = leg.Terms.Detail.Gearing ?? 1;
            double value = 0;
            foreach (var p in leg.Periods)
            {
                if (p.PaymentDate <= valuationDate) continue;
                var quote = n == line.ParLeg ? parQuote : p.Rate;
                if (quote is null) throw new PricingFailure($"{p.Entity} has no rate.");
                var coupon = leg.Terms.Floating
                    ? (gearing * (Df(p.AccrualStart) / Df(p.AccrualEnd) - 1) / p.YearFraction + quote.Value / 100) * p.YearFraction
                    : quote.Value / 100 * p.YearFraction;
                value += p.Notional * coupon * Df(p.PaymentDate);
            }
            foreach (var e in leg.Exchanges.Where(e => e.PaymentDate > valuationDate))
                value += (e.Kind == ExchangeKind.Final ? e.Amount : -e.Amount) * Df(e.PaymentDate);
            var fx = leg.Terms.Currency == reporting ? 1 : Fx(market, leg.Terms.Currency, reporting);
            var received = (line.Direction == Direction.Pay) == (n == 2);
            total += (received ? 1 : -1) * value * fx;
        }
        return total;
    }

    /// <summary>
    /// The toy curve a leg is projected and discounted on: its index's curve, or for a fixed leg the currency's only
    /// curve. A real provider discounts on the collateral's curve; the fake does not pretend to.
    /// </summary>
    static CurveQuotes CurveOf(EffectiveLeg leg, MarketSnapshot market)
    {
        if (market.Curves.TryGetValue(MarketSnapshot.CurveKey(leg.Terms.Currency, leg.Terms.Index), out var own)) return own;
        if (leg.Terms.Floating) throw new PricingFailure($"The market has no curve {leg.Terms.Currency}/{leg.Terms.Index}.");
        var candidates = market.Curves.Values.Where(c => c.Currency == leg.Terms.Currency).ToList();
        return candidates.Count == 1 ? candidates[0]
            : throw new PricingFailure($"The market has {candidates.Count} {leg.Terms.Currency} curves; the fixed leg's is not determined.");
    }

    static double Years(DateOnly from, DateOnly to) => (to.DayNumber - from.DayNumber) / 365.0;

    static double Level(CurveQuotes curve, double years)
    {
        var points = curve.Pillars.Select(p => (T: SyntheticMarket.YearsOf(p.Tenor), L: p.LevelPercent)).OrderBy(p => p.T).ToList();
        if (years <= points[0].T) return points[0].L;
        for (var i = 1; i < points.Count; i++)
            if (years <= points[i].T)
                return points[i - 1].L + (points[i].L - points[i - 1].L) * (years - points[i - 1].T) / (points[i].T - points[i - 1].T);
        return points[^1].L;
    }

    /// <summary>Units of <paramref name="to"/> per unit of <paramref name="from"/>, from rates quoted as units of each currency per base unit.</summary>
    static double Fx(MarketSnapshot market, string from, string to)
    {
        double PerBase(string ccy) => market.Fx.TryGetValue(ccy, out var r) ? r : throw new PricingFailure($"The market has no FX rate for {ccy}.");
        return PerBase(to) / PerBase(from);
    }
}

public static class PricingLines
{
    /// <summary>The priceable lines of a workspace revision, as the provider takes them. Lines that are not priceable are left to the caller.</summary>
    public static PricingLine From(EffectiveLine line, long inputRevision) => line.Priceable
        ? new(line.Line, inputRevision, line.Terms!.Direction, line.Legs, line.ParLeg, line.Terms.Risk)
        : throw new InvalidOperationException($"{line.Line} is not priceable: {string.Join("; ", line.Issues.Concat(line.Held))}");
}
