# Blazor client state, input transactions, and large grids

The initial client is **Blazor + MudBlazor + ExGrid**. The initial framework baseline is the package's proposed .NET version; resolve and pin actual package versions during implementation. ExGrid is the actual grid dependency, not a plain HTML facsimile claimed to be ExGrid. The existing production application's large chained Fluxor effects are a migration source, not a pattern to reproduce.

**CONFIRMED** marks user requirements. **DEFAULT PROPOSAL** marks implementation choices that can be changed without changing the requirements.

## 1. Separate four kinds of state

| State | Contents | Lifetime and owner |
|---|---|---|
| Source snapshots/cache | Search results, available source records, immutable baseline snapshots, source trade inputs | A query/cache abstraction; keyed by complete context and version. A refresh does not itself overwrite an edited workspace. |
| Workspace domain state | Selected context, accepted effective rows/configuration, field ownership, validation, current revision | A workspace-scoped store and application coordinator. This is the input model represented by the fixed UI. |
| Presentation state | Focus, active cell editor, viewport, sort/filter, selection, open panels | Component/grid state scoped to the correct workspace/view. It does not determine the full pricing universe. |
| Execution/read state | Submitted Run references, progress, results, history navigation | Execution/result client services; authoritative Run state is server-owned. |

The same browser tab can own an editable workspace and view several completed Runs. `UiSessionId` is diagnostic and ephemeral. `WorkspaceId` identifies reusable work; `RunId` identifies one accepted business submission. A UI refresh may resume a workspace by an authorized ID, but must not be necessary for creating another Run.

The state store is not a global mutable singleton containing every user's workspace. In Blazor WASM scope it to a workspace and route. In a server-interactive deployment, verify DI/circuit lifetimes and isolate user workspaces explicitly.

## 2. Suggested responsibilities

These are conceptual names, not a requirement to create one class for every line.

| Component | Responsibility |
|---|---|
| `XvaWorkspaceStore` | Hold immutable/revisioned workspace state, apply state changes, notify views. |
| `WorkspaceCoordinator` | Execute context-change and submit transactions, invoke data services, serialize conflicting operations. |
| `ContextPreparationService` | Resolve and load a target context without knowledge of grid controls or Fluxor. |
| `WorkspaceReconciler` | Pure transformation of current state + new data + explicit policies into next state and validation issues. |
| `WorkspaceValidator` | Pure structural/domain checks plus a boundary for provider-dependent validation. |
| `SubmissionBuilder` | Create a canonical immutable submission from an accepted workspace revision. No network access or default mutation. |
| `IGridEditSession` or equivalent | Commit/cancel active editors and expose validation completion without leaking grid internals to the domain. |
| `RunClient` | Submit with idempotency, monitor status, retrieve results and diagnostic links. |

Use C# records or other immutable state structures where practical. For a large grid, update rows by stable keys and preserve unchanged row references. Do not deep-clone a 10,000-row portfolio for every keypress merely to imitate an immutable pattern.

Fluxor can be retained as a small store/notification mechanism if useful. The business transition logic and preparation service must be callable and testable without dispatching a cascade of framework actions. A hand-written store is also acceptable. This handoff does not mandate a new state-management library.

## 3. Reusable context preparation

One preparation entry point can support date, counterparty, and main-netting-set changes if its input is an explicit **target context** and it resolves dependencies deliberately.

Conceptual signature:

```csharp
Task<PreparedContext> PrepareAsync(
    ContextSelection target,
    PreparationOptions options,
    CancellationToken cancellationToken);
```

`ContextSelection` contains value date, counterparty registration, and the explicitly selected or unresolved agreement/netting-set IDs. `PreparedContext` contains versioned source data, resolved choices, validation issues, and unresolved choices when a decision is required. It does not mutate UI state internally.

The coordinator may optimize downloads by comparing dependency keys. Correctness must not depend on which UI event called it:

1. Resolve the registration and available agreement/netting-set candidates.
2. Preserve a still-valid explicit choice, apply an authorized documented default, or return a choice request.
3. Download source data whose prerequisites are now known. Independent credit and portfolio/reference downloads can run concurrently.
4. Return one coherent prepared result with the exact context key and source versions.
5. Reconcile and adopt it only if the initiating context revision is still current.

Resolve the applicable baseline/default configuration profile during preparation as well, retaining its source and version in the prepared context. This includes the configurable default uncollateralized discount curve, such as `jpy_ois`. Apply resolved defaults to automatically bound fields before presenting the ready state. A later configuration change must not replace a user-visible or manually edited value during Price.

Do not expose `PrepareAsync` as a replay of 'set date, dispatch counterparty changed, dispatch netting set changed'. Fixed pages and AI/workflows can prepare the same target directly.

## 4. Prevent stale loads and partial context mixing

