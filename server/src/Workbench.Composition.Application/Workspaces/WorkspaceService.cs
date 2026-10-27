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

    /// <summary>How many edits of each module kind a scope holds, for the confirmation before dropping it.</summary>
    public IReadOnlyDictionary<EditTarget, Override> InScope(string scope) =>
        Overrides.Values.Concat(Conflicts.Values.Select(c => c.Override)).Concat(Lost.Values.Select(l => l.Override))
            .Where(o => o.Target.Scope == scope)
            .ToDictionary(o => o.Target);
}

/// <summary>What re-acquiring did to the overrides (ADR-0003 §7): said to the user after every re-acquisition.</summary>
public sealed record CarrySummary(int Carried, int Conflicted, int Lost);

/// <summary>A frozen revision: what Price submits. The Run keeps this copy; the workspace may later be deleted.</summary>
public sealed record WorkspaceFreeze(
    string WorkspaceId,
    long Revision,
    JsonObject Context,
    string SourceVersion,
    IReadOnlyList<Override> Overrides);

/// <summary>A command refused. Nothing changed.</summary>
public sealed class WorkspaceRefused(string code, string message, IReadOnlyList<EditTarget>? targets = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<EditTarget> Targets { get; } = targets ?? [];
}

/// <summary>
/// Workspaces in process memory (slice 02; durable storage follows). Every command names the revision it was
/// taken against and is refused if the workspace has moved since, so an outcome never depends on timing.
/// </summary>
public sealed class WorkspaceService(IReadOnlyDictionary<string, IEditRules> rules, TimeProvider clock)
{
    readonly Dictionary<string, WorkspaceState> workspaces = [];
    readonly Lock gate = new();

    public WorkspaceState Create(string owner, string app, JsonObject context, ISourceVersion source)
    {
        RulesOf(app);
        var id = $"ws-{Guid.CreateVersion7():N}";
        var log = ImmutableList<EditEntry>.Empty
            .Add(new EditEntry(1, clock.GetUtcNow(), owner, EditKind.Context, null, null, context.DeepClone(), null))
            .Add(new EditEntry(2, clock.GetUtcNow(), owner, EditKind.Acquire, null, null, JsonValue.Create(source.Version), null));
        var state = new WorkspaceState(id, owner, app, (JsonObject)context.DeepClone(), source,
            ImmutableDictionary<EditTarget, Override>.Empty, ImmutableDictionary<EditTarget, Conflict>.Empty,
            ImmutableDictionary<EditTarget, LostTarget>.Empty, log);
        lock (gate) workspaces[id] = state;
        return state;
    }

    public WorkspaceState? Find(string workspaceId)
    {
        lock (gate) return workspaces.GetValueOrDefault(workspaceId);
    }

    public IReadOnlyList<WorkspaceState> OwnedBy(string owner)
    {
        lock (gate) return workspaces.Values.Where(w => w.Owner == owner).ToList();
    }

