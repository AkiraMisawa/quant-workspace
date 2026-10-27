import { EventType, type BaseEvent, type Context, type RunAgentInput } from '@ag-ui/core';
import { ApiError, call } from '../platform/api';
import type { ErrorEnvelope } from '../platform/contracts';

// The AI pane's side of AG-UI (ADR-0007 §1): a run is posted as RunAgentInput and its events are read as server-sent
// events. A run belongs to the server: a stream that breaks is read again from the last event it delivered, never
// posted again, so a request is never answered twice.

export interface AgentRuntimeInfo {
  readonly name: string;
  readonly label: string;
  readonly simulated: boolean;
}

export interface AgentTransport {
  runtime(): Promise<AgentRuntimeInfo>;
  /** Posts one request and delivers every event of its run, in order, until the run finishes or fails. */
  run(input: RunAgentInput, onEvent: (event: BaseEvent) => void): Promise<void>;
  cancel(threadId: string, runId: string): Promise<void>;
}

const TERMINAL = new Set<string>([EventType.RUN_FINISHED, EventType.RUN_ERROR]);

/** How many times a broken stream is read again before the pane says the run's end was not seen. */
const RESUMES = 3;

export const httpAgentTransport: AgentTransport = {
  runtime: () => call('/api/agent/runtime'),

  async run(input, onEvent) {
    let seen = 0;
    let ended = false;
    const deliver = (event: BaseEvent) => {
      seen += 1;
      if (TERMINAL.has(event.type)) ended = true;
      onEvent(event);
    };
    await readStream(await open('/api/agent/runs', { method: 'POST', body: JSON.stringify(input) }), deliver);
    for (let attempt = 0; !ended && attempt < RESUMES; attempt++) {
      const path = `/api/agent/threads/${encodeURIComponent(input.threadId)}/runs/${encodeURIComponent(input.runId)}/events?after=${seen}`;
      try {
        await readStream(await open(path, { method: 'GET' }), deliver);
      } catch (error) {
        if (error instanceof ApiError || attempt === RESUMES - 1) throw error;
      }
    }
    if (!ended) throw new Error('The connection to the agent was lost before its run ended; it may still be running on the server.');
  },

  async cancel(threadId, runId) {
    const response = await fetch(`/api/agent/threads/${encodeURIComponent(threadId)}/runs/${encodeURIComponent(runId)}/cancel`, { method: 'POST' });
    if (!response.ok) throw await refusal(response);
  },
};

async function open(path: string, init: RequestInit): Promise<Response> {
  const response = await fetch(path, { ...init, headers: { 'content-type': 'application/json', accept: 'text/event-stream' } });
  if (!response.ok) throw await refusal(response);
  if (response.body === null) throw new Error('The agent answered without a stream.');
  return response;
}

async function refusal(response: Response): Promise<ApiError> {
  const text = await response.text();
  let envelope: ErrorEnvelope;
  try {
    envelope = JSON.parse(text) as ErrorEnvelope;
  } catch {
    envelope = { code: `Http${response.status}`, message: text || response.statusText, issues: [] };
  }
  return new ApiError(response.status, envelope);
}

/** Reads `data:` frames until the stream closes. A frame that is not an AG-UI event fails the read. */
async function readStream(response: Response, deliver: (event: BaseEvent) => void): Promise<void> {
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += value;
    let end: number;
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      const event = parseFrame(frame);
      if (event !== undefined) deliver(event);
    }
  }
}

export function parseFrame(frame: string): BaseEvent | undefined {
  const data = frame.split('\n').filter((l) => l.startsWith('data:')).map((l) => l.slice(5).replace(/^ /, '')).join('\n');
  if (data === '') return undefined;
  const event = JSON.parse(data) as BaseEvent;
  if (typeof event.type !== 'string') throw new Error(`The agent sent an event without a type: ${data}`);
  return event;
}

/** The AG-UI context entry an app gives for the panel the user is looking at (ADR-0007 §8). */
export type AgentContext = Context;
