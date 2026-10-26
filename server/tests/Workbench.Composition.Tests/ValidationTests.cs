using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

// CO07: invalid definitions are refused, structurally and before anything executes.
public class ValidationTests
{
    readonly Harness harness = new();

    WorkflowDefinition Summary => harness.Workflow("workflow.sample.station-summary");

    IReadOnlyList<CompositionIssue> Issues(WorkflowDefinition workflow) => new CompositionValidator(harness.Registry).Validate(workflow).ToList();

    [Fact]
    public void Registered_sample_definitions_are_valid() => Assert.Empty(Issues(Summary));

    [Fact]
    public void A_cycle_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[0] = steps[0] with { DependsOn = ["summary"] };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "Cycle");
    }

    [Fact]
    public void An_undeclared_port_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[2] = steps[2] with { Bindings = new Dictionary<string, Binding> { ["readings"] = new OutputBinding("readings", "nonexistent") } };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "UndeclaredPort");
    }

    [Fact]
    public void An_unbound_input_port_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[0] = steps[0] with { Bindings = new Dictionary<string, Binding>() };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "UnboundPort");
    }

    [Fact]
    public void Reading_a_step_without_depending_on_it_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[2] = steps[2] with { DependsOn = [] };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "UndeclaredDependency");
    }

    [Fact]
    public void An_incompatible_contract_is_refused()
    {
        // The summariser takes many readings; the station list is one dataset of another contract.
        var steps = Summary.Steps.ToList();
        steps[2] = steps[2] with
        {
            DependsOn = ["stations"],
            Bindings = new Dictionary<string, Binding> { ["readings"] = new OutputBinding("stations", "stations") },
        };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "IncompatibleContract");
    }

    [Fact]
    public void An_unregistered_capability_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[0] = steps[0] with { CapabilityVersion = "9.9.9" };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "UnknownCapability");
    }

    [Fact]
    public void An_item_binding_outside_a_map_is_refused()
    {
        var steps = Summary.Steps.ToList();
        steps[0] = steps[0] with { Bindings = new Dictionary<string, Binding> { ["region"] = new ItemBinding("") } };
        Assert.Contains(Issues(Summary with { Steps = steps }), i => i.Code == "ItemOutsideMap");
    }

    [Theory] // A binding is data, never code: an unknown binding kind or an extra member fails to parse.
    [InlineData("""{ "kind": "expression", "source": "context.region.toUpperCase()" }""")]
    [InlineData("""{ "kind": "context", "path": "/region", "script": "fetch('x')" }""")]
    public void A_code_like_binding_is_refused(string binding)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Binding>(binding, WireJson.Options));
    }

    [Fact]
    public void A_view_with_an_unknown_action_or_unapproved_component_is_refused()
    {
        var view = harness.Registry.FindView("view.sample.station-summary", "1.0.0")!;
        var grid = view.Components[0];
        var bad = view with
        {
            Components =
            [
                grid with { Actions = [new("bookTrade", "Book", new Dictionary<string, RowBinding>())] },
                grid with { Id = "chart", Kind = ComponentKind.Chart },
            ],
        };
        var issues = new CompositionValidator(harness.Registry).Validate(bad, new HashSet<string> { "summary" }).ToList();
        Assert.Contains(issues, i => i.Code == "UnknownAction");
        Assert.Contains(issues, i => i.Code == "UnsupportedComponent");
    }

    [Fact]
    public void A_view_bound_to_data_that_is_not_there_is_refused()
    {
        var view = harness.Registry.FindView("view.sample.readings", "1.0.0")!;
        var issues = new CompositionValidator(harness.Registry).Validate(view, new HashSet<string> { "summary" }).ToList();
        Assert.Contains(issues, i => i.Code == "UnknownData");
    }

    [Fact]
    public void An_invalid_registered_definition_stops_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new CompositionRegistry([new Samples.Synthetic.SyntheticModule(), new BrokenModule()]));
        Assert.Contains("UnknownCapability", error.Message);
    }

    sealed class BrokenModule : IWorkbenchModule
    {
        public void Register(CompositionRegistryBuilder builder) => builder.AddWorkflow(Harness.Parse<WorkflowDefinition>("""
            {
              "documentType": "workflow", "schemaVersion": "1.0.0", "definitionId": "workflow.test.broken", "version": "1.0.0",
              "title": "Broken", "inputContractId": "sample.station-summary-input", "inputContractVersion": "1.0.0",
              "steps": [ { "stepId": "x", "capabilityId": "nothing.here", "capabilityVersion": "1.0.0", "dependsOn": [],
                           "bindings": {}, "failurePolicy": "stop" } ],
              "outputs": { "x": { "kind": "output", "stepId": "x", "port": "y" } }
            }
            """));
    }
}
