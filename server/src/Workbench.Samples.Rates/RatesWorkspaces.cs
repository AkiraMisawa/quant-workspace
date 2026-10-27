using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Rates;

/// <summary>
/// The market the server publishes (docs/07 §7): a synthetic market advanced one snapshot at a time. A ticker
/// calls <see cref="AdvanceAsync"/> on a clock; tests call it directly. Snapshots are kept for the process, so
/// every identifier a frame or a Run names stays resolvable while it runs.
/// </summary>
public sealed class MarketFeed(SyntheticMarket market, RatesLiveService live)
{
    readonly Lock gate = new();
    long next;

    public MarketSnapshot? Latest { get; private set; }

    public MarketSnapshot? Resolve(string id) => market.Resolve(id);

    public async Task<MarketSnapshot> AdvanceAsync()
    {
        MarketSnapshot snapshot;
        lock (gate)
        {
            snapshot = market.Snapshot(next++);
            Latest = snapshot;
        }
        // Each tick also stops what nobody has watched for the idle limit.
        live.SweepIdle();
        await live.PublishAsync(snapshot.Update);
        return snapshot;
    }
}

/// <summary>A line as the grid shows it: its fields, which of them the user set, and why it is not priceable.</summary>
public sealed record LineView(
    string Line,
    IReadOnlyDictionary<string, JsonNode?> Fields,
    IReadOnlyList<string> UserFields,
    bool Priceable,
    int? ParLeg,
    DateOnly? Spot,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Held);

/// <summary>One period of a line's cashflows, for the detail panel; the user's edited fields are named.</summary>
public sealed record PeriodView(
    string Entity,
    int Leg,
    DateOnly AccrualStart,
    DateOnly AccrualEnd,
    DateOnly PaymentDate,
    double Notional,
    double? Rate,
    double YearFraction,
    IReadOnlyList<string> UserFields);

public sealed record ExchangeView(int Leg, ExchangeKind Kind, DateOnly PaymentDate, double Amount, string Currency);

public sealed record CashflowsView(string Line, long Revision, IReadOnlyList<PeriodView> Periods, IReadOnlyList<ExchangeView> Exchanges);

/// <summary>An edit held back for the user, as in the XVA page: a conflict with the schedule's new values, or a lost period.</summary>
public sealed record RatesHeldEdit(string Scope, string Entity, string Field, JsonNode? Value, IReadOnlyDictionary<string, JsonNode?>? Source, bool Acknowledged);

public sealed record RatesRunLink(string RunId, long Revision);

public sealed record RatesWorkspaceView(
    string WorkspaceId,
    long Revision,
    DateOnly ValuationDate,
    IReadOnlyList<LineView> Lines,
    IReadOnlyList<RatesHeldEdit> Conflicts,
    IReadOnlyList<RatesHeldEdit> Lost,
    CarrySummary? Rederived,
    IReadOnlyList<RatesRunLink> Runs,
    LiveStatus? Live);

/// <summary>The frame a capture names: what the user saw, so a newer frame is never saved under it (spine 6).</summary>
public sealed record FrameIdentity(int SubscriptionRevision, long InputRevision, string MarketSnapshotId);

