using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Xva;

/// <summary>A sort the grid asked for: a column of <see cref="EffectiveTrade"/> and its direction.</summary>
public sealed record RowSort(string Column, string Direction);

/// <summary>One page of effective rows, in the shape ExGrid's fetching source takes (ADR-0003 §4).</summary>
public sealed record RowPage(IReadOnlyList<EffectiveTrade> Rows, int Start, int TotalCount, string OrderToken, long Revision);

/// <summary>What the workspace panel shows beside the grid.</summary>
public sealed record XvaWorkspaceView(
    string WorkspaceId,
    long Revision,
    JsonObject Context,
    string SourceVersion,
    Registration Registration,
    Agreement Agreement,
    IReadOnlyList<EffectiveCsa> Csas,
    EffectiveCredit Credit,
    int Trades,
    int Overrides,
    IReadOnlyList<HeldEdit> Conflicts,
    IReadOnlyList<HeldEdit> Lost,
    IReadOnlyList<RunLink> Runs);

/// <summary>An edit held back for the user: a conflict (with the source's new values) or a lost target.</summary>
public sealed record HeldEdit(string Scope, string Entity, string Field, JsonNode? Value, IReadOnlyDictionary<string, JsonNode?>? Source, bool Acknowledged);

/// <summary>A Run this workspace submitted (its latest twenty, newest first).</summary>
public sealed record RunLink(string RunId, long Revision);

/// <summary>How many edits a counterparty change would drop, by module edit kind, for the user to confirm.</summary>
public sealed record ScopeDrop(string Scope, int Count, IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// The XVA workspace application service: what the endpoints call. It decides scopes and sources; the generic
/// kernel stores and checks the edits. The user is passed in; who may act is decided before this is called.
/// </summary>
public sealed class XvaWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs)
{
    // Effective rows per immutable state: a page request at the same revision does not materialise again.
    readonly ConditionalWeakTable<WorkspaceState, IReadOnlyList<EffectiveTrade>> materialised = new();

    public static readonly IReadOnlyList<string> SortableColumns =
        ["tradeId", "isNew", "product", "currency", "notional", "maturity", "fixedRate", "schedule", "lastFixing", "csa", "curve", "before", "after"];

    public WorkspaceState Create(string user, string registration, DateOnly valueDate, int existingCount) =>
        workspaces.Create(user, XvaEditRules.App, Context(registration, valueDate, existingCount), new XvaSourceVersion(registration, valueDate, existingCount));

    public XvaWorkspaceView View(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        var source = Source(state);
        var links = runs.List(new RunQuery(user, 0, 20, workspaceId)).Runs
            .Select(r => new RunLink(r.RunId, r.Relations.WorkspaceRevision ?? throw new InvalidOperationException($"Run {r.RunId} names a workspace but no revision.")))
            .ToList();
        return new(state.WorkspaceId, state.Revision, state.Context, source.Version, source.Registration, source.Agreement,
            XvaMaterialiser.Csas(state), XvaMaterialiser.Credit(state), Rows(state).Count, state.Overrides.Count,
            [.. state.Conflicts.Values.OrderBy(c => c.Override.Seq).Select(c => new HeldEdit(c.Override.Target.Scope, c.Override.Target.Entity, c.Override.Target.Field, c.Override.Value, c.Source, false))],
            [.. state.Lost.Values.OrderBy(l => l.Override.Seq).Select(l => new HeldEdit(l.Override.Target.Scope, l.Override.Target.Entity, l.Override.Target.Field, l.Override.Value, null, l.Acknowledged))],
            links);
    }

    public IReadOnlyList<WorkspaceState> OwnedBy(string user) =>
        [.. workspaces.OwnedBy(user).Where(w => w.App == XvaEditRules.App)];

