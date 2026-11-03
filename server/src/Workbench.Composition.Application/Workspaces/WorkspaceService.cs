using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Workbench.Composition.Application.Workspaces;

/// <summary>
/// A workspace as it stands: its context, the source version its overrides are read against, the user's
/// overrides, the ones held back as conflicts or lost targets, and the log that explains how it got here.
/// The revision is the last log entry's sequence number.
/// </summary>
public sealed record WorkspaceState(
    string WorkspaceId,
    string Owner,
    string App,
    JsonObject Context,
    ISourceVersion Source,
    ImmutableDictionary<EditTarget, Override> Overrides,
    ImmutableDictionary<EditTarget, Conflict> Conflicts,
    ImmutableDictionary<EditTarget, LostTarget> Lost,
    ImmutableList<EditEntry> Log)
{
    public long Revision => Log.Count == 0 ? 0 : Log[^1].Seq;

    /// <summary>The entities the user created in this workspace (ADR-0004), by entity.</summary>
    public ImmutableDictionary<string, CreatedEntity> Created { get; init; } = ImmutableDictionary<string, CreatedEntity>.Empty;

    /// <summary>The module's derived sources as of this revision, by derivation scope (ADR-0004). Never stored: derived again on load.</summary>
    public ImmutableDictionary<string, ISourceVersion> Derived { get; init; } = ImmutableDictionary<string, ISourceVersion>.Empty;

    /// <summary>The source an edit on this target is read against: its derivation scope's, its created entity's, or the acquired one.</summary>
    public ISourceVersion SourceOf(EditTarget target) =>
        Derived.TryGetValue(target.Scope, out var derived) ? derived
        : Created.TryGetValue(target.Entity, out var created) ? new CreatedSource(created)
        : Source;

    /// <summary>What the derivation run by the last command did to the edits (ADR-0004); null when no derived scope moved. Not stored.</summary>
    public CarrySummary? Rederived { get; init; }

    /// <summary>True when the target is read from the acquired source, which a re-acquisition replaces.</summary>
    public bool IsAcquired(EditTarget target) => !Derived.ContainsKey(target.Scope) && !Created.ContainsKey(target.Entity);

    /// <summary>How many edits of each module kind a scope holds, for the confirmation before dropping it.</summary>
    public IReadOnlyDictionary<EditTarget, Override> InScope(string scope) =>
        Overrides.Values.Concat(Conflicts.Values.Select(c => c.Override)).Concat(Lost.Values.Select(l => l.Override))
            .Where(o => o.Target.Scope == scope)
            .ToDictionary(o => o.Target);
}

/// <summary>What re-acquiring did to the overrides (ADR-0003 §7): said to the user after every re-acquisition.</summary>
public sealed record CarrySummary(int Carried, int Conflicted, int Lost);

/// <summary>
/// A frozen revision: what Price submits. The Run keeps this copy; the workspace may later be deleted.
/// <see cref="State"/> is the immutable state at that revision, from which the module builds its accepted input.
/// </summary>
public sealed record WorkspaceFreeze(
    string WorkspaceId,
    long Revision,
    JsonObject Context,
    string SourceVersion,
    IReadOnlyList<Override> Overrides,
    WorkspaceState State);

/// <summary>A command refused. Nothing changed.</summary>
public sealed class WorkspaceRefused(string code, string message, IReadOnlyList<EditTarget>? targets = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<EditTarget> Targets { get; } = targets ?? [];
}

/// <summary>
/// The workspace commands. Every command names the revision it was taken against and is refused if the
/// workspace has moved since, so an outcome never depends on timing. Each accepted command is written to the
/// store before it is answered (ADR-0003 §6); states are cached in memory once loaded.
/// </summary>
public sealed class WorkspaceService(IReadOnlyDictionary<string, IEditRules> rules, TimeProvider clock, IWorkspaceStore? store = null)
{
    readonly IWorkspaceStore store = store ?? new InMemoryWorkspaceStore();
    readonly Dictionary<string, WorkspaceState> workspaces = [];
    readonly Lock gate = new();

