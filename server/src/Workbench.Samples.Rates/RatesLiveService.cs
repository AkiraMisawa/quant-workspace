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
    IReadOnlyList<NotPricedLine> NotPriced);

/// <summary>A line left out of a calculation, with why: shown beside the figures, never as a zero.</summary>
public sealed record NotPricedLine(string Line, IReadOnlyList<string> Reasons);

/// <summary>The input a subscription prices: the priceable lines of one revision, and the lines left out with why.</summary>
public sealed record LiveInput(long Revision, DateOnly ValuationDate, IReadOnlyList<PricingLine> Lines, IReadOnlyList<NotPricedLine> NotPriced);

/// <summary>The frame a subscription shows now, with the exact input it was calculated from: what capture saves.</summary>
public sealed record ShownFrame(LiveDelivery Delivery, LiveInput Input);

/// <summary>Why a live subscription stopped: the user asked, nobody watched it for the idle limit, or its workspace went.</summary>
public enum LiveStopReason { User, Idle, Deleted }

/// <summary>
/// A workspace's live state, for a page that opens on it: the running subscription to attach to, or the last one
/// stopped, why and when, with the frame it showed last. A stopped subscription is never resumed; Start makes a new one.
/// </summary>
public sealed record LiveStatus(string SubscriptionId, bool Running, int Viewers, LiveStopReason? StoppedBy, DateTimeOffset? StoppedAt,
    TimeSpan IdleLimit, LiveDelivery? LastFrame);

