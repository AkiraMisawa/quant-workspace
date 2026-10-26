using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>
/// The bounded reference DAG executor (D09). It runs a validated workflow's steps when their dependencies
/// have finished, independent steps together, mapped items with bounded concurrency, and retains every
/// output port's value as immutable data. Downstream steps receive references, never copies.
/// </summary>
public sealed class WorkflowExecutor(CompositionRegistry registry, IDataStore store)
{
    public sealed record Result(RunStatus Status, IReadOnlyDictionary<string, TypedDataRef> Outputs, OutcomeError? Error);

    sealed record StepState(bool Usable, IReadOnlyDictionary<string, TypedDataRef> Outputs, bool HadFailures);

    public async Task<Result> ExecuteAsync(
        string runId,
        WorkflowDefinition workflow,
        JsonObject context,
        JsonObject input,
        Action<InvocationRecord> record,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, StepState>();
        var pending = workflow.Steps.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(s => s.DependsOn.All(states.ContainsKey)).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("A validated workflow has no runnable step; validation was bypassed.");
            var results = await Task.WhenAll(ready.Select(async step =>
            {
                var blockedBy = step.DependsOn.FirstOrDefault(d => !states[d].Usable);
                var state = blockedBy is null
                    ? await RunStepAsync(runId, step, context, input, states, record, cancellationToken)
                    : Blocked(step, blockedBy, record);
                return (step.StepId, state);
            }));
            foreach (var (stepId, state) in results) states[stepId] = state;
            pending.RemoveAll(ready.Contains);
        }

