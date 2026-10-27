using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

public class CompositionTests
{
    const string Summary = "workflow.sample.station-summary";
    const string SingleStation = "workflow.sample.single-station-summary";

    [Fact] // CO01: the generic kernel builds without any domain or sample module
    public void Kernel_assemblies_reference_no_module()
    {
        foreach (var assembly in new[] { typeof(TypedDataRef).Assembly, typeof(RunService).Assembly })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
            Assert.DoesNotContain(references, r => r.StartsWith("Workbench.Samples", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.StartsWith("Workbench.Web", StringComparison.Ordinal));
            Assert.DoesNotContain(references, r => r.Contains("Xva", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact] // CO01: a module adds a capability, workflow and view without a kernel change
    public async Task A_new_module_runs_through_the_same_kernel()
    {
        var harness = new Harness(new EchoModule());
        var run = await harness.RunAsync(harness.Workflow("workflow.test.echo"), Harness.Context(), new JsonObject { ["text"] = "hello" });
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal("hello", harness.Dataset(run.Outputs["echo"]).Rows.Single()["text"]!.GetValue<string>());
    }

    [Fact] // CO02: views do not own execution; two views read the one output reference of one Run
    public async Task Two_views_bind_the_same_output_without_executing()
    {
        var harness = new Harness();
        var run = await harness.RunAsync(harness.Workflow(Summary), Harness.Context(), new JsonObject { ["days"] = 5 });
        var invocations = run.Invocations.Count;

        var full = ActionService.Bind(harness.Registry.FindView("view.sample.station-summary", "1.0.0")!, "summary", run.Outputs["summary"]);
        var compact = ActionService.Bind(harness.Registry.FindView("view.sample.station-summary-compact", "1.0.0")!, "summary", run.Outputs["summary"]);

        Assert.Equal(run.Outputs["summary"], Assert.IsType<DataReferenceData>(full.Components[0].Data).Reference);
        Assert.Equal(run.Outputs["summary"], Assert.IsType<DataReferenceData>(compact.Components[0].Data).Reference);
        Assert.Equal(invocations, harness.Runs.Find(run.RunId)!.Invocations.Count);
    }

    [Fact] // CO03: a hidden intermediate is shown later without re-execution; expired data reports unavailable
    public async Task Intermediate_readings_open_without_execution_and_expire_honestly()
    {
        var harness = new Harness();
        var run = await harness.RunAsync(harness.Workflow(Summary), Harness.Context(), new JsonObject { ["days"] = 5 });
        var row = harness.Dataset(run.Outputs["summary"]).Rows.First(r => r["status"]!.GetValue<string>() == "available");
        var parameters = new Dictionary<string, JsonNode?> { ["readings"] = row["readings"]!.DeepClone() };

        var opened = Assert.IsType<ActionResult.ViewOpened>(harness.Actions.Invoke("sample.showReadings", parameters, Harness.Context(), "a1", "akira"));
        var reference = Assert.IsType<DataReferenceData>(opened.View.Components[0].Data).Reference;
        Assert.Equal(5, harness.Dataset(reference).Rows.Count);
        Assert.Equal(run.Invocations.Count, harness.Runs.Find(run.RunId)!.Invocations.Count);

        harness.Store.Expire(reference);
        var refused = Assert.IsType<ActionResult.Refused>(harness.Actions.Invoke("sample.showReadings", parameters, Harness.Context(), "a2", "akira"));
        Assert.Equal("DataUnavailable", refused.Code);
    }

    [Fact] // CO04: rebinding an unsaved workflow revalidates and makes a new Run; the old Run is unchanged
    public async Task Rebinding_makes_a_new_run_and_leaves_the_old_one_alone()
    {
        var harness = new Harness();
        var workflow = harness.Workflow(Summary);
        var north = await harness.RunAsync(workflow, Harness.Context("North"), new JsonObject { ["days"] = 3 });
        var northSummary = north.Outputs["summary"];
        var west = await harness.RunAsync(workflow, Harness.Context("West"), new JsonObject { ["days"] = 3 });

        Assert.NotEqual(north.RunId, west.RunId);
        Assert.Equal(northSummary, harness.Runs.Find(north.RunId)!.Outputs["summary"]);
        Assert.All(harness.Dataset(west.Outputs["summary"]).Rows, r => Assert.StartsWith("ST-0", r["stationId"]!.GetValue<string>()));
        Assert.Equal(["ST-07", "ST-08"], harness.Dataset(west.Outputs["summary"]).Rows.Select(r => r["stationId"]!.GetValue<string>()));

        var missing = harness.Runs.Submit(new(workflow, new JsonObject { ["asOf"] = "2026-10-26" }, new JsonObject { ["days"] = 3 }, "k-missing", "akira", RunOrigin.Workflow, RunRelations.None));
        var rejected = Assert.IsType<SubmissionResult.Rejected>(missing);
        Assert.Contains(rejected.Issues, i => i.Code == "ContextUnresolved" && i.Path == "/context/region");

        var invalid = harness.Runs.Submit(new(workflow, Harness.Context(), new JsonObject { ["days"] = 0 }, "k-invalid", "akira", RunOrigin.Workflow, RunRelations.None));
        Assert.Contains(Assert.IsType<SubmissionResult.Rejected>(invalid).Issues, i => i.Path == "/input/days");
    }

    [Fact] // CO05: a row action builds a validated submission through the same path
    public async Task Row_action_submits_the_registered_workflow()
    {
        var harness = new Harness();
        var result = harness.Actions.Invoke("sample.summariseStation",
            new Dictionary<string, JsonNode?> { ["stationId"] = "ST-02" }, Harness.Context(), "row-action-1", "akira");
        var accepted = Assert.IsType<SubmissionResult.Accepted>(Assert.IsType<ActionResult.Submitted>(result).Submission);
        await harness.Runs.Completion(accepted.Run.RunId);
        var run = harness.Runs.Find(accepted.Run.RunId)!;

        Assert.Equal(SingleStation, run.Workflow.DefinitionId);
        Assert.Equal(7, run.Input["days"]!.GetValue<int>());
        Assert.Equal("ST-02", harness.Dataset(run.Outputs["summary"]).Rows.Single()["stationId"]!.GetValue<string>());
    }

    [Fact] // EX06 in the generic kernel: a failed item stays visible as unavailable, never as zero
    public async Task Failed_item_is_unavailable_not_zero()
    {
        var harness = new Harness();
        var run = await harness.RunAsync(harness.Workflow(Summary), Harness.Context("South"), new JsonObject { ["days"] = 4 });

        Assert.Equal(RunStatus.PartiallySucceeded, run.Status);
        var offline = harness.Dataset(run.Outputs["summary"]).Rows.Single(r => r["stationId"]!.GetValue<string>() == "ST-05");
        Assert.Equal("unavailable", offline["status"]!.GetValue<string>());
        Assert.Null(offline["meanC"]);
        Assert.Null(offline["count"]);
        Assert.Contains(run.Invocations, i => i.InvocationKey == "readings/ST-05" && i.Status == InvocationStatus.Failed);
    }

    [Fact] // XV13 in the generic kernel: a retried submission returns the same Run; a changed one under the key is refused
    public void Idempotency_key_returns_the_same_run_or_refuses_a_different_submission()
    {
        var harness = new Harness();
        var workflow = harness.Workflow(Summary);
        var first = Assert.IsType<SubmissionResult.Accepted>(harness.Runs.Submit(new(workflow, Harness.Context(), new JsonObject { ["days"] = 2 }, "same", "akira", RunOrigin.Workflow, RunRelations.None)));
        var retry = Assert.IsType<SubmissionResult.Accepted>(harness.Runs.Submit(new(workflow, Harness.Context(), new JsonObject { ["days"] = 2 }, "same", "akira", RunOrigin.Workflow, RunRelations.None)));
        Assert.Equal(first.Run.RunId, retry.Run.RunId);

        var changed = harness.Runs.Submit(new(workflow, Harness.Context(), new JsonObject { ["days"] = 3 }, "same", "akira", RunOrigin.Workflow, RunRelations.None));
        Assert.Equal("IdempotencyConflict", Assert.IsType<SubmissionResult.Rejected>(changed).Code);
    }

    [Fact] // Determinism: the same inputs give the same synthetic readings in another Run
    public async Task Synthetic_readings_are_deterministic()
    {
        var harness = new Harness();
        var a = await harness.RunAsync(harness.Workflow(Summary), Harness.Context(), new JsonObject { ["days"] = 6 });
        var b = await harness.RunAsync(harness.Workflow(Summary), Harness.Context(), new JsonObject { ["days"] = 6 });
        static IEnumerable<double?> Means(Harness h, RunRecord r) => h.Dataset(r.Outputs["summary"]).Rows.Select(x => x["meanC"]?.GetValue<double>());
        Assert.Equal(Means(harness, a), Means(harness, b));
    }
}

/// <summary>A module the kernel has never seen: one capability, one workflow, one view.</summary>
sealed class EchoModule : IWorkbenchModule
{
    sealed class Echo : ICapabilityHandler
    {
        public CapabilityDescriptor Descriptor { get; } = new("test.echo", "1.0.0", "Echo",
            [new("text", "test.text", "1.0.0", Cardinality.One)], [new("echo", "test.echo-table", "1.0.0", Cardinality.One)],
            ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["echo"] = Datasets.Create("Echo", [new("text", DatasetColumnType.String, false)],
                    [new JsonObject { ["text"] = invocation.Inputs["text"]!.DeepClone() }],
                    new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId)),
            });
    }

    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new Echo());
        builder.AddContract(new("test.echo-input", "1.0.0", "Echo input", JsonNode.Parse("""
            { "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"], "additionalProperties": false }
            """)!.AsObject()));
        builder.AddWorkflow(Harness.Parse<WorkflowDefinition>("""
            {
              "documentType": "workflow", "schemaVersion": "1.0.0", "definitionId": "workflow.test.echo", "version": "1.0.0",
              "title": "Echo", "inputContractId": "test.echo-input", "inputContractVersion": "1.0.0",
              "steps": [ { "stepId": "echo", "capabilityId": "test.echo", "capabilityVersion": "1.0.0", "dependsOn": [],
                           "bindings": { "text": { "kind": "input", "path": "/text" } }, "failurePolicy": "stop" } ],
              "outputs": { "echo": { "kind": "output", "stepId": "echo", "port": "echo" } }
            }
            """));
    }
}