Use a monotonically increasing workspace/context generation, even when requests also support cancellation. Cancellation alone cannot guarantee a remote response will not arrive.

For a context change:

1. Increment `ContextRevision` and create a request token containing the complete target context key.
2. Mark affected sections as preparing and make the target context visible.
3. Cancel obsolete work where possible.
4. Load into staging data, never into live rows one response at a time.
5. Compare the returned token to the active generation.
6. Discard stale responses.
7. Atomically reconcile and adopt the coherent prepared context, then validate.

Existing accepted data can remain visible while loading, but it must be clearly identified as the previous context and unavailable for a new Price until the target is resolved. Never display a new counterparty label with the old counterparty's trades as if they were current.

Search queries and context preparation use separate generations. A delayed autocomplete result must not roll back an accepted registration selection.

**DEFAULT PROPOSAL:** During context preparation, disable context-dependent cell editing while allowing safe presentation operations. This simplifies reconciliation. Other designs may retain concurrent edits through a command journal, but must preserve the same invariants.

## 5. Field ownership and automatic following

Each editable field that can follow a default needs enough metadata to distinguish value from ownership. A useful conceptual representation is:

```text
EffectiveValue
Origin: Source | Automatic | User
DependencyKey and source/context revision, when automatic
Last user edit revision, when user-owned
Validation issues
```

This is explanatory notation, not a required serialized DTO or generic wrapper around every scalar.

Example for a new trade:

1. Import assigns main CSA with `Origin = Automatic`.
2. Its discount curve follows that CSA with `Origin = Automatic`.
3. User selects another CSA on the row: CSA becomes user-owned; its still-automatic curve follows the selected CSA.
4. User edits the curve: curve becomes user-owned.
5. Price submits the two final values shown by the model.

**DEFAULT PROPOSAL:** Subsequent main changes only change automatically owned fields. An explicit 'Reset to automatic' removes a user override and recalculates against current dependencies. The user has confirmed that edits after automatic following win; persistence of those edits across later main changes remains a policy proposal.

Avoid circular updates such as 'CSA changed updates curve, curve changed resets CSA'. Reconciliation is a pure dependency evaluation with an explicit order and one resulting state transition.

The same reconciler must preserve the distinction between an explicitly uncollateralized context/row and an unresolved CSA choice. **CONFIRMED:** an uncollateralized trade's CSA property contains the sentinel `nocsa_{ma id}`, for example `nocsa_MA-001`, rather than null. Validate its agreement binding without requiring a physical CSA record. Its discount curve initially follows the configured baseline default, and a later manual curve edit is retained in the final snapshot. In a collateralized context, a missing or ineligible real CSA remains a validation error.

**CONFIRMED:** preserve a null/empty `cvaRecoveryOverride` as absent. Analytics then uses the applicable CDS recovery from the pinned submitted data; neither the client nor server synthesizes a replacement override or requires an `effectiveCvaRecovery` field. An explicit zero remains an explicit override. The UI may explain this fallback, but must not fill the editor with a computed value and thereby turn absence into user intent.

## 6. Price is a transaction over a workspace revision

**CONFIRMED:** Clicking Price while a cell editor is active must not submit its old value.

The submission sequence is:

1. Commit all relevant active editors, including composition/IME input and pending pasted values.
2. Wait for edit commit and required validation to complete. A method returning from a grid API call is not by itself proof that asynchronous validation completed.
3. Ensure context preparation and automatic following for the current revision have completed.
4. Validate the complete effective dataset and configuration.
5. Capture one immutable workspace revision containing the complete typed values or an equivalent server-pinned, fully materialized dataset version.
6. Build the submission from that revision, attach the baseline/input references and provenance, and compute/request an idempotency key for this submission attempt.
7. Send it; the server validates and accepts or returns field-specific errors.
8. Associate the returned Run with the frozen submission. Further edits create a new workspace revision and cannot alter the accepted Run.

For a straightforward initial implementation, briefly prevent editing during steps 1–6. If any revision changes during capture, restart validation/capture rather than combining two revisions.

The server may reject invalid permissions, relationships, unsupported provider settings, stale expected workspace versions, or inconsistent snapshot references. It must not 'repair' the accepted economics silently. Return actionable issues, with row/field IDs, so the workspace can show the necessary changes.

For individual-addition mode, build `candidateTradeInstanceIds` from exactly the eligible New rows whose After flag is checked in the frozen revision. Validate that the set is nonempty and unique; the server must reject an inconsistent supplied set. Sorting, filtering, or a separate stale selection control must not change this candidate set.

Double-clicking Price or retrying an HTTP request after a lost response must not accidentally create two Runs. Reuse the idempotency key for transport retries of the same frozen submission; use a new key for an intentional new business submission.

### 6.1 Server-backed large data

