import { EventType, type BaseEvent, type RunAgentInput } from '@ag-ui/core';
import type { AgentTransport } from './agui';
import { createAiStore, type OnToolDone } from './aiStore';
import { applyEvent, newCard } from './cards';
import { slashMatches } from './AiPane';

// ADR-0007 §7: one card per request, built from the run's events; a result applies its effect once; a request is not
// sent while another is answered; the chat's own commands are found by `/`.

const created = JSON.stringify({ status: 'done', capability: 'rates.line.create', version: '1.0.0', outputs: { line: { workspaceId: 'W-1', valuationDate: '2026-10-27' } } });
const refused = JSON.stringify({ status: 'refused', capability: 'rates.live.start', code: 'ValidationFailed', issues: [{ code: 'Required', path: '/inputs/workspace/workspaceId', message: 'workspaceId is required.' }] });

const run: BaseEvent[] = [
  { type: EventType.RUN_STARTED, threadId: 't', runId: 'r', metadata: { workbench: { runtime: 'scripted', label: 'scripted · no model', simulated: true } } } as BaseEvent,
  { type: EventType.TOOL_CALL_START, toolCallId: 'c-1', toolCallName: 'rates_line_create', parentMessageId: 'm' } as BaseEvent,
  { type: EventType.TOOL_CALL_ARGS, toolCallId: 'c-1', delta: '{"terms":{}}' } as BaseEvent,
  { type: EventType.TOOL_CALL_END, toolCallId: 'c-1' } as BaseEvent,
  { type: EventType.TOOL_CALL_RESULT, messageId: 'm-2', toolCallId: 'c-1', content: created, role: 'tool' } as BaseEvent,
  { type: EventType.TOOL_CALL_START, toolCallId: 'c-2', toolCallName: 'rates_live_start', parentMessageId: 'm-3' } as BaseEvent,
  { type: EventType.TOOL_CALL_END, toolCallId: 'c-2' } as BaseEvent,
  { type: EventType.TOOL_CALL_RESULT, messageId: 'm-4', toolCallId: 'c-2', content: refused, role: 'tool' } as BaseEvent,
  { type: EventType.TEXT_MESSAGE_START, messageId: 'm-5', role: 'assistant' } as BaseEvent,
  { type: EventType.TEXT_MESSAGE_CONTENT, messageId: 'm-5', delta: 'rates.live.start was refused.' } as BaseEvent,
  { type: EventType.TEXT_MESSAGE_END, messageId: 'm-5' } as BaseEvent,
  { type: EventType.RUN_FINISHED, threadId: 't', runId: 'r', outcome: { type: 'success' } } as BaseEvent,
];

function transport(events: readonly BaseEvent[], hold?: Promise<void>): AgentTransport & { inputs: RunAgentInput[] } {
  const inputs: RunAgentInput[] = [];
  return {
    inputs,
    runtime: () => Promise.resolve({ name: 'scripted', label: 'scripted · no model', simulated: true }),
    async run(input, onEvent) {
      inputs.push(input);
      await hold;
      for (const e of events) onEvent(e);
    },
    cancel: () => Promise.resolve(),
  };
}

describe('the AI pane', () => {
  it('ADR-0007 §7: a card shows each tool call with its outcome, the reply and the runtime', () => {
    const card = run.reduce(applyEvent, newCard('r', 'make a swap and tick it'));
    expect(card.status).toBe('done');
    expect(card.runtime).toEqual({ label: 'scripted · no model', simulated: true });
    expect(card.steps.map((s) => [s.tool, s.outcome?.status])).toEqual([['rates_line_create', 'done'], ['rates_live_start', 'refused']]);
    expect(card.steps[0]!.args).toBe('{"terms":{}}');
    expect(card.reply).toBe('rates.live.start was refused.');
  });

  it('ADR-0007 §7: a run that fails says so, with its message', () => {
    const card = [run[0]!, { type: EventType.RUN_ERROR, message: 'scripted · no model failed: boom', code: 'AgentFailed' } as BaseEvent].reduce(applyEvent, newCard('r', 'x'));
    expect(card.status).toBe('failed');
    expect(card.error).toBe('scripted · no model failed: boom');
  });

  it('ADR-0007 §2, §7: sends only the new request with its context, and applies a done result once', async () => {
    const onDone = vi.fn<OnToolDone>(() => ({ panelId: 'panel-9', title: 'Rates 2026-10-27' }));
    const agent = transport(run);
    const store = createAiStore(agent, onDone);
    const context = [{ description: 'The Rates workspace open in the active panel', value: '{"kind":"rates.workspace","workspaceId":"W-1"}' }];

    expect(store.getState().ask('  make a swap  ', context)).toBeUndefined();
    await vi.waitFor(() => expect(store.getState().cards[0]?.status).toBe('done'));

    const input = agent.inputs[0]!;
    expect(input.messages).toEqual([expect.objectContaining({ role: 'user', content: 'make a swap' })]);
    expect(input.tools).toEqual([]);
    expect(input.context).toEqual(context);
    expect(onDone).toHaveBeenCalledTimes(1);
    expect(onDone.mock.calls[0]![0]).toMatchObject({ capability: 'rates.line.create' });
    expect(store.getState().cards[0]!.opened).toEqual([{ panelId: 'panel-9', title: 'Rates 2026-10-27' }]);
    expect(store.getState().open).toBe(true);
  });

  it('refuses a second request while one is answered, and a new conversation then', async () => {
    let release!: () => void;
    const store = createAiStore(transport(run, new Promise<void>((r) => { release = r; })), () => undefined);
    store.getState().ask('one', []);
    const thread = store.getState().threadId;

    expect(store.getState().ask('two', [])).toMatch(/still answering/);
    expect(store.getState().restart()).toMatch(/stop it first/);
    release();
    await vi.waitFor(() => expect(store.getState().cards[0]?.status).toBe('done'));

    expect(store.getState().restart()).toBeUndefined();
    expect(store.getState().cards).toEqual([]);
    expect(store.getState().threadId).not.toBe(thread);
  });

  it('a transport failure fails the card instead of leaving it answering', async () => {
    const broken: AgentTransport = { ...transport([]), run: () => Promise.reject(new Error('The connection to the agent was lost before its run ended; it may still be running on the server.')) };
    const store = createAiStore(broken, () => undefined);
    store.getState().ask('x', []);
    await vi.waitFor(() => expect(store.getState().cards[0]?.status).toBe('failed'));
    expect(store.getState().cards[0]!.error).toMatch(/may still be running/);
  });

  it('ADR-0006 §2: `/` lists the chat commands, narrowed as they are typed', () => {
    expect(slashMatches('/').map((c) => c.words)).toEqual(['new', 'stop', 'help', 'close']);
    expect(slashMatches('/st').map((c) => c.words)).toEqual(['stop']);
    expect(slashMatches('make a swap')).toEqual([]);
  });
});