    public WorkspaceState Create(string owner, string app, JsonObject context, ISourceVersion source, RunOrigin? origin = null)
    {
        RulesOf(app);
        var id = $"ws-{Guid.CreateVersion7():N}";
        var log = ImmutableList<EditEntry>.Empty
            .Add(new EditEntry(1, clock.GetUtcNow(), owner, EditKind.Context, null, null, context.DeepClone(), null, origin))
            .Add(new EditEntry(2, clock.GetUtcNow(), owner, EditKind.Acquire, null, null, Acquired(source), null, origin));
        var state = new WorkspaceState(id, owner, app, (JsonObject)context.DeepClone(), source,
            ImmutableDictionary<EditTarget, Override>.Empty, ImmutableDictionary<EditTarget, Conflict>.Empty,
            ImmutableDictionary<EditTarget, LostTarget>.Empty, log);
        lock (gate)
        {
            store.Insert(state);
            workspaces[id] = state;
        }
        return state;
    }

    public WorkspaceState? Find(string workspaceId)
    {
        lock (gate) return Cached(workspaceId);
    }

    public IReadOnlyList<WorkspaceState> OwnedBy(string owner)
    {
        lock (gate) return [.. store.OwnedBy(owner).Select(id => Cached(id) ?? throw new InvalidOperationException($"Workspace {id} is listed but cannot be loaded."))];
    }

