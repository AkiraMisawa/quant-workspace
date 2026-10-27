import { createContext, useContext, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useStore } from 'zustand';
import type { Api } from '../api';
import type { Catalog, Dataset, RunRecord, TypedDataRef } from '../contracts';
import { createWorkspaceStore, type WorkspaceState, type WorkspaceStore } from './workspaceStore';

interface Workspace {
  readonly api: Api;
  readonly catalog: Catalog;
  readonly store: WorkspaceStore;
  readonly notify: (message: string) => void;
}

const WorkspaceContext = createContext<Workspace | undefined>(undefined);

export const CONTEXT_CONTRACT = 'workbench.context';

export function WorkspaceProvider({ api, catalog, initialContext, notify, children }: {
  readonly api: Api;
  readonly catalog: Catalog;
  readonly initialContext: Readonly<Record<string, unknown>>;
  readonly notify: (message: string) => void;
  readonly children: ReactNode;
}) {
  const contract = catalog.contracts.find((c) => c.contractId === CONTEXT_CONTRACT);
  if (contract === undefined) throw new Error(`No module registered the ${CONTEXT_CONTRACT} contract; the workbench has no context to bind.`);
  const [store] = useState(() => createWorkspaceStore(initialContext, contract));
  return <WorkspaceContext.Provider value={{ api, catalog, store, notify }}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspaceContext(): Workspace {
  const workspace = useContext(WorkspaceContext);
  if (workspace === undefined) throw new Error('useWorkspaceContext is used outside a WorkspaceProvider.');
  return workspace;
}

/** Subscribes to one slice of the workspace store; components re-render only when that slice changes. */
export function useWorkspace<T>(selector: (state: WorkspaceState) => T): T {
  return useStore(useWorkspaceContext().store, selector);
}

export function useRun(runId: string) {
  const { api } = useWorkspaceContext();
  return useQuery<RunRecord>({
    queryKey: ['run', runId],
    queryFn: () => api.run(runId),
    // A finished Run never changes; a running one is polled until it finishes.
    refetchInterval: (query) => (query.state.data?.status === 'running' ? 300 : false),
    staleTime: (query) => (query.state.data?.status === 'running' ? 0 : Infinity),
  });
}

/** Retained data is immutable by revision, so it is fetched once and never refetched. */
export function useDataset(reference: TypedDataRef | undefined) {
  const { api } = useWorkspaceContext();
  return useQuery<Dataset>({
    queryKey: ['data', reference?.dataId, reference?.revision],
    queryFn: () => {
      if (reference === undefined) throw new Error('No reference to read.');
      return api.dataset(reference);
    },
    enabled: reference !== undefined,
    staleTime: Infinity,
    retry: false,
  });
}
