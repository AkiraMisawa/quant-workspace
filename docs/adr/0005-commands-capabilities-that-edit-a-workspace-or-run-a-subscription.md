# ADR-0005: Command capabilities: editing a workspace and running a subscription, outside Runs, as the caller

Status: ACCEPTED 2026-10-27 by the owner. Raised by the Rates line pricer: "ask the AI to make a
10-year swap and start ticking" (owner, 2026-10-27). Widens docs/09's initial scope; changes nothing a Run does.

## Context

Every capability today runs as a workflow step inside a Run, and declares only `ReadData`, `Compute` or
`PersistAnalysis` (docs/09: "initial scope is read-only analytics"). A handler is handed its inputs and the data it
may read, and nothing about who asked.

The line pricer's fixed page does three other things through its own HTTP endpoints: it creates lines and edits
their terms (ADR-0003, ADR-0004), and it starts and stops the workspace's live subscription (docs/07). CLAUDE.md
requires the AI to call application capabilities, never page navigation or grid automation, so an AI asked to
"make a 10-year swap and start ticking" has nothing to call.

Three things stop these from being ordinary capabilities:

1. **They are not business submissions.** One RunId identifies one submission (docs/05). A Run for every line
   added or every live start would fill the Pricing DB with things nobody priced, and a live start is the opposite
   of a Run: frames, no Run per tick.
2. **They act for someone.** An edit is logged under a user, and only the owner may edit a workspace (C31). A
   handler that took the user from its inputs would let the agent choose whom it acts as; docs/09 says
   agent-supplied identity is ignored.
3. **They change state.** A workflow replays its steps' inputs; a step that edits a workspace would edit it again
   on a retry, and a Run would stop being a pure function of its accepted input.

## Decision

1. **Two new effects.** `EditWorkspace`: the capability changes a workspace of its caller's through the same
   application service and edit log as the page (so the carry check, revisions and refusals are the same).
   `Subscribe`: it starts, joins or stops a subscription (docs/07), which runs and stops by its own rules
   (owner, 2026-10-27: stopped once unwatched for the idle limit).
2. **Commands are invoked outside Runs.** A capability with either effect is a *command*. The validator refuses a
   command as a workflow step, so a Run keeps computing and persisting only. Commands are invoked through a
   command gateway: the same descriptor, input validation against its contracts, and an idempotency key, and no
   Run. What a command did is recorded where it happened: an edit in the workspace's log, a live start or stop in
   the workspace's live status.
3. **The caller is the server's.** The gateway hands the handler a `Caller` (the authenticated user and the origin:
   `pricer`, `ai`, `workflow`, `action`), never taken from inputs. An edit's log entry records its origin, so the
   log says which edits the AI made on the user's behalf.
4. **No invented terms.** A command that creates something checks its required inputs and refuses with every
   missing one by name ("currency", "notional", "direction"), and the AI asks the user. It does not fill a default
   the user did not choose; the only defaults are those the user's own data already defines (ADR-0003: a new
   trade's main CSA), or that a confirmed decision names (C40: an empty quote is solved at par).
5. **The page and the AI share the service.** The fixed page's endpoints and the commands call the same
   application methods, so neither can do what the other cannot.

The Rates commands are `rates.line.create` (one line from its terms, in a workspace of the caller's, created if
none is named, with the valuation date given), `rates.live.start` and `rates.live.stop`. Pricing once stays
`rates.price`, a Run, reached through a workflow over the frozen accepted input, as now.

## Consequences

- docs/09's "initial scope is read-only analytics" becomes "read-only analytics, and editing the caller's own
  workspaces and live subscriptions". Booking, position mutation and external quoting stay out.
- The kernel gains `CapabilityEffect.EditWorkspace` and `.Subscribe`, `Caller` on the invocation, a command
  gateway, an origin on edit log entries (one nullable column in `EditLog`), and a validator rule. XVA's
  capabilities are unchanged; XVA may later expose its own edits the same way.
- An AI-started live subscription that nobody watches stops after the idle limit, as a page's does.

## Rejected

- **A Run for every command.** Fills the history with non-submissions and breaks "no Run per tick".
- **The AI driving the page's HTTP endpoints.** Page orchestration, against CLAUDE.md; and no catalog describes them.
- **One capability for live and one-off pricing.** Their results differ in kind: a Run with a result against a
  subscription id and a stream of frames. They already share the provider and the input shape, which is what makes
  their figures agree (docs/07 acceptance 4).
