using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>
/// A module adds capabilities, contracts, workflows, views and actions. The kernel knows modules only
/// through this interface; it never references one (acceptance CO01).
/// </summary>
public interface IWorkbenchModule
{
    void Register(CompositionRegistryBuilder builder);
}

public sealed class CompositionRegistryBuilder
{
    internal readonly Dictionary<(string, string), ICapabilityHandler> Capabilities = [];
    internal readonly Dictionary<(string, string), ContractDefinition> Contracts = [];
    internal readonly Dictionary<(string, string), WorkflowDefinition> Workflows = [];
    internal readonly Dictionary<(string, string), ViewDefinition> Views = [];
    internal readonly Dictionary<string, ActionDefinition> Actions = [];
    internal readonly Dictionary<string, IHistoryProjection> HistoryProjections = [];

    public CompositionRegistryBuilder AddCapability(ICapabilityHandler handler) =>
        Add(Capabilities, (handler.Descriptor.CapabilityId, handler.Descriptor.Version), handler, "capability");

    public CompositionRegistryBuilder AddContract(ContractDefinition contract) =>
        Add(Contracts, (contract.ContractId, contract.ContractVersion), contract, "contract");

    public CompositionRegistryBuilder AddWorkflow(WorkflowDefinition workflow) =>
        Add(Workflows, (workflow.DefinitionId, workflow.Version), workflow, "workflow");

    public CompositionRegistryBuilder AddView(ViewDefinition view) =>
        Add(Views, (view.DefinitionId, view.Version), view, "view");

    public CompositionRegistryBuilder AddAction(ActionDefinition action)
    {
        if (!Actions.TryAdd(action.ActionId, action))
            throw new InvalidOperationException($"Action {action.ActionId} is registered twice.");
        return this;
    }

    public CompositionRegistryBuilder AddHistoryProjection(IHistoryProjection projection) =>
        Add(HistoryProjections, projection.Domain, projection, "history view");

    CompositionRegistryBuilder Add<TKey, TValue>(Dictionary<TKey, TValue> into, TKey key, TValue value, string what) where TKey : notnull
    {
        if (!into.TryAdd(key, value)) throw new InvalidOperationException($"The {what} {key} is registered twice.");
        return this;
    }
}

/// <summary>Everything registered, validated once as a whole when the application starts.</summary>
public sealed class CompositionRegistry
{
    readonly IReadOnlyDictionary<(string, string), ICapabilityHandler> capabilities;
    readonly IReadOnlyDictionary<(string, string), ContractDefinition> contracts;
    readonly IReadOnlyDictionary<(string, string), WorkflowDefinition> workflows;
    readonly IReadOnlyDictionary<(string, string), ViewDefinition> views;
    readonly IReadOnlyDictionary<string, ActionDefinition> actions;
    readonly IReadOnlyDictionary<string, IHistoryProjection> historyProjections;

    public CompositionRegistry(IEnumerable<IWorkbenchModule> modules)
    {
        var builder = new CompositionRegistryBuilder();
        foreach (var module in modules) module.Register(builder);
        capabilities = builder.Capabilities;
        contracts = builder.Contracts;
        workflows = builder.Workflows;
        views = builder.Views;
        actions = builder.Actions;
        historyProjections = builder.HistoryProjections;

        var validator = new CompositionValidator(this);
        var issues = contracts.Values.SelectMany(ContractChecks.SchemaProblems)
            .Concat(workflows.Values.SelectMany(w => validator.Validate(w).Select(i => $"{w.DefinitionId}: {i}")))
            .Concat(views.Values.SelectMany(v => validator.Validate(v, boundNames: null).Select(i => $"{v.DefinitionId}: {i}")))
            .Concat(actions.Values.SelectMany(a => validator.Validate(a).Select(i => $"{a.ActionId}: {i}")))
            .ToList();
        if (issues.Count > 0)
            throw new InvalidOperationException("Registered definitions are invalid:\n" + string.Join("\n", issues));
    }

    public IEnumerable<CapabilityDescriptor> CapabilityDescriptors => capabilities.Values.Select(h => h.Descriptor);
    public IEnumerable<ContractDefinition> ContractDefinitions => contracts.Values;
    public IEnumerable<WorkflowDefinition> WorkflowDefinitions => workflows.Values;
    public IEnumerable<ViewDefinition> ViewDefinitions => views.Values;
    public IEnumerable<ActionDefinition> ActionDefinitions => actions.Values;
    public IEnumerable<IHistoryProjection> HistoryProjections => historyProjections.Values.OrderBy(p => p.Domain, StringComparer.Ordinal);

    public ICapabilityHandler? FindCapability(string id, string version) => capabilities.GetValueOrDefault((id, version));
    public ContractDefinition? FindContract(string id, string version) => contracts.GetValueOrDefault((id, version));
    public WorkflowDefinition? FindWorkflow(string id, string version) => workflows.GetValueOrDefault((id, version));
    public ViewDefinition? FindView(string id, string version) => views.GetValueOrDefault((id, version));
    public ActionDefinition? FindAction(string id) => actions.GetValueOrDefault(id);
}
