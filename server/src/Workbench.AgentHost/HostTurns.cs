using System.Collections.Concurrent;
using System.Threading.Channels;
using Workbench.AgentHost.Contracts;

namespace Workbench.AgentHost;

/// <summary>
/// The turns the host is answering, by run id. A turn writes its lines to a channel the response reads, and waits on a
/// pending call until the workbench posts its result. Aborting cancels the turn; so does the workbench going away.
/// </summary>
public sealed class HostTurns(IModelBackend backend)
{
    readonly ConcurrentDictionary<string, Turn> turns = new(StringComparer.Ordinal);

    /// <summary>Starts a turn, or answers null when one with this run id is already answering.</summary>
    public Turn? Start(HostTurn request, CancellationToken connection)
    {
        var turn = new Turn(request.RunId, CancellationTokenSource.CreateLinkedTokenSource(connection));
        if (!turns.TryAdd(request.RunId, turn)) return null;
        _ = Task.Run(async () =>
        {
            try
            {
                await backend.RunAsync(request, turn, turn.Cancellation.Token);
                turn.Lines.Writer.TryWrite(turn.Cancellation.IsCancellationRequested ? HostLine.Failed("The turn was aborted.") : HostLine.Done);
            }
            catch (OperationCanceledException) when (turn.Cancellation.IsCancellationRequested)
            {
                turn.Lines.Writer.TryWrite(HostLine.Failed("The turn was aborted."));
            }
            catch (Exception failure)
            {
                turn.Lines.Writer.TryWrite(HostLine.Failed(failure.Message));
            }
            finally
            {
                turn.Lines.Writer.TryComplete();
                turns.TryRemove(request.RunId, out _);
                turn.Cancellation.Dispose();
            }
        });
        return turn;
    }

    public bool Answer(string runId, string callId, string content) =>
        turns.TryGetValue(runId, out var turn) && turn.Answer(callId, content);

    public bool Abort(string runId)
    {
        if (!turns.TryGetValue(runId, out var turn)) return false;
        try
        {
            turn.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn ended between the lookup and the cancel; there is nothing left to abort.
        }
        return true;
    }

    public sealed class Turn(string runId, CancellationTokenSource cancellation) : IModelTurnSink
    {
        readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pending = new(StringComparer.Ordinal);

        public string RunId { get; } = runId;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Channel<HostLine> Lines { get; } = Channel.CreateUnbounded<HostLine>(new() { SingleReader = true });

        public void Say(string text)
        {
            if (!string.IsNullOrEmpty(text)) Lines.Writer.TryWrite(HostLine.Said(text));
        }

        public async Task<string> CallAsync(string id, string name, string arguments, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(id, result)) throw new InvalidOperationException($"Tool call {id} was asked for twice in one turn.");
            Lines.Writer.TryWrite(HostLine.Call(id, name, arguments));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Cancellation.Token);
            return await result.Task.WaitAsync(linked.Token);
        }

        internal bool Answer(string callId, string content) => pending.TryRemove(callId, out var result) && result.TrySetResult(content);
    }
}
