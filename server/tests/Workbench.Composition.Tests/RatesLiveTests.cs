using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The live subscription's rules (docs/07 §6), driven by hand: no clock, no thread, no sleep.
public class RatesLiveTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    static MarketUpdate Tick(long seq) => new(seq, $"syn-{seq}", T0.AddSeconds(seq), "synthetic");

    static LiveFrame Done(LiveWork work) => new(work, T0.AddSeconds(work.Market.Sequence).AddMilliseconds(500), "simulated", []);

    [Fact] // RT03: a result for a superseded input revision never replaces the current row
    public void An_edit_during_a_calculation_discards_the_old_input_result()
    {
        var live = new LiveSubscription("sub-1", "akira", inputRevision: 1);
        var oldWork = live.OnMarket(Tick(1))!;

        Assert.Null(live.OnInputChanged(2));
        var (outcome, next) = live.OnCompleted(Done(oldWork));

        Assert.Equal(FrameOutcome.StaleInput, outcome);
        Assert.Null(live.Shown);
        Assert.Equal((2L, 1L), (next!.InputRevision, next.Market.Sequence));
        Assert.Equal(FrameOutcome.Applied, live.OnCompleted(Done(next)).Outcome);
        Assert.Equal(2, live.Shown!.Work.InputRevision);
    }

    [Fact] // RT04: ticks faster than the engine wait one deep, and the shown frames only move forward
    public void A_slow_engine_shows_newer_frames_without_waiting_for_the_latest_tick()
    {
        var live = new LiveSubscription("sub-1", "akira", 1);
        var first = live.OnMarket(Tick(1))!;
        foreach (var seq in new long[] { 2, 3, 4, 5 }) Assert.Null(live.OnMarket(Tick(seq)));

        var (outcome, next) = live.OnCompleted(Done(first));
        Assert.Null(live.OnMarket(Tick(6)));
        live.OnCompleted(Done(next!));

        Assert.Equal(FrameOutcome.Applied, outcome);
        Assert.Equal(5, live.Shown!.Work.Market.Sequence);
        Assert.Equal(3, live.Coalesced);
        Assert.Equal(6, live.InFlight!.Market.Sequence);
    }

    [Fact] // docs/07 §6: an older market frame is never shown after a newer one
    public void A_frame_older_than_the_shown_one_is_discarded()
    {
        var live = new LiveSubscription("sub-1", "akira", 1);
        var w1 = live.OnMarket(Tick(1))!;
        live.OnMarket(Tick(2));
        var w2 = live.OnCompleted(Done(w1)).Next!;
        live.OnCompleted(Done(w2));

        Assert.Equal(FrameOutcome.StaleMarket, live.OnCompleted(Done(w1)).Outcome);
        Assert.Equal(2, live.Shown!.Work.Market.Sequence);
    }

    [Fact] // RT05: stop keeps the last frame with its own market time and starts nothing more
    public void Stop_keeps_the_last_frame_and_discards_late_ones()
    {
        var live = new LiveSubscription("sub-1", "akira", 1);
        var w1 = live.OnMarket(Tick(1))!;
        live.OnCompleted(Done(w1));
        var w2 = live.OnMarket(Tick(2))!;
        live.OnMarket(Tick(3));

        live.Stop();
        var (outcome, next) = live.OnCompleted(Done(w2));

        Assert.Equal(FrameOutcome.Stopped, outcome);
        Assert.Null(next);
        Assert.Null(live.OnMarket(Tick(4)));
        Assert.Equal((1L, T0.AddSeconds(1)), (live.Shown!.Work.Market.Sequence, live.Shown.Work.Market.AsOf));
    }

    [Fact] // docs/07 §6: the market sequence is ordered; a repeated or older update starts nothing
    public void A_repeated_market_update_is_ignored()
    {
        var live = new LiveSubscription("sub-1", "akira", 1);
        live.OnMarket(Tick(2));

        Assert.Null(live.OnMarket(Tick(2)));
        Assert.Null(live.OnMarket(Tick(1)));
        Assert.Null(live.Pending);
    }
}