    public void Delete(string workspaceId, string user)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "Only the workspace's owner deletes it.");
            workspaces.Remove(workspaceId);
        }
    }

    /// <summary>Overrides a field. The field must exist in the current version and not be held as a conflict.</summary>
    public WorkspaceState Set(string workspaceId, long expectedRevision, string user, EditTarget target, JsonNode? value) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (state.Conflicts.ContainsKey(target))
                throw new WorkspaceRefused("ConflictUnresolved", "Keep or take the source value before editing this field again.", [target]);
            if (state.Source.Read(target.Entity, target.Field) is null)
                throw new WorkspaceRefused("UnknownTarget", $"{target.Entity} has no field {target.Field} in this version.", [target]);
            var seq = state.Revision + 1;
            var item = new Override(target, value?.DeepClone(), Carry.BaseOf(target, state.Source, RulesOf(state.App)), state.Source.Version, seq);
            return state with
            {
                Overrides = state.Overrides.SetItem(target, item),
                Lost = state.Lost.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.Set, target, value)),
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
    /// Adopts a new context and source version within the same scopes (a value date change, a refresh), and
    /// checks every override against it: carried, conflicted, or lost.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) Reacquire(string workspaceId, long expectedRevision, string user, JsonObject context, ISourceVersion source)
    {
        CarrySummary summary = new(0, 0, 0);
        var state = Apply(workspaceId, expectedRevision, state =>
        {
            var rules = RulesOf(state.App);
            var all = state.Overrides.Values
                .Concat(state.Conflicts.Values.Select(c => c.Override))
                .Concat(state.Lost.Values.Select(l => l.Override));
            var overrides = ImmutableDictionary.CreateBuilder<EditTarget, Override>();
            var conflicts = ImmutableDictionary.CreateBuilder<EditTarget, Conflict>();
            var lost = ImmutableDictionary.CreateBuilder<EditTarget, LostTarget>();
            foreach (var item in all)
                switch (Carry.Check(item, source, rules))
                {
                    case CarryOutcome.Carried c: overrides[item.Target] = c.Override; break;
                    case CarryOutcome.Conflicted c: conflicts[item.Target] = c.Conflict; break;
                    case CarryOutcome.Lost l: lost[item.Target] = l.Target; break;
                }
            summary = new(overrides.Count, conflicts.Count, lost.Count);
            var seq = state.Revision + 1;
            return state with
            {
                Context = (JsonObject)context.DeepClone(),
                Source = source,
                Overrides = overrides.ToImmutable(),
                Conflicts = conflicts.ToImmutable(),
                Lost = lost.ToImmutable(),
                Log = state.Log
                    .Add(Entry(state, seq, user, EditKind.Context, null, context))
                    .Add(Entry(state, seq + 1, user, EditKind.Acquire, null, JsonValue.Create(source.Version))),
            };
        });
        return (state, summary);
    }

    /// <summary>
    /// Leaves a scope (a counterparty change): drops every edit in it. The caller passes the count the user
    /// confirmed; a scope that holds another count by now is refused, so nothing is dropped unseen.
    /// </summary>
    public WorkspaceState DropScope(string workspaceId, long expectedRevision, string user, string scope, int confirmedCount) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            var dropped = state.InScope(scope);
            if (dropped.Count != confirmedCount)
                throw new WorkspaceRefused("ConfirmationStale", $"The scope holds {dropped.Count} edits; {confirmedCount} were confirmed.");
            var seq = state.Revision + 1;
            return state with
            {
                Overrides = state.Overrides.RemoveRange(dropped.Keys),
                Conflicts = state.Conflicts.RemoveRange(dropped.Keys),
                Lost = state.Lost.RemoveRange(dropped.Keys),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.DropScope, null, JsonValue.Create(scope), $"{dropped.Count} edits")),
            };
        });

    /// <summary>
    /// Freezes the revision for Price. Refused while an entity it would price has an unresolved conflict or an
    /// unacknowledged lost target; entities not priced do not block it.
    /// </summary>
    public WorkspaceFreeze Freeze(string workspaceId, long expectedRevision, Func<string, bool> priced)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var blocking = state.Conflicts.Keys
                .Concat(state.Lost.Where(l => !l.Value.Acknowledged).Select(l => l.Key))
                .Where(t => priced(t.Entity))
                .ToList();
            if (blocking.Count > 0)
                throw new WorkspaceRefused("UnresolvedEdits", "Resolve the conflicts and lost edits on the trades being priced.", blocking);
            return new WorkspaceFreeze(state.WorkspaceId, state.Revision, (JsonObject)state.Context.DeepClone(), state.Source.Version,
                state.Overrides.Values.OrderBy(o => o.Seq).ToList());
        }
    }

    WorkspaceState Apply(string workspaceId, long expectedRevision, Func<WorkspaceState, WorkspaceState> change)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var next = change(state);
            workspaces[workspaceId] = next;
            return next;
        }
    }

    WorkspaceState Get(string workspaceId) =>
        workspaces.GetValueOrDefault(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");

    IEditRules RulesOf(string app) =>
        rules.GetValueOrDefault(app) ?? throw new InvalidOperationException($"No module registered edit rules for {app}.");

    static Conflict ConflictOn(WorkspaceState state, EditTarget target) =>
        state.Conflicts.GetValueOrDefault(target) ?? throw new WorkspaceRefused("NoConflict", "This field has no conflict.", [target]);

    EditEntry Entry(WorkspaceState state, long seq, string user, EditKind kind, EditTarget? target, JsonNode? value, string? note = null) =>
        new(seq, clock.GetUtcNow(), user, kind, target is null ? null : RulesOf(state.App).KindOf(target), target, value?.DeepClone(), note);
}
