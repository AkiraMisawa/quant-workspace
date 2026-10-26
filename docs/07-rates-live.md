# Rates pricing: manual, live, and captured analysis

## 1. Purpose and decision status

The rates pricer is one of the first fixed workflow applications built on the platform. A user defines interest-rate swaps or cross-currency swaps as editable one-line grid rows and receives mark-to-market values and an explicitly defined fair-rate or fair-spread measure. The same calculation capabilities remain callable from workflows and the AI interface.

**Confirmed requirements**

- Use Blazor, MudBlazor, and ExGrid for the first client.
- Support keyboard, mouse, touch, light/dark themes, and responsive layouts.
- Provide live ticking, turning ticking off, and manual one-off calculation.
- Use realistically bounded simulated market updates in the personal project.
- Support IRS and cross-currency swaps in the intended initial product; implement the products in explicit milestones.
- Do not add a direct native analytics dependency to the Web server project.
- Do not create a persisted `AnalysisRun` for every market tick.
- Approximately 20 users is the initial user population, not a measured capacity guarantee.

**Implementation proposals**

- Use .NET 10 for the new application, subject to checking the actual repository and company environment.
- Start with a real IRS engine in a persistent separate process, followed by a clearly specified real cross-currency product implementation.
- Select QuantLib, Quantra, or another permitted engine after a small integration spike. No engine, binding, package version, or deployment image is selected by this document.
- Start with per-workspace batch calculation and a bounded scheduler. Avoid global cross-user trade deduplication until measurements justify it.
- Use ordinary application streaming, for example SignalR, for quote updates. Market ticks do not pass through an LLM or require an agent turn.

The synthetic market source and a real pricing engine are independent choices. A result may legitimately be **real engine / simulated market**. A deterministic fake result must be labeled **simulated calculation**.

## 2. Product scope and financial meaning

### IRS milestone

Define a supported fixed/float IRS convention set and document it before implementation. A row must identify, directly or through an explicitly resolved convention profile:

- Trade row identity and input revision.
- Currency, notional, payer/receiver direction, effective date, maturity, and fixed rate.
- Floating index, tenor, spread, reset/payment frequencies, calendars, day-count conventions, and business-day adjustment.
- Discounting/collateral context and forwarding curve selection.
- Requested measures, at least PV and fair fixed rate where defined.

The one-line editor is a compact representation of this typed input. Resolve any shorthand into inspectable typed terms; preserve the original shorthand for convenience, not as the only authoritative contract.

### Cross-currency milestone

Choose and name the first supported cross-currency structure. A reasonable **proposed** first scope is a non-resetting floating/floating cross-currency basis swap with explicit initial/final notional exchanges. Resettable notionals, optional breaks, and other structures require explicit additional coverage.

A cross-currency row must identify both legs, currencies, notional/exchange rules, FX context, curves, collateral assumptions, and the leg/parameter to solve for. A generic field named `SwapRate` is insufficient: distinguish a fair fixed rate from a fair basis spread and identify the target leg. Do not approximate unsupported cross-currency structures with two unrelated IRS calculations and present the result as supported pricing.

Provider coverage determines which inputs are executable. Unsupported structures remain explicit validation failures. They must not silently receive a demo number or a financial default with different meaning.

## 3. User operations and persistence

| Operation | Required behavior | Persistence |
|---|---|---|
| Edit a row | Commit typed terms, validate, increment input revision, mark previous values stale | Update the editable workspace according to its draft-save policy |
| Start live | Commit current editors; validate all active rows; establish a subscription for that input revision | Store subscription metadata only if needed operationally; no `AnalysisRun` for each tick |
| Receive a market update | Schedule a bounded batch calculation for a fixed market snapshot | Retain enough market/input context to support the current frame and capture |
| Edit while live | Apply the edit through the same typed command path; replace the subscription's accepted input revision | Prior saved Runs remain unchanged |
| Stop live | Stop requesting and delivering new frames; retain and label the last completed frame with its timestamp | Does not implicitly create an `AnalysisRun` |
| Price once | Commit and validate inputs; pin a market snapshot; execute one analysis | Create one `AnalysisRun`, with Cases if the chosen workflow needs them |
| Capture result | Freeze an identified completed frame and its exact inputs/market/provider identity | Create a saved `AnalysisRun` record referencing that result; do not relabel it with a newer market snapshot |
| Resume live | Validate current inputs and create/resume an appropriate subscription revision | Does not mutate a previous capture |

