# ADR-0003: Client state, and where the effective pricing input is resolved

Status: **Accepted** (2026-10-26, owner's decisions; section 4 clarified and section 5 resolved the same day).

## 1. Four kinds of client state (CONFIRMED)

| State | Examples | Owner |
|---|---|---|
| Server state | Counterparty registrations and search results, agreements and CSAs, CDS data, baseline snapshots, existing trades, historical runs and result datasets | TanStack Query, keyed by immutable references |
| Workspace state | Value date, counterparty registration, master agreement, active baseline reference, configuration, main CSA, trade-level overrides, B/A membership, user-defined CSAs and scenarios, draft-trade references | Workspace-scoped Zustand stores |
| Local UI state | Grid selection and focus, open dialogs and panels, pending cell edits, column and presentation preferences | The component or the grid adapter |
| Execution state | Submitted immutable inputs, Run IDs, progress, results, artifact references | Independent of the editable workspace |

There is no single application-wide store. A query refetch never replaces a pinned baseline snapshot.

## 2. Domain logic is pure TypeScript, not Zustand code (CONFIRMED)

Resolving a trade's effective CSA and discount curve, applying overrides, resolving B/A membership,
validating portfolio composition and preparing the frozen pricing input are pure functions, tested with
Vitest. Stores call them; they do not import stores.

Context loading (value date, counterparty registration, main netting set) is a dedicated coordinator,
not a chain of React effects. It distinguishes the requested context from the active prepared context,
discards obsolete responses, never mixes contexts, and blocks pricing while data is unresolved.
TanStack Query fetches and caches; business orchestration stays outside it.

## 3. ExGrid is presentation, behind an adapter (CONFIRMED)

ExGrid renders, virtualises, navigates, selects, edits, copies and pastes. It decides nothing about CSA
applicability, curve following, B/A meaning, reloads, overrides or input authority. An explicit adapter
maps stable row and column IDs to the grid, turns the grid's edit and paste intents into workspace
commands, and feeds partial updates without recreating unrelated row objects.

## 4. Large portfolios (CONFIRMED, owner 2026-10-26)

Counterparties may have tens of thousands of existing trades. The owner clarified that the requirement
is that the workspace stays responsive at that size ("it must not become heavy"), not that the original
data stays out of the browser.

Decision:

- The client downloads the original (immutable baseline) data of the workspace and keeps the user's
  changes separately. The grid shows the original with the changes applied, resolved by the pure
  TypeScript rules of section 2, for every row.
- Rendering is virtualised by ExGrid; no row is rendered unless it is in view. Heavy per-trade content
  that no rule reads may be fetched on demand, but every field a rule can change or follow is held for
  every row.
- Price freezes the effective values of every row and submits them. The backend validates them
  (eligibility of each CSA for its agreement, existence of each curve, the `nocsa_{ma id}` sentinel,
  membership consistency) and translates them; it does not re-derive them with its own copy of the
  following rules, so the rules have one implementation.
- Changes are kept as exceptions over the original (B/A as a default membership plus exceptions; field
  overrides by row ID), so a bulk change does not rewrite thousands of row objects.
- Responsiveness is measured, not assumed: benchmarks at 10,000 and 50,000 rows measure download size
  and time, memory, Zustand subscriptions, domain projection, ExGrid updates and React rendering
  separately, for a single edit, a CSA curve change reaching many rows, a bulk B/A change, sort and
  filter, a large paste and repeated pricing. No per-cell atom or reactive graph is added without such
  a measurement.

## 5. Resolved: who materialises the effective input

*(Open earlier on 2026-10-26, between rules in two languages, the TypeScript rules run on the server, and
the browser materialising every row. Resolved the same day by section 4: the browser holds every row's
rule-relevant fields, so it materialises every effective value and the server only validates.)*

Example that raised it (owner's rule, `docs/03-xva-domain.md` §2.4): 8,000 of 30,000 existing trades
reference CSA B and the user changes CSA B's discount curve. Every trade on B whose curve still follows
its CSA takes the new curve; the client computes that for all 8,000 rows, and the submission carries
them.