    /// <summary>A page of effective rows under a sort. Refused for an unknown column, never sorted by a guess.</summary>
    public RowPage Page(string workspaceId, string user, int start, int count, IReadOnlyList<RowSort> sorts)
    {
        if (start < 0 || count < 1) throw new WorkspaceRefused("InvalidRange", "A range starts at 0 or later and holds at least one row.");
        var state = Owned(workspaceId, user);
        var keys = sorts.Select(sort =>
        {
            if (!SortableColumns.Contains(sort.Column))
                throw new WorkspaceRefused("UnknownColumn", $"Rows cannot be sorted by {sort.Column}.");
            var descending = sort.Direction switch
            {
                "ascending" => false,
                "descending" => true,
                _ => throw new WorkspaceRefused("InvalidSort", $"Sort direction {sort.Direction} is not ascending or descending."),
            };
            return (sort.Column, Descending: descending);
        }).ToList();
        IEnumerable<EffectiveTrade> rows = Rows(state);
        // A stable sort: rows equal under every key keep the source's order.
        var ordered = keys.Count == 0 ? rows : rows.Order(Comparer<EffectiveTrade>.Create((a, b) =>
        {
            foreach (var (column, descending) in keys)
                if (Compare(Key(a, column), Key(b, column), descending) is var c && c != 0) return c;
            return 0;
        }));
        var all = ordered.ToList();
        // Unsorted, the order is the source's and moves only with a new version; sorted, any edit may move it.
        var token = sorts.Count == 0 ? state.Source.Version : $"{state.Source.Version}/r{state.Revision}";
        var page = start >= all.Count ? [] : all.GetRange(start, Math.Min(count, all.Count - start));
        return new(page, start, all.Count, token, state.Revision);
    }

    public WorkspaceState Edit(string workspaceId, long revision, string user, string entity, string field, JsonNode? value)
    {
        var state = Owned(workspaceId, user);
        if (!Editable(entity, field))
            throw new WorkspaceRefused("NotEditable", $"{entity}.{field} is not editable in this workspace.");
        Validate(state, entity, field, value);
        return workspaces.Set(workspaceId, revision, user, XvaEditRules.Target(Source(state), entity, field), value);
    }

    public WorkspaceState Resolve(string workspaceId, long revision, string user, string entity, string field, string resolution)
    {
        var target = Held(Owned(workspaceId, user), entity, field);
        return resolution switch
        {
            "reset" => workspaces.Reset(workspaceId, revision, user, target),
            "keep" => workspaces.KeepOverride(workspaceId, revision, user, target),
            "takeSource" => workspaces.TakeSource(workspaceId, revision, user, target),
            "acknowledge" => workspaces.AcknowledgeLost(workspaceId, revision, user, target),
            _ => throw new WorkspaceRefused("UnknownResolution", $"{resolution} is not reset, keep, takeSource or acknowledge."),
        };
    }

    /// <summary>Moves the value date: a new version of the same counterparty's trades; edits carry or are held.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeValueDate(string workspaceId, long revision, string user, DateOnly valueDate)
    {
        var source = Source(Owned(workspaceId, user));
        var count = source.Descriptor["existingCount"]!.GetValue<int>();
        return workspaces.Reacquire(workspaceId, revision, user, Context(source.Registration.Key, valueDate, count),
            new XvaSourceVersion(source.Registration.Key, valueDate, count));
    }

