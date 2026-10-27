# ADR-0007: The AI pane speaks AG-UI to a replaceable agent, which acts only through commands

Status: PROPOSED 2026-10-27. Raised by the owner: "add the AI interface" (2026-10-27). Builds on D10, C34, ADR-0005 and
ADR-0006 §2 and §7.

## Context

ADR-0005 gave the agent something to call: command capabilities at `POST /api/agent/commands/{id}/{version}`, for a
caller the server names, with origin `ai`. ADR-0006 gave the user a way in: `ai <request>` on the command line, and an
AI chat whose `/` opens its own commands. Nothing joins them: there is no conversation, no transport and no agent.

The company agent is GitHub Copilot with several selectable models (C34), and how the server reaches it is not known
yet. D10 prefers AG-UI as the UI-agent transport and keeps the agent SDK, model and authentication replaceable. docs/09
asks for conversation identity apart from workspaces and Runs, reconnect without resubmitting, tool calls with
idempotency keys through the application executor, and a scripted transcript labelled as such.

## Decision

1. **AG-UI is the wire between the pane and the server.** The pane posts AG-UI's `RunAgentInput` to
   `POST /api/agent/runs` and reads AG-UI events (`RUN_STARTED`, `TEXT_MESSAGE_*`, `TOOL_CALL_*`,
   `TOOL_CALL_RESULT`, `RUN_FINISHED`, `RUN_ERROR`) as server-sent events, in the shapes of `@ag-ui/core` 1.0.3
   (protocol 1.0). AG-UI's `threadId` is the ConversationId; its `runId` is one request of the user's and is not a
   workbench Run: an agent run creates no Run unless a tool it calls submits one.
2. **The server owns the conversation.** A thread belongs to the user who started it. The server keeps its messages,
   and takes from each `RunAgentInput` only the user messages it has not seen; history, tool results and identity sent
   by the client are not believed. Posting a `runId` the thread already has answers that run's events again instead of
   running it twice. While a run is going, another on the same thread is refused.
3. **Tools are the server's, and they are the commands.** The agent is offered one tool per registered command
   capability (ADR-0005), whose parameters are the capability's input contracts; nothing else. Tools the client offers
   (AG-UI's frontend tools) are refused, so a page cannot widen what the agent may do (AI06). A tool call is invoked
   through `CommandService` for `Caller(user, ai)` with the idempotency key `agent:{threadId}:{toolCallId}`, so a
   repeated call answers its first result. Its result is the command's outputs or its refusal with every issue; a
   refusal goes back to the agent, which asks the user (ADR-0005 §4).
4. **A run outlives its connection.** It runs on the server and records its events in order; the connection only
   reads them. A reader that lost the stream reads the rest from where it stopped
   (`GET /api/agent/threads/{threadId}/runs/{runId}/events?after=n`), and the thread's messages can be read whole
   (AI04). Cancelling stops the run between tool calls; a command already invoked is not interrupted, since its effect
   may already be in place. Threads are held in memory for now and a restart loses them; that is recorded as a limit.
5. **The agent is a port.** `IAgentRuntime` takes the conversation, the tools and the context, and answers text and
   tool calls; the loop that invokes the tools, records events and decides when a run ends is the platform's. The
   runtime is chosen by configuration (`Agent:Runtime`); an unknown or unavailable one stops startup or fails the run,
   and never falls back to another (CLAUDE.md: never silently fall back from a real provider to a mock).
   **The agent runs under its own identity, which can do nothing in the workbench (C44).** The Copilot SDK runtime
   runs under the agent's service account, apart from the quants' service account and its write access; outside the
   web process, so the two identities never share one. That account reaches the model and nothing else. The runtime
   proposes tool calls; the loop invokes them in the workbench for the signed-in user (§3), so what the agent can
   change is what the user may change through a registered command, never what either service account may write.
6. **Until the Copilot adapter exists, the runtime is a scripted agent, and it says so.** It has no model. Modules
   register the requests it understands (the Rates module: create an IRS line from the terms written in the request,
   start and stop ticking), it fills only the terms the request states, and a refusal's missing terms become its
   question. The pane names it `scripted · no model` in its header and on every card. It is a stand-in for the UI and the gateway, not
   evidence that an agent works (docs/11 AI01 stays open).
7. **The pane is beside the panels, and never takes the keyboard from them.** It is the right-hand pane (a drawer on
   a phone). One card per request shows the request, each tool call with its state and result, and the reply. A panel
   the AI opens is added behind the active one with a link on the card; the AI never activates a panel or moves
   focus. `ai <request>` on the command line opens the pane and sends the request with the current context; the
   chat's `/` opens its own commands (`/new`, `/stop`, `/help`).
8. **The context is a description, not authority.** The pane sends the active panel (for a Rates workspace, its id
   and valuation date) as AG-UI `context`. The agent may use it as the default target (ADR-0006 §5); the command still
   checks that the caller owns the target.

## Consequences

- A new project `Workbench.Agent` holds the AG-UI events, threads, the loop, the tool gateway, the runtime port and the
  scripted runtime. It knows no domain; the Rates module registers its scripted requests.
- The Copilot adapter is one more `IAgentRuntime`, configured in the composition root, talking to a separate agent
  process built on the Copilot SDK (C44). That process authenticates as a GitHub App installation with the Copilot
  Requests permission only and runs beside the web server on its machine (C45).
- docs/11 AI04 (reconnect) and AI06 (no unregistered path) gain an application part; AI01 needs a real runtime.

## Rejected

- **The pane calling `/api/agent/commands` itself.** The agent would then be in the browser, holding whatever
  credentials the model needs, and the conversation would be the client's to rewrite.
- **AG-UI frontend tools that drive the page.** Page automation, against CLAUDE.md, and a way for a page to grant the
  agent powers the server has not registered.
- **A Run per agent request.** A conversation is not a business submission (docs/05); commands already record what
  they did where it happened (ADR-0005 §2).