/// <summary>
/// Runs live subscriptions (docs/07 §4, §6): each market update reaches every running subscription, whose rules
/// (<see cref="LiveSubscription"/>) decide what to calculate; calculations run on at most a fixed number of
/// workers; a frame is delivered only when the rules apply it. No Run is created for a tick. Market updates
/// arrive through <see cref="PublishAsync"/>, so tests drive the service without a clock.
///
/// A workspace has at most one running subscription, on the server, whatever pages watch it: a page attaches to
/// its frames and detaches when it goes. Once nobody has watched it for <c>idleLimit</c>, <see cref="SweepIdle"/>
/// stops it, so nothing is calculated for nobody (decided with the owner, 2026-10-27).
/// </summary>
public sealed class RatesLiveService(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, TimeProvider clock,
    TimeSpan idleLimit, int maxWorkers = 4)
{
    sealed class Entry(LiveSubscription rules, string workspaceId, LiveInput input)
    {
        public LiveSubscription Rules { get; } = rules;
        public string WorkspaceId { get; } = workspaceId;
        public LiveInput Input { get; set; } = input;
        public Dictionary<long, LiveInput> Inputs { get; } = new() { [input.Revision] = input };
        public ShownFrame? Shown { get; set; }
        /// <summary>One channel per attached page: the newest frame only, when that page falls behind.</summary>
        public List<Channel<LiveDelivery>> Viewers { get; } = [];
        /// <summary>Since when nobody has watched it; null while someone does.</summary>
        public DateTimeOffset? UnwatchedSince { get; set; }
    }

    readonly Dictionary<string, Entry> subscriptions = [];
    readonly Dictionary<string, LiveStatus> stopped = [];
    readonly Lock gate = new();
    readonly SemaphoreSlim workers = new(maxWorkers, maxWorkers);
    MarketUpdate? latest;

    /// <summary>Calculations started and not yet finished, for tests and for the stop/disconnect check (docs/07 §9.3).</summary>
    public int InFlight { get { lock (gate) return subscriptions.Values.Count(e => e.Rules.InFlight is not null); } }

    public int Count { get { lock (gate) return subscriptions.Count; } }

    public TimeSpan IdleLimit => idleLimit;

    /// <summary>
    /// Starts the workspace's live subscription, or answers the one already running, which then prices this input if
    /// it is newer. Unwatched until a page attaches.
    /// </summary>
    public string Start(string owner, string workspaceId, LiveInput input)
    {
        var id = $"live-{Guid.CreateVersion7():N}";
        LiveWork? work;
        lock (gate)
        {
            var running = subscriptions.FirstOrDefault(s => s.Value.WorkspaceId == workspaceId);
            if (running.Value is { } existing)
            {
                if (existing.Rules.Owner != owner) throw new KeyNotFoundException($"No live subscription on {workspaceId} for this user.");
                id = running.Key;
                work = input.Revision > existing.Rules.InputRevision ? Changed(existing, input) : null;
            }
            else
            {
                var entry = new Entry(new LiveSubscription(id, owner, input.Revision), workspaceId, input) { UnwatchedSince = clock.GetUtcNow() };
                subscriptions[id] = entry;
                stopped.Remove(workspaceId);
                work = latest is null ? null : entry.Rules.OnMarket(latest);
            }
        }
        Launch(work);
        return id;
    }

    /// <summary>
    /// The accepted input moved through the edit path; the subscription now prices the new revision only. Two
    /// edits may report out of order; a revision no newer than the current one is superseded and ignored.
    /// </summary>
    public void InputChanged(string owner, string subscriptionId, LiveInput input)
    {
        LiveWork? work;
        lock (gate)
        {
            var entry = Owned(owner, subscriptionId);
            if (input.Revision <= entry.Rules.InputRevision) return;
            work = Changed(entry, input);
        }
        Launch(work);
    }

    LiveWork? Changed(Entry entry, LiveInput input)
    {
        entry.Input = input;
        entry.Inputs[input.Revision] = input;
        return entry.Rules.OnInputChanged(input.Revision);
    }

    /// <summary>Stops the subscription for every page watching it. A calculation still running finishes and is discarded.</summary>
    public void Stop(string owner, string subscriptionId, LiveStopReason reason = LiveStopReason.User)
    {
        lock (gate) StopLocked(Owned(owner, subscriptionId), subscriptionId, reason);
    }

    void StopLocked(Entry entry, string subscriptionId, LiveStopReason reason)
    {
        entry.Rules.Stop();
        foreach (var viewer in entry.Viewers) viewer.Writer.TryComplete();
        subscriptions.Remove(subscriptionId);
        if (reason == LiveStopReason.Deleted) stopped.Remove(entry.WorkspaceId);
        else stopped[entry.WorkspaceId] = new LiveStatus(subscriptionId, false, 0, reason, clock.GetUtcNow(), idleLimit, entry.Shown?.Delivery);
    }

    /// <summary>
    /// Attaches a page: it hears the frame shown now, if any, then every later one, the newest only when it falls
    /// behind. Disposing the answer detaches it; the last page to go starts the idle limit.
    /// </summary>
    public (ChannelReader<LiveDelivery> Frames, IDisposable Detach) Attach(string owner, string subscriptionId)
    {
        lock (gate)
        {
            var entry = Owned(owner, subscriptionId);
            var channel = Channel.CreateBounded<LiveDelivery>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            if (entry.Shown is { } shown) channel.Writer.TryWrite(shown.Delivery);
            entry.Viewers.Add(channel);
            entry.UnwatchedSince = null;
            return (channel.Reader, new Detacher(() =>
            {
                lock (gate)
                {
                    if (!entry.Viewers.Remove(channel)) return;
                    if (entry.Viewers.Count == 0) entry.UnwatchedSince = clock.GetUtcNow();
                }
            }));
        }
    }

    sealed class Detacher(Action detach) : IDisposable
    {
        int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) detach(); }
    }

    /// <summary>Stops every subscription nobody has watched for the idle limit. Called on each market tick.</summary>
    public int SweepIdle()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var idle = subscriptions.Where(s => s.Value.UnwatchedSince is { } since && now - since >= idleLimit).ToList();
            foreach (var (id, entry) in idle) StopLocked(entry, id, LiveStopReason.Idle);
            return idle.Count;
        }
    }

    /// <summary>The workspace's live state: its running subscription, or the last one stopped; null when it never ran.</summary>
    public LiveStatus? StatusOf(string owner, string workspaceId)
    {
        lock (gate)
        {
            var running = subscriptions.FirstOrDefault(s => s.Value.WorkspaceId == workspaceId && s.Value.Rules.Owner == owner);
            if (running.Value is { } entry)
                return new LiveStatus(running.Key, true, entry.Viewers.Count, null, null, idleLimit, entry.Shown?.Delivery);
            return stopped.GetValueOrDefault(workspaceId);
        }
    }

    /// <summary>The running subscriptions on a workspace, by owner, for the edit path to report a new input to.</summary>
    public IReadOnlyList<(string Owner, string SubscriptionId)> OnWorkspace(string workspaceId)
    {
        lock (gate) return [.. subscriptions.Where(s => s.Value.WorkspaceId == workspaceId).Select(s => (s.Value.Rules.Owner, s.Key))];
    }

    /// <summary>The frame shown now and its input, or null before the first one.</summary>
    public ShownFrame? Shown(string owner, string subscriptionId)
    {
        lock (gate) return Owned(owner, subscriptionId).Shown;
    }

    /// <summary>The workspace a subscription prices.</summary>
    public string WorkspaceOf(string owner, string subscriptionId)
    {
        lock (gate) return Owned(owner, subscriptionId).WorkspaceId;
    }

    /// <summary>The newest market update published, or null before the first.</summary>
    public MarketUpdate? Latest
    {
        get { lock (gate) return latest; }
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
            {
                var delivery = new LiveDelivery(work.SubscriptionId, work.SubscriptionRevision, work.InputRevision, work.Market,
                    frame.CompletedAt, result.Provider, result.CalculationMode, result.Rows, input.NotPriced);
                entry.Shown = new ShownFrame(delivery, input);
                foreach (var viewer in entry.Viewers) viewer.Writer.TryWrite(delivery);
            }
            foreach (var old in entry.Inputs.Keys.Where(r => r < entry.Rules.InputRevision && entry.Rules.InFlight?.InputRevision != r).ToList())
                entry.Inputs.Remove(old);
        }
        Launch(next);
    }
}
