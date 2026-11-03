import { createContext, useContext, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useStore } from 'zustand';
import { useWorkspaceContext } from '../platform/workspace/WorkspaceProvider';
import { httpAgentTransport, type AgentTransport } from './agui';
import { createAiStore, type AiState, type AiStore } from './aiStore';
import { aiEffectOf } from './effects';

interface Ai {
  readonly store: AiStore;
  readonly transport: AgentTransport;
}

const AiContext = createContext<Ai | undefined>(undefined);

/** The AI pane's store, inside the workspace whose panels its results may open. */
export function AiProvider({ transport = httpAgentTransport, children }: { readonly transport?: AgentTransport; readonly children: ReactNode }) {
  const workspace = useWorkspaceContext();
  const queryClient = useQueryClient();
  const [store] = useState(() => createAiStore(transport, (outcome) =>
    aiEffectOf(outcome.capability)?.(outcome.outputs, { workspace: workspace.store.getState(), queryClient })));
  return <AiContext.Provider value={{ store, transport }}>{children}</AiContext.Provider>;
}

export function useAiContext(): Ai {
  const ai = useContext(AiContext);
  if (ai === undefined) throw new Error('useAiContext is used outside an AiProvider.');
  return ai;
}

/** The AI store where a pane is mounted, else undefined (the command line works without one). */
export function useOptionalAiStore(): AiStore | undefined {
  return useContext(AiContext)?.store;
}

export function useAi<T>(selector: (state: AiState) => T): T {
  return useStore(useAiContext().store, selector);
}
