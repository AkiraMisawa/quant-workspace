import { createStore } from 'zustand/vanilla';
import type { ContractDefinition, ViewDefinition } from '../contracts';
import { validate, type FieldIssue } from '../forms';

// One store per workspace (ADR-0003 §1): the context it works in and the panels it shows. Server data
// lives in TanStack Query; a panel holds references to it, never copies of rows.

export type Panel =
  | { readonly id: string; readonly kind: 'run'; readonly title: string; readonly runId: string; readonly viewKey: string | undefined }
  | { readonly id: string; readonly kind: 'data'; readonly title: string; readonly view: ViewDefinition }
  /** A fixed application page: an app registered in `src/apps` draws it from its parameters. */
  | { readonly id: string; readonly kind: 'app'; readonly title: string; readonly app: string; readonly params: Readonly<Record<string, string>> };

export type PanelDraft = Panel extends infer P ? (P extends Panel ? Omit<P, 'id'> : never) : never;

export interface ContextState {
  /** What the user asked for, possibly invalid or incomplete. */
  readonly requested: Readonly<Record<string, unknown>>;
  /** The last requested context that met its contract. Submissions use this, never `requested`. */
  readonly prepared: Readonly<Record<string, unknown>> | undefined;
  readonly issues: readonly FieldIssue[];
}

export interface WorkspaceState {
  readonly context: ContextState;
  readonly panels: readonly Panel[];
  readonly activePanelId: string | undefined;
  /** The workflow whose input form is open, by `definitionId@version`. */
  readonly runDialog: string | undefined;
  /** The app dialog that is open, by the name its app registered. */
  readonly appDialog: string | undefined;
  requestContext(contract: ContractDefinition, changes: Readonly<Record<string, unknown>>): void;
  openPanel(panel: PanelDraft): string;
  setPanelView(panelId: string, viewKey: string): void;
  closePanel(panelId: string): void;
  activate(panelId: string): void;
  cycle(step: 1 | -1): void;
  openRunDialog(workflowKey: string | undefined): void;
  openAppDialog(dialog: string | undefined): void;
  /** Activates the app panel with these parameters if one is open, else opens it. */
  showApp(app: string, title: string, params: Readonly<Record<string, string>>): string;
  /**
   * Opens the app panel with these parameters behind the active one, or finds it open, without activating it; it is
   * active only when no panel was (ADR-0007 §7: what the AI opens never takes the user's place).
   */
  openPanelBehind(app: string, title: string, params: Readonly<Record<string, string>>): string;
}

export type WorkspaceStore = ReturnType<typeof createWorkspaceStore>;

export function createWorkspaceStore(initialContext: Readonly<Record<string, unknown>>, contract: ContractDefinition) {
  let next = 0;
  const initialIssues = validate(contract, initialContext);
  return createStore<WorkspaceState>()((set, get) => ({
    context: { requested: initialContext, prepared: initialIssues.length === 0 ? initialContext : undefined, issues: initialIssues },
    panels: [],
    activePanelId: undefined,
    runDialog: undefined,
    appDialog: undefined,

    requestContext(contextContract, changes) {
      const requested = { ...get().context.requested, ...changes };
      const issues = validate(contextContract, requested);
      set({ context: { requested, prepared: issues.length === 0 ? requested : get().context.prepared, issues } });
    },

    openPanel(draft) {
      const id = `panel-${++next}`;
      set((state) => ({ panels: [...state.panels, { ...draft, id } as Panel], activePanelId: id }));
      return id;
    },

    setPanelView(panelId, viewKey) {
      set((state) => ({
        panels: state.panels.map((p) => (p.id === panelId && p.kind === 'run' ? { ...p, viewKey } : p)),
      }));
    },

    closePanel(panelId) {
      set((state) => {
        const index = state.panels.findIndex((p) => p.id === panelId);
        const panels = state.panels.filter((p) => p.id !== panelId);
        const active = state.activePanelId === panelId ? panels[Math.min(index, panels.length - 1)]?.id : state.activePanelId;
        return { panels, activePanelId: active };
      });
    },

    activate(panelId) {
      if (get().panels.some((p) => p.id === panelId)) set({ activePanelId: panelId });
    },

    cycle(step) {
      const { panels, activePanelId } = get();
      if (panels.length === 0) return;
      const index = panels.findIndex((p) => p.id === activePanelId);
      const target = panels[(index + step + panels.length) % panels.length];
      if (target !== undefined) set({ activePanelId: target.id });
    },

    openRunDialog(workflowKey) {
      set({ runDialog: workflowKey });
    },

    openAppDialog(dialog) {
      set({ appDialog: dialog });
    },

    showApp(app, title, params) {
      const same = (p: Panel) => p.kind === 'app' && p.app === app && JSON.stringify(p.params) === JSON.stringify(params);
      const open = get().panels.find(same);
      if (open !== undefined) {
        set({ activePanelId: open.id });
        return open.id;
      }
      return get().openPanel({ kind: 'app', title, app, params });
    },

    openPanelBehind(app, title, params) {
      const open = get().panels.find((p) => p.kind === 'app' && p.app === app && JSON.stringify(p.params) === JSON.stringify(params));
      if (open !== undefined) return open.id;
      const id = `panel-${++next}`;
      set((state) => ({ panels: [...state.panels, { kind: 'app', title, app, params, id }], activePanelId: state.activePanelId ?? id }));
      return id;
    },
  }));
}

export const keyOf = (definition: { readonly definitionId: string; readonly version: string }): string =>
  `${definition.definitionId}@${definition.version}`;
