using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>A reason a definition is refused, located by a JSON Pointer into the definition.</summary>
public sealed record CompositionIssue(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

/// <summary>
/// Structural validation of workflow, view and action definitions against the registry: membership,
/// versions, ports, bindings, acyclicity and output-to-input contract agreement. Domain validators check
/// economic meaning separately; passing here does not authorise anything (D11).
/// </summary>
public sealed class CompositionValidator(CompositionRegistry registry)
{
    public const string SchemaVersion = "1.0.0";

    public IEnumerable<CompositionIssue> Validate(WorkflowDefinition workflow)
    {
        if (workflow.DocumentType != "workflow")
            yield return new("DocumentType", "/documentType", "A workflow's documentType is 'workflow'.");
        if (workflow.SchemaVersion != SchemaVersion)
            yield return new("SchemaVersion", "/schemaVersion", $"Schema version {workflow.SchemaVersion} is not supported.");
        if (registry.FindContract(workflow.InputContractId, workflow.InputContractVersion) is null)
            yield return new("UnknownContract", "/inputContractId", $"Input contract {workflow.InputContractId}@{workflow.InputContractVersion} is not registered.");
        if (workflow.Steps.Count == 0)
            yield return new("NoSteps", "/steps", "A workflow has at least one step.");

        var steps = new Dictionary<string, (WorkflowStep Step, CapabilityDescriptor? Descriptor, int Index)>();
        for (var i = 0; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];
            var descriptor = registry.FindCapability(step.CapabilityId, step.CapabilityVersion)?.Descriptor;
            if (!steps.TryAdd(step.StepId, (step, descriptor, i)))
                yield return new("DuplicateStep", $"/steps/{i}/stepId", $"Step id {step.StepId} is used twice.");
            if (descriptor is null)
                yield return new("UnknownCapability", $"/steps/{i}/capabilityId", $"Capability {step.CapabilityId}@{step.CapabilityVersion} is not registered.");
            else if (descriptor.IsCommand())
                yield return new("CommandAsStep", $"/steps/{i}/capabilityId",
                    $"{step.CapabilityId} edits a workspace or runs a subscription; it is invoked for a caller, not as a workflow step.");
        }

        foreach (var (step, descriptor, i) in steps.Values)
        {
            foreach (var dependency in step.DependsOn.Where(d => !steps.ContainsKey(d)))
                yield return new("UnknownDependency", $"/steps/{i}/dependsOn", $"Step {dependency} does not exist.");

            var mapped = step.ForEach is not null;
            if (mapped != step.ItemMode.HasValue)
                yield return new("MapShape", $"/steps/{i}", "forEach and itemMode are given together or not at all.");
            if (step.ItemMode == ItemMode.Value && step.ItemKeyPath is null)
                yield return new("MapShape", $"/steps/{i}/itemKeyPath", "A value map names the item key path.");
            if (step.ForEach is { } source)
                foreach (var issue in ValidateBinding(source, $"/steps/{i}/forEach", step, steps, allowItem: false))
                    yield return issue;

            var referenced = new HashSet<string>();
            foreach (var (port, binding) in step.Bindings)
            {
                var path = $"/steps/{i}/bindings/{port}";
                var inputPort = descriptor?.InputPorts.FirstOrDefault(p => p.Name == port);
                if (descriptor is not null && inputPort is null)
                    yield return new("UndeclaredPort", path, $"{descriptor.CapabilityId} has no input port {port}.");
                foreach (var issue in ValidateBinding(binding, path, step, steps, allowItem: mapped))
                    yield return issue;
                if (binding is OutputBinding output)
                {
                    referenced.Add(output.StepId);
                    if (inputPort is not null && output.Path is null && OutputPortOf(output, steps) is { } producer && !Agrees(producer, inputPort))
                        yield return new("IncompatibleContract", path,
                            $"{output.StepId}.{output.Port} ({Describe(producer)}) does not meet {port} ({Describe(inputPort)}).");
                }
            }
            if (step.ForEach is OutputBinding mapSource) referenced.Add(mapSource.StepId);
            foreach (var port in descriptor?.InputPorts.Where(p => !step.Bindings.ContainsKey(p.Name)) ?? [])
                yield return new("UnboundPort", $"/steps/{i}/bindings", $"Input port {port.Name} is not bound.");
            foreach (var missing in referenced.Where(r => steps.ContainsKey(r) && !step.DependsOn.Contains(r)))
                yield return new("UndeclaredDependency", $"/steps/{i}/dependsOn", $"Step {step.StepId} reads {missing} but does not depend on it.");
        }

        if (FindCycle(steps.Values.Select(s => s.Step).ToList()) is { } cycle)
            yield return new("Cycle", "/steps", $"The steps form a cycle: {string.Join(" -> ", cycle)}.");

        if (workflow.Outputs.Count == 0)
            yield return new("NoOutputs", "/outputs", "A workflow names at least one output.");
        foreach (var (name, binding) in workflow.Outputs)
        {
            if (binding is not OutputBinding)
                yield return new("OutputBinding", $"/outputs/{name}", "A workflow output is bound to a step output.");
            foreach (var issue in ValidateBinding(binding, $"/outputs/{name}", null, steps, allowItem: false))
                yield return issue;
        }
    }

    public IEnumerable<CompositionIssue> Validate(ViewDefinition view, IReadOnlySet<string>? boundNames)
    {
        if (view.DocumentType != "view")
            yield return new("DocumentType", "/documentType", "A view's documentType is 'view'.");
        if (view.SchemaVersion != SchemaVersion)
            yield return new("SchemaVersion", "/schemaVersion", $"Schema version {view.SchemaVersion} is not supported.");
        if (view.Layout.WideColumns is < 1 or > 3 || view.Layout.NarrowColumns != 1)
            yield return new("Layout", "/layout", "Wide layouts have 1 to 3 columns and narrow layouts 1.");
        if (view.Components.Count == 0)
            yield return new("NoComponents", "/components", "A view has at least one component.");

        var ids = new HashSet<string>();
        for (var i = 0; i < view.Components.Count; i++)
        {
            var component = view.Components[i];
            var path = $"/components/{i}";
            if (!ids.Add(component.Id))
                yield return new("DuplicateComponent", $"{path}/id", $"Component id {component.Id} is used twice.");
            if (component.Kind is not (ComponentKind.Grid or ComponentKind.Details))
                yield return new("UnsupportedComponent", $"{path}/kind", $"Component kind {component.Kind} has no approved renderer yet.");
            if (component.Data is WorkflowOutputData output && boundNames is not null && !boundNames.Contains(output.Name))
                yield return new("UnknownData", $"{path}/data", $"No data named {output.Name} is bound to this view.");
            if (component.Kind == ComponentKind.Grid && (component.Columns is null || component.Columns.Count == 0))
                yield return new("NoColumns", $"{path}/columns", "A grid names its columns.");
            var fields = new HashSet<string>();
            foreach (var (column, c) in (component.Columns ?? []).Select((column, c) => (column, c)))
                if (!fields.Add(column.Field))
                    yield return new("DuplicateColumn", $"{path}/columns/{c}", $"Field {column.Field} is shown twice.");
            foreach (var (action, a) in (component.Actions ?? []).Select((action, a) => (action, a)))
            {
                var definition = registry.FindAction(action.ActionId);
                if (definition is null)
                {
                    yield return new("UnknownAction", $"{path}/actions/{a}", $"Action {action.ActionId} is not registered.");
                    continue;
                }
                var parameters = definition.Parameters.Select(p => p.Name).ToHashSet();
                if (!parameters.SetEquals(action.Bindings.Keys))
                    yield return new("ActionBindings", $"{path}/actions/{a}/bindings",
                        $"Action {action.ActionId} takes [{string.Join(", ", parameters)}].");
                foreach (var (name, binding) in action.Bindings.Where(b => b.Value.Kind != "row"))
                    yield return new("ActionBindings", $"{path}/actions/{a}/bindings/{name}", "An action parameter is bound to a row field.");
            }
        }
    }

    public IEnumerable<CompositionIssue> Validate(ActionDefinition action)
    {
        var parameters = action.Parameters.Select(p => p.Name).ToHashSet();
        switch (action.Target)
        {
            case OpenViewTarget open:
                var view = registry.FindView(open.ViewId, open.ViewVersion);
                if (view is null)
                    yield return new("UnknownView", "/target/viewId", $"View {open.ViewId}@{open.ViewVersion} is not registered.");
                else
                    foreach (var issue in Validate(view, new HashSet<string> { open.DataParameter }))
                        yield return issue with { Path = $"/target/view{issue.Path}" };
                if (action.Parameters.FirstOrDefault(p => p.Name == open.DataParameter) is not { Kind: ActionParameterKind.DataRef })
                    yield return new("ActionParameter", "/target/dataParameter", $"{open.DataParameter} is not a data reference parameter.");
                break;
            case RunWorkflowTarget run:
                if (registry.FindWorkflow(run.WorkflowId, run.WorkflowVersion) is null)
                    yield return new("UnknownWorkflow", "/target/workflowId", $"Workflow {run.WorkflowId}@{run.WorkflowVersion} is not registered.");
                foreach (var source in run.InputFromParameters.Values.Where(v => !parameters.Contains(v)))
                    yield return new("ActionParameter", "/target/inputFromParameters", $"{source} is not a parameter of {action.ActionId}.");
                break;
        }
    }

    IEnumerable<CompositionIssue> ValidateBinding(
        Binding binding, string path, WorkflowStep? step,
        IReadOnlyDictionary<string, (WorkflowStep Step, CapabilityDescriptor? Descriptor, int Index)> steps, bool allowItem)
    {
        string? pointer = binding switch
        {
            InputBinding b => b.Path,
            ContextBinding b => b.Path,
            ItemBinding b => b.Path,
            OutputBinding b => b.Path,
            _ => null,
        };
        if (pointer is not null && !JsonPointer.IsValid(pointer))
            yield return new("BindingPath", path, $"'{pointer}' is not a JSON Pointer.");
        if (binding is ItemBinding && !allowItem)
            yield return new("ItemOutsideMap", path, "An item binding is only valid in a mapped step.");
        if (binding is OutputBinding output)
        {
            if (!steps.TryGetValue(output.StepId, out var producer))
                yield return new("UnknownStep", path, $"Step {output.StepId} does not exist.");
            else if (producer.Descriptor is { } descriptor && descriptor.OutputPorts.All(p => p.Name != output.Port))
                yield return new("UndeclaredPort", path, $"{descriptor.CapabilityId} has no output port {output.Port}.");
            if (step is not null && output.StepId == step.StepId)
                yield return new("Cycle", path, $"Step {step.StepId} reads its own output.");
        }
    }

    /// <summary>What a step's output port carries downstream: a mapped step lifts it to a collection of outcomes.</summary>
    static Port? OutputPortOf(OutputBinding output, IReadOnlyDictionary<string, (WorkflowStep Step, CapabilityDescriptor? Descriptor, int Index)> steps)
    {
        if (!steps.TryGetValue(output.StepId, out var producer) || producer.Descriptor is null) return null;
        var port = producer.Descriptor.OutputPorts.FirstOrDefault(p => p.Name == output.Port);
        if (port is null) return null;
        return producer.Step.ForEach is null ? port : port with { Cardinality = Cardinality.Many };
    }

    static bool Agrees(Port produced, Port consumed) =>
        produced.ContractId == consumed.ContractId
        && produced.ContractVersion == consumed.ContractVersion
        && produced.Cardinality == consumed.Cardinality;

    static string Describe(Port port) => $"{port.ContractId}@{port.ContractVersion}, {port.Cardinality}";

    static IReadOnlyList<string>? FindCycle(IReadOnlyList<WorkflowStep> steps)
    {
        var edges = steps.ToDictionary(s => s.StepId, s => s.DependsOn);
        var state = new Dictionary<string, int>();
        var trail = new List<string>();
        foreach (var start in edges.Keys)
            if (Visit(start) is { } cycle) return cycle;
        return null;

        IReadOnlyList<string>? Visit(string id)
        {
            if (!edges.TryGetValue(id, out var next)) return null;
            if (state.GetValueOrDefault(id) == 2) return null;
            if (state.GetValueOrDefault(id) == 1) return [.. trail.SkipWhile(t => t != id), id];
            state[id] = 1;
            trail.Add(id);
            foreach (var n in next)
                if (Visit(n) is { } cycle) return cycle;
            trail.RemoveAt(trail.Count - 1);
            state[id] = 2;
            return null;
        }
    }
}
