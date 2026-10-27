// The wire vocabulary of contracts/composition.schema.json as the client reads it. The server's
// records are the authority; these types describe what it sends and accepts, nothing more.

export interface TypedDataRef {
  readonly dataId: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly revision: string;
}

export interface Port {
  readonly name: string;
  readonly contractId: string;
  readonly contractVersion: string;
  readonly cardinality: 'one' | 'many';
}

export interface CapabilityDescriptor {
  readonly capabilityId: string;
  readonly version: string;
  readonly title: string;
  readonly inputPorts: readonly Port[];
  readonly outputPorts: readonly Port[];
  readonly executionKind: 'requestResponse' | 'job' | 'subscription';
  readonly effects: readonly string[];
}

export interface FieldSchema {
  readonly type: 'string' | 'integer' | 'number' | 'boolean';
  readonly title?: string;
  readonly description?: string;
  readonly enum?: readonly string[];
  readonly minimum?: number;
  readonly maximum?: number;
  readonly minLength?: number;
  readonly format?: 'date';
  readonly default?: unknown;
}

export interface ContractDefinition {
  readonly contractId: string;
  readonly contractVersion: string;
  readonly title: string;
  readonly schema: {
    readonly type: 'object';
    readonly title?: string;
    readonly properties: Readonly<Record<string, FieldSchema>>;
    readonly required?: readonly string[];
    readonly additionalProperties: false;
  };
}

export type Binding =
  | { readonly kind: 'input'; readonly path: string }
  | { readonly kind: 'context'; readonly path: string }
  | { readonly kind: 'constant'; readonly value: unknown }
  | { readonly kind: 'output'; readonly stepId: string; readonly port: string; readonly path?: string }
  | { readonly kind: 'item'; readonly path: string }
  | { readonly kind: 'dataReference'; readonly reference: TypedDataRef };

export interface WorkflowStep {
  readonly stepId: string;
  readonly capabilityId: string;
  readonly capabilityVersion: string;
  readonly dependsOn: readonly string[];
  readonly bindings: Readonly<Record<string, Binding>>;
  readonly failurePolicy: 'stop' | 'collect';
  readonly forEach?: Binding;
  readonly maxConcurrency?: number;
  readonly itemMode?: 'value' | 'outcomeValue';
  readonly itemKeyPath?: string;
}

export interface WorkflowDefinition {
  readonly documentType: 'workflow';
  readonly schemaVersion: string;
  readonly definitionId: string;
  readonly version: string;
  readonly title: string;
  readonly inputContractId: string;
  readonly inputContractVersion: string;
  readonly steps: readonly WorkflowStep[];
  readonly outputs: Readonly<Record<string, Binding>>;
}

export type ViewData =
  | { readonly kind: 'workflowOutput'; readonly name: string }
  | { readonly kind: 'dataReference'; readonly reference: TypedDataRef };

export type ColumnFormat = 'text' | 'number' | 'currency' | 'percent' | 'date' | 'status';

export interface ViewColumn {
  readonly field: string;
  readonly label: string;
  readonly format: ColumnFormat;
  readonly currencyField?: string;
}

export interface ViewAction {
  readonly actionId: string;
  readonly label: string;
  readonly bindings: Readonly<Record<string, { readonly kind: 'row'; readonly field: string }>>;
}

export type ComponentKind = 'form' | 'choice' | 'grid' | 'chart' | 'metric' | 'progress' | 'details';

export interface ViewComponent {
  readonly id: string;
  readonly kind: ComponentKind;
  readonly title: string;
  readonly data: ViewData;
  readonly columns?: readonly ViewColumn[];
  readonly actions?: readonly ViewAction[];
}

export interface ViewDefinition {
  readonly documentType: 'view';
  readonly schemaVersion: string;
  readonly definitionId: string;
  readonly version: string;
  readonly title: string;
  readonly layout: { readonly wideColumns: number; readonly narrowColumns: 1 };
  readonly components: readonly ViewComponent[];
}

export type ActionTarget =
  | { readonly kind: 'openView'; readonly viewId: string; readonly viewVersion: string; readonly dataParameter: string }
  | { readonly kind: 'runWorkflow'; readonly workflowId: string; readonly workflowVersion: string; readonly inputFromParameters: Readonly<Record<string, string>> };

export interface ActionDefinition {
  readonly actionId: string;
  readonly title: string;
  readonly parameters: readonly { readonly name: string; readonly kind: 'dataRef' | 'text' }[];
  readonly target: ActionTarget;
}

export interface Catalog {
  readonly capabilities: readonly CapabilityDescriptor[];
  readonly contracts: readonly ContractDefinition[];
  readonly workflows: readonly WorkflowDefinition[];
  readonly views: readonly ViewDefinition[];
  readonly actions: readonly ActionDefinition[];
}

export type DatasetColumnType = 'string' | 'number' | 'integer' | 'boolean' | 'date' | 'dataRef';

export interface Dataset {
  readonly documentType: 'dataset';
  readonly schemaVersion: string;
  readonly reference: TypedDataRef;
  readonly title: string;
  readonly columns: readonly { readonly field: string; readonly type: DatasetColumnType; readonly nullable: boolean }[];
  readonly rows: readonly Readonly<Record<string, unknown>>[];
  readonly provenance: {
    readonly executionMode: 'actual' | 'simulated';
    readonly dataOrigin: 'source' | 'synthetic' | 'mixed';
    readonly producerInvocationId: string;
  };
}

export type RunStatus = 'running' | 'succeeded' | 'partiallySucceeded' | 'failed';

export interface OutcomeError {
  readonly code: string;
  readonly message: string;
  readonly causedByItemKey?: string;
}

export interface InvocationRecord {
  readonly invocationKey: string;
  readonly stepId: string;
  readonly capabilityId: string;
  readonly capabilityVersion: string;
  readonly status: 'running' | 'succeeded' | 'failed' | 'blocked';
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly error?: OutcomeError;
}

export interface RunRecord {
  readonly runId: string;
  readonly workflow: WorkflowDefinition;
  readonly context: Readonly<Record<string, unknown>>;
  readonly input: Readonly<Record<string, unknown>>;
  readonly status: RunStatus;
  readonly invocations: readonly InvocationRecord[];
  readonly outputs: Readonly<Record<string, TypedDataRef>>;
  readonly error?: OutcomeError;
  readonly acceptedAt: string;
  readonly completedAt?: string;
  readonly owner: string;
  readonly origin: RunOrigin;
  readonly relations: RunRelations;
  readonly resultContracts: readonly string[];
}

/** What started a Run (docs/05 §7). */
export type RunOrigin = 'pricer' | 'ai' | 'workflow' | 'action';

/** A Run's explicit links to related work; never inferred from names or timing. */
export interface RunRelations {
  readonly workspaceId?: string;
  readonly workspaceRevision?: number;
  readonly parentRunId?: string;
  readonly analysisGroupId?: string;
  readonly conversationId?: string;
}

export interface CompositionIssue {
  readonly code: string;
  readonly path: string;
  readonly message: string;
}

export interface ErrorEnvelope {
  readonly code: string;
  readonly message: string;
  readonly issues: readonly CompositionIssue[];
}
