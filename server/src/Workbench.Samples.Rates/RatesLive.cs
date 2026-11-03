namespace Workbench.Samples.Rates;

/// <summary>A fixed market snapshot the source can resolve again; a tick counter alone is not one (docs/07 §7).</summary>
public sealed record MarketUpdate(long Sequence, string SnapshotId, DateTimeOffset AsOf, string Origin);

/// <summary>One calculation a live subscription asks for: this input revision against this snapshot.</summary>
public sealed record LiveWork(string SubscriptionId, int SubscriptionRevision, long InputRevision, MarketUpdate Market);

/// <summary>A completed batch, as the provider returns it for one <see cref="LiveWork"/>.</summary>
public sealed record LiveFrame(LiveWork Work, DateTimeOffset CompletedAt, string CalculationMode, IReadOnlyList<RowResult> Rows);

/// <summary>One row's measures, or why it has none. A failed or unsupported measure is absent, never zero.</summary>
public sealed record RowResult(string RowKey, IReadOnlyDictionary<string, Measure> Measures, string? Failure);

/// <summary>A figure with its unit (a currency, "rate" or "bp"). Transport does not round it.</summary>
public sealed record Measure(double Value, string Unit);

public enum FrameOutcome { Applied, StaleInput, StaleMarket, Stopped }

/// <summary>
/// One live subscription's rules, without a clock or a thread (docs/07 §6, RT03 to RT05). At most one
/// calculation is in flight and at most the newest market update waits behind it; a newer update replaces
/// the waiting one. A frame is shown only when it was taken against the current input revision and a market
/// newer than the last one shown, so an engine slower than the ticks still shows frames, always forward.
/// </summary>
public sealed class LiveSubscription(string subscriptionId, string owner, long inputRevision)
{
    public string SubscriptionId { get; } = subscriptionId;
    public string Owner { get; } = owner;
    public int SubscriptionRevision { get; private set; } = 1;
    public long InputRevision { get; private set; } = inputRevision;
    public bool Running { get; private set; } = true;
    public LiveWork? InFlight { get; private set; }
    public MarketUpdate? Pending { get; private set; }
    public LiveFrame? Shown { get; private set; }
    /// <summary>Market updates replaced before they were calculated; a live display's freshness policy only.</summary>
    public long Coalesced { get; private set; }

    MarketUpdate? latest;
    long lastAppliedSequence = long.MinValue;

    /// <summary>A market update arrived. Returns the work to start now, or null when one is already running.</summary>
    public LiveWork? OnMarket(MarketUpdate update)
    {
        if (!Running || (latest is not null && update.Sequence <= latest.Sequence)) return null;
        latest = update;
        if (InFlight is null) return Start(update);
        if (Pending is not null) Coalesced++;
        Pending = update;
        return null;
    }

    /// <summary>
    /// The accepted input changed through the typed edit path. Frames of the old revision are no longer shown,
    /// and the newest market is calculated again for the new one.
    /// </summary>
    public LiveWork? OnInputChanged(long inputRevision)
    {
        if (inputRevision <= InputRevision)
            throw new InvalidOperationException($"Input revision {inputRevision} does not follow {InputRevision}.");
        InputRevision = inputRevision;
        SubscriptionRevision++;
        lastAppliedSequence = long.MinValue;
        if (!Running || latest is null) return null;
        if (InFlight is null) return Start(latest);
        Pending = latest;
        return null;
    }

    /// <summary>A calculation finished. Decides whether its frame is shown, and returns the next work, if any.</summary>
    public (FrameOutcome Outcome, LiveWork? Next) OnCompleted(LiveFrame frame)
    {
        if (InFlight is not null && frame.Work == InFlight) InFlight = null;
        var outcome = Judge(frame);
        if (outcome == FrameOutcome.Applied)
        {
            Shown = frame;
            lastAppliedSequence = frame.Work.Market.Sequence;
        }
        LiveWork? next = null;
        if (Running && InFlight is null && Pending is not null)
        {
            var pending = Pending;
            Pending = null;
            next = Start(pending);
        }
        return (outcome, next);
    }

    /// <summary>Stops asking for frames. The last shown frame stays, with its own market time; later frames are discarded.</summary>
    public void Stop()
    {
        Running = false;
        Pending = null;
    }

    FrameOutcome Judge(LiveFrame frame)
    {
        if (!Running) return FrameOutcome.Stopped;
        if (frame.Work.SubscriptionId != SubscriptionId || frame.Work.InputRevision != InputRevision
            || frame.Work.SubscriptionRevision != SubscriptionRevision) return FrameOutcome.StaleInput;
        return frame.Work.Market.Sequence > lastAppliedSequence ? FrameOutcome.Applied : FrameOutcome.StaleMarket;
    }

    LiveWork Start(MarketUpdate market)
    {
        InFlight = new LiveWork(SubscriptionId, SubscriptionRevision, InputRevision, market);
        return InFlight;
    }
}
