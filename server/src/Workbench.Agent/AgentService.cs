using System.Text.Json.Nodes;

namespace Workbench.Agent;

/// <summary>What starting a run produced: the run to read (new, or the one this run id already names), or a refusal.</summary>
public abstract record AgentStart
{
    public sealed record Running(AgentRun Run) : AgentStart;
    public sealed record Refused(string Code, string Message) : AgentStart;
}

sealed class AgentRunaway(string message) : Exception(message);

/// <summary>
/// The agent loop (ADR-0007): takes an AG-UI <c>RunAgentInput</c> for the signed-in user, keeps the conversation on
/// the server, asks the configured runtime for each turn, invokes the tool calls it proposes through the tool
/// gateway, and records every step as AG-UI events. The runtime proposes; only this loop acts.
/// </summary>
public sealed class AgentService(IAgentRuntime runtime, AgentToolGateway tools, AgentThreads threads)
{
    /// <summary>Tool calls a run may make before it is stopped as a runaway.</summary>
    public const int MaxToolCalls = 16;

    public AgentRuntimeInfo Runtime => runtime.Info;

    public AgentStart Start(JsonObject input, string userId)
    {
        if (Text(input, "threadId") is not { } threadId) return new AgentStart.Refused("ValidationFailed", "threadId is required.");
        if (Text(input, "runId") is not { } runId) return new AgentStart.Refused("ValidationFailed", "runId is required.");
        // Frontend tools would let a page give the agent powers the server has not registered (AI06).
        if (input["tools"] is JsonArray { Count: > 0 })
            return new AgentStart.Refused("FrontendToolsRefused", "The agent calls the server's registered commands only; tools offered by the client are refused.");
        if (!TryContext(input["context"], out var context, out var contextProblem)) return new AgentStart.Refused("ValidationFailed", contextProblem);

        var thread = threads.GetOrStart(threadId, userId);
        if (thread.Owner != userId) return new AgentStart.Refused("UnknownThread", $"Thread {threadId} does not exist.");

        AgentRun run;
        lock (thread.Gate)
        {
            // The same run id answers the same run again, so a retried request is never run twice.
            if (thread.FindRun(runId) is { } existing) return new AgentStart.Running(existing);
            if (thread.Active is { Finished: false } active)
                return new AgentStart.Refused("RunInProgress", $"Run {active.RunId} is still answering on this thread; stop it or wait for it.");
            if (!TryNewRequests(input["messages"], thread, out var requests, out var problem)) return new AgentStart.Refused("ValidationFailed", problem);
            foreach (var request in requests) thread.Add(request);
            run = new AgentRun(threadId, runId);
            thread.AddRun(run);
            thread.Active = run;
        }
        _ = Task.Run(() => RunAsync(thread, run, context, userId));
        return new AgentStart.Running(run);
    }

    async Task RunAsync(AgentThread thread, AgentRun run, IReadOnlyList<AgentContext> context, string userId)
    {
        try
        {
            run.Append(AgUiEvents.RunStarted(run.ThreadId, run.RunId, runtime.Info));
            await runtime.RunAsync(new(run.ThreadId, run.RunId, thread.Messages, tools.Tools, context), new Sink(thread, run, tools, userId), run.Cancellation);
            run.Append(AgUiEvents.RunFinished(run.ThreadId, run.RunId, cancelled: run.Cancellation.IsCancellationRequested), last: true);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Append(AgUiEvents.RunFinished(run.ThreadId, run.RunId, cancelled: true), last: true);
        }
        catch (AgentRunaway runaway)
        {
            run.Append(AgUiEvents.RunError("TooManyToolCalls", runaway.Message), last: true);
        }
        catch (Exception failure)
        {
            run.Append(AgUiEvents.RunError("AgentFailed", $"{runtime.Info.Label} failed: {failure.Message}"), last: true);
        }
    }

    /// <summary>
    /// What a runtime reaches the run through: every word it says and every tool it calls is recorded here, in order,
    /// and a call is invoked by the gateway, never by the runtime. A cancelled run invokes nothing more.
    /// </summary>
    sealed class Sink(AgentThread thread, AgentRun run, AgentToolGateway tools, string userId) : IAgentTurnSink
    {
        int calls;

        public Task SayAsync(string text, CancellationToken cancellationToken)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
            var messageId = NewId("msg");
            foreach (var e in AgUiEvents.TextMessage(messageId, text)) run.Append(e);
            thread.Add(new AgentMessage(messageId, "assistant", text));
            return Task.CompletedTask;
        }

        public async Task<ToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (++calls > MaxToolCalls)
                throw new AgentRunaway($"The agent asked for more than {MaxToolCalls} tool calls in one run; the run was stopped.");
            var messageId = NewId("msg");
            foreach (var e in AgUiEvents.ToolCall(call, messageId)) run.Append(e);
            thread.Add(new AgentMessage(messageId, "assistant", null, [call]));
            // A command once invoked is not interrupted: its effect may already be in place (ADR-0007 §4).
            var result = await tools.InvokeAsync(call, userId, thread.ThreadId, CancellationToken.None);
            var resultId = NewId("msg");
            thread.Add(AgentMessage.Tool(resultId, call.Id, result.Text));
            run.Append(AgUiEvents.ToolCallResult(resultId, call.Id, result.Text));
            return result;
        }
    }

    /// <summary>The user messages the thread has not seen. History the client sends is not believed (ADR-0007 §2).</summary>
    static bool TryNewRequests(JsonNode? node, AgentThread thread, out List<AgentMessage> requests, out string problem)
    {
        requests = [];
        problem = "";
        if (node is not JsonArray messages)
        {
            problem = "messages is required.";
            return false;
        }
        foreach (var item in messages)
        {
            if (item is not JsonObject message || Text(message, "id") is not { } id || Text(message, "role") is not { } role)
            {
                problem = "Every message has an id and a role.";
                return false;
            }
            if (thread.Knows(id) || requests.Any(r => r.Id == id)) continue;
            if (role != "user")
            {
                problem = $"Message {id} ({role}) is not one this thread holds; only new user messages are taken from the client.";
                return false;
            }
            if (message["content"] is not JsonValue content || !content.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            {
                problem = $"Message {id} has no text.";
                return false;
            }
            requests.Add(AgentMessage.User(id, text));
        }
        if (requests.Count == 0)
        {
            problem = "A run answers a new user message, and none was sent.";
            return false;
        }
        return true;
    }

    static bool TryContext(JsonNode? node, out List<AgentContext> context, out string problem)
    {
        context = [];
        problem = "";
        if (node is null) return true;
        if (node is not JsonArray entries)
        {
            problem = "context is a list.";
            return false;
        }
        foreach (var entry in entries)
        {
            if (entry is not JsonObject e || Text(e, "description") is not { } description || e["value"] is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                problem = "Every context entry has a description and a value, both text.";
                return false;
            }
            context.Add(new(description, text));
        }
        return true;
    }

    static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    static string NewId(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}";
}
