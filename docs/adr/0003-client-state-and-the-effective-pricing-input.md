# ADR-0003: Client state, and where the effective pricing input is resolved

Status: **Accepted in part** (2026-10-26). Sections 1–4 are the owner's decisions. Section 5 is an open
question that gates the XVA workspace slice, not slice 01.

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

## 4. Large portfolios (CONFIRMED requirement, DEFAULT design)

Counterparties may have tens of thousands of existing trades. Pricing must not require every trade to be
loaded or rendered in the browser.

DEFAULT: existing trades stay in an immutable server dataset revision and reach the grid through a
paged `GridSource`; the workspace holds rules and exceptions (main CSA, per-row overrides, B/A as a
default membership plus exceptions), not a materialised copy of every row. Benchmarks at 10,000–50,000
rows measure Zustand subscriptions, domain projection, ExGrid updates, React rendering and memory
separately before any per-cell atom or reactive graph is considered.

## 5. OPEN: who materialises the effective input of rows the browser never loaded

Sections 2 and 4 together raise a question the update does not answer. If the browser holds rules and
exceptions over a server dataset, then the effective CSA, curve and membership of an unloaded row are
computed by the domain rules somewhere other than the browser, and the backend must validate the
submitted effective values.

| Option | What happens | Cost |
|---|---|---|
| A. Rules in both languages, held together by shared cases | The TypeScript rules drive the UI; a C# implementation materialises and validates on the server; JSON cases run against both (ex-grid's own technique for its C# and TypeScript cores) | Two implementations of financial rules |
| B. One TypeScript rules package, also run on the server | The same package runs in a Node process the backend calls to materialise and validate | A Node runtime beside ASP.NET Core |
| C. The browser materialises every row before Price | Exact "what the grid shows" semantics | Contradicts section 4 for large portfolios |

Recommendation: A for financial rules that must be validated server-side, because the backend is
ASP.NET Core and the engines' adapters are C#; the frozen submission records the rules version that
produced it. Decision required before the XVA workspace slice.
