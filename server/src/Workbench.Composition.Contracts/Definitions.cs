using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workbench.Composition.Contracts;

// The wire vocabulary of contracts/composition.schema.json. Property names follow the schema through
// WireJson's camelCase policy; nothing here knows any financial domain.

/// <summary>An immutable revision of a retained dataset or artifact. Not a path and not a grant.</summary>
public sealed record TypedDataRef(string DataId, string ContractId, string ContractVersion, string Revision);

public enum Cardinality { One, Many }

public enum ExecutionKind { RequestResponse, Job, Subscription }

/// <summary>
/// What invoking a capability does. <c>EditWorkspace</c> and <c>Subscribe</c> make it a command (ADR-0005): invoked
/// for a caller outside Runs, never as a workflow step.
/// </summary>
public enum CapabilityEffect { ReadData, Compute, PersistAnalysis, EditWorkspace, Subscribe }

public sealed record Port(string Name, string ContractId, string ContractVersion, Cardinality Cardinality);

public sealed record CapabilityDescriptor(
    string CapabilityId,
    string Version,
    string Title,
    IReadOnlyList<Port> InputPorts,
    IReadOnlyList<Port> OutputPorts,
    ExecutionKind ExecutionKind,
    IReadOnlyList<CapabilityEffect> Effects);

public sealed record CapabilityCatalog(string DocumentType, string SchemaVersion, IReadOnlyList<CapabilityDescriptor> Capabilities);

/// <summary>Where a step input, a map source or a workflow output takes its value from. No expressions.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(InputBinding), "input")]
[JsonDerivedType(typeof(ContextBinding), "context")]
[JsonDerivedType(typeof(ConstantBinding), "constant")]
[JsonDerivedType(typeof(OutputBinding), "output")]
[JsonDerivedType(typeof(ItemBinding), "item")]
[JsonDerivedType(typeof(DataReferenceBinding), "dataReference")]
public abstract record Binding;

public sealed record InputBinding(string Path) : Binding;
public sealed record ContextBinding(string Path) : Binding;
public sealed record ConstantBinding(JsonNode? Value) : Binding;
public sealed record OutputBinding(string StepId, string Port, string? Path = null) : Binding;
public sealed record ItemBinding(string Path) : Binding;
public sealed record DataReferenceBinding(TypedDataRef Reference) : Binding;

public enum FailurePolicy { Stop, Collect }

public enum ItemMode { Value, OutcomeValue }

public sealed record WorkflowStep(
    string StepId,
    string CapabilityId,
    string CapabilityVersion,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, Binding> Bindings,
    FailurePolicy FailurePolicy,
    Binding? ForEach = null,
    int? MaxConcurrency = null,
    ItemMode? ItemMode = null,
    string? ItemKeyPath = null);

public sealed record WorkflowDefinition(
    string DocumentType,
    string SchemaVersion,
    string DefinitionId,
    string Version,
    string Title,
    string InputContractId,
    string InputContractVersion,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyDictionary<string, Binding> Outputs);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(WorkflowOutputData), "workflowOutput")]
[JsonDerivedType(typeof(DataReferenceData), "dataReference")]
public abstract record ViewData;

public sealed record WorkflowOutputData(string Name) : ViewData;
public sealed record DataReferenceData(TypedDataRef Reference) : ViewData;

public enum ColumnFormat { Text, Number, Currency, Percent, Date, Status }

public sealed record ViewColumn(string Field, string Label, ColumnFormat Format, string? CurrencyField = null);

public sealed record RowBinding(string Kind, string Field);

public sealed record ViewAction(string ActionId, string Label, IReadOnlyDictionary<string, RowBinding> Bindings);

public enum ComponentKind { Form, Choice, Grid, Chart, Metric, Progress, Details }

public sealed record ViewComponent(
    string Id,
    ComponentKind Kind,
    string Title,
    ViewData Data,
    IReadOnlyList<ViewColumn>? Columns = null,
    IReadOnlyList<ViewAction>? Actions = null);

public sealed record ViewLayout(int WideColumns, int NarrowColumns);

public sealed record ViewDefinition(
    string DocumentType,
    string SchemaVersion,
    string DefinitionId,
    string Version,
    string Title,
    ViewLayout Layout,
    IReadOnlyList<ViewComponent> Components);

public enum DatasetColumnType { String, Number, Integer, Boolean, Date, DataRef }

public sealed record DatasetColumn(string Field, DatasetColumnType Type, bool Nullable);

public enum ExecutionMode { Actual, Simulated }

public enum DataOrigin { Source, Synthetic, Mixed }

public sealed record DatasetProvenance(ExecutionMode ExecutionMode, DataOrigin DataOrigin, string ProducerInvocationId);

public sealed record Dataset(
    string DocumentType,
    string SchemaVersion,
    TypedDataRef Reference,
    string Title,
    IReadOnlyList<DatasetColumn> Columns,
    IReadOnlyList<JsonObject> Rows,
    DatasetProvenance Provenance);

public enum OutcomeStatus { Succeeded, Failed, Blocked }

public sealed record OutcomeError(string Code, string Message, string? CausedByItemKey = null);

/// <summary>One item of a mapped step: a value or an error, never both, keyed by a stable item key.</summary>
public sealed record Outcome(
    string ItemKey,
    IReadOnlyDictionary<string, string> Identity,
    OutcomeStatus Status,
    JsonNode? Value = null,
    OutcomeError? Error = null);

public sealed record OutcomeCollection(
    string DocumentType,
    string SchemaVersion,
    string ElementContractId,
    string ElementContractVersion,
    IReadOnlyList<Outcome> Items);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(OpenViewTarget), "openView")]
[JsonDerivedType(typeof(RunWorkflowTarget), "runWorkflow")]
public abstract record ActionTarget;

/// <summary>Shows a registered view over retained data: nothing executes.</summary>
public sealed record OpenViewTarget(string ViewId, string ViewVersion, string DataParameter) : ActionTarget;

/// <summary>Submits a registered workflow, its input built from the action's parameters.</summary>
public sealed record RunWorkflowTarget(string WorkflowId, string WorkflowVersion, IReadOnlyDictionary<string, string> InputFromParameters) : ActionTarget;

public enum ActionParameterKind { DataRef, Text }

public sealed record ActionParameter(string Name, ActionParameterKind Kind);

/// <summary>A registered action a view may offer on a row. Its parameters are bound from row fields.</summary>
public sealed record ActionDefinition(string ActionId, string Title, IReadOnlyList<ActionParameter> Parameters, ActionTarget Target);
