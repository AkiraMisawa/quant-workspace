using System.Collections.Concurrent;
using Workbench.Samples.Rates;

namespace Workbench.Composition.Tests;

// The live service over a provider the test holds (docs/07 §6, §9). Results are released by the test, never by a sleep.
public class RatesLiveServiceTests
{
    static readonly DateOnly ValuationDate = new(2026, 10, 27);

    sealed class HeldProvider : IRatesPricingProvider
    {
        public readonly ConcurrentQueue<(RatesBatchInput Input, TaskCompletionSource<RatesBatchResult> Result)> Calls = new();
        readonly SemaphoreSlim arrived = new(0);
        public string Name => "held";

        public Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<RatesBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Enqueue((input, result));
            arrived.Release();
            return result.Task;
        }

        public async Task<(RatesBatchInput Input, TaskCompletionSource<RatesBatchResult> Result)> NextCall()
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), "No calculation was started.");
            Assert.True(Calls.TryDequeue(out var call));
            return call;
        }

        public static RatesBatchResult Rows(RatesBatchInput input) =>
            new("held", "simulated", [.. input.Lines.Select(l => new RowResult(l.RowKey, new Dictionary<string, Measure> { ["pv"] = new(input.InputRevision, "TST") }, null))]);
    }

    static MarketSnapshot Snapshot(long n) => new($"m:{n}", n, DateTimeOffset.UnixEpoch.AddSeconds(n), "synthetic", 1, "t", new Dictionary<string, CurveQuotes>(), new Dictionary<string, double>());

    static LiveInput Input(long revision) => new(revision, ValuationDate,
        [new PricingLine("L1", revision, Direction.Pay, [], null, false)], []);

    static RatesLiveService Service(IRatesPricingProvider provider) => new(provider, id => Snapshot(long.Parse(id[2..])), TimeProvider.System, TimeSpan.FromMinutes(5));

    static async Task<LiveDelivery> NextFrame(RatesLiveService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (frames, _) = service.Attach("akira", id);
        return await frames.ReadAsync(timeout.Token);
    }

    [Fact] // RT03: an edit during a calculation never lets the old input's result reach the screen
    public async Task An_edit_during_a_calculation_delivers_only_the_new_input()
    {
        var provider = new HeldProvider();
        var service = Service(provider);
        var id = service.Start("akira", "ws-1", Input(1));
        await service.PublishAsync(Snapshot(1).Update);
        var first = await provider.NextCall();

        service.InputChanged("akira", id, Input(2));
        first.Result.SetResult(HeldProvider.Rows(first.Input));
        var second = await provider.NextCall();
        second.Result.SetResult(HeldProvider.Rows(second.Input));

        var frame = await NextFrame(service, id);
        Assert.Equal((2L, 1L), (frame.InputRevision, frame.Market.Sequence));
        Assert.Equal(1, first.Input.InputRevision);
    }

    [Fact] // docs/07 §9.7: a provider failure is a visible failure on every row, not a zero or another provider
    public async Task A_provider_failure_reaches_every_row_as_a_failure()
    {
        var provider = new HeldProvider();
        var service = Service(provider);
        var id = service.Start("akira", "ws-1", Input(1));
        await service.PublishAsync(Snapshot(1).Update);

        (await provider.NextCall()).Result.SetException(new InvalidOperationException("worker down"));

        var frame = await NextFrame(service, id);
        Assert.Equal("failed", frame.CalculationMode);
        Assert.All(frame.Rows, r => Assert.Contains("worker down", r.Failure));
        Assert.All(frame.Rows, r => Assert.Empty(r.Measures));
    }

    [Fact] // RT05, docs/07 §9.3: stop ends the stream, starts nothing more and leaves no subscription behind
    public async Task Stop_ends_the_stream_and_discards_a_late_result()
    {
        var provider = new HeldProvider();
        var service = Service(provider);
        var id = service.Start("akira", "ws-1", Input(1));
        await service.PublishAsync(Snapshot(1).Update);
        var call = await provider.NextCall();
        var (frames, _) = service.Attach("akira", id);

        service.Stop("akira", id);
        call.Result.SetResult(HeldProvider.Rows(call.Input));
        await service.PublishAsync(Snapshot(2).Update);

        await frames.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(frames.TryRead(out _));
        Assert.Equal(0, service.Count);
        Assert.True(provider.Calls.IsEmpty);
    }

    [Fact] // RT07: another user cannot read or stop a subscription
    public void Another_user_cannot_reach_a_subscription()
    {
        var service = Service(new HeldProvider());
        var id = service.Start("akira", "ws-1", Input(1));

        Assert.Throws<KeyNotFoundException>(() => service.Attach("someone", id));
        Assert.Throws<KeyNotFoundException>(() => service.Stop("someone", id));
    }

    /// <summary>A clock the test moves by hand.</summary>
    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact] // Owner, 2026-10-27: a subscription nobody watches stops after the idle limit, and says so
    public void An_unwatched_subscription_stops_after_the_idle_limit()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var service = new RatesLiveService(new HeldProvider(), id => Snapshot(long.Parse(id[2..])), clock, TimeSpan.FromMinutes(5));
        var id = service.Start("akira", "ws-1", Input(1));
        var (_, detach) = service.Attach("akira", id);

        clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(0, service.SweepIdle());
        detach.Dispose();
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.Equal(0, service.SweepIdle());
        clock.Now += TimeSpan.FromMinutes(1);

        Assert.Equal(1, service.SweepIdle());
        Assert.Equal(0, service.Count);
        var status = service.StatusOf("akira", "ws-1")!;
        Assert.Equal((false, LiveStopReason.Idle, id), (status.Running, status.StoppedBy, status.SubscriptionId));
    }

    [Fact] // Owner, 2026-10-27: pages join the workspace's one subscription, and a new page sees the frame shown now
    public async Task Pages_share_one_subscription_and_a_late_page_sees_the_shown_frame()
    {
        var service = Service(new FakeRatesPricing());
        var id = service.Start("akira", "ws-1", Input(1));
        var first = await AttachedFrame(service, id, Snapshot(1));

        Assert.Equal(id, service.Start("akira", "ws-1", Input(1)));
        var (late, _) = service.Attach("akira", id);
        Assert.True(late.TryRead(out var shown));
        Assert.Equal(first.Market.SnapshotId, shown!.Market.SnapshotId);
        Assert.Equal(1, service.Count);
    }

    static async Task<LiveDelivery> AttachedFrame(RatesLiveService service, string id, MarketSnapshot snapshot)
    {
        var (frames, _) = service.Attach("akira", id);
        await service.PublishAsync(snapshot.Update);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await frames.ReadAsync(timeout.Token);
    }
}
