using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Agent;

/// <summary>
/// The tools an agent may call (ADR-0007 §3): one per registered command capability (ADR-0005), whose arguments are its
/// input ports by their contracts, and nothing else. A call is invoked through the command gateway for the server's
/// caller with origin <c>ai</c>, under a key made from the conversation and the tool call, so a repeated call answers
/// its first result instead of acting twice.
/// </summary>
public sealed class AgentToolGateway
{
    readonly CommandService commands;
    readonly Dictionary<string, CapabilityDescriptor> byName = new(StringComparer.Ordinal);

    public AgentToolGateway(CompositionRegistry registry, CommandService commands)
    {
        this.commands = commands;
        var tools = new List<AgentTool>();
        foreach (var descriptor in registry.CapabilityDescriptors.Where(d => d.IsCommand()).OrderBy(d => d.CapabilityId, StringComparer.Ordinal))
        {
            var name = NameOf(descriptor.CapabilityId);
            if (!byName.TryAdd(name, descriptor))
                throw new InvalidOperationException($"Two commands are offered to the agent as {name}; a tool names one capability version.");
            var properties = new JsonObject();
            foreach (var port in descriptor.InputPorts)
            {
                var contract = registry.FindContract(port.ContractId, port.ContractVersion)
                    ?? throw new InvalidOperationException($"{descriptor.CapabilityId} names contract {port.ContractId}@{port.ContractVersion}, which is not registered.");
                properties[port.Name] = contract.Schema.DeepClone();
            }
            tools.Add(new(name, descriptor.Title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. descriptor.InputPorts.Select(p => JsonValue.Create(p.Name))]),
                ["additionalProperties"] = false,
            }));
        }
        Tools = tools;
    }

    public IReadOnlyList<AgentTool> Tools { get; }

    /// <summary>A capability's tool name: its id with <c>_</c> for <c>.</c>, as function-calling models accept names.</summary>
    public static string NameOf(string capabilityId) => capabilityId.Replace('.', '_');

    /// <summary>The capability a tool calls, or null for a name that is not offered.</summary>
    public string? CapabilityOf(string toolName) => byName.GetValueOrDefault(toolName)?.CapabilityId;

    /// <summary>Invokes one tool call for the user, and answers its result as the JSON text the agent reads back.</summary>
    public async Task<ToolResult> InvokeAsync(AgentToolCall call, string userId, string threadId, CancellationToken cancellationToken)
    {
        if (!byName.TryGetValue(call.Name, out var descriptor))
            return ToolResult.Refused(call.Name, "UnknownTool", [new("UnknownTool", "/name", $"No tool {call.Name} is offered; only registered commands can be called.")]);
        JsonObject? inputs;
        try
        {
            inputs = JsonNode.Parse(call.Arguments) as JsonObject;
        }
        catch (JsonException)
        {
            inputs = null;
        }
        if (inputs is null)
            return ToolResult.Refused(descriptor.CapabilityId, "InvalidArguments", [new("InvalidArguments", "/arguments", "The arguments are not a JSON object.")]);

        var result = await commands.InvokeAsync(descriptor.CapabilityId, descriptor.Version, inputs, new Caller(userId, RunOrigin.Ai),
            $"agent:{threadId}:{call.Id}", cancellationToken);
        return result switch
        {
            CommandResult.Done done => ToolResult.Done(done.CapabilityId, done.Version, done.Outputs),
            CommandResult.Refused refused => ToolResult.Refused(descriptor.CapabilityId, refused.Code, refused.Issues),
            _ => throw new InvalidOperationException("Unhandled command result."),
        };
    }
}

/// <summary>A tool call's result: the command's outputs, or its refusal with every issue.</summary>
public sealed record ToolResult(string CapabilityId, bool Succeeded, JsonObject Content)
{
    public static ToolResult Done(string capabilityId, string version, IReadOnlyDictionary<string, JsonNode?> outputs) => new(capabilityId, true, new JsonObject
    {
        ["status"] = "done",
        ["capability"] = capabilityId,
        ["version"] = version,
        ["outputs"] = new JsonObject([.. outputs.Select(o => KeyValuePair.Create(o.Key, o.Value?.DeepClone()))]),
    });

    public static ToolResult Refused(string capabilityId, string code, IReadOnlyList<CompositionIssue> issues) => new(capabilityId, false, new JsonObject
    {
        ["status"] = "refused",
        ["capability"] = capabilityId,
        ["code"] = code,
        ["issues"] = JsonSerializer.SerializeToNode(issues, WireJson.Options),
    });

    public string Text => Content.ToJsonString();

    /// <summary>Reads back a result written by <see cref="Text"/>, as a runtime finds it in a tool message.</summary>
    public static ToolResult Parse(string text)
    {
        var content = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("A tool result is a JSON object.");
        return new(content["capability"]!.GetValue<string>(), content["status"]!.GetValue<string>() == "done", content);
    }

    public JsonObject Outputs => Content["outputs"] as JsonObject ?? throw new InvalidOperationException("A refused tool call has no outputs.");

    public IEnumerable<CompositionIssue> Issues => (Content["issues"] as JsonArray ?? []).Select(i =>
        new CompositionIssue(i!["code"]!.GetValue<string>(), i["path"]!.GetValue<string>(), i["message"]!.GetValue<string>()));
}
