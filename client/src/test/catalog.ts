import type { Catalog, ContractDefinition, ViewDefinition, WorkflowDefinition } from '../platform/contracts';

// Small synthetic definitions for unit tests. Shapes follow the wire contract; names are illustrative.

export const contextContract: ContractDefinition = {
  contractId: 'workbench.context',
  contractVersion: '1.0.0',
  title: 'Context',
  schema: {
    type: 'object',
    properties: {
      region: { type: 'string', title: 'Region', enum: ['North', 'South'] },
      asOf: { type: 'string', title: 'As of', format: 'date' },
    },
    required: ['region', 'asOf'],
    additionalProperties: false,
  },
};

export const inputContract: ContractDefinition = {
  contractId: 'test.input',
  contractVersion: '1.0.0',
  title: 'Input',
  schema: {
    type: 'object',
    properties: {
      days: { type: 'integer', title: 'Days', minimum: 1, maximum: 31 },
      label: { type: 'string', title: 'Label', minLength: 1, default: 'x' },
    },
    required: ['days'],
    additionalProperties: false,
  },
};

export function workflow(definitionId: string, title: string, outputs: readonly string[]): WorkflowDefinition {
  return {
    documentType: 'workflow',
    schemaVersion: '1.0.0',
    definitionId,
    version: '1.0.0',
    title,
    inputContractId: inputContract.contractId,
    inputContractVersion: inputContract.contractVersion,
    steps: [],
    outputs: Object.fromEntries(outputs.map((o) => [o, { kind: 'output', stepId: 's', port: o }])),
  } as WorkflowDefinition;
}

export function view(definitionId: string, title: string, reads: readonly string[]): ViewDefinition {
  return {
    documentType: 'view',
    schemaVersion: '1.0.0',
    definitionId,
    version: '1.0.0',
    title,
    layout: { wideColumns: 1, narrowColumns: 1 },
    components: reads.map((name, i) => ({ id: `c${i}`, kind: 'grid', title: name, data: { kind: 'workflowOutput', name } })),
  } as ViewDefinition;
}

export function catalog(workflows: readonly WorkflowDefinition[], views: readonly ViewDefinition[]): Catalog {
  return { capabilities: [], contracts: [contextContract, inputContract], workflows, views, actions: [] };
}
