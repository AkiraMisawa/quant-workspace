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

## 6. OPEN: how the user's changes are stored

The owner wants the server to store what the user changed. Still to decide: whether changes are saved as
each edit is made (a draft that survives closing the tab) or only with a Run at Price, and whether the
record is the net change per field (from, to, origin, user, time) or the full ordered history including
undone edits. Recommended: saved per edit with optimistic concurrency; net change per field with user
and time; the Run keeps an immutable copy of the changes it priced.
