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

/// <summary>What the agent is asked to continue: the whole conversation, the tools it may call, and the context.</summary>
public sealed record AgentTurn(IReadOnlyList<AgentMessage> Messages, IReadOnlyList<AgentTool> Tools, IReadOnlyList<AgentContext> Context);

/// <summary>The agent's answer to one turn: text for the user, tool calls, or both. No tool calls ends the run.</summary>
public sealed record AgentReply(string? Text, IReadOnlyList<AgentToolCall> ToolCalls);

/// <summary>Which runtime answers, as the pane shows it. <c>Simulated</c> is true for anything that is not a model.</summary>
public sealed record AgentRuntimeInfo(string Name, string Label, bool Simulated);

/// <summary>
/// The agent runtime port (ADR-0007 §5): model interaction behind one interface, so the scripted agent, a Copilot
/// adapter or any other runtime is chosen in the composition root and nowhere else. The runtime proposes; the
/// platform's loop invokes tools, records events and ends runs.
/// </summary>
public interface IAgentRuntime
{
    AgentRuntimeInfo Info { get; }

    Task<AgentReply> ReplyAsync(AgentTurn turn, CancellationToken cancellationToken);
}