    /// <summary>Deletes the workspace. Runs keep their own frozen copies; the store keeps the log that explains them.</summary>
    public void Delete(string workspaceId, string user)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "Only the workspace's owner deletes it.");
            store.Delete(workspaceId, clock.GetUtcNow());
            workspaces.Remove(workspaceId);
        }
    }

    /// <summary>Overrides a field. The field must exist in the current version and not be held as a conflict.</summary>
    public WorkspaceState Set(string workspaceId, long expectedRevision, string user, EditTarget target, JsonNode? value, RunOrigin? origin = null) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (state.Conflicts.ContainsKey(target))
                throw new WorkspaceRefused("ConflictUnresolved", "Keep or take the source value before editing this field again.", [target]);
            var source = state.SourceOf(target);
            if (source.Read(target.Entity, target.Field) is null)
                throw new WorkspaceRefused("UnknownTarget", $"{target.Entity} has no field {target.Field} in this version.", [target]);
            var seq = state.Revision + 1;
            var item = new Override(target, value?.DeepClone(), Carry.BaseOf(target, source, RulesOf(state.App)), source.Version, seq);
            return state with
            {
                Overrides = state.Overrides.SetItem(target, item),
                Lost = state.Lost.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.Set, target, value, origin: origin)),
            };
        });

    /// <summary>Removes an override: the field follows its source or its automatic rule again.</summary>
    public WorkspaceState Reset(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (!state.Overrides.ContainsKey(target) && !state.Lost.ContainsKey(target))
                throw new WorkspaceRefused("NotOverridden", "There is no override on this field.", [target]);
            var seq = state.Revision + 1;
            return state with
            {
                Overrides = state.Overrides.Remove(target),
                Lost = state.Lost.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.Reset, target, null)),
            };
        });

    /// <summary>Resolves a conflict by keeping the user's value; its base moves to the new version.</summary>
    public WorkspaceState KeepOverride(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            var conflict = ConflictOn(state, target);
            var seq = state.Revision + 1;
            var kept = conflict.Override with { Base = conflict.Source, BaseVersion = conflict.SourceVersion, Seq = seq };
            return state with
            {
                Overrides = state.Overrides.SetItem(target, kept),
                Conflicts = state.Conflicts.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.KeepOverride, target, kept.Value)),
            };
        });

    /// <summary>Resolves a conflict by dropping the user's value for the new source value.</summary>
    public WorkspaceState TakeSource(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            ConflictOn(state, target);
            var seq = state.Revision + 1;
            return state with
            {
                Conflicts = state.Conflicts.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.TakeSource, target, null)),
            };
        });

    /// <summary>Records that the user has seen an override that lost its target. It stays out of pricing.</summary>
    public WorkspaceState AcknowledgeLost(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (!state.Lost.TryGetValue(target, out var lost))
                throw new WorkspaceRefused("NotLost", "This override has not lost its target.", [target]);
            var seq = state.Revision + 1;
            return state with
            {
                Lost = state.Lost.SetItem(target, lost with { Acknowledged = true }),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.AcknowledgeLost, target, null)),
            };
        });

    /// <summary>
    /// Creates an entity the workspace holds (ADR-0004), with the module's initial field values as its source
    /// values. The module names the entity; one that exists already is refused.
    /// </summary>
    public WorkspaceState Create(string workspaceId, long expectedRevision, string user, string scope, string entity, JsonObject fields,
        RunOrigin? origin = null) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (state.Created.ContainsKey(entity))
                throw new WorkspaceRefused("EntityExists", $"{entity} exists already.");
            var seq = state.Revision + 1;
            var target = new EditTarget(scope, entity, "*");
            return state with
            {
                Created = state.Created.Add(entity, new CreatedEntity(scope, entity, (JsonObject)fields.DeepClone(), seq)),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.Create, target, fields, origin: origin)),
            };
        });

    /// <summary>
    /// Removes a created entity. Its own overrides and conflicts end with it, counted in the log entry's note: the
    /// user removed them, so they are not lost targets to acknowledge. What was derived from it is checked when the
    /// derivation runs.
    /// </summary>
    public WorkspaceState Remove(string workspaceId, long expectedRevision, string user, string entity, RunOrigin? origin = null) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (!state.Created.TryGetValue(entity, out var created))
                throw new WorkspaceRefused("UnknownEntity", $"{entity} was not created in this workspace.");
            var seq = state.Revision + 1;
            var gone = state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys)
                .Where(t => t.Entity == entity && !state.Derived.ContainsKey(t.Scope)).Distinct().ToList();
            return state with
            {
                Created = state.Created.Remove(entity),
                Overrides = state.Overrides.RemoveRange(gone),
                Conflicts = state.Conflicts.RemoveRange(gone),
                Lost = state.Lost.RemoveRange(gone),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.Remove, new EditTarget(created.Scope, entity, "*"), null, $"{gone.Count} edits", origin)),
            };
        });

    /// <summary>
    /// Adopts a new context and source version within the same scopes (a value date change, a refresh), and
    /// checks every override against it: carried, conflicted, or lost.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) Reacquire(string workspaceId, long expectedRevision, string user, JsonObject context, ISourceVersion source)
    {
        CarrySummary summary = new(0, 0, 0);
        var state = Apply(workspaceId, expectedRevision, state =>
        {
            (var next, summary) = ReacquireOn(state, user, context, source);
            return next;
        });
        return (state, summary);
    }

    /// <summary>
    /// Leaves a scope (a counterparty change): drops every edit in it. The caller passes the count the user
    /// confirmed; a scope that holds another count by now is refused, so nothing is dropped unseen.
    /// </summary>
    public WorkspaceState DropScope(string workspaceId, long expectedRevision, string user, string scope, int confirmedCount) =>
        Apply(workspaceId, expectedRevision, state => DropOn(state, user, scope, confirmedCount));

    /// <summary>
    /// Moves to another scope in one step (ADR-0003 §6): drops the confirmed edits of the scope being left,
    /// then re-acquires for the new context, checking what remains (a new trade's own edits) as any refresh
    /// does. Either both happen or neither.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeScope(string workspaceId, long expectedRevision, string user,
        string leaving, int confirmedCount, JsonObject context, ISourceVersion source)
    {
        CarrySummary summary = new(0, 0, 0);
        var state = Apply(workspaceId, expectedRevision, state =>
        {
            (var next, summary) = ReacquireOn(DropOn(state, user, leaving, confirmedCount), user, context, source);
            return next;
        });
        return (state, summary);
    }

    (WorkspaceState, CarrySummary) ReacquireOn(WorkspaceState state, string user, JsonObject context, ISourceVersion source)
    {
        // Only what the acquired source holds is checked against the new one; created and derived entities are not
        // in it (ADR-0004), and a derivation that reads the new context runs after the command.
        var (carried, summary) = CarryWhere(state, state.IsAcquired, source);
        var seq = state.Revision + 1;
        var next = carried with
        {
            Context = (JsonObject)context.DeepClone(),
            Source = source,
            Log = state.Log
                .Add(Entry(state, seq, user, EditKind.Context, null, context))
                .Add(Entry(state, seq + 1, user, EditKind.Acquire, null, Acquired(source))),
        };
        return (next, summary);
    }

    /// <summary>Checks the overrides, conflicts and lost edits the filter selects against a source; the rest stay as they are.</summary>
    (WorkspaceState, CarrySummary) CarryWhere(WorkspaceState state, Func<EditTarget, bool> selected, ISourceVersion source)
    {
        var rules = RulesOf(state.App);
        var all = state.Overrides.Values
            .Concat(state.Conflicts.Values.Select(c => c.Override))
            .Concat(state.Lost.Values.Select(l => l.Override))
            .Where(o => selected(o.Target))
            .ToList();
        var overrides = state.Overrides.ToBuilder();
        var conflicts = state.Conflicts.ToBuilder();
        var lost = state.Lost.ToBuilder();
        var (c1, c2, c3) = (0, 0, 0);
        foreach (var item in all)
        {
            overrides.Remove(item.Target);
            conflicts.Remove(item.Target);
            lost.Remove(item.Target);
            switch (Carry.Check(item, source, rules))
            {
                case CarryOutcome.Carried c: overrides[item.Target] = c.Override; c1++; break;
                case CarryOutcome.Conflicted c: conflicts[item.Target] = c.Conflict; c2++; break;
                case CarryOutcome.Lost l: lost[item.Target] = l.Target; c3++; break;
            }
        }
        return (state with { Overrides = overrides.ToImmutable(), Conflicts = conflicts.ToImmutable(), Lost = lost.ToImmutable() }, new(c1, c2, c3));
    }

    /// <summary>
    /// Runs the module's derivation (ADR-0004) and checks the edits of every derivation scope whose version changed
    /// against its new source; a scope that is gone leaves its edits without a target.
    /// </summary>
    WorkspaceState Rederive(WorkspaceState state)
    {
        var derived = RulesOf(state.App).Derive(state).ToImmutableDictionary();
        if (derived.Count == 0 && state.Derived.Count == 0) return state with { Rederived = null };
        var next = state with { Derived = derived, Rederived = null };
        CarrySummary? total = null;
        foreach (var scope in state.Derived.Keys.Union(derived.Keys).Order(StringComparer.Ordinal))
        {
            var before = state.Derived.GetValueOrDefault(scope);
            var after = derived.GetValueOrDefault(scope);
            if (before is not null && after is not null && before.Version == after.Version) continue;
            var source = after ?? new EmptySource($"gone:{state.Revision}");
            (next, var summary) = CarryWhere(next, t => t.Scope == scope, source);
            total = new((total?.Carried ?? 0) + summary.Carried, (total?.Conflicted ?? 0) + summary.Conflicted, (total?.Lost ?? 0) + summary.Lost);
            // The log says why edits in the scope became conflicts or lost: the scope moved to this version.
            next = next with
            {
                Log = next.Log.Add(new EditEntry(next.Revision + 1, clock.GetUtcNow(), "derivation", EditKind.Derive, null, null,
                    new JsonObject { ["scope"] = scope, ["version"] = source.Version },
                    $"{summary.Carried} carried, {summary.Conflicted} conflicted, {summary.Lost} lost")),
            };
        }
        return next with { Rederived = total };
    }

    WorkspaceState DropOn(WorkspaceState state, string user, string scope, int confirmedCount)
    {
        // Entities the user created in the scope go with it, and count as edits the user confirmed (ADR-0004).
        var dropped = state.InScope(scope);
        var created = state.Created.Values.Where(c => c.Scope == scope).Select(c => c.Entity).ToList();
        if (dropped.Count + created.Count != confirmedCount)
            throw new WorkspaceRefused("ConfirmationStale", $"The scope holds {dropped.Count + created.Count} edits; {confirmedCount} were confirmed.");
        var seq = state.Revision + 1;
        return state with
        {
            Created = state.Created.RemoveRange(created),
            Overrides = state.Overrides.RemoveRange(dropped.Keys),
            Conflicts = state.Conflicts.RemoveRange(dropped.Keys),
            Lost = state.Lost.RemoveRange(dropped.Keys),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.DropScope, null, JsonValue.Create(scope), $"{dropped.Count + created.Count} edits")),
        };
    }

    /// <summary>
    /// Freezes the revision for Price. Refused while an entity it would price has an unresolved conflict or an
    /// unacknowledged lost target; entities not priced do not block it.
    /// </summary>
    public WorkspaceFreeze Freeze(string workspaceId, long expectedRevision, Func<string, bool> priced) =>
        Freeze(workspaceId, expectedRevision, _ => priced);

    /// <summary>As <see cref="Freeze(string, long, Func{string, bool})"/>, with the priced entities decided from the state being frozen.</summary>
    public WorkspaceFreeze Freeze(string workspaceId, long expectedRevision, Func<WorkspaceState, Func<string, bool>> pricedIn)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var priced = pricedIn(state);
            var blocking = state.Conflicts.Keys
                .Concat(state.Lost.Where(l => !l.Value.Acknowledged).Select(l => l.Key))
                .Where(t => priced(t.Entity))
                .ToList();
            if (blocking.Count > 0)
                throw new WorkspaceRefused("UnresolvedEdits", "Resolve the conflicts and lost edits on the trades being priced.", blocking);
            return new WorkspaceFreeze(state.WorkspaceId, state.Revision, (JsonObject)state.Context.DeepClone(), state.Source.Version,
                state.Overrides.Values.OrderBy(o => o.Seq).ToList(), state);
        }
    }

    WorkspaceState Apply(string workspaceId, long expectedRevision, Func<WorkspaceState, WorkspaceState> change)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var next = Rederive(change(state));
            store.Commit(state, next, clock.GetUtcNow());
            workspaces[workspaceId] = next;
            return next;
        }
    }

    WorkspaceState? Cached(string workspaceId)
    {
        if (workspaces.TryGetValue(workspaceId, out var state)) return state;
        if (store.Load(workspaceId) is not { } stored) return null;
        // Derived sources are a function of the stored state, so deriving again restores them as they were.
        var loaded = stored with { Derived = RulesOf(stored.App).Derive(stored).ToImmutableDictionary() };
        workspaces[workspaceId] = loaded;
        return loaded;
    }

    WorkspaceState Get(string workspaceId) =>
        Cached(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");

    static JsonObject Acquired(ISourceVersion source) =>
        new() { ["version"] = source.Version, ["descriptor"] = source.Descriptor.DeepClone() };

    IEditRules RulesOf(string app) =>
        rules.GetValueOrDefault(app) ?? throw new InvalidOperationException($"No module registered edit rules for {app}.");

    static Conflict ConflictOn(WorkspaceState state, EditTarget target) =>
        state.Conflicts.GetValueOrDefault(target) ?? throw new WorkspaceRefused("NoConflict", "This field has no conflict.", [target]);

    EditEntry Entry(WorkspaceState state, long seq, string user, EditKind kind, EditTarget? target, JsonNode? value, string? note = null,
        RunOrigin? origin = null) =>
        new(seq, clock.GetUtcNow(), user, kind, target is null ? null : RulesOf(state.App).KindOf(target), target, value?.DeepClone(), note, origin);
}