        var outputs = new Dictionary<string, TypedDataRef>();
        foreach (var (name, binding) in workflow.Outputs)
        {
            var output = (OutputBinding)binding;
            if (!states[output.StepId].Usable)
                return new(RunStatus.Failed, outputs, new("StepFailed", $"Output {name} is unavailable: step {output.StepId} did not succeed."));
            outputs[name] = states[output.StepId].Outputs[output.Port];
        }
        var partial = states.Values.Any(s => s.HadFailures);
        return new(partial ? RunStatus.PartiallySucceeded : RunStatus.Succeeded, outputs, null);
    }

    static StepState Blocked(WorkflowStep step, string blockedBy, Action<InvocationRecord> record)
    {
        record(new(step.StepId, step.StepId, step.CapabilityId, step.CapabilityVersion, InvocationStatus.Blocked,
            new Dictionary<string, TypedDataRef>(), new("Blocked", $"Step {blockedBy} did not succeed.", blockedBy)));
        return new(false, new Dictionary<string, TypedDataRef>(), true);
    }

    async Task<StepState> RunStepAsync(
        string runId, WorkflowStep step, JsonObject context, JsonObject input,
        IReadOnlyDictionary<string, StepState> states, Action<InvocationRecord> record, CancellationToken cancellationToken)
    {
        var handler = registry.FindCapability(step.CapabilityId, step.CapabilityVersion)
            ?? throw new InvalidOperationException($"Capability {step.CapabilityId}@{step.CapabilityVersion} vanished after validation.");

        if (step.ForEach is null)
        {
            var outcome = await InvokeAsync(runId, step.StepId, step, handler, context, input, states, item: null, record, cancellationToken);
            return outcome.Status == OutcomeStatus.Succeeded
                ? new(true, outcome.Outputs, false)
                : new(false, outcome.Outputs, true);
        }

        var items = MapItems(step, context, input, states);
        using var gate = new SemaphoreSlim(step.MaxConcurrency ?? 4);
        var outcomes = await Task.WhenAll(items.Select(async item =>
        {
            if (item.Blocked is { } cause)
            {
                record(new($"{step.StepId}/{item.Key}", step.StepId, step.CapabilityId, step.CapabilityVersion, InvocationStatus.Blocked,
                    new Dictionary<string, TypedDataRef>(), cause));
                return (item, Status: OutcomeStatus.Blocked, Outputs: (IReadOnlyDictionary<string, TypedDataRef>)new Dictionary<string, TypedDataRef>(), Error: (OutcomeError?)cause);
            }
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await InvokeAsync(runId, $"{step.StepId}/{item.Key}", step, handler, context, input, states, item.Value, record, cancellationToken);
                return (item, result.Status, result.Outputs, result.Error);
            }
            finally { gate.Release(); }
        }));

        // Each output port of a mapped step becomes one retained collection of outcomes, item order kept.
        var collections = new Dictionary<string, TypedDataRef>();
        foreach (var port in handler.Descriptor.OutputPorts)
        {
            var collection = new OutcomeCollection("mappedOutcomes", CompositionValidator.SchemaVersion, port.ContractId, port.ContractVersion,
                [.. outcomes.Select(o => o.Status == OutcomeStatus.Succeeded
                    ? new Outcome(o.item.Key, o.item.Identity, o.Status, JsonSerializer.SerializeToNode(o.Outputs[port.Name], WireJson.Options))
                    : new Outcome(o.item.Key, o.item.Identity, o.Status, Error: o.Error))]);
            collections[port.Name] = store.Put($"{runId}/{step.StepId}/{port.Name}", "qwb.mapped-outcomes", CompositionValidator.SchemaVersion,
                JsonSerializer.SerializeToNode(collection, WireJson.Options)!);
        }
        var failures = outcomes.Any(o => o.Status != OutcomeStatus.Succeeded);
        return new(!(failures && step.FailurePolicy == FailurePolicy.Stop), collections, failures);
    }

    sealed record MapItem(string Key, IReadOnlyDictionary<string, string> Identity, JsonNode? Value, OutcomeError? Blocked);

    List<MapItem> MapItems(WorkflowStep step, JsonObject context, JsonObject input, IReadOnlyDictionary<string, StepState> states)
    {
        var source = Resolve(step.ForEach!, context, input, states, item: null, resolveReferences: true);
        if (step.ItemMode == ItemMode.Value)
        {
            if (source is not JsonArray array)
                throw new CapabilityFailure("MapSource", $"Step {step.StepId} maps over a value that is not a collection.");
            var keyName = step.ItemKeyPath!.Split('/')[^1];
            var items = new List<MapItem>();
            foreach (var value in array)
            {
                if (!JsonPointer.TryResolve(value, step.ItemKeyPath!, out var keyNode) || keyNode is not JsonValue key)
                    throw new CapabilityFailure("MapKey", $"An item of step {step.StepId} has no key at {step.ItemKeyPath}.");
                items.Add(new(key.ToString(), new Dictionary<string, string> { [keyName] = key.ToString() }, value, null));
            }
            var duplicate = items.GroupBy(i => i.Key).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new CapabilityFailure("MapKey", $"Item key {duplicate.Key} occurs twice in step {step.StepId}; identity would be ambiguous.");
            return items;
        }

        var collection = source.Deserialize<OutcomeCollection>(WireJson.Options)
            ?? throw new CapabilityFailure("MapSource", $"Step {step.StepId} maps over outcomes that are null.");
        return [.. collection.Items.Select(o => o.Status == OutcomeStatus.Succeeded
            ? new MapItem(o.ItemKey, o.Identity, o.Value, null)
            : new MapItem(o.ItemKey, o.Identity, null, new OutcomeError("Blocked", $"Upstream item {o.ItemKey} did not succeed.", o.ItemKey)))];
    }

    sealed record InvocationOutcome(OutcomeStatus Status, IReadOnlyDictionary<string, TypedDataRef> Outputs, OutcomeError? Error);

    async Task<InvocationOutcome> InvokeAsync(
        string runId, string key, WorkflowStep step, ICapabilityHandler handler, JsonObject context, JsonObject input,
        IReadOnlyDictionary<string, StepState> states, JsonNode? item, Action<InvocationRecord> record, CancellationToken cancellationToken)
    {
        var descriptor = handler.Descriptor;
        record(new(key, step.StepId, descriptor.CapabilityId, descriptor.Version, InvocationStatus.Running, new Dictionary<string, TypedDataRef>(), null));
        try
        {
            var inputs = step.Bindings.ToDictionary(b => b.Key, b => Resolve(b.Value, context, input, states, item, resolveReferences: false));
            var produced = await handler.InvokeAsync(new($"{runId}/{key}", inputs, store), cancellationToken);
            var outputs = new Dictionary<string, TypedDataRef>();
            foreach (var port in descriptor.OutputPorts)
            {
                if (!produced.TryGetValue(port.Name, out var value) || value is null)
                    throw new CapabilityFailure("MissingOutput", $"{descriptor.CapabilityId} returned no value for {port.Name}.");
                outputs[port.Name] = store.Put($"{runId}/{key}/{port.Name}", port.ContractId, port.ContractVersion, value);
            }
            record(new(key, step.StepId, descriptor.CapabilityId, descriptor.Version, InvocationStatus.Succeeded, outputs, null));
            return new(OutcomeStatus.Succeeded, outputs, null);
        }
        catch (CapabilityFailure failure)
        {
            var error = new OutcomeError(failure.Code, failure.Message);
            record(new(key, step.StepId, descriptor.CapabilityId, descriptor.Version, InvocationStatus.Failed, new Dictionary<string, TypedDataRef>(), error));
            return new(OutcomeStatus.Failed, new Dictionary<string, TypedDataRef>(), error);
        }
    }

    /// <summary>
    /// Evaluates a binding. An output binding without a path is the producer's reference; with a path, or
    /// when <paramref name="resolveReferences"/> asks, it is read from the retained content.
    /// </summary>
    JsonNode? Resolve(Binding binding, JsonObject context, JsonObject input, IReadOnlyDictionary<string, StepState> states, JsonNode? item, bool resolveReferences)
    {
        return binding switch
        {
            InputBinding b => Pointer(input, b.Path, "input"),
            ContextBinding b => Pointer(context, b.Path, "context"),
            ItemBinding b => Pointer(item, b.Path, "item"),
            ConstantBinding b => b.Value?.DeepClone(),
            DataReferenceBinding b => resolveReferences ? store.ReadRequired(b.Reference) : Serialize(b.Reference),
            OutputBinding b => Output(b),
            _ => throw new InvalidOperationException($"Binding {binding.GetType().Name} has no evaluation."),
        };

        JsonNode? Output(OutputBinding b)
        {
            var reference = states[b.StepId].Outputs[b.Port];
            if (b.Path is null && !resolveReferences) return Serialize(reference);
            return Pointer(store.ReadRequired(reference), b.Path ?? "", $"{b.StepId}.{b.Port}");
        }
    }

    static JsonNode? Serialize(TypedDataRef reference) => JsonSerializer.SerializeToNode(reference, WireJson.Options);

    static JsonNode? Pointer(JsonNode? root, string path, string what) =>
        JsonPointer.TryResolve(root, path, out var node)
            ? node?.DeepClone()
            : throw new CapabilityFailure("BindingUnresolved", $"{what} has no value at '{path}'.");
}
