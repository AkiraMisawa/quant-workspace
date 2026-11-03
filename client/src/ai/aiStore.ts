import { createStore } from 'zustand/vanilla';
import type { Context } from '@ag-ui/core';
import type { AgentTransport } from './agui';
import { applyEvent, newCard, type AiCard, type OpenedPanel, type ToolOutcome } from './cards';

// The AI pane's state: whether it is shown, the conversation it speaks in, and one card per request. The conversation
// itself is the server's (ADR-0007 §2); the pane sends only the new request and draws what the run's events say.

export interface AiState {
  readonly open: boolean;
  readonly threadId: string;
  readonly cards: readonly AiCard[];
  setOpen(open: boolean): void;
  /** Sends one request with the context it was asked in. Refused while a request is still being answered. */
  ask(request: string, context: readonly Context[]): string | undefined;
  /** Stops the request being answered, before its next tool call. */
  stop(): string | undefined;
  /** Starts a new conversation; the cards of the old one go with it. */
  restart(): string | undefined;
}

export type AiStore = ReturnType<typeof createAiStore>;

/** Called once a tool call is done, to apply its effect; answers the panel it opened, if any. */
export type OnToolDone = (outcome: Extract<ToolOutcome, { status: 'done' }>) => OpenedPanel | undefined;

const id = (prefix: string) => `${prefix}-${crypto.randomUUID()}`;

export function createAiStore(transport: AgentTransport, onToolDone: OnToolDone) {
  const store = createStore<AiState>()((set, get) => ({
    open: false,
    threadId: id('thread'),
    cards: [],

    setOpen(open) {
      set({ open });
    },

    ask(request, context) {
      const text = request.trim();
      if (text === '') return 'Write a request first.';
      if (answering(get())) return 'The AI is still answering; wait for it or /stop it.';
      const threadId = get().threadId;
      const runId = id('run');
      set((s) => ({ open: true, cards: [...s.cards, newCard(runId, text)] }));
      const update = (change: (card: AiCard) => AiCard) =>
        set((s) => (s.threadId !== threadId ? s : { cards: s.cards.map((c) => (c.runId === runId ? change(c) : c)) }));
      void transport
        .run(
          { threadId, runId, state: {}, messages: [{ id: id('msg'), role: 'user', content: text }], tools: [], context: [...context], forwardedProps: {} },
          (event) => {
            const card = get().threadId === threadId ? get().cards.find((c) => c.runId === runId) : undefined;
            if (card === undefined) return;
            let next = applyEvent(card, event);
            // A result applies its effect once, as it arrives.
            const done = next.steps.find((s, i) => s.outcome?.status === 'done' && card.steps[i]?.outcome === undefined)?.outcome;
            if (done?.status === 'done') {
              const opened = onToolDone(done);
              if (opened !== undefined) next = { ...next, opened: [...next.opened, opened] };
            }
            update(() => next);
          },
        )
        .catch((error: unknown) => update((card) => ({ ...card, status: 'failed', error: error instanceof Error ? error.message : String(error) })));
      return undefined;
    },

    stop() {
      const card = get().cards.find((c) => c.status === 'sending' || c.status === 'running');
      if (card === undefined) return 'Nothing is being answered.';
      transport.cancel(get().threadId, card.runId).catch((error: unknown) =>
        set((s) => ({ cards: s.cards.map((c) => (c.runId === card.runId ? { ...c, error: `Stop was refused: ${error instanceof Error ? error.message : String(error)}` } : c)) })));
      return undefined;
    },

    restart() {
      if (answering(get())) return 'The AI is still answering; /stop it first.';
      set({ threadId: id('thread'), cards: [] });
      return undefined;
    },
  }));
  return store;
}

export function answering(state: Pick<AiState, 'cards'>): boolean {
  return state.cards.some((c) => c.status === 'sending' || c.status === 'running');
}
