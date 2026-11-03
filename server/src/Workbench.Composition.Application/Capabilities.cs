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

/// <summary>
/// What a handler is given: its bound inputs, its invocation identity, the data it may read and, for a command,
/// who it acts for. A workflow step has no caller: it computes over its inputs alone (ADR-0005).
/// </summary>
public sealed record CapabilityInvocation(
    string InvocationId,
    IReadOnlyDictionary<string, JsonNode?> Inputs,
    IDataReader Data,
    Caller? Caller = null);

/// <summary>
/// Who a command acts for, as the server authenticated them, and where the request came from (ADR-0005). Never
/// taken from a command's inputs, so an agent cannot choose whom it acts as.
/// </summary>
public sealed record Caller(string UserId, RunOrigin Origin);

/// <summary>A domain or provider failure of one invocation, with a stable code and, when it has several, each reason.</summary>
public sealed class CapabilityFailure(string code, string message, IReadOnlyList<CompositionIssue>? issues = null) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>The reasons by field, for a refusal a caller can act on one by one (a missing term, an invalid one).</summary>
    public IReadOnlyList<CompositionIssue> Issues { get; } = issues ?? [];
}

public static class CapabilityEffects
{
    /// <summary>A capability that edits a workspace or runs a subscription: invoked for a caller, never in a Run (ADR-0005).</summary>
    public static bool IsCommand(this CapabilityDescriptor descriptor) =>
        descriptor.Effects.Any(e => e is CapabilityEffect.EditWorkspace or CapabilityEffect.Subscribe);
}

/// <summary>A JSON Schema for a contract version whose values are small enough to enter on a form.</summary>
public sealed record ContractDefinition(string ContractId, string ContractVersion, string Title, JsonObject Schema);
