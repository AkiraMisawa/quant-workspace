using System.Text.Json.Nodes;

namespace Workbench.Composition.Application.Workspaces;

// The generic workspace (ADR-0003 §6, §7). The kernel stores what the user changed and checks whether it
// still means what they meant; what an entity, a field or a scope is belongs to the domain module.

/// <summary>
/// What an edit changes: one field of one entity, within a scope. All three are opaque to the kernel; the
/// module that registered the edit kind gives them meaning (for XVA a scope is a counterparty registration).
/// </summary>
public sealed record EditTarget(string Scope, string Entity, string Field);

/// <summary>A source value as read: present (possibly empty), never confused with absent.</summary>
public sealed record SourceValue(JsonNode? Value);

/// <summary>
/// One acquired version of everything a workspace's overrides may point at. <see cref="Read"/> returns
/// null when the entity or the field does not exist in this version.
/// </summary>
public interface ISourceVersion
{
    string Version { get; }

    /// <summary>
    /// What acquires exactly this version again: stored with the workspace, so the workspace can be restored
    /// after a restart. The module that resolves it must answer the same <see cref="Version"/>, or refuse.
    /// </summary>
    JsonObject Descriptor { get; }

    SourceValue? Read(string entity, string field);
}

/// <summary>Restores a module's source version from its descriptor. Registered per workspace app.</summary>
public delegate ISourceVersion SourceResolver(JsonObject descriptor);

/// <summary>
/// A user's override: its value, and the base it overrode, the source values of the field's conflict group
/// in the version current when it was made.
/// </summary>
public sealed record Override(
    EditTarget Target,
    JsonNode? Value,
    IReadOnlyDictionary<string, JsonNode?> Base,
    string BaseVersion,
    long Seq);

/// <summary>An override whose base changed under it. It is not applied until the user decides.</summary>
public sealed record Conflict(Override Override, IReadOnlyDictionary<string, JsonNode?> Source, string SourceVersion);

/// <summary>An override whose entity or field is gone from the current version. It is never moved elsewhere.</summary>
public sealed record LostTarget(Override Override, string SourceVersion, bool Acknowledged);

public enum EditKind
{
    Context,
    Acquire,
    Set,
    Reset,
    KeepOverride,
    TakeSource,
    AcknowledgeLost,
    DropScope,
}

/// <summary>
/// One entry of the append-only edit log. An undo is a new entry, never a removal. <c>DomainKind</c> names
/// the module's edit kind (for example <c>xva.assignment</c>), so one log holds every domain's edits.
/// </summary>
public sealed record EditEntry(
    long Seq,
    DateTimeOffset At,
    string User,
    EditKind Kind,
    string? DomainKind,
    EditTarget? Target,
    JsonNode? Value,
    string? Note);

/// <summary>What a module declares about its edits: which fields are compared together.</summary>
public interface IEditRules
{
    /// <summary>The module's name for this kind of edit, recorded on its log entries.</summary>
    string KindOf(EditTarget target);

    /// <summary>The fields compared as one with this target's field. Must include the field itself.</summary>
    IReadOnlyList<string> ConflictGroup(EditTarget target);
}

/// <summary>Each field is its own group: the default when a module declares nothing.</summary>
public sealed class FieldByField(string kind) : IEditRules
{
    public string KindOf(EditTarget target) => kind;

    public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
}

/// <summary>Where each override stands against a new source version.</summary>
public abstract record CarryOutcome
{
    public sealed record Carried(Override Override) : CarryOutcome;
    public sealed record Conflicted(Conflict Conflict) : CarryOutcome;
    public sealed record Lost(LostTarget Target) : CarryOutcome;
}

public static class Carry
{
    /// <summary>
    /// Checks an override against a new version (ADR-0003 §7): its target gone, its base unchanged, or its
    /// base changed. A conflict group field missing from the new version counts as changed.
    /// </summary>
    public static CarryOutcome Check(Override item, ISourceVersion next, IEditRules rules)
    {
        if (next.Read(item.Target.Entity, item.Target.Field) is null)
            return new CarryOutcome.Lost(new LostTarget(item, next.Version, false));
        var now = BaseOf(item.Target, next, rules);
        var same = now.Count == item.Base.Count
            && item.Base.All(b => now.TryGetValue(b.Key, out var v) && JsonNode.DeepEquals(v, b.Value));
        return same
            ? new CarryOutcome.Carried(item with { BaseVersion = next.Version })
            : new CarryOutcome.Conflicted(new Conflict(item, now, next.Version));
    }

    /// <summary>The source values of a target's conflict group in a version; absent fields are left out.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> BaseOf(EditTarget target, ISourceVersion source, IEditRules rules)
    {
        var group = rules.ConflictGroup(target);
        if (!group.Contains(target.Field))
            throw new InvalidOperationException($"The conflict group of {target.Field} does not contain the field itself.");
        var values = new Dictionary<string, JsonNode?>();
        foreach (var field in group)
            if (source.Read(target.Entity, field) is { } value)
                values[field] = value.Value?.DeepClone();
        return values;
    }
}