/// <summary>
/// The Rates workspace application service: what the endpoints call (docs/07 §4's coordinator). It names lines,
/// checks types, and reports every accepted edit to the workspace's live subscriptions. The generic kernel stores
/// and carries the edits; the module derives the schedules.
/// </summary>
public sealed class RatesWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, RatesLiveService live, MarketFeed market,
    ConventionRegistry conventions)
{
    /// <summary>The source a Rates workspace acquires: none; everything priced is created or derived in it (ADR-0004).</summary>
    public static readonly ISourceVersion NoAcquiredSource = new EmptySource("rates:none");

    public static ISourceVersion Resolve(JsonObject descriptor) => NoAcquiredSource;

    public WorkspaceState Create(string user, DateOnly valuationDate) =>
        workspaces.Create(user, RatesEditRules.App, Context(valuationDate), NoAcquiredSource);

    public IReadOnlyList<WorkspaceState> OwnedBy(string user) => [.. workspaces.OwnedBy(user).Where(w => w.App == RatesEditRules.App)];

    public RatesWorkspaceView View(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        var links = runs.List(new RunQuery(user, 0, 20, workspaceId)).Runs
            .Select(r => new RatesRunLink(r.RunId, r.Relations.WorkspaceRevision ?? throw new InvalidOperationException($"Run {r.RunId} names a workspace but no revision.")))
            .ToList();
        var lines = RatesMaterialiser.Lines(state, conventions).Select(l => new LineView(l.Line, l.Fields, l.UserFields, l.Priceable, l.ParLeg, l.Spot,
            [.. l.Legs.Select(g => $"{g.Profile.Id}@{g.Profile.Version}")], l.Issues, l.Held)).ToList();
        return new(state.WorkspaceId, state.Revision, RatesEditRules.ValuationDate(state), lines,
            [.. state.Conflicts.Values.OrderBy(c => c.Override.Seq).Select(c => new RatesHeldEdit(c.Override.Target.Scope, c.Override.Target.Entity, c.Override.Target.Field, c.Override.Value, c.Source, false))],
            [.. state.Lost.Values.OrderBy(l => l.Override.Seq).Select(l => new RatesHeldEdit(l.Override.Target.Scope, l.Override.Target.Entity, l.Override.Target.Field, l.Override.Value, null, l.Acknowledged))],
            state.Rederived, links, live.StatusOf(user, workspaceId));
    }

    public CashflowsView Cashflows(string workspaceId, string user, string line)
    {
        var state = Owned(workspaceId, user);
        if (!state.Created.ContainsKey(line)) throw new WorkspaceRefused("UnknownEntity", $"No line {line}.");
        var effective = RatesMaterialiser.Line(state, line, RatesEditRules.ValuationDate(state), conventions);
        return new(line, state.Revision,
            [.. effective.Legs.SelectMany((leg, i) => leg.Periods.Select(p =>
                new PeriodView(p.Entity, i + 1, p.AccrualStart, p.AccrualEnd, p.PaymentDate, p.Notional, p.Rate, p.YearFraction, p.UserFields)))],
            [.. effective.Legs.SelectMany((leg, i) => leg.Exchanges.Select(e => new ExchangeView(i + 1, e.Kind, e.PaymentDate, e.Amount, leg.Terms.Currency)))]);
    }

    /// <summary>Adds a line of a product with every field empty but its kind, named by the revision that creates it.</summary>
    public (WorkspaceState State, string Line) AddLine(string workspaceId, long revision, string user, RatesProduct product)
    {
        Owned(workspaceId, user);
        var line = $"L{revision + 1}";
        return (Reported(workspaceId, workspaces.Create(workspaceId, revision, user, RatesEditRules.LinesScope, line, LineFields.Template(product))), line);
    }

    public WorkspaceState RemoveLine(string workspaceId, long revision, string user, string line)
    {
        Owned(workspaceId, user);
        return Reported(workspaceId, workspaces.Remove(workspaceId, revision, user, line));
    }

    public WorkspaceState EditLine(string workspaceId, long revision, string user, string line, string field, JsonNode? value)
    {
        var state = Owned(workspaceId, user);
        if (!state.Created.ContainsKey(line)) throw new WorkspaceRefused("UnknownEntity", $"No line {line}.");
        if (!LineFields.All.Contains(field) || field == LineFields.Product)
            throw new WorkspaceRefused("NotEditable", $"{field} is not an editable line field.");
        var target = new EditTarget(RatesEditRules.LinesScope, line, field);
        // Types only: whether the terms make a line is the resolver's, and shows on the row.
        if (value is not null and not JsonValue)
            throw new WorkspaceRefused("InvalidValue", $"{field} takes a single value.", [target]);
        return Reported(workspaceId, workspaces.Set(workspaceId, revision, user, target, value));
    }

    public WorkspaceState EditCashflow(string workspaceId, long revision, string user, string line, string period, string field, JsonNode? value)
    {
        var state = Owned(workspaceId, user);
        var scope = RatesEditRules.SchedulePrefix + line;
        if (!state.Derived.TryGetValue(scope, out var schedule) || schedule.Read(period, field) is null)
            throw new WorkspaceRefused("UnknownEntity", $"{line} has no period {period} with a {field}.");
        var target = new EditTarget(scope, period, field);
        string? problem = field switch
        {
            "accrualStart" or "accrualEnd" or "paymentDate" =>
                value is JsonValue d && d.TryGetValue<string>(out var text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? null : "A date is written yyyy-MM-dd.",
            "notional" => value is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? null : "A notional is a number.",
            "rate" => value is null || value is JsonValue r && r.GetValueKind() == System.Text.Json.JsonValueKind.Number ? null : "A rate is a number or empty.",
            _ => $"{field} is not an editable cashflow field.",
        };
        if (problem is not null) throw new WorkspaceRefused("InvalidValue", problem, [target]);
        return Reported(workspaceId, workspaces.Set(workspaceId, revision, user, target, value));
    }

    public WorkspaceState Resolve(string workspaceId, long revision, string user, string scope, string entity, string field, string resolution)
    {
        var state = Owned(workspaceId, user);
        var target = new EditTarget(scope, entity, field);
        if (!state.Overrides.ContainsKey(target) && !state.Conflicts.ContainsKey(target) && !state.Lost.ContainsKey(target))
            throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit.");
        return Reported(workspaceId, resolution switch
        {
            "reset" => workspaces.Reset(workspaceId, revision, user, target),
            "keep" => workspaces.KeepOverride(workspaceId, revision, user, target),
            "takeSource" => workspaces.TakeSource(workspaceId, revision, user, target),
            "acknowledge" => workspaces.AcknowledgeLost(workspaceId, revision, user, target),
            _ => throw new WorkspaceRefused("UnknownResolution", $"{resolution} is not reset, keep, takeSource or acknowledge."),
        });
    }

    /// <summary>Moves the grid's valuation date. Every schedule is derived again, and its cashflow edits carry or are held.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeValuationDate(string workspaceId, long revision, string user, DateOnly valuationDate)
    {
        Owned(workspaceId, user);
        var (state, _) = workspaces.Reacquire(workspaceId, revision, user, Context(valuationDate), NoAcquiredSource);
        return (Reported(workspaceId, state), state.Rederived ?? new CarrySummary(0, 0, 0));
    }

    /// <summary>
    /// Price once (docs/07 §3): freezes the revision the user saw and prices its priceable lines against the market
    /// snapshot the user names; the lines left out are listed in the result with why. Refused when nothing is priceable.
    /// </summary>
    public SubmissionResult Price(string workspaceId, long revision, string user, string idempotencyKey, string marketSnapshotId)
    {
        Owned(workspaceId, user);
        var snapshot = market.Resolve(marketSnapshotId)
            ?? throw new WorkspaceRefused("UnknownMarket", $"Market snapshot {marketSnapshotId} cannot be resolved.");
        LiveInput? input = null;
        var freeze = workspaces.Freeze(workspaceId, revision, state =>
        {
            input = RatesAcceptedInput.Of(state, conventions);
            var priced = input.Lines.Select(l => l.RowKey).ToHashSet();
            return entity => priced.Contains(LineOf(entity));
        });
        if (input!.Lines.Count == 0)
            throw new WorkspaceRefused("NothingToPrice", "No line in this workspace can be priced.");
        var accepted = RatesAcceptedInput.Build(freeze.WorkspaceId, freeze.Revision, freeze.Context, snapshot.Update, input, freeze.Overrides);
        var reference = store.Put($"rates-input-{freeze.WorkspaceId}-r{freeze.Revision}-{Sanitise(snapshot.Id)}", RatesContracts.AcceptedInput, RatesContracts.Version, accepted);
        return runs.Submit(new RunSubmission(RatesModule.Price(reference), freeze.Context,
            new JsonObject { ["workspaceId"] = freeze.WorkspaceId, ["revision"] = freeze.Revision, ["marketSnapshotId"] = snapshot.Id },
            idempotencyKey, user, RunOrigin.Pricer, new RunRelations(freeze.WorkspaceId, freeze.Revision)));
    }

    /// <summary>Starts ticking the workspace's current revision. Later edits move it to their revision.</summary>
    public string StartLive(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        return live.Start(user, workspaceId, RatesAcceptedInput.Of(state, conventions));
    }

    public void StopLive(string subscriptionId, string user) => live.Stop(user, subscriptionId);

    /// <summary>
    /// Captures the frame the user saw as a Run (docs/07 §3): refused when the shown frame is no longer that one,
    /// so a newer frame or a newer edit is never saved under what the user chose.
    /// </summary>
    public SubmissionResult Capture(string subscriptionId, string user, FrameIdentity seen, string idempotencyKey)
    {
        var workspaceId = live.WorkspaceOf(user, subscriptionId);
        Owned(workspaceId, user);
        var shown = live.Shown(user, subscriptionId)
            ?? throw new WorkspaceRefused("NoFrame", "The subscription has shown no frame yet.");
        var d = shown.Delivery;
        if (d.SubscriptionRevision != seen.SubscriptionRevision || d.InputRevision != seen.InputRevision || d.Market.SnapshotId != seen.MarketSnapshotId)
            throw new WorkspaceRefused("FrameMoved",
                $"The shown frame is {d.Market.SnapshotId} at revision {d.InputRevision}, not the one captured ({seen.MarketSnapshotId} at {seen.InputRevision}).");
        var context = Context(shown.Input.ValuationDate);
        var accepted = RatesAcceptedInput.Build(workspaceId, d.InputRevision, context, d.Market, shown.Input, null);
        var key = $"{workspaceId}-r{d.InputRevision}-{Sanitise(d.Market.SnapshotId)}-{subscriptionId}-s{d.SubscriptionRevision}";
        var inputRef = store.Put($"rates-capture-input-{key}", RatesContracts.AcceptedInput, RatesContracts.Version, accepted);
        var (lines, risk) = RatesResults.Datasets(new RatesBatchResult(d.Provider, d.CalculationMode, d.Rows), shown.Input.Lines, shown.Input.NotPriced, d.Market,
            $"{subscriptionId}/s{d.SubscriptionRevision}/{d.Market.SnapshotId}");
        var frameRef = store.Put($"rates-capture-frame-{key}", RatesContracts.LineResult, RatesContracts.Version, lines);
        var riskRef = store.Put($"rates-capture-risk-{key}", RatesContracts.Risk, RatesContracts.Version, risk);
        return runs.Submit(new RunSubmission(RatesModule.Capture(inputRef, frameRef, riskRef), context,
            new JsonObject
            {
                ["workspaceId"] = workspaceId, ["revision"] = d.InputRevision, ["marketSnapshotId"] = d.Market.SnapshotId,
                ["subscriptionId"] = subscriptionId, ["subscriptionRevision"] = d.SubscriptionRevision,
            },
            idempotencyKey, user, RunOrigin.Pricer, new RunRelations(workspaceId, d.InputRevision)));
    }

    public void Delete(string workspaceId, string user)
    {
        Owned(workspaceId, user);
        foreach (var (owner, id) in live.OnWorkspace(workspaceId)) live.Stop(owner, id, LiveStopReason.Deleted);
        workspaces.Delete(workspaceId, user);
    }

    /// <summary>Reports an accepted revision to the workspace's running subscriptions, which then price only it.</summary>
    WorkspaceState Reported(string workspaceId, WorkspaceState state)
    {
        var subscriptions = live.OnWorkspace(workspaceId);
        if (subscriptions.Count == 0) return state;
        var input = RatesAcceptedInput.Of(state, conventions);
        foreach (var (owner, id) in subscriptions) live.InputChanged(owner, id, input);
        return state;
    }

    WorkspaceState Owned(string workspaceId, string user)
    {
        var state = workspaces.Find(workspaceId);
        if (state is null || state.App != RatesEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No Rates workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return state;
    }

    /// <summary>The line an entity belongs to: a line itself, or a period named <c>{line}/leg{n}/…</c>.</summary>
    static string LineOf(string entity) => entity.Split('/')[0];

    static string Sanitise(string id) => id.Replace(':', '-');

    static JsonObject Context(DateOnly valuationDate) => new() { ["valuationDate"] = valuationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
}