**Capture proposal:** capture one coherent completed batch frame. If the screen contains rows from different market revisions, either select a coherent frame or explicitly show that capture is pending until a coherent frame is available. Do not claim that mixed row revisions are one simultaneous valuation.

On a narrow screen, use a row-detail panel for terms that do not fit. Support the same commands through keyboard shortcuts, buttons, and touch controls. Tapping controls must execute actual commands, not merely focus a hover-only element.

## 4. Service responsibilities

| Component | Responsibility |
|---|---|
| `RatesWorkspaceCoordinator` | Typed row edits, validation, input revisions, manual/live/capture commands |
| `RatesLiveService` | Subscription ownership, scheduling, delivery, stop/disconnect cleanup |
| `IMarketDataSource` | Resolve fixed snapshots and publish ordered market updates |
| `IRatesPricingProvider` | Price a fixed typed input batch against a fixed market snapshot |
| Native Worker | Load the selected engine, construct/cache engine objects safely, calculate |
| Analysis execution service | Persist manual/captured analyses and expose history |

Names are illustrative. Keep boundaries; do not create one distributed service for every interface. The live service and application services may initially run in the same application process. The native engine must remain in a separate process.

The first live implementation can repeatedly call a batch pricing provider. If a company service already publishes live prices, implement a separate live-provider adapter; retain the same application-level revision and delivery semantics. Do not force a long-running subscription into a one-shot job API.

## 5. Fixed input and output contracts

The following C# is a contract sketch, not a complete compilable SDK:

```csharp
public interface IRatesPricingProvider
{
    Task<RatesBatchResult> PriceBatchAsync(
        RatesBatchInput input,
        CancellationToken cancellationToken);
}

public interface IMarketDataSource
{
    Task<MarketSnapshot> GetSnapshotAsync(
        MarketSnapshotId id,
        CancellationToken cancellationToken);

    IAsyncEnumerable<MarketUpdate> SubscribeAsync(
        MarketSubscription request,
        CancellationToken cancellationToken);
}
```

`RatesBatchInput` must contain the accepted input revision, typed row terms, requested measures, valuation date, a resolvable fixed `MarketSnapshotId`, and the resolved pricing configuration. It must not contain native engine objects or depend on a mutable “latest market” lookup inside the worker.

A live result envelope should identify:

- `SubscriptionId` and `SubscriptionRevision`.
- `InputRevision` and stable row identities.
- `MarketSnapshotId`, ordered market sequence, and market as-of time.
- Provider binding, engine/configuration versions, and measure definitions.
- Calculation completion time, row results, and explicit row failures.
- Market origin (`synthetic` or an actual source identifier) and calculation mode (`real` or `simulated`).

Numbers carry units and currency. A missing, failed, or unsupported measure is not zero. Result transport does not round values to the displayed number of decimal places.

## 6. Scheduling, coalescing, and stale results

Use a bounded queue and bounded worker concurrency. For each live subscription/input revision, allow an in-flight calculation plus at most the most recent pending market update as an initial policy.

When a newer market arrives while a live calculation is busy, replace the pending update. This is a freshness policy for a live display. **Do not apply it to explicitly requested scenarios, risk shocks, historical jobs, or saved analyses:** each of those represents required work.

On result delivery:

1. Verify that the subscription still exists and belongs to the requesting principal/workspace.
2. Verify that the subscription and input revisions match the active accepted input.
3. Discard frames older than the last applied market sequence for that input revision.
4. Accept a coherent newer completed frame and show its actual market timestamp/age.

Do not require a completed result to match the latest received market tick exactly. An engine slower than the tick source would then never display a result. Correctness is “right input revision, monotonically advancing completed market frames,” with visible freshness.

After stop, input change, or disconnect, late results may complete internally but must not overwrite the current screen. A cancellation token requests cancellation; it does not prove a native or remote calculation stopped. Track/discard its output accordingly and clean up resources when the operation actually terminates.

Use a deterministic scheduler and injected clock/market source for tests. Avoid wall-clock sleeps in business-rule tests.

