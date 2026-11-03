using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workbench.AgentHost.Contracts;

/// <summary>A tool the model may call, as the workbench offers it: its name, what it does and its arguments' schema.</summary>
public sealed record HostTool(string Name, string Description, JsonObject Parameters);

/// <summary>
/// One turn the workbench asks the agent host to answer (ADR-0007 §5, C44). <c>Prompt</c> is the user's latest request
/// with the context it was made in. <c>Earlier</c> is the thread's conversation before it, written out, which the host
/// reads only when it holds no session for the thread (after a restart of either process). The host answers on the
/// response as <see cref="HostLine"/>s, one JSON object per line.
/// </summary>
public sealed record HostTurn(
    string ThreadId,
    string RunId,
    string Model,
    string Instructions,
    string Prompt,
    string? Earlier,
    IReadOnlyList<HostTool> Tools);

/// <summary>
/// One line of a turn's answer. <c>text</c>: the model said <c>Text</c>. <c>call</c>: the model calls tool <c>Name</c>
/// with <c>Arguments</c> (JSON text) as call <c>Id</c>, and the host waits for its result to be posted to
/// <c>/turns/{runId}/calls/{id}</c>. <c>done</c>: the turn ended. <c>error</c>: it failed, as <c>Text</c> says.
/// </summary>
public sealed record HostLine(string Type, string? Text = null, string? Id = null, string? Name = null, string? Arguments = null)
{
    public static HostLine Said(string text) => new("text", text);
    public static HostLine Call(string id, string name, string arguments) => new("call", null, id, name, arguments);
    public static readonly HostLine Done = new("done");
    public static HostLine Failed(string message) => new("error", message);
}

/// <summary>The result of one tool call, as the JSON text the model reads.</summary>
public sealed record HostToolResult(string Content);

/// <summary>The wire's routes, header and JSON form, shared by the host and its client so neither restates them.</summary>
public static class AgentHostWire
{
    /// <summary>The header that carries the key the two processes share; the host refuses a request without it.</summary>
    public const string KeyHeader = "X-Agent-Host-Key";

    public const string Turns = "/turns";

    public static string Result(string runId, string callId) => $"/turns/{Uri.EscapeDataString(runId)}/calls/{Uri.EscapeDataString(callId)}";

    public static string Abort(string runId) => $"/turns/{Uri.EscapeDataString(runId)}/abort";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
