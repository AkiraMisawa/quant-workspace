import type { TypedDataRef, ViewAction, ViewComponent, ViewDefinition, WorkflowDefinition } from '../contracts';

// Pure rules for attaching declarative views to retained data. Nothing here executes anything.

/** The data names a view reads by name. */
export function boundNames(view: ViewDefinition): Set<string> {
  return new Set(view.components.flatMap((c) => (c.data.kind === 'workflowOutput' ? [c.data.name] : [])));
}

/** Registered views that can show a workflow's outputs: every name they read is one of its outputs. */
export function viewsFor(workflow: WorkflowDefinition, views: readonly ViewDefinition[]): ViewDefinition[] {
  const outputs = new Set(Object.keys(workflow.outputs));
  return views.filter((view) => {
    const names = boundNames(view);
    return names.size > 0 && [...names].every((name) => outputs.has(name));
  });
}

/**
 * Where a component's rows come from: a named output of the Run it is attached to, or the concrete
 * reference the view carries. A name the Run did not produce is reported, not guessed.
 */
export function referenceOf(component: ViewComponent, outputs: Readonly<Record<string, TypedDataRef>>): TypedDataRef | { readonly missing: string } {
  if (component.data.kind === 'dataReference') return component.data.reference;
  return outputs[component.data.name] ?? { missing: component.data.name };
}

export type ActionParameters =
  | { readonly ok: true; readonly parameters: Record<string, unknown> }
  | { readonly ok: false; readonly missing: readonly string[] };

/**
 * An action's parameters taken from a row's fields. A field that is absent or null on this row makes
 * the action unavailable for it (a station without readings has no readings to show).
 */
export function actionParameters(action: ViewAction, row: Readonly<Record<string, unknown>>): ActionParameters {
  const parameters: Record<string, unknown> = {};
  const missing: string[] = [];
  for (const [name, binding] of Object.entries(action.bindings)) {
    const value = row[binding.field];
    if (value === undefined || value === null) missing.push(name);
    else parameters[name] = value;
  }
  return missing.length === 0 ? { ok: true, parameters } : { ok: false, missing };
}