## 7. Simulated market data

The demo source should be repeatable from a seed and clearly marked synthetic. Start from a documented curve/FX configuration and apply bounded, correlated changes rather than independently randomizing every cell.

Proposed construction:

1. Configure a valuation date, base currency curves, FX rates, and required fixing history.
2. Apply small bounded level/slope perturbations to curve inputs and bounded log-space changes to FX.
3. Rebuild or update curves using the selected engine's documented construction method.
4. Emit a monotonically ordered immutable market snapshot with source seed/scenario version and as-of time.

Validate the engine's required domains and curve construction. Positive discount factors and positive FX rates are useful checks; do not incorrectly prohibit all negative rates. Smooth simulated quotes alone are not a proof of an arbitrage-free market. Record the construction assumptions instead of making that claim.

An identifier must resolve actual values, interpolation/construction settings, fixing history, and versions needed for a saved calculation. Saving only a tick counter is insufficient.

Use bounded retention for transient market snapshots, but pin the snapshots referenced by a captured/manual Run. Active in-flight work must also retain its snapshots until completion or confirmed disposal.

## 8. Native worker and caching

The Worker is long-lived. Reuse its process and communication client; do not start a process for every tick. HTTP is an acceptable first transport proposal. gRPC can be evaluated if the deployment supports it and measurements justify the extra work.

Initially assume the native engine instance is not safe for concurrent mutation until its actual build and integration have been verified. Serialize access within a worker or use isolated worker processes. In particular, concurrent valuation dates or mutable global engine settings must not leak between requests.

Possible worker caches include parsed instruments and constructed curves. Keys must cover every input that changes their meaning, including:

- Typed trade terms and row/input version.
- Valuation date and convention/calendar/fixing versions.
- Market snapshot or market content identity.
- CSA/collateral/curve configuration.
- Provider binding, engine build, model/configuration, and requested measure semantics as applicable.

Avoid cross-user result sharing initially. Sharing authorized market data or immutable instrument construction is a separate design decision from sharing a user's valuation results. Any later shared result cache must preserve authorization and use complete semantic keys.

## 9. Performance and acceptance

The following is a **proposed benchmark fixture**, not a promised or measured service level:

- 20 simultaneous users.
- 50 active rows per user.
- One market update per second.
- A documented mix of supported IRS and cross-currency structures.

Measure CPU, memory, queue length, worker utilization, dropped/coalesced live updates, disconnect cleanup, and end-to-end freshness percentiles. Record hardware, engine build, transport, and product mix. Agree a target after the first real-engine measurement.

Required behavioral checks:

1. Editing a row while a calculation is in flight never applies an old-input result.
2. A slower worker still displays monotonically newer completed frames.
3. Stop/disconnect does not leave unbounded background subscriptions.
4. A manual calculation and a live batch with identical fixed input/market/configuration use the same pricing semantics.
5. Capture stores the exact frame context, not the current screen's newer edit or market state.
6. Failed rows remain identifiable and are never converted to zero.
7. Worker failure produces a visible provider failure; it does not switch to a fake calculation.
8. Multiple users cannot subscribe to each other's unshared workspace results.
9. The Web process can start/build without installing the native engine.
10. Keyboard, touch, and responsive layouts support start, stop, edit, manual price, and capture.

## 10. Milestones

1. Typed IRS input and deterministic fake provider; revision and capture tests.
2. Persistent real-engine Worker; manual IRS valuation using a fixed synthetic snapshot.
3. Live simulated market updates, bounded scheduling, and stop/reconnect behavior.
4. Explicitly scoped real cross-currency pricing and its reference examples.
5. Benchmark, usability checks, and provider-replacement proof.

The fake stage is useful implementation scaffolding. Do not report the intended rates feature complete until the specified real product calculations and acceptance checks pass.

## References

- [Provider boundaries](08-provider-boundaries.md).
- [Execution and history](05-execution-history.md).
- [Microsoft: gRPC performance guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance?view=aspnetcore-10.0). This is a transport evaluation reference, not a decision to require gRPC.
- [QuantLib source and documentation entry point](https://www.quantlib.org/). Verify the selected engine/binding against its current official documentation during the integration spike.