    /// <summary>What leaving the current counterparty would drop, for the confirmation (ADR-0003 §6).</summary>
    public ScopeDrop PreviewCounterpartyChange(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        var scope = $"cp:{Source(state).Registration.Key}";
        var rules = new XvaEditRules();
        var held = state.InScope(scope);
        return new(scope, held.Count, held.Keys.GroupBy(rules.KindOf).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Changes the counterparty: the confirmed edits of the previous one are dropped, a new trade's own edits stay.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeCounterparty(string workspaceId, long revision, string user, string registration, int confirmedCount)
    {
        var source = Source(Owned(workspaceId, user));
        if (source.Registration.Key == registration)
            throw new WorkspaceRefused("SameCounterparty", $"The workspace is already on {registration}.");
        var next = new XvaSourceVersion(registration, source.ValueDate, source.Descriptor["existingCount"]!.GetValue<int>());
        return workspaces.ChangeScope(workspaceId, revision, user, $"cp:{source.Registration.Key}", confirmedCount,
            Context(registration, source.ValueDate, source.Descriptor["existingCount"]!.GetValue<int>()), next);
    }

    /// <summary>Price: freezes the revision the user saw and submits one Run for it.</summary>
    public SubmissionResult Price(string workspaceId, long revision, string user, string idempotencyKey)
    {
        Owned(workspaceId, user);
        return runs.Submit(XvaPrice.Submission(workspaces, store, workspaceId, revision, idempotencyKey, user));
    }

    public void Delete(string workspaceId, string user) => workspaces.Delete(workspaceId, user);

    IReadOnlyList<EffectiveTrade> Rows(WorkspaceState state) => materialised.GetValue(state, s => XvaMaterialiser.Trades(s));

    WorkspaceState Owned(string workspaceId, string user)
    {
        var state = workspaces.Find(workspaceId);
        if (state is null || state.App != XvaEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No XVA workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return state;
    }

    static XvaSourceVersion Source(WorkspaceState state) => (XvaSourceVersion)state.Source;

    static JsonObject Context(string registration, DateOnly valueDate, int existingCount) => new()
    {
        ["registration"] = registration,
        ["valueDate"] = valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["portfolioSize"] = existingCount,
    };

    static bool Editable(string entity, string field) => (Entity.Split(entity).Kind, field) switch
    {
        (Entity.Trade, var f) => XvaMaterialiser.EditableFields.Contains(f),
        (Entity.Csa, "curve") => true,
        (Entity.Credit, "cvaRecovery") => true,
        _ => false,
    };

    // Type checks only: the meaning (eligibility, availability) is the materialiser's, and shows on the row.
    static void Validate(WorkspaceState state, string entity, string field, JsonNode? value)
    {
        string? problem = field switch
        {
            "notional" => value is JsonValue v && v.TryGetValue<decimal>(out var n) && n != 0 ? null : "A notional is a non-zero number.",
            "fixedRate" => value is null || value is JsonValue r && r.TryGetValue<decimal>(out _) ? null : "A fixed rate is a number or empty.",
            "cvaRecovery" => value is null || value is JsonValue c && c.TryGetValue<decimal>(out var rec) && rec is >= 0 and < 1 ? null : "A CVA recovery is empty or from 0 up to 1.",
            "before" or "after" => value is JsonValue b && b.TryGetValue<bool>(out _) ? null : "Before and After are true or false.",
            "schedule" => value is JsonValue s && s.TryGetValue<string>(out var t) && t is "3M" or "6M" ? null : "A schedule is 3M or 6M.",
            "csa" or "curve" => value is JsonValue x && x.TryGetValue<string>(out var id) && id.Length > 0 ? null : $"A {field} is an identifier.",
            _ => null,
        };
        if (problem is not null) throw new WorkspaceRefused("InvalidValue", problem, [XvaEditRules.Target(Source(state), entity, field)]);
    }

    static EditTarget Held(WorkspaceState state, string entity, string field) =>
        state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys).FirstOrDefault(t => t.Entity == entity && t.Field == field)
        ?? throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit.");

    static IComparable? Key(EffectiveTrade row, string column) => column switch
    {
        "tradeId" => row.TradeId,
        "isNew" => row.IsNew,
        "product" => row.Product,
        "currency" => row.Currency,
        "notional" => row.Notional,
        "maturity" => row.Maturity,
        "fixedRate" => row.FixedRate,
        "schedule" => row.Schedule,
        "lastFixing" => row.LastFixing,
        "csa" => row.Csa,
        "curve" => row.Curve,
        "before" => row.Before,
        "after" => row.After,
        _ => throw new InvalidOperationException(column),
    };

    // As ExGrid's reference semantics (its ADR-0023): a Blank sorts last in both directions, and text compares
    // OrdinalIgnoreCase.
    static int Compare(IComparable? a, IComparable? b, bool descending) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string x, string y) => (descending ? -1 : 1) * string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        _ => (descending ? -1 : 1) * a.CompareTo(b),
    };
}
