using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

public enum RunStatus { Running, Succeeded, PartiallySucceeded, Failed }

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
/// produced. Nothing in it changes after completion.
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
    DateTimeOffset? CompletedAt);

/// <summary>A request to execute a workflow against a context and an input.</summary>
public sealed record RunSubmission(WorkflowDefinition Workflow, JsonObject Context, JsonObject Input, string IdempotencyKey);
