namespace Workbench.Composition.Application.Workspaces;

/// <summary>
/// Where workspaces live between commands (ADR-0003 §6). One generic store serves every domain: it keeps the
/// current state of each workspace and appends its log, and never knows what an entity or a scope means.
/// </summary>
public interface IWorkspaceStore
{
    void Insert(WorkspaceState state);

    /// <summary>
    /// Replaces <paramref name="previous"/> with <paramref name="next"/> and appends the log entries after the
    /// previous revision. Throws <see cref="WorkspaceRefused"/> (<c>RevisionMoved</c>) when the stored
    /// workspace is no longer at the previous revision.
    /// </summary>
    void Commit(WorkspaceState previous, WorkspaceState next, DateTimeOffset at);

    /// <summary>The workspace, or null when it never existed or was deleted.</summary>
    WorkspaceState? Load(string workspaceId);

    IReadOnlyList<string> OwnedBy(string owner);

    /// <summary>Removes the workspace from use. Its log is kept: Runs and audits may still need its explanation.</summary>
    void Delete(string workspaceId, DateTimeOffset at);
}

/// <summary>Process memory only, for tests and for running without a database. It claims no durability.</summary>
public sealed class InMemoryWorkspaceStore : IWorkspaceStore
{
    readonly Dictionary<string, WorkspaceState> states = [];
    readonly Lock gate = new();

    public void Insert(WorkspaceState state)
    {
        lock (gate)
            if (!states.TryAdd(state.WorkspaceId, state))
                throw new InvalidOperationException($"Workspace {state.WorkspaceId} exists already.");
    }

    public void Commit(WorkspaceState previous, WorkspaceState next, DateTimeOffset at)
    {
        lock (gate)
        {
            if (!states.TryGetValue(previous.WorkspaceId, out var stored) || stored.Revision != previous.Revision)
                throw new WorkspaceRefused("RevisionMoved", $"Workspace {previous.WorkspaceId} is no longer at revision {previous.Revision}.");
            states[next.WorkspaceId] = next;
        }
    }

    public WorkspaceState? Load(string workspaceId)
    {
        lock (gate) return states.GetValueOrDefault(workspaceId);
    }

    public IReadOnlyList<string> OwnedBy(string owner)
    {
        lock (gate) return [.. states.Values.Where(s => s.Owner == owner).Select(s => s.WorkspaceId)];
    }

    public void Delete(string workspaceId, DateTimeOffset at)
    {
        lock (gate) states.Remove(workspaceId);
    }
}
