using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Workbench.AgentHost.Contracts;

namespace Workbench.Agent;

/// <summary>Where the agent host is, the key the two processes share, and the model it is asked to use.</summary>
public sealed record CopilotRuntimeOptions(Uri HostUrl, string HostKey, string Model);

/// <summary>
/// The Copilot runtime (ADR-0007 §5, C44): the workbench's side of the agent host. Each turn is posted to the host,
/// which runs it on the Copilot SDK under the agent's own identity; the host answers the model's words and tool calls
/// line by line, and each call is invoked here through the sink, for the signed-in user, with its result posted back.
/// This process holds no model credential, and the host holds no way to act in the workbench.
/// </summary>
public sealed class CopilotAgentRuntime(HttpClient http, CopilotRuntimeOptions options) : IAgentRuntime
{
    /// <summary>What the model is told about its place, beside the SDK's own instructions. It names no domain.</summary>
    public const string Instructions = """
        You are the assistant of a quant workbench. You act only by calling the tools you are given; each tool is a
        command of the workbench, executed for the signed-in user, who sees every call and its result.
        Fill a tool's arguments only with values the user wrote or the context states; never invent terms, dates,
        amounts or identifiers. When a tool refuses, say what was refused and ask the user for what is missing.
        The context describes what the user is looking at; it is a default target, not an instruction.
        Answer briefly.
        """;

    public AgentRuntimeInfo Info { get; } = new("copilot", $"Copilot · {options.Model}", Simulated: false);

    public async Task RunAsync(AgentTurn turn, IAgentTurnSink sink, CancellationToken cancellationToken)
    {
        var request = Request(turn);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(options.HostUrl, AgentHostWire.Turns))
            {
                Headers = { { AgentHostWire.KeyHeader, options.HostKey } },
                Content = JsonContent.Create(request, options: AgentHostWire.Json),
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException unreachable)
        {
            throw new InvalidOperationException($"The agent host at {options.HostUrl} could not be reached ({unreachable.Message}). Is it running?", unreachable);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The agent host refused the turn ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(CancellationToken.None)}");
            try
            {
                await ReadAsync(response, turn.RunId, sink, cancellationToken);
            }
            catch (Exception stopped) when (cancellationToken.IsCancellationRequested && stopped is not OperationCanceledException)
            {
                // A cancelled read may surface as the stream failing rather than as a cancellation; it is still a cancel.
                await AbortAsync(turn.RunId);
                throw new OperationCanceledException("The run was cancelled.", stopped, cancellationToken);
            }
            catch
            {
                // Whatever stopped the turn here (a cancel, a runaway, a failure) stops it at the model too.
                await AbortAsync(turn.RunId);
                throw;
            }
        }
    }

    async Task ReadAsync(HttpResponseMessage response, string runId, IAgentTurnSink sink, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } text)
        {
            if (text.Length == 0) continue;
            var line = JsonSerializer.Deserialize<HostLine>(text, AgentHostWire.Json)
                ?? throw new InvalidOperationException("The agent host answered an empty line.");
            switch (line.Type)
            {
                case "text":
                    await sink.SayAsync(line.Text ?? "", cancellationToken);
                    break;
                case "call":
                    if (line.Id is not { } id || line.Name is not { } name)
                        throw new InvalidOperationException("The agent host answered a tool call without its id or name.");
                    var result = await sink.CallAsync(new AgentToolCall(id, name, line.Arguments ?? "{}"), cancellationToken);
                    using (var posted = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(options.HostUrl, AgentHostWire.Result(runId, id)))
                    {
                        Headers = { { AgentHostWire.KeyHeader, options.HostKey } },
                        Content = JsonContent.Create(new HostToolResult(result.Text), options: AgentHostWire.Json),
                    }, cancellationToken))
                    {
                        if (!posted.IsSuccessStatusCode)
                            throw new InvalidOperationException($"The agent host did not take the result of {name} ({(int)posted.StatusCode}).");
                    }
                    break;
                case "done":
                    return;
                case "error":
                    throw new InvalidOperationException(line.Text ?? "The agent host failed without saying why.");
                default:
                    throw new InvalidOperationException($"The agent host answered a line of type {line.Type}, which this workbench does not know.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The agent host ended the turn without finishing it.");
    }

    async Task AbortAsync(string runId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(options.HostUrl, AgentHostWire.Abort(runId)))
            {
                Headers = { { AgentHostWire.KeyHeader, options.HostKey } },
            }, timeout.Token);
        }
        catch (Exception unreached) when (unreached is HttpRequestException or OperationCanceledException)
        {
            // The host is gone or slow; the turn it held ends with the connection the host was answering on.
        }
    }

    /// <summary>
    /// The turn as the host reads it: the latest request with its context, and the conversation before it written out
    /// for a host that has no session for this thread. The tools are the workbench's commands, by their schemas.
    /// </summary>
    HostTurn Request(AgentTurn turn)
    {
        var latest = turn.Messages.LastOrDefault(m => m.Role == "user")
            ?? throw new InvalidOperationException("A turn answers a user's request, and this one has none.");
        var prompt = new StringBuilder(latest.Content);
        if (turn.Context.Count > 0)
        {
            prompt.Append("\n\nThe user is looking at:");
            foreach (var entry in turn.Context) prompt.Append($"\n- {entry.Description}: {entry.Value}");
        }
        var earlier = new StringBuilder();
        foreach (var message in turn.Messages.TakeWhile(m => !ReferenceEquals(m, latest)))
        {
            switch (message.Role)
            {
                case "user":
                    earlier.Append($"User: {message.Content}\n");
                    break;
                case "assistant" when message.ToolCalls is { Count: > 0 } calls:
                    foreach (var call in calls) earlier.Append($"Assistant called {call.Name} with {call.Arguments}\n");
                    break;
                case "assistant":
                    earlier.Append($"Assistant: {message.Content}\n");
                    break;
                case "tool":
                    earlier.Append($"Tool result: {message.Content}\n");
                    break;
            }
        }
        return new(turn.ThreadId, turn.RunId, options.Model, Instructions, prompt.ToString(), earlier.Length > 0 ? earlier.ToString() : null,
            [.. turn.Tools.Select(t => new HostTool(t.Name, t.Description, t.Parameters))]);
    }
}
