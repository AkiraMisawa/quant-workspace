import type { QueryClient } from '@tanstack/react-query';
import type { Context } from '@ag-ui/core';
import type { Panel, WorkspaceState } from '../platform/workspace/workspaceStore';

// What an app registers with the AI pane, so the pane knows no domain: the context its panel gives the agent
// (ADR-0007 §8), and what the pane shows once one of its commands has done something (§7). A result may open a panel
// behind the active one; it never activates a panel or moves the keyboard.

export interface AiEffectTarget {
  readonly workspace: Pick<WorkspaceState, 'openPanelBehind'>;
  readonly queryClient: QueryClient;
}

/** What a done command's outputs change on screen. Answers the panel it opened, if any. */
export type AiEffect = (outputs: Readonly<Record<string, unknown>>, target: AiEffectTarget) => { readonly panelId: string; readonly title: string } | undefined;

const effects = new Map<string, AiEffect>();
const contexts = new Map<string, (panel: Extract<Panel, { kind: 'app' }>) => Context | undefined>();

// Registered when an app's module loads; loading it again (a hot reload) replaces what it registered.
export function registerAiEffect(capabilityId: string, effect: AiEffect): void {
  effects.set(capabilityId, effect);
}

export function aiEffectOf(capabilityId: string): AiEffect | undefined {
  return effects.get(capabilityId);
}

export function registerAiContext(app: string, context: (panel: Extract<Panel, { kind: 'app' }>) => Context | undefined): void {
  contexts.set(app, context);
}

/** The context the active panel gives: what its app registered, or nothing. */
export function aiContextOf(panel: Panel | undefined): Context[] {
  if (panel?.kind !== 'app') return [];
  const entry = contexts.get(panel.app)?.(panel);
  return entry === undefined ? [] : [entry];
}
