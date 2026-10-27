import type { Api } from './api';
import { ApiError } from './api';
import type { Catalog, ViewAction } from './contracts';
import { viewsFor } from './views/binding';
import { keyOf, type WorkspaceStore } from './workspace/workspaceStore';

interface Target {
  readonly api: Api;
  readonly catalog: Catalog;
  readonly store: WorkspaceStore;
  readonly notify: (message: string) => void;
}

/**
 * Invokes a registered row action on the server and shows what it produced: a view over retained data
 * in a new panel, or a new Run. The server validates; a refusal is shown with its reason.
 */
export async function invokeAction(target: Target, action: ViewAction, parameters: Record<string, unknown>, context: Readonly<Record<string, unknown>>): Promise<void> {
  try {
    const outcome = await target.api.invokeAction(action.actionId, parameters, { ...context }, crypto.randomUUID());
    const { openPanel } = target.store.getState();
    if (outcome.kind === 'openView') {
      openPanel({ kind: 'data', title: outcome.view.title, view: outcome.view });
    } else {
      const view = viewsFor(outcome.run.workflow, target.catalog.views)[0];
      openPanel({ kind: 'run', title: outcome.run.workflow.title, runId: outcome.run.runId, viewKey: view === undefined ? undefined : keyOf(view) });
    }
  } catch (error) {
    target.notify(error instanceof ApiError ? `${action.label}: ${error.envelope.message}` : `${action.label} failed: ${String(error)}`);
  }
}

/** Submits a workflow with the prepared context and opens its Run in a panel. */
export async function submitWorkflow(target: Target, workflowKey: string, input: Record<string, unknown>, context: Readonly<Record<string, unknown>>, idempotencyKey: string): Promise<boolean> {
  const workflow = target.catalog.workflows.find((w) => keyOf(w) === workflowKey);
  if (workflow === undefined) throw new Error(`Workflow ${workflowKey} is not in the catalog.`);
  try {
    const run = await target.api.submitRun(workflow, { ...context }, input, idempotencyKey);
    const view = viewsFor(workflow, target.catalog.views)[0];
    target.store.getState().openPanel({ kind: 'run', title: workflow.title, runId: run.runId, viewKey: view === undefined ? undefined : keyOf(view) });
    return true;
  } catch (error) {
    target.notify(error instanceof ApiError ? `${workflow.title}: ${error.envelope.issues.map((i) => `${i.path} ${i.message}`).join('; ') || error.envelope.message}` : String(error));
    return false;
  }
}
