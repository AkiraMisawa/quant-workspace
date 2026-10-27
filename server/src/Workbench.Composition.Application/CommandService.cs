using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>What invoking a command produced: its outputs, or a refusal with every reason by field.</summary>
public abstract record CommandResult
{
    public sealed record Done(string CapabilityId, string Version, IReadOnlyDictionary<string, JsonNode?> Outputs) : CommandResult;
    public sealed record Refused(string Code, IReadOnlyList<CompositionIssue> Issues) : CommandResult;
}

/// <summary>
/// The command gateway (ADR-0005): invokes a capability that edits a workspace or runs a subscription, for a caller
/// the server authenticated, outside Runs. Inputs are checked against the capability's contracts before the handler
/// sees them; what the command did is recorded where it happened (the workspace's edit log, its live status).
///
/// An idempotency key makes a retry answer the first result instead of acting twice; the same key with other
/// inputs is refused. Keys are held in memory for the life of the process: a retry after a restart acts again.
/// </summary>
public sealed class CommandService(CompositionRegistry registry, IDataReader data)
{
    readonly ConcurrentDictionary<(string User, string Key), (string Request, Lazy<Task<CommandResult>> Result)> done = new();

    public async Task<CommandResult> InvokeAsync(string capabilityId, string version, JsonObject inputs, Caller caller, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Refuse("ValidationFailed", "Required", "/idempotencyKey", "An idempotency key is required.");
        var handler = registry.FindCapability(capabilityId, version);
        if (handler is null)
            return Refuse("UnknownCapability", "UnknownCapability", "/capabilityId", $"Capability {capabilityId}@{version} is not registered.");
        if (!handler.Descriptor.IsCommand())
            return Refuse("NotACommand", "NotACommand", "/capabilityId", $"{capabilityId} computes in a Run; submit a workflow that uses it.");
        var issues = Check(handler.Descriptor, inputs).ToList();
        if (issues.Count > 0) return new CommandResult.Refused("ValidationFailed", issues);

        var request = $"{capabilityId}@{version}:{inputs.ToJsonString()}";
        var entry = done.GetOrAdd((caller.UserId, idempotencyKey),
            _ => (request, new Lazy<Task<CommandResult>>(() => Invoke(handler, inputs, caller, cancellationToken))));
        if (entry.Request != request)
            return Refuse("IdempotencyConflict", "IdempotencyConflict", "/idempotencyKey", "This key was used for another command or other inputs.");
        CommandResult result;
        try
        {
            result = await entry.Result.Value;
        }
        catch
        {
            done.TryRemove(new((caller.UserId, idempotencyKey), entry));
            throw;
        }
        // Only what was done is answered again; a refusal leaves nothing done, so a retry tries again.
        if (result is CommandResult.Refused) done.TryRemove(new((caller.UserId, idempotencyKey), entry));
        return result;
    }

    async Task<CommandResult> Invoke(ICapabilityHandler handler, JsonObject inputs, Caller caller, CancellationToken cancellationToken)
    {
        var bound = handler.Descriptor.InputPorts.ToDictionary(p => p.Name, p => inputs[p.Name]?.DeepClone());
        try
        {
            var outputs = await handler.InvokeAsync(new($"cmd-{Guid.CreateVersion7():N}", bound, data, caller), cancellationToken);
            var missing = handler.Descriptor.OutputPorts.Where(p => !outputs.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"{handler.Descriptor.CapabilityId} did not answer {string.Join(", ", missing)}.");
            return new CommandResult.Done(handler.Descriptor.CapabilityId, handler.Descriptor.Version, outputs);
        }
        catch (CapabilityFailure failure)
        {
            return new CommandResult.Refused(failure.Code, failure.Issues.Count > 0 ? failure.Issues : [new(failure.Code, "", failure.Message)]);
        }
    }

    /// <summary>Every input port is given once and nothing else; a form-sized one meets its contract.</summary>
    IEnumerable<CompositionIssue> Check(CapabilityDescriptor descriptor, JsonObject inputs)
    {
        foreach (var extra in inputs.Select(p => p.Key).Where(k => descriptor.InputPorts.All(p => p.Name != k)))
            yield return new("UnknownInput", $"/inputs/{extra}", $"{descriptor.CapabilityId} takes no input {extra}.");
        foreach (var port in descriptor.InputPorts)
        {
            if (inputs[port.Name] is not JsonObject value)
            {
                yield return new("Required", $"/inputs/{port.Name}", $"{port.Name} is required, as an object of {port.ContractId}.");
                continue;
            }
            var contract = registry.FindContract(port.ContractId, port.ContractVersion)
                ?? throw new InvalidOperationException($"{descriptor.CapabilityId} names contract {port.ContractId}@{port.ContractVersion}, which is not registered.");
            foreach (var issue in ContractChecks.Check(contract, value, $"/inputs/{port.Name}")) yield return issue;
        }
    }

    static CommandResult.Refused Refuse(string code, string issue, string path, string message) => new(code, [new(issue, path, message)]);
}
