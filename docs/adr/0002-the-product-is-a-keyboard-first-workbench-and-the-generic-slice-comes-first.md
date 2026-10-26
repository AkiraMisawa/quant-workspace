# ADR-0002: The product is a keyboard-first workbench, and the generic slice comes first

Status: **Accepted** (2026-10-26, owner's decision).

## Context

The handoff already put capability and UI composition first (C01, P1 before P2). The owner's update of
2026-10-26 sharpened it: the project is not an XVA pricer. It is Quant Workbench, an AI-native,
keyboard-first, composable front-office platform inspired by Bloomberg Terminal workflows. XVA, Rates,
IM/SIMM and Position Management are domain applications on it.

The same update contained two orderings. Its section 10 described a first slice of XVA trade editing
(main CSA propagation, a trade override, B/A, a pricing snapshot). Its closing "implementation priority
correction" said not to start with XVA trade editing, and to start with a generic workbench slice. The
correction is the later and more specific instruction, and it agrees with the handoff's P1.

## Decision

**CONFIRMED:**

1. Product objectives: a unified keyboard command system; composable capabilities with typed,
   semantically validated contracts; composable workflows (sequential, parallel, conditional,
   intermediate artifacts, context rebinding); views independent of workflows; AI through AG-UI that
   proposes validated workflow and view definitions and never owns calculation or bypasses validation;
   fixed workspaces (XVA, Rates first) as clients of the same platform; context-aware actions and reuse
   without saving; history and provenance by Run ID.
2. A capability may be implemented by a local function, an internal service, an external API, an
   MQ-backed analytic, a worker process or an MCP integration.
3. **Primary acceptance criterion:** a developer adds a new capability and exposes it through commands,
   AI interaction, user-defined workflows and dynamically composed views without modifying the XVA
   pricer and without a bespoke page for that capability.
4. **Slice order:** first the generic workbench slice of
   [slice 01](../plans/slice-01-generic-workbench.md), with at least one non-XVA capability; then the
   XVA workspace slice that section 10 of the update describes; then Rates live pricing, the AI
   workspace and execution history expansion.

Position Management is named as a domain application. Nothing about it is specified yet, and no
behaviour is to be invented for it.

## Consequences

- `docs/12-implementation-plan.md` keeps its phases; P1 is now slice 01, and P2 starts with the XVA
  workspace slice.
- Every slice is judged also against criterion 3: a capability added in a later slice must not need a
  kernel or fixed-workspace edit.
