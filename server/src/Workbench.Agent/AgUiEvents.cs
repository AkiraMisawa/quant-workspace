using System.Text.Json.Nodes;

namespace Workbench.Agent;

/// <summary>
/// AG-UI events (protocol 1.0, the shapes of <c>@ag-ui/core</c> 1.0.3) as the JSON objects sent on the wire. Only the
/// events the workbench produces are here.
/// </summary>
public static class AgUiEvents
{
    public const string ProtocolVersion = "1.0";

    public static JsonObject RunStarted(string threadId, string runId, AgentRuntimeInfo runtime) => Event("RUN_STARTED", new()
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["protocolVersion"] = ProtocolVersion,
        ["metadata"] = new JsonObject
        {
            ["workbench"] = new JsonObject { ["runtime"] = runtime.Name, ["label"] = runtime.Label, ["simulated"] = runtime.Simulated },
        },
    });

    public static JsonObject RunFinished(string threadId, string runId, bool cancelled) => Event("RUN_FINISHED", new()
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["outcome"] = new JsonObject { ["type"] = cancelled ? "cancelled" : "success" },
    });

    public static JsonObject RunError(string code, string message) => Event("RUN_ERROR", new() { ["message"] = message, ["code"] = code });

    public static IEnumerable<JsonObject> TextMessage(string messageId, string text)
    {
        yield return Event("TEXT_MESSAGE_START", new() { ["messageId"] = messageId, ["role"] = "assistant" });
        yield return Event("TEXT_MESSAGE_CONTENT", new() { ["messageId"] = messageId, ["delta"] = text });
        yield return Event("TEXT_MESSAGE_END", new() { ["messageId"] = messageId });
    }

    public static IEnumerable<JsonObject> ToolCall(AgentToolCall call, string parentMessageId)
    {
        yield return Event("TOOL_CALL_START", new() { ["toolCallId"] = call.Id, ["toolCallName"] = call.Name, ["parentMessageId"] = parentMessageId });
        yield return Event("TOOL_CALL_ARGS", new() { ["toolCallId"] = call.Id, ["delta"] = call.Arguments });
        yield return Event("TOOL_CALL_END", new() { ["toolCallId"] = call.Id });
    }

    public static JsonObject ToolCallResult(string messageId, string toolCallId, string content) => Event("TOOL_CALL_RESULT", new()
    {
        ["messageId"] = messageId, ["toolCallId"] = toolCallId, ["content"] = content, ["role"] = "tool",
    });

    /// <summary>A message as AG-UI's <c>Message</c> union writes it.</summary>
    public static JsonObject Message(AgentMessage message)
    {
        var json = new JsonObject { ["id"] = message.Id, ["role"] = message.Role };
        if (message.Content is not null) json["content"] = message.Content;
        if (message.ToolCalls is { Count: > 0 } calls)
            json["toolCalls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        if (message.ToolCallId is not null) json["toolCallId"] = message.ToolCallId;
        return json;
    }

    static JsonObject Event(string type, JsonObject fields)
    {
        var json = new JsonObject { ["type"] = type, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        foreach (var (key, value) in fields.ToList())
        {
            fields.Remove(key);
            json[key] = value;
        }
        return json;
    }
}
