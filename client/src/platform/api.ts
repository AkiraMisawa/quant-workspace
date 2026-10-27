import type { Catalog, Dataset, ErrorEnvelope, RunRecord, TypedDataRef, ViewDefinition, WorkflowDefinition } from './contracts';

/** A refusal from the server, carrying its code and the located issues. Never turned into an empty result. */
export class ApiError extends Error {
  constructor(readonly status: number, readonly envelope: ErrorEnvelope) {
    super(envelope.message);
  }
}

export async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'content-type': 'application/json', ...init?.headers },
  });
  if (!response.ok) {
    const text = await response.text();
    let envelope: ErrorEnvelope;
    try {
      envelope = JSON.parse(text) as ErrorEnvelope;
    } catch {
      envelope = { code: `Http${response.status}`, message: text || response.statusText, issues: [] };
    }
    throw new ApiError(response.status, envelope);
  }
  return (await response.json()) as T;
}

export type ActionOutcome =
  | { readonly kind: 'openView'; readonly view: ViewDefinition }
  | { readonly kind: 'run'; readonly run: RunRecord };

export interface Api {
  catalog(): Promise<Catalog>;
  submitRun(workflow: WorkflowDefinition, context: Record<string, unknown>, input: Record<string, unknown>, idempotencyKey: string): Promise<RunRecord>;
  run(runId: string): Promise<RunRecord>;
  dataset(reference: TypedDataRef): Promise<Dataset>;
  invokeAction(actionId: string, parameters: Record<string, unknown>, context: Record<string, unknown>, idempotencyKey: string): Promise<ActionOutcome>;
}

export const httpApi: Api = {
  catalog: () => call('/api/catalog'),
  submitRun: (workflow, context, input, idempotencyKey) =>
    call('/api/runs', { method: 'POST', body: JSON.stringify({ workflow, context, input, idempotencyKey }) }),
  run: (runId) => call(`/api/runs/${encodeURIComponent(runId)}`),
  dataset: (reference) => call('/api/data/read', { method: 'POST', body: JSON.stringify(reference) }),
  invokeAction: (actionId, parameters, context, idempotencyKey) =>
    call(`/api/actions/${encodeURIComponent(actionId)}/invoke`, { method: 'POST', body: JSON.stringify({ parameters, context, idempotencyKey }) }),
};
