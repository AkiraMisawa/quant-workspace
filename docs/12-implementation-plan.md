# Implementation plan

Deliver narrow runnable slices. Persist progress in a new `IMPLEMENTATION_STATUS.md` once development starts. Do not mark unconnected mock capabilities as real model integration.

## P0 — Repository and dependency discovery

- Inspect existing instructions and code, .NET SDK, operating systems, ExGrid source/version and available package feeds.
- Verify ExGrid MudBlazor API and target-browser support with a small real component page.
- Record proposed solution boundaries and package pins. Keep core standalone; do not add native packages to Web.
- Record the same-process compatibility path now: upgrade the existing host, target compatible shared modules, or defer host integration with an explicit boundary. A .NET10-only module cannot be assumed to load into an unchanged .NET8 host.
- Select a first IRS worker approach in a time-boxed spike. Record XCCY coverage separately.
- Verify the actual agent/runtime authentication only when reaching the AI slice; it does not block generic composition.

Exit: reproducible build/run instructions, an actual ExGrid sample, and a short ADR list of choices. This package itself is not that build.

## P1 — Generic composition and view proof

- Versioned registry/contracts and bounded workflow validator/executor interface.
- Two or three nonfinancial synthetic capabilities with typed intermediate outputs.
- Form/grid/chart/details renderers and registered row actions.
- One workflow headless, with a final view, and with a new intermediate view.
- Unsaved rebind and a minimal personal Save/Pin path.

Exit: CO01-CO07; one useful composed screen without a new bespoke Razor page. Do not spend this phase creating a universal workflow engine.

## P2 — XVA from editing through history

- Source adapters, immutable baseline, TradeInput import, workspace state and coordinated preparation.
- Fixed XVA page: search, CDS/recovery, NS/CSA, trades/B/A and scenarios.
- Commit/validate/freeze submission and deterministic asynchronous analytics provider.
- Run/case/request/attempt metadata, immutable artifacts and RunId diagnostics.
- Independent Before/After; CRIF prerequisite stub; config splits; individual-addition cases and partial failures.

Exit: XV01-XV16 plus EX01-EX10 using deterministic fixtures; active-editor/manual-override and empty-recovery/nocsa acceptance are mandatory.

## P3 — Independent margin and dynamic analysis

- Expose independent CRIF generation and SIMM calculation capabilities.
- Multi-case input binding with explicit MA/CSA selection.
- Result datasets, comparison measures, dynamic result views and follow-up row actions.
- Preserve methodology/model/currency/market compatibility and partial failures.

Exit: scripted deterministic SIMM example works through the same executor with no XVA UI dependency. Scripted agent text remains a harness.

## P4 — Real Rates worker and live page

- Real IRS single/batch pricing through an out-of-process worker.
- Explicitly scoped XCCY pricing after convention/coverage discovery.
- Coherent synthetic markets, subscription manager, bounded work, input/market revisions.
- Live/manual/capture and retention; real ExGrid one-line entry.
- Provider swap tests and provisional 20-user workload measurement.

Exit: RT01-RT07 and PR01-PR04; documentation distinguishes random market data from real pricing calculations.

## P5 — Real AI integration

- One approved runtime adapter and AG-UI transport adapter with pinned versions.
- Typed tools using existing executor; material choices; reconnect/turn revisions.
- Validated workflow/view proposals; same data references and row actions as manual composition.
- One additional analysis beyond SIMM comparison to prove the platform is not hard-coded to that prompt.

Exit: AI01-AI06 with a real runtime. No company service-account support is claimed until verified.

## P6 — Company integration and parallel rollout

- Implement company data sources and engine adapters in private modules.
- Reuse existing bulk provider/HPC service, MQ semantics and supported native services.
- Compare reference portfolios with matched conventions; verify payload mapping and correlation.
- Add developer-only routes and server authorization in the existing host where compatible; preserve old pages and state.
- Index legacy folders read-only; postpone physical migration until needed.
- Run UX01-UX03 and the accepted target-browser matrix; canary rollout by entitlement/config flag.

Exit: production integration criteria met and rollback path demonstrated. Deployment requires the user's actual instruction and the company's existing release process.

## Avoid dependency traps

Composition, storage and UI can use deterministic providers while real engines are being integrated. AI is not necessary for normal saved workflow execution. Production schema, final quant conventions and runtime selection are decision gates for their own slices, not reasons to stop all useful development.

## Suggested first coding task

After P0, implement P1 plus a thin path toward P2: one synthetic portfolio dataset, one mock calculation and one result view using actual ExGrid, retaining a typed intermediate ref and creating a diagnostic Run. Do not build all menus, every field or a chat facade before this works.
