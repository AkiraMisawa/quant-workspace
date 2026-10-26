using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>What a submission led to: an accepted Run, or the reasons it was refused.</summary>
public abstract record SubmissionResult
{
    public sealed record Accepted(RunRecord Run) : SubmissionResult;
    public sealed record Rejected(string Code, IReadOnlyList<CompositionIssue> Issues) : SubmissionResult;
}

/// <summary>
/// Accepts submissions and runs them: one public Run per business submission, idempotent by key.
/// Fixed workspaces, row actions, the command palette and the agent all submit here.
/// </summary>
public sealed class RunService(CompositionRegistry registry, IDataStore store, TimeProvider clock)
{
    readonly ConcurrentDictionary<string, RunRecord> runs = new();
    readonly ConcurrentDictionary<string, (string Digest, string RunId)> idempotency = new();
    readonly ConcurrentDictionary<string, Task> executions = new();
    readonly Lock gate = new();

    public SubmissionResult Submit(RunSubmission submission)
    {
        var issues = Validate(submission).ToList();
        if (issues.Count > 0) return new SubmissionResult.Rejected("ValidationFailed", issues);

        var digest = Digest(submission);
        lock (gate)
        {
            if (idempotency.TryGetValue(submission.IdempotencyKey, out var known))
                return known.Digest == digest
                    ? new SubmissionResult.Accepted(runs[known.RunId])
                    : new SubmissionResult.Rejected("IdempotencyConflict",
                        [new("IdempotencyConflict", "/idempotencyKey", "This key was used for a different submission.")]);

            var runId = $"run-{Guid.CreateVersion7():N}";
            var run = new RunRecord(runId, submission.Workflow, (JsonObject)submission.Context.DeepClone(), (JsonObject)submission.Input.DeepClone(),
                RunStatus.Running, [], new Dictionary<string, TypedDataRef>(), null, clock.GetUtcNow(), null);
            runs[runId] = run;
            idempotency[submission.IdempotencyKey] = (digest, runId);
            executions[runId] = Task.Run(() => ExecuteAsync(run));
            return new SubmissionResult.Accepted(run);
        }
    }

    public RunRecord? Find(string runId) => runs.GetValueOrDefault(runId);

    /// <summary>Completes when the Run has finished executing. For tests and for awaiting callers.</summary>
    public Task Completion(string runId) => executions.TryGetValue(runId, out var task) ? task : Task.CompletedTask;

    IEnumerable<CompositionIssue> Validate(RunSubmission submission)
    {
        var structural = new CompositionValidator(registry).Validate(submission.Workflow).ToList();
        if (structural.Count > 0) return structural;
        return ValidateInput(submission);
    }

    /// <summary>
    /// Rebinding revalidates: every context and input path the workflow reads must exist in this submission,
    /// and the input must meet its declared contract. Nothing from an earlier Run is reused.
    /// </summary>
    IEnumerable<CompositionIssue> ValidateInput(RunSubmission submission)
    {
        var contract = registry.FindContract(submission.Workflow.InputContractId, submission.Workflow.InputContractVersion)!;
        foreach (var issue in ContractChecks.Check(contract, submission.Input, "/input")) yield return issue;

        var bindings = submission.Workflow.Steps.SelectMany(s => s.Bindings.Values.Append(s.ForEach).OfType<Binding>());
        foreach (var binding in bindings)
        {
            if (binding is ContextBinding c && !JsonPointer.TryResolve(submission.Context, c.Path, out _))
                yield return new("ContextUnresolved", $"/context{c.Path}", $"The context has no value at '{c.Path}'.");
            if (binding is InputBinding i && !JsonPointer.TryResolve(submission.Input, i.Path, out _))
                yield return new("InputUnresolved", $"/input{i.Path}", $"The input has no value at '{i.Path}'.");
            if (binding is DataReferenceBinding d && store.Read(d.Reference) is not DataLookup.Found)
                yield return new("DataUnavailable", "/workflow", $"{d.Reference.DataId}@{d.Reference.Revision} is not available.");
        }
    }

    async Task ExecuteAsync(RunRecord accepted)
    {
        var invocations = new ConcurrentDictionary<string, InvocationRecord>();
        var order = new ConcurrentQueue<string>();
        void Record(InvocationRecord invocation)
        {
            if (invocations.TryAdd(invocation.InvocationKey, invocation)) order.Enqueue(invocation.InvocationKey);
            else invocations[invocation.InvocationKey] = invocation;
            runs[accepted.RunId] = runs[accepted.RunId] with { Invocations = [.. order.Select(k => invocations[k])] };
        }

        WorkflowExecutor.Result result;
        try
        {
            result = await new WorkflowExecutor(registry, store).ExecuteAsync(
                accepted.RunId, accepted.Workflow, accepted.Context, accepted.Input, Record, CancellationToken.None);
        }
        catch (CapabilityFailure failure)
        {
            result = new(RunStatus.Failed, new Dictionary<string, TypedDataRef>(), new(failure.Code, failure.Message));
        }
        runs[accepted.RunId] = runs[accepted.RunId] with
        {
            Status = result.Status,
            Outputs = result.Outputs,
            Error = result.Error,
            Invocations = [.. order.Select(k => invocations[k])],
            CompletedAt = clock.GetUtcNow(),
        };
    }

    static string Digest(RunSubmission submission)
    {
        var canonical = JsonSerializer.Serialize(new { submission.Workflow, submission.Context, submission.Input }, WireJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