It is acceptable to keep very large source datasets on the server and page/virtualize their presentation. In that design, final workspace state must also exist as an authorized, immutable/revisioned server dataset before submission.

The browser may submit a verified dataset revision reference instead of retransmitting every row, provided the referenced dataset represents exactly the values and membership the UI has committed. Unsynchronized editor state or 'original data plus best-effort patches against latest' does not meet this requirement.

For the first implementation, loading the complete intended pricing input into the workspace is a reasonable proposal if measured memory and responsiveness are acceptable. Virtual rendering and server-side data virtualization are different decisions.

## 7. Grid behavior and responsiveness

### Stable identity

- Use stable trade-instance IDs for edits, errors, checkbox membership, focus restoration, and result mapping.
- Do not use row number as an ID; sort/filter changes it.
- Distinguish a trade's input version from its current workspace assignment.
- Retain typed full precision independently of display rounding and locale.

### Dataset-wide operations

- B/A select-all addresses the declared full grid dataset, not just the viewport or a fetched page.
- Display the target scope/count and tri-state header status.
- Sorting/filtering does not alter pricing membership.
- Bulk paste validates all affected cells with a clear partial/atomic policy. **DEFAULT PROPOSAL:** stage a paste, show errors, and apply valid transaction results atomically; do not silently lose invalid cells.
- Avoid revalidating every unchanged row synchronously on every keystroke. Use dependency-aware row/configuration validation, followed by complete submission validation.

### Keyboard, touch, and layout

**CONFIRMED:** An experienced user must be able to search/select context, navigate/edit grids, change B/A flags, and run pricing using a keyboard. Mouse operation remains available. Responsive layouts, light/dark appearance, and real touch interactions are required.

Route buttons, hotkeys, and command-palette entries through shared application commands. Commands enforce the same availability and validation rules. Avoid capturing shortcuts while the user is typing in an editor unless they are explicit editor commands. Document a small stable keymap before adding many shortcuts.

On narrow screens, use summary columns and a row-detail editor/drawer; preserve access to B/A, CSA, and validation. A large grid scaled down until text is unreadable is not a mobile design. Verify actual touch activation, editor focus, scrolling, and keyboard interactions on target browsers. Do not infer iPhone/Safari support merely from Blazor or a grid package's existence; establish current dependency support and test the resulting product.

## 8. State transitions to make explicit

| State | User can do | Price behavior |
|---|---|---|
| Empty/unresolved context | Search and resolve context, import reusable drafts where supported | Disabled with a precise reason. |
| Preparing context | Inspect previous data as previous data; choose/cancel pending context | Disabled until a coherent target revision is ready. |
| Ready with errors | Edit invalid inputs and inspect errors | Disabled or returns the same focused validation report. |
| Ready | Edit, inspect, submit | Commit, validate, freeze, submit. |
| Submitting | Inspect the frozen submission; avoid duplicate intent | Reuse the active submission/idempotency state. |
| Run active | Inspect progress; after freeze, continue editing a new workspace revision if supported | A later intentional submission creates another Run. |
| Run completed/failed/cancelled | Inspect results and diagnostics; edit/reprice | Never promote After to Original automatically. |

Execution status and editing status are separate state machines. A running Run does not require freezing the workspace until analytics finishes. Cancellation requested is not cancellation confirmed.

## 9. Testing seams and minimum regressions

Test behavior without mounting the whole page whenever possible:

- Pure reconciliation tests: source/default/user ownership, dependency changes, reset to automatic, invalid CSA relationships.
- Coordinator tests: out-of-order preparation completion, failure, cancellation, coherent adoption, and workspace revision changes.
- Submission tests: active editor commit, paste completion, full-dataset membership, immutable captured revision, idempotent retry.
- Small component tests: command binding, validation display, focus behavior, and header-checkbox scope.
- A small number of browser journeys: the complete keyboard workflow, touch editing, virtualized select-all, and the main-CSA-then-manual-override scenario.

Use a controllable fake data service that can complete requests in any order. Use fake providers for deterministic execution tests. Do not write hundreds of assertions against internal Fluxor action sequences or copy the implementation into test expectations.

Initial performance scenarios are proposals, not measured claims: use a 10,000-row existing portfolio for grid responsiveness and bulk B/A operations; record browser memory, interaction latency, render behavior, and submission size. Define budgets in the acceptance plan and verify them on representative hardware.

## 10. Migration boundaries

Expose the new fixed pricer behind developer authorization and a feature route in the existing application process where deployment allows it. Keep new workspace services/state separate from the production Fluxor feature. Reuse authorized backend capabilities through the new application services rather than having the new UI dispatch legacy page effects.

Migrate feature by feature. The existing pricer remains available while the new path is validated against frozen inputs and reference cases. New data models do not require immediate physical migration of every old Run folder; use the history/storage migration design for read-only legacy references.
