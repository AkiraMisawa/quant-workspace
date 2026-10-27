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

    static RatesLiveService Service(IRatesPricingProvider provider) => new(provider, id => Snapshot(long.Parse(id[2..])), TimeProvider.System);

    static async Task<LiveDelivery> NextFrame(RatesLiveService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await service.Frames("akira", id).ReadAsync(timeout.Token);
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
        var frames = service.Frames("akira", id);

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

        Assert.Throws<KeyNotFoundException>(() => service.Frames("someone", id));
        Assert.Throws<KeyNotFoundException>(() => service.Stop("someone", id));
    }
}
