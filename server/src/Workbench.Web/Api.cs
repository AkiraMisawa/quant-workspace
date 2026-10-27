using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Web;

/// <summary>Thin HTTP endpoints over the application services. No business logic lives here.</summary>
public static class Api
{
    public sealed record ErrorEnvelope(string Code, string Message, IReadOnlyList<CompositionIssue> Issues, bool Retryable = false);

    public sealed record Catalog(
        IEnumerable<CapabilityDescriptor> Capabilities,
        IEnumerable<ContractDefinition> Contracts,
        IEnumerable<WorkflowDefinition> Workflows,
        IEnumerable<ViewDefinition> Views,
        IEnumerable<ActionDefinition> Actions);

    public sealed record RunRequest(WorkflowDefinition Workflow, JsonObject Context, JsonObject Input, string IdempotencyKey);

    public sealed record ActionRequest(IReadOnlyDictionary<string, JsonNode?> Parameters, JsonObject Context, string IdempotencyKey);

    public sealed record ViewValidationRequest(ViewDefinition View, IReadOnlyList<string> BoundNames);

    public static void MapWorkbenchApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/catalog", (CompositionRegistry registry) => new Catalog(
            registry.CapabilityDescriptors, registry.ContractDefinitions, registry.WorkflowDefinitions,
            registry.ViewDefinitions, registry.ActionDefinitions));

        api.MapPost("/runs", (RunRequest request, RunService runs, CurrentUser user) =>
            ToResult(runs.Submit(new(request.Workflow, request.Context, request.Input, request.IdempotencyKey, user.Id, RunOrigin.Workflow, RunRelations.None))));

        api.MapGet("/runs/{runId}", (string runId, RunService runs) =>
            runs.Find(runId) is { } run ? Results.Ok(run) : NotFound("UnknownRun", $"Run {runId} does not exist."));

        api.MapPost("/data/read", (TypedDataRef reference, IDataStore store) => store.Read(reference) switch
        {
            DataLookup.Found found => Results.Ok(found.Content),
            DataLookup.Expired => Results.Json(new ErrorEnvelope("DataExpired", $"{reference.DataId} has expired and is not recomputed.", []), statusCode: 410),
            _ => NotFound("DataUnavailable", $"{reference.DataId}@{reference.Revision} is not retained."),
        });

        api.MapPost("/actions/{actionId}/invoke", (string actionId, ActionRequest request, ActionService actions, CurrentUser user) =>
            actions.Invoke(actionId, request.Parameters, request.Context, request.IdempotencyKey, user.Id) switch
            {
                ActionResult.ViewOpened opened => Results.Ok(new { kind = "openView", view = opened.View }),
                ActionResult.Submitted { Submission: SubmissionResult.Accepted accepted } => Results.Ok(new { kind = "run", run = accepted.Run }),
                ActionResult.Submitted { Submission: SubmissionResult.Rejected rejected } => Rejected(rejected.Code, rejected.Issues),
                ActionResult.Refused refused => Rejected(refused.Code, refused.Issues),
                _ => throw new InvalidOperationException("Unhandled action result."),
            });

        api.MapPost("/definitions/validate-workflow", (WorkflowDefinition workflow, CompositionRegistry registry) =>
            Results.Ok(new CompositionValidator(registry).Validate(workflow).ToList()));

        api.MapPost("/definitions/validate-view", (ViewValidationRequest request, CompositionRegistry registry) =>
            Results.Ok(new CompositionValidator(registry).Validate(request.View, request.BoundNames.ToHashSet()).ToList()));
    }

    static IResult ToResult(SubmissionResult result) => result switch
    {
        SubmissionResult.Accepted accepted => Results.Accepted($"/api/runs/{accepted.Run.RunId}", accepted.Run),
        SubmissionResult.Rejected rejected => Rejected(rejected.Code, rejected.Issues),
        _ => throw new InvalidOperationException("Unhandled submission result."),
    };

    static IResult Rejected(string code, IReadOnlyList<CompositionIssue> issues) => Results.Json(
        new ErrorEnvelope(code, issues.Count == 1 ? issues[0].Message : $"{issues.Count} problems were found.", issues),
        statusCode: code == "IdempotencyConflict" ? 409 : code is "DataUnavailable" ? 410 : 422);

    static IResult NotFound(string code, string message) => Results.Json(new ErrorEnvelope(code, message, []), statusCode: 404);
}
