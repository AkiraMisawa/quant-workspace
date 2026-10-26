using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>
/// One implementation of a capability version. A handler may call a local function, a service, a worker
/// or an external API; the kernel sees only this interface.
/// </summary>
public interface ICapabilityHandler
{
    CapabilityDescriptor Descriptor { get; }

    /// <summary>
    /// Executes one invocation. Every declared output port must be present in the result. A failure is
    /// thrown as <see cref="CapabilityFailure"/>, never returned as a plausible value.
    /// </summary>
    Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>What a handler is given: its bound inputs, its invocation identity, and the data it may read.</summary>
public sealed record CapabilityInvocation(
    string InvocationId,
    IReadOnlyDictionary<string, JsonNode?> Inputs,
    IDataReader Data);

/// <summary>A domain or provider failure of one invocation, with a stable code.</summary>
public sealed class CapabilityFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A JSON Schema for a contract version whose values are small enough to enter on a form.</summary>
public sealed record ContractDefinition(string ContractId, string ContractVersion, string Title, JsonObject Schema);
