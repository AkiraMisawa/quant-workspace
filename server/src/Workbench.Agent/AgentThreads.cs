using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Workbench.Agent;

/// <summary>
/// One AG-UI run: one request of the user's, answered on the server and recorded as its events in order (ADR-0007 §4).
/// Readers follow the record; a reader that goes away leaves the run going, and a later reader reads from where it
/// stopped.
/// </summary>
public sealed class AgentRun(string threadId, string runId)
{
    readonly List<JsonObject> events = [];
    readonly CancellationTokenSource cancel = new();
    TaskCompletionSource appended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string ThreadId { get; } = threadId;
    public string RunId { get; } = runId;
    public bool Finished { get; private set; }
    internal CancellationToken Cancellation => cancel.Token;

    public int Count
    {
        get { lock (events) return events.Count; }
    }

    /// <summary>Asks the run to stop before its next tool call or turn. A command already invoked is not interrupted.</summary>
    public void Cancel() => cancel.Cancel();

    internal void Append(JsonObject @event, bool last = false)
    {
        TaskCompletionSource wake;
        lock (events)
        {
            if (Finished) throw new InvalidOperationException($"Run {RunId} has finished; nothing follows its last event.");
            events.Add(@event);
            Finished = last;
            wake = appended;
            appended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        wake.TrySetResult();
    }

    /// <summary>The events after the first <paramref name="after"/>, each with its position, until the run finishes.</summary>
    public async IAsyncEnumerable<(int Index, JsonObject Event)> ReadAsync(int after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = Math.Max(after, 0);
        while (true)
        {
            List<JsonObject> batch;
            bool finished;
            Task wait;
            lock (events)
            {
                batch = events.Skip(next).ToList();
                finished = Finished;
                wait = appended.Task;
            }
            foreach (var e in batch) yield return (next++, e);
            if (finished) yield break;
            await wait.WaitAsync(cancellationToken);
        }
    }
}

/// <summary>A conversation (AG-UI's thread): its owner, its messages as the server holds them, and its runs.</summary>
public sealed class AgentThread(string threadId, string owner)
{
    readonly List<AgentMessage> messages = [];
    readonly Dictionary<string, AgentRun> runs = new(StringComparer.Ordinal);

    public string ThreadId { get; } = threadId;
    public string Owner { get; } = owner;
    internal object Gate { get; } = new();
    internal AgentRun? Active { get; set; }

    public IReadOnlyList<AgentMessage> Messages
    {
        get { lock (Gate) return [.. messages]; }
    }

    public AgentRun? ActiveRun
    {
        get { lock (Gate) return Active is { Finished: false } active ? active : null; }
    }

    internal bool Knows(string messageId) => messages.Any(m => m.Id == messageId);

    internal void Add(AgentMessage message)
    {
        lock (Gate) messages.Add(message);
    }

    internal AgentRun? FindRun(string runId) => runs.GetValueOrDefault(runId);

    internal void AddRun(AgentRun run) => runs.Add(run.RunId, run);

    public AgentRun? Run(string runId)
    {
        lock (Gate) return runs.GetValueOrDefault(runId);
    }
}

/// <summary>Conversations, held in memory for the life of the process (ADR-0007 §4: a restart loses them).</summary>
public sealed class AgentThreads
{
    readonly ConcurrentDictionary<string, AgentThread> threads = new(StringComparer.Ordinal);

    public AgentThread? Find(string threadId) => threads.GetValueOrDefault(threadId);

    internal AgentThread GetOrStart(string threadId, string owner) => threads.GetOrAdd(threadId, id => new AgentThread(id, owner));
}
