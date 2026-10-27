# ADR-0003: Client state, and where the effective pricing input is resolved

Status: **Accepted** (2026-10-26, owner's decisions; sections 2 and 4 rewritten and section 5 resolved the same day). Section 6 is open.

## 1. Four kinds of client state (CONFIRMED)

| State | Examples | Owner |
|---|---|---|
| Server state | Counterparty registrations and search results, agreements and CSAs, CDS data, baseline snapshots, existing trades, historical runs and result datasets | TanStack Query, keyed by immutable references |
| Workspace state | Value date, counterparty registration, master agreement, active baseline reference, configuration, main CSA, trade-level overrides, B/A membership, user-defined CSAs and scenarios, draft-trade references | Authoritative on the server (section 2); the client's workspace-scoped Zustand store holds the selection, the workspace revision it shows and pending commands |
| Local UI state | Grid selection and focus, open dialogs and panels, pending cell edits, column and presentation preferences | The component or the grid adapter |
| Execution state | Submitted immutable inputs, Run IDs, progress, results, artifact references | Independent of the editable workspace |

There is no single application-wide store. A query refetch never replaces a pinned baseline snapshot.

## 2. The following rules live on the server, once (CONFIRMED, owner 2026-10-26)

*(Rewritten the same day. The update first asked for pure TypeScript functions in the client; the owner
then chose to keep client-side conversion minimal and let the server build the analytics slice from the
original and the user's changes. TypeScript copies are added only if measured edit latency requires it.)*

- The client sends the user's changes as typed commands (set a CSA's discount curve, set a trade's CSA
  or curve, change B/A membership, add a user-defined CSA) and does not compute following itself.
- The server holds the workspace: the pinned original (baseline revision), the user's changes, and the
  effective values the following rules produce from them. It records every change in its database.
- The grid shows the server's effective values through a paged `GridSource`; after a change, the server
  answers with the new workspace revision and the grid reads the rows in view again.
- Price freezes one workspace revision on the server: the original's reference, the changes and the
  effective slice, which goes to analytics. What the grid showed and what is priced come from the same
  code. The server never refetches the original or reapplies newer defaults at Price.
- **If edits feel slow:** add a TypeScript copy of the rules that shows the effect at once and is
  replaced by the server's answer; the server stays the authority, a disagreement is detected and
  reported, and JSON cases shared by both implementations hold them together. This is added only after
  measuring edit latency (decided with the owner, 2026-10-26).

The context coordinator (value date, counterparty registration, main netting set) distinguishes the
requested from the prepared context, discards obsolete responses, never mixes contexts, and blocks
pricing while data is unresolved. TanStack Query fetches and caches; business orchestration stays
outside it.

## 3. ExGrid is presentation, behind an adapter (CONFIRMED)

ExGrid renders, virtualises, navigates, selects, edits, copies and pastes. It decides nothing about CSA
applicability, curve following, B/A meaning, reloads, overrides or input authority. An explicit adapter
maps stable row and column IDs to the grid, turns the grid's edit and paste intents into workspace
commands, and feeds partial updates without recreating unrelated row objects.

## 4. Large portfolios (CONFIRMED, owner 2026-10-26)

Counterparties may have tens of thousands of existing trades. The requirement is that the workspace
stays responsive at that size. With section 2, the browser holds the rows in view, not the portfolio:
the server pages effective rows to the grid, ExGrid renders only what is visible, and a change that
reaches thousands of rows (a CSA's curve) is one command, not thousands of client updates. Changes are
stored as exceptions over the original (B/A as a default membership plus exceptions; field overrides by
row ID).

Measured before any target is agreed, at 10,000 and 50,000 rows: edit round-trip latency, a CSA curve
change reaching many rows, bulk B/A, sort and filter, a large paste, repeated pricing, server memory per
workspace, and browser memory.

## 5. Resolved: who materialises the effective input

*(Open on 2026-10-26 between rules in two languages, TypeScript rules run on the server, and the browser
materialising every row. Resolved the same day by section 2: the server materialises it with the one
implementation of the rules.)*

Example that raised it (owner's rule, `docs/03-xva-domain.md` §2.4): 8,000 of 30,000 existing trades
reference CSA B and the user changes CSA B's discount curve. Every trade on B whose curve still follows its
CSA takes the new curve; the server computes it, the grid shows it, and the frozen slice carries it.

## 6. Resolved: how the user's changes are stored

*(Open on 2026-10-26. Decided with the owner on 2026-10-27.)*

- **Saved per edit, frozen at Price.** Every edit is appended to the workspace's edit log in the
  database as it is made, so the current state is on the server before any Run exists and survives a
  closed tab. Price freezes one revision: the Run records the log position it froze, keeps an immutable
  copy of the changes it priced, and stores the materialised accepted input it sent. The log is
  append-only; an undo is a new entry.
- **The log explains, the frozen input reproduces.** Reproducing a Run reads its frozen accepted input
  and native request artifacts, never a replay of the log.
- **A counterparty change drops the previous counterparty's edits, after confirmation.** The user is
  told how many edits of each kind will no longer apply and confirms. The dropped edits stay in the log
  and in any Run that priced them; bringing them back is an explicit re-apply from a Run or from the log,
  checked as in section 7. Nothing comes back by itself.
- **A workspace is made for AI- and workflow-started analyses too.** It holds the plan's contexts, the
  answers to the AI's choices and the bindings between steps, under the same log and the same freeze.
- **A trade acquired for another value date is another version.** An existing trade keeps its identity
  (its source trade ID within the counterparty registration, legal entity included); each acquisition for
  a value date is a new version of it, as a re-acquired draft trade is a new TradeInput version.
- **A user may hold several workspaces at once, and a workspace may be deleted.** A Run never depends on
  its workspace still existing: it carries its frozen input and the copy of the changes it priced.

## 7. OPEN (proposed 2026-10-27): what an edit applies to, and how it carries to new versions

An edit is kept only while it still means what the user meant. Where that cannot be told, the platform
asks rather than guessing either way.

**What an edit records.** Its target (an identity and a field path), its new value, and its base: the
source value it overrode and the version that value came from. Editing the same field again replaces the
value and moves the base to the version current at that edit.

**Scope.** Each kind of edit belongs to one scope, which decides when it stops applying. Scopes are the
domain module's, not the kernel's: a module registers its edit kinds, the scope key of each, and its
conflict groups, as it registers capabilities and history projections. The kernel stores every edit in
the same tables with an opaque scope key and target path, and runs the same check below; it never knows
what a counterparty or a fixing is. Rates, for example, would scope a curve edit to its currency and
market snapshot, with no counterparty at all. XVA's kinds:

| Edit | Examples | Scope |
|---|---|---|
| Trade content | A new trade's economic fields | That trade (its TradeInstanceId) |
| Membership | Before/After on a trade | That trade within the counterparty registration |
| Assignment | A row's CSA, netting set or discount curve | The counterparty registration |
| CSA definition | A CSA's curve or terms; a user-created CSA | That CSA, within its agreement |
| Credit configuration | Recovery, proxy mapping | The counterparty registration |

**New versions of the same scope (a value date change, an explicit refresh).** Each live edit is
checked against the new version:

1. **Target gone** (a matured trade, a CSA or curve that no longer exists): the edit stops applying and
   is listed as having no target. It is never moved onto something else.
2. **Base unchanged** (the new version's source value equals the edit's base): the edit carries over
   and stays user-owned.
3. **Base changed** (a fixing landed on a field the user had overridden): a conflict. The row shows the
   old source value, the new source value and the user's value. The user keeps the edit (its base moves
   to the new version) or takes the source. The choice is a log entry. An AI-started workspace returns
   it as a choice; it is never resolved automatically.
4. **Carried but no longer valid** in the new context (an ineligible CSA, a curve unavailable on the new
   date): a validation issue, as today.

Fields still automatic follow the new version and their dependencies, with nothing to resolve. After the
check the workspace says what happened: how many edits carried, conflict, or lost their target.

**Price** is refused while a trade it would price has an unresolved conflict or a lost-target edit not
acknowledged. Rows outside Before and After do not block it.

**Fields compared together.** A field-by-field comparison misses edits that depend on a neighbour: an
overridden fixed rate means something else once the source changes the schedule. The domain module
declares conflict groups, sets of fields compared as one; a source change to any field of a group
conflicts with an override anywhere in it. Without a declaration a group is the field alone.

**Still to settle:** the conflict groups for each product; whether a user may override a fixing at all;
and how long a workspace is kept before it may be deleted.
