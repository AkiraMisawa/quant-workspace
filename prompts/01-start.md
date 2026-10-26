Read `CLAUDE.md`, `START_HERE.md`, `docs/00-decisions.md`, `docs/01-product-scope.md`, `docs/02-composition-architecture.md`, and `docs/12-implementation-plan.md` before implementing.

Build this application incrementally. The primary objective is independent Capability / Workflow / View / Context composition. Fixed XVA and Rates pages are the first domain applications. Blazor + MudBlazor + the actual ExGrid family is the initial client choice.

First inspect the repository, SDKs, dependency feeds, existing instructions and the pinned ExGrid API. If this is an empty repository, create an appropriate .NET10 solution after recording the relevant choices. If source already exists, preserve unrelated work and integrate with its conventions.

Complete P0 and the first runnable P1 slice. Prove a workflow with hidden typed intermediate data, a dynamic ExGrid result view, a newly attached intermediate view, a registered row action, and unsaved context rebinding. Keep the generic kernel free of quant/company types. Continue into a narrow XVA input-to-history path once this works; do not stop after producing another plan or static mockup.

Use the documents marked DEFAULT as implementable initial choices. Record important substitutions in a short ADR. Ask only when an unresolved material financial or operational decision blocks the current slice; finish other useful work first.

Before implementing fixed XVA, read `docs/03-xva-domain.md` and `docs/04-client-state.md`. Preserve the latest rule: Price commits and validates the complete typed UI input, then freezes it. The server must not reapply defaults or fetch new data that changes that input.

Use deterministic providers initially, clearly marked as simulated. The Rates real-engine milestone and real-AI milestone require actual integration, not renamed mocks. Keep native libraries out of the Web process and all company SDKs out of the standalone core build.

Create `IMPLEMENTATION_STATUS.md` containing actual run/build/test commands, completed acceptance IDs, real versus mocked providers, observed limitations and the next slice. Report working behavior with evidence. Do not publish, deploy or claim production readiness merely because the handoff describes a future rollout.
