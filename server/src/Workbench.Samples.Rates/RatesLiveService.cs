using System.Threading.Channels;

namespace Workbench.Samples.Rates;

/// <summary>What a live subscription delivers: a frame shown, with what it was taken against (docs/07 §5).</summary>
public sealed record LiveDelivery(
    string SubscriptionId,
    int SubscriptionRevision,
    long InputRevision,
    MarketUpdate Market,
    DateTimeOffset CompletedAt,
    string Provider,
    string CalculationMode,
    IReadOnlyList<RowResult> Rows,
    IReadOnlyList<string> NotPriced);

/// <summary>The input a subscription prices: the priceable lines of one revision, and the lines left out with why.</summary>
public sealed record LiveInput(long Revision, DateOnly ValuationDate, IReadOnlyList<PricingLine> Lines, IReadOnlyList<string> NotPriced);

/// <summary>
/// Runs live subscriptions (docs/07 §4, §6): each market update reaches every running subscription, whose rules
/// (<see cref="LiveSubscription"/>) decide what to calculate; calculations run on at most a fixed number of
/// workers; a frame is delivered only when the rules apply it. No Run is created for a tick. Market updates
/// arrive through <see cref="PublishAsync"/>, so tests drive the service without a clock.
/// </summary>
public sealed class RatesLiveService(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, TimeProvider clock, int maxWorkers = 4)
{
    sealed class Entry(LiveSubscription rules, string workspaceId, LiveInput input)
    {
        public LiveSubscription Rules { get; } = rules;
        public string WorkspaceId { get; } = workspaceId;
        public LiveInput Input { get; set; } = input;
        public Dictionary<long, LiveInput> Inputs { get; } = new() { [input.Revision] = input };
        public Channel<LiveDelivery> Out { get; } = Channel.CreateBounded<LiveDelivery>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    readonly Dictionary<string, Entry> subscriptions = [];
    readonly Lock gate = new();
    readonly SemaphoreSlim workers = new(maxWorkers, maxWorkers);
    MarketUpdate? latest;

    /// <summary>Calculations started and not yet finished, for tests and for the stop/disconnect check (docs/07 §9.3).</summary>
    public int InFlight { get { lock (gate) return subscriptions.Values.Count(e => e.Rules.InFlight is not null); } }

    public int Count { get { lock (gate) return subscriptions.Count; } }

    public string Start(string owner, string workspaceId, LiveInput input)
    {
        var id = $"live-{Guid.CreateVersion7():N}";
        LiveWork? work;
        lock (gate)
        {
            var entry = new Entry(new LiveSubscription(id, owner, input.Revision), workspaceId, input);
            subscriptions[id] = entry;
            work = latest is null ? null : entry.Rules.OnMarket(latest);
        }
        Launch(work);
        return id;
    }

    /// <summary>The accepted input moved through the edit path; the subscription now prices the new revision only.</summary>
    public void InputChanged(string owner, string subscriptionId, LiveInput input)
    {
        LiveWork? work;
        lock (gate)
        {
            var entry = Owned(owner, subscriptionId);
            entry.Input = input;
            entry.Inputs[input.Revision] = input;
            work = entry.Rules.OnInputChanged(input.Revision);
        }
        Launch(work);
    }

    /// <summary>Stops and forgets the subscription. A calculation still running finishes and is discarded.</summary>
    public void Stop(string owner, string subscriptionId)
    {
        lock (gate)
        {
            var entry = Owned(owner, subscriptionId);
            entry.Rules.Stop();
            entry.Out.Writer.TryComplete();
            subscriptions.Remove(subscriptionId);
        }
    }

    /// <summary>The frames a subscription shows, newest only when the reader falls behind. Ends when it stops.</summary>
    public ChannelReader<LiveDelivery> Frames(string owner, string subscriptionId)
    {
        lock (gate) return Owned(owner, subscriptionId).Out.Reader;
    }

    public Task PublishAsync(MarketUpdate update)
    {
        var started = new List<LiveWork>();
        lock (gate)
        {
            if (latest is not null && update.Sequence <= latest.Sequence) return Task.CompletedTask;
            latest = update;
            foreach (var entry in subscriptions.Values)
                if (entry.Rules.OnMarket(update) is { } work) started.Add(work);
        }
        foreach (var work in started) Launch(work);
        return Task.CompletedTask;
    }

    Entry Owned(string owner, string subscriptionId) =>
        subscriptions.TryGetValue(subscriptionId, out var entry) && entry.Rules.Owner == owner
            ? entry
            : throw new KeyNotFoundException($"No live subscription {subscriptionId} for this user.");

    void Launch(LiveWork? work)
    {
        if (work is null) return;
        _ = Task.Run(() => Calculate(work));
    }

    async Task Calculate(LiveWork work)
    {
        LiveFrame frame;
        LiveInput input;
        lock (gate)
        {
            if (!subscriptions.TryGetValue(work.SubscriptionId, out var entry)) return;
            input = entry.Inputs[work.InputRevision];
        }
        RatesBatchResult result;
        await workers.WaitAsync();
        try
        {
            var market = resolveMarket(work.Market.SnapshotId)
                ?? throw new InvalidOperationException($"Market snapshot {work.Market.SnapshotId} cannot be resolved.");
            result = await provider.PriceBatchAsync(new RatesBatchInput(input.Revision, input.ValuationDate, market, input.Lines), CancellationToken.None);
        }
        catch (Exception failure)
        {
            // A provider failure is shown as one, on every row: never a switch to another provider, never a zero.
            result = new RatesBatchResult(provider.Name, "failed",
                [.. input.Lines.Select(l => new RowResult(l.RowKey, new Dictionary<string, Measure>(), $"Provider failed: {failure.Message}"))]);
        }
        finally
        {
            workers.Release();
        }
        frame = new LiveFrame(work, clock.GetUtcNow(), result.CalculationMode, result.Rows);

        LiveWork? next;
        lock (gate)
        {
            if (!subscriptions.TryGetValue(work.SubscriptionId, out var entry)) return;
            (var outcome, next) = entry.Rules.OnCompleted(frame);
            if (outcome == FrameOutcome.Applied)
                entry.Out.Writer.TryWrite(new LiveDelivery(work.SubscriptionId, work.SubscriptionRevision, work.InputRevision, work.Market,
                    frame.CompletedAt, result.Provider, result.CalculationMode, result.Rows, input.NotPriced));
            foreach (var old in entry.Inputs.Keys.Where(r => r < entry.Rules.InputRevision && entry.Rules.InFlight?.InputRevision != r).ToList())
                entry.Inputs.Remove(old);
        }
        Launch(next);
    }
}
