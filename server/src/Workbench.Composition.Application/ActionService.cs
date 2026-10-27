using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>What invoking an action produced: a view over retained data, a submission, or a refusal.</summary>
public abstract record ActionResult
{
    public sealed record ViewOpened(ViewDefinition View) : ActionResult;
    public sealed record Submitted(SubmissionResult Submission) : ActionResult;
    public sealed record Refused(string Code, IReadOnlyList<CompositionIssue> Issues) : ActionResult;
}

/// <summary>
/// Invokes registered actions. Row values arrive as untrusted parameters: a data reference must name
/// retained data of the expected contract, and a workflow is submitted through the same path as any other.
/// </summary>
public sealed class ActionService(CompositionRegistry registry, IDataStore store, RunService runs)
{
    public ActionResult Invoke(string actionId, IReadOnlyDictionary<string, JsonNode?> parameters, JsonObject context, string idempotencyKey, string owner)
    {
        var action = registry.FindAction(actionId);
        if (action is null)
            return new ActionResult.Refused("UnknownAction", [new("UnknownAction", "/actionId", $"Action {actionId} is not registered.")]);

        var issues = new List<CompositionIssue>();
        foreach (var parameter in action.Parameters)
            if (!parameters.TryGetValue(parameter.Name, out var value) || value is null)
                issues.Add(new("Required", $"/parameters/{parameter.Name}", $"{parameter.Name} has no value on this row."));
        foreach (var extra in parameters.Keys.Except(action.Parameters.Select(p => p.Name)))
            issues.Add(new("UnknownParameter", $"/parameters/{extra}", $"{actionId} takes no parameter {extra}."));
        if (issues.Count > 0) return new ActionResult.Refused("ValidationFailed", issues);

        switch (action.Target)
        {
            case OpenViewTarget open:
            {
                var reference = parameters[open.DataParameter].Deserialize<TypedDataRef>(WireJson.Options);
                if (reference is null)
                    return new ActionResult.Refused("ValidationFailed", [new("InvalidReference", $"/parameters/{open.DataParameter}", "Not a data reference.")]);
                if (store.Read(reference) is not DataLookup.Found)
                    return new ActionResult.Refused("DataUnavailable",
                        [new("DataUnavailable", $"/parameters/{open.DataParameter}", $"{reference.DataId} is no longer retained; it is not recomputed.")]);
                var view = registry.FindView(open.ViewId, open.ViewVersion)!;
                return new ActionResult.ViewOpened(Bind(view, open.DataParameter, reference));
            }
            case RunWorkflowTarget run:
            {
                var workflow = registry.FindWorkflow(run.WorkflowId, run.WorkflowVersion)!;
                var input = new JsonObject();
                foreach (var (field, parameter) in run.InputFromParameters) input[field] = parameters[parameter]?.DeepClone();
                foreach (var (field, value) in Defaults(workflow).Where(d => !input.ContainsKey(d.Key))) input[field] = value;
                return new ActionResult.Submitted(runs.Submit(new(workflow, context, input, idempotencyKey, owner, RunOrigin.Action, RunRelations.None)));
            }
            default:
                throw new InvalidOperationException($"Action target {action.Target.GetType().Name} has no handling.");
        }
    }

    /// <summary>Replaces a view's named data with a concrete reference, so the view shows exactly that revision.</summary>
    public static ViewDefinition Bind(ViewDefinition view, string name, TypedDataRef reference) => view with
    {
        Components = [.. view.Components.Select(c => c.Data is WorkflowOutputData output && output.Name == name
            ? c with { Data = new DataReferenceData(reference) }
            : c)],
    };

    /// <summary>The input contract's declared defaults. A field without one stays absent and is reported as missing.</summary>
    IEnumerable<KeyValuePair<string, JsonNode?>> Defaults(WorkflowDefinition workflow)
    {
        var contract = registry.FindContract(workflow.InputContractId, workflow.InputContractVersion)!;
        foreach (var (name, field) in contract.Schema["properties"]?.AsObject() ?? [])
            if (field?["default"] is { } value) yield return new(name, value.DeepClone());
    }
}
