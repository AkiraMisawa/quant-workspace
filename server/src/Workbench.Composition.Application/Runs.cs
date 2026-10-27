using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

public enum RunStatus { Running, Succeeded, PartiallySucceeded, Failed }

/// <summary>What started a Run (docs/05 §7): the history lists every origin together.</summary>
public enum RunOrigin { Pricer, Ai, Workflow, Action }

/// <summary>
/// A Run's explicit links to related work (docs/05 §7). Never inferred from names or timing; a grouping in the
/// history reads these and nothing else.
/// </summary>
public sealed record RunRelations(
    string? WorkspaceId = null,
    long? WorkspaceRevision = null,
    string? ParentRunId = null,
    string? AnalysisGroupId = null,
    string? ConversationId = null)
{
    public static RunRelations None { get; } = new();
}

public enum InvocationStatus { Running, Succeeded, Failed, Blocked }

/// <summary>
/// One executed invocation. A mapped step records one per item, under an expanded key such as
/// <c>readings/ST-03</c> that is stable across retries.
/// </summary>
public sealed record InvocationRecord(
    string InvocationKey,
    string StepId,
    string CapabilityId,
    string CapabilityVersion,
    InvocationStatus Status,
    IReadOnlyDictionary<string, TypedDataRef> Outputs,
    OutcomeError? Error);

/// <summary>
/// One accepted business submission: its pinned workflow, context and input, and what executing them
/// produced. Nothing in it changes after completion. <c>ResultContracts</c> are the contracts its workflow
/// declares for its outputs, known at acceptance, so a domain's history finds its failed Runs too.
/// </summary>
public sealed record RunRecord(
    string RunId,
    WorkflowDefinition Workflow,
    JsonObject Context,
    JsonObject Input,
    RunStatus Status,
    IReadOnlyList<InvocationRecord> Invocations,
    IReadOnlyDictionary<string, TypedDataRef> Outputs,
    OutcomeError? Error,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt,
    string Owner,
    RunOrigin Origin,
    RunRelations Relations,
    IReadOnlyList<string> ResultContracts);

/// <summary>
/// A request to execute a workflow against a context and an input, on behalf of <paramref name="Owner"/>. The
/// origin and relations are set by the server-side caller, never taken from a client as given.
/// </summary>
public sealed record RunSubmission(
    WorkflowDefinition Workflow,
    JsonObject Context,
    JsonObject Input,
    string IdempotencyKey,
    string Owner,
    RunOrigin Origin,
    RunRelations Relations);
