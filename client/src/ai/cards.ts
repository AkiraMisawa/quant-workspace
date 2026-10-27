import { EventType, type BaseEvent } from '@ag-ui/core';

// One card per request (ADR-0007 §7): what was asked, each tool call with its state and result, and the reply, built
// from the run's AG-UI events alone.

export interface ToolIssue {
  readonly code: string;
  readonly path: string;
  readonly message: string;
}

/** A tool call's result as the server's tool gateway writes it. */
export type ToolOutcome =
  | { readonly status: 'done'; readonly capability: string; readonly version: string; readonly outputs: Readonly<Record<string, unknown>> }
  | { readonly status: 'refused'; readonly capability: string; readonly code: string; readonly issues: readonly ToolIssue[] };

export interface AiStep {
  readonly toolCallId: string;
  readonly tool: string;
  readonly args: string;
  readonly outcome: ToolOutcome | undefined;
}

export interface OpenedPanel {
  readonly panelId: string;
  readonly title: string;
}

export interface AiCard {
  readonly runId: string;
  readonly request: string;
  readonly status: 'sending' | 'running' | 'done' | 'cancelled' | 'failed';
  /** The runtime that answered, as the run names it, and whether it is a model at all. */
  readonly runtime: { readonly label: string; readonly simulated: boolean } | undefined;
  readonly steps: readonly AiStep[];
  readonly reply: string;
  readonly error: string | undefined;
  /** Panels this request's results opened behind the active one. */
  readonly opened: readonly OpenedPanel[];
}

export function newCard(runId: string, request: string): AiCard {
  return { runId, request, status: 'sending', runtime: undefined, steps: [], reply: '', error: undefined, opened: [] };
}

type Fields = Readonly<Record<string, unknown>>;

const text = (event: Fields, name: string): string => {
  const value = event[name];
  if (typeof value !== 'string') throw new Error(`An AG-UI ${String(event.type)} event has no ${name}.`);
  return value;
};

/** The card after one event of its run. An event this card does not draw leaves it as it is. */
export function applyEvent(card: AiCard, event: BaseEvent): AiCard {
  const e = event as unknown as Fields;
  switch (event.type) {
    case EventType.RUN_STARTED: {
      const workbench = (e.metadata as Fields | undefined)?.workbench as Fields | undefined;
      const runtime = workbench === undefined ? undefined : { label: text(workbench, 'label'), simulated: workbench.simulated === true };
      return { ...card, status: 'running', runtime };
    }
    case EventType.TEXT_MESSAGE_CONTENT:
      return { ...card, reply: card.reply + text(e, 'delta') };
    case EventType.TEXT_MESSAGE_END:
      return card.reply === '' || card.reply.endsWith('\n') ? card : { ...card, reply: card.reply + '\n' };
    case EventType.TOOL_CALL_START:
      return { ...card, steps: [...card.steps, { toolCallId: text(e, 'toolCallId'), tool: text(e, 'toolCallName'), args: '', outcome: undefined }] };
    case EventType.TOOL_CALL_ARGS:
      return step(card, text(e, 'toolCallId'), (s) => ({ ...s, args: s.args + text(e, 'delta') }));
    case EventType.TOOL_CALL_RESULT: {
      const content = e.content;
      if (typeof content !== 'string') throw new Error('A tool result is the JSON text of its outcome.');
      return step(card, text(e, 'toolCallId'), (s) => ({ ...s, outcome: JSON.parse(content) as ToolOutcome }));
    }
    case EventType.RUN_FINISHED: {
      const outcome = (e.outcome as Fields | undefined)?.type;
      return { ...card, status: outcome === 'cancelled' ? 'cancelled' : 'done', reply: card.reply.trimEnd() };
    }
    case EventType.RUN_ERROR:
      return { ...card, status: 'failed', error: text(e, 'message'), reply: card.reply.trimEnd() };
    default:
      return card;
  }
}

function step(card: AiCard, toolCallId: string, change: (s: AiStep) => AiStep): AiCard {
  if (!card.steps.some((s) => s.toolCallId === toolCallId)) throw new Error(`An event names tool call ${toolCallId}, which did not start.`);
  return { ...card, steps: card.steps.map((s) => (s.toolCallId === toolCallId ? change(s) : s)) };
}
