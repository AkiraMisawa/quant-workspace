using System.Text.Json.Nodes;

namespace Workbench.Agent;

/// <summary>A tool call the agent asked for: the tool's name and its arguments as the JSON text the agent wrote.</summary>
public sealed record AgentToolCall(string Id, string Name, string Arguments);

/// <summary>
/// One message of a conversation, in AG-UI's roles: <c>user</c> (content), <c>assistant</c> (content, tool calls) and
/// <c>tool</c> (the result of one tool call, as JSON text).
/// </summary>
public sealed record AgentMessage(string Id, string Role, string? Content, IReadOnlyList<AgentToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static AgentMessage User(string id, string content) => new(id, "user", content);
    public static AgentMessage Tool(string id, string toolCallId, string content) => new(id, "tool", content, null, toolCallId);
}

/// <summary>A tool offered to the agent: its name, what it does, and the JSON Schema of its arguments.</summary>
public sealed record AgentTool(string Name, string Description, JsonObject Parameters);

/// <summary>AG-UI's context entry: a description of something the user is looking at. Not authority (ADR-0007 §8).</summary>
public sealed record AgentContext(string Description, string Value);

/// <summary>What the agent is asked to continue: the thread and run, the whole conversation, the tools it may call, and the context.</summary>
public sealed record AgentTurn(string ThreadId, string RunId, IReadOnlyList<AgentMessage> Messages, IReadOnlyList<AgentTool> Tools, IReadOnlyList<AgentContext> Context);

/// <summary>
/// The run a runtime answers into. Words go to the user as they are said; a tool call is invoked by the platform's
/// gateway for the signed-in user and its result is answered back. The runtime holds no way to act of its own.
/// </summary>
public interface IAgentTurnSink
{
    Task SayAsync(string text, CancellationToken cancellationToken);

    Task<ToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken);
}

/// <summary>Which runtime answers, as the pane shows it. <c>Simulated</c> is true for anything that is not a model.</summary>
public sealed record AgentRuntimeInfo(string Name, string Label, bool Simulated);

/// <summary>
/// The agent runtime port (ADR-0007 §5): model interaction behind one interface, so the scripted agent, a Copilot
/// adapter or any other runtime is chosen in the composition root and nowhere else. The runtime decides what to say
/// and which tools to call, through the sink; the platform invokes the tools, records the events and ends the run.
/// A runtime whose own SDK runs the model's loop (the Copilot SDK) fits as well as one that is called turn by turn.
/// </summary>
public interface IAgentRuntime
{
    AgentRuntimeInfo Info { get; }

    /// <summary>Answers the conversation's latest request; returning ends the run.</summary>
    Task RunAsync(AgentTurn turn, IAgentTurnSink sink, CancellationToken cancellationToken);
}
