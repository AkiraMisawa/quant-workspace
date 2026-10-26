using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;
using Workbench.Samples.Synthetic;

namespace Workbench.Composition.Tests;

/// <summary>The application services wired as the composition root wires them, without HTTP.</summary>
sealed class Harness
{
    public CompositionRegistry Registry { get; }
    public InMemoryDataStore Store { get; } = new();
    public RunService Runs { get; }
    public ActionService Actions { get; }

    public Harness(params IWorkbenchModule[] extra)
    {
        Registry = new CompositionRegistry([new SyntheticModule(), .. extra]);
        Runs = new RunService(Registry, Store, TimeProvider.System);
        Actions = new ActionService(Registry, Store, Runs);
    }

    public static JsonObject Context(string region = "North", string asOf = "2026-10-26") =>
        new() { ["region"] = region, ["asOf"] = asOf };

    public WorkflowDefinition Workflow(string id) => Registry.FindWorkflow(id, "1.0.0")!;

    public async Task<RunRecord> RunAsync(WorkflowDefinition workflow, JsonObject context, JsonObject input, string? key = null)
    {
        var result = Runs.Submit(new(workflow, context, input, key ?? Guid.NewGuid().ToString()));
        var accepted = Assert.IsType<SubmissionResult.Accepted>(result);
        await Runs.Completion(accepted.Run.RunId);
        return Runs.Find(accepted.Run.RunId)!;
    }

    public Dataset Dataset(TypedDataRef reference) =>
        Assert.IsType<DataLookup.Found>(Store.Read(reference)).Content.Deserialize<Dataset>(WireJson.Options)!;

    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, WireJson.Options)!;
}
