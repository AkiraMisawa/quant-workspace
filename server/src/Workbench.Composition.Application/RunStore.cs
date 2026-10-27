using System.Collections.Concurrent;

namespace Workbench.Composition.Application;

/// <summary>
/// A page of the history, newest first. <c>ResultContracts</c>, when given, keeps the Runs whose workflow
/// declares any of them: how a domain's view finds its own Runs (docs/05 §7).
/// </summary>
public sealed record RunQuery(string? Owner, int Start, int Count, string? WorkspaceId = null, IReadOnlyList<string>? ResultContracts = null);

/// <summary>
/// Where Runs are kept (docs/05). A Run is saved when it is accepted and again when it completes; the
/// idempotency index is saved with it, so a retried submission finds its Run after a restart.
/// </summary>
public interface IRunStore
{
    /// <summary>Saves an accepted Run under its idempotency key. False when the key is taken: nothing is saved.</summary>
    bool Accept(RunRecord run, string idempotencyKey, string digest);

    void Complete(RunRecord run);

    RunRecord? Find(string runId);

    /// <summary>The Run and digest a key was used for, or null.</summary>
    (string RunId, string Digest)? FindKey(string owner, string idempotencyKey);

    /// <summary>Runs in the order they were accepted, newest first, and how many the query matches in all.</summary>
    (IReadOnlyList<RunRecord> Runs, int Total) List(RunQuery query);

    /// <summary>Runs left running by a process that stopped. Called once at start-up.</summary>
    IReadOnlyList<RunRecord> Unfinished();
}

/// <summary>Process memory only, for tests and for running without a database.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    readonly ConcurrentDictionary<string, RunRecord> runs = new();
    readonly ConcurrentDictionary<(string, string), (string RunId, string Digest)> keys = new();

    public bool Accept(RunRecord run, string idempotencyKey, string digest)
    {
        if (!keys.TryAdd((run.Owner, idempotencyKey), (run.RunId, digest))) return false;
        runs[run.RunId] = run;
        return true;
    }

    public void Complete(RunRecord run) => runs[run.RunId] = run;

    public RunRecord? Find(string runId) => runs.GetValueOrDefault(runId);

    public (string RunId, string Digest)? FindKey(string owner, string idempotencyKey) =>
        keys.TryGetValue((owner, idempotencyKey), out var known) ? known : null;

    public (IReadOnlyList<RunRecord> Runs, int Total) List(RunQuery query)
    {
        var all = runs.Values.Where(r => (query.Owner is null || r.Owner == query.Owner) && (query.WorkspaceId is null || r.Relations.WorkspaceId == query.WorkspaceId)
                && (query.ResultContracts is null || r.ResultContracts.Intersect(query.ResultContracts).Any()))
            .OrderByDescending(r => r.AcceptedAt).ThenByDescending(r => r.RunId, StringComparer.Ordinal).ToList();
        return ([.. all.Skip(query.Start).Take(query.Count)], all.Count);
    }

    public IReadOnlyList<RunRecord> Unfinished() => [];
}
