# ADR-0001: The client is React, and its grids are ExGrid's React adapter

Status: **Accepted in part** (2026-10-26). The choice of React is confirmed by the owner. The
consequences listed under "Open" are not decided yet and are tracked in this ADR until they are.

## Context

The handoff of 2026-10-24 recorded C03: "Initial client is Blazor with MudBlazor and the ExGrid
family. React comparison is deferred." On 2026-10-26 the owner wrote that the client is to be
implemented in React, that data grids use the in-development ExGrid, and that the client must work
on mobile, tablet, laptop and desktop. The handoff's precedence rule makes the latest explicit owner
instruction win.

What ExGrid offers React, observed at `AkiraMisawa/ex-grid` `main` 79c0c70:

- `@ex-grid/react` (`src/npm/react`) exports `ExGrid`, `templateColumn` and the `ExGridApi` handle
  over `@ex-grid/core`, the TypeScript Script Core (ex-grid ADR-0350, ADR-0370).
- ExSheet, ExPivot, ExChart and ExBoard have React adapters in the same workspace
  (`sheet-react`, `pivot-react`, `chart-react`, `board-react`).
- `@ex-grid/mui`, the MUI counterpart of `ExGrid.MudBlazor`, is planned for ex-grid 0.3.0
  (ex-grid ADR-0350 section 11) and does not exist yet.
- None of the `@ex-grid/*` packages is published to the npm registry.
- Touch and responsive behaviour is decided in ex-grid (ADR-0045, ADR-0260 to ADR-0263) but its
  Definition of Done says "not implemented", and the TypeScript core has no pointer-type handling.

## Decision

**CONFIRMED:** The first client is a React application. Its data grids are the actual ExGrid family
through its React adapters; a homemade table is never presented as ExGrid. Mobile, tablet, laptop and
desktop are target form factors, still subject to C04 and UX02: mobile is accepted only after
verification on the target devices.

This supersedes C03. Text in the handoff that names Blazor, MudBlazor, Razor pages or Blazor
hosting modes is superseded where it names the client technology; the behaviour it describes
(input authority, state separation, keyboard/touch/theme requirements, declarative views) is
unchanged. Those passages are rewritten once the open questions below are settled.

Unchanged by this ADR: the server side (.NET 10 per D01, application services, workers, storage),
the generic-versus-domain boundary, and the rule that views are validated declarative definitions
rendered by approved components, never generated code.

## Open

| ID | Question | Recommended default | Why it matters |
|---|---|---|---|
| A1 | Which component library replaces MudBlazor? | MUI, to meet `@ex-grid/mui` when it ships | Theming, density and Chrome must match the grid's |
| A2 | Where do workspace reconciliation, validation and submission building run? | Server-authoritative workspace in C#; the client sends typed commands and holds presentation state and editor buffers | One implementation of financial rules, shared by fixed UI and AI |
| A3 | How does this repository consume `@ex-grid/*` before it is published? | Pin an ex-grid commit and install packed tarballs built from it | Reproducible builds against an unpublished, moving dependency |
| A4 | Who implements ExGrid touch support needed for UX02? | ex-grid's own work; this project reports what it needs | Mobile acceptance depends on it |
| A5 | Does C18 (same-process rollout in the existing host) still hold? | Yes: the React build is served as static files by the existing host behind developer routes | Deployment path and the .NET 8/10 compatibility gate |
