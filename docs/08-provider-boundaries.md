# Replaceable calculation providers and company integration

## 1. Goal

Develop the personal application so that company development can retain the common platform, fixed Blazor workflows, AI integration, result views, and history while replacing calculation and data-access implementations.

**Confirmed:** the Web server must not acquire a direct native analytics dependency. Company analytics may be reached through existing services, MQ/HPC, or a dedicated process that loads a company DLL. The latest committed and validated XVA screen input is authoritative; replacement adapters must preserve its financial meaning.

**Proposed implementation:** neutral typed contracts, separately registered execution providers, separate data-source interfaces, and a persistent native Worker. Use startup dependency-injection registration and validated environment configuration first. Dynamic DLL discovery/hot loading is not required.

Replacement means writing an adapter once for the company contract, then selecting it without editing consumers. It does not mean that an unknown company API automatically fits, that every engine supports every product, or that numerical equality follows from a shared interface.

## 2. Four distinct concepts

| Concept | Meaning | Examples |
|---|---|---|
| Capability | Versioned business operation and its typed input/output/error contract | Rates valuation, CRIF generation, SIMM calculation |
| Workflow | Composition of capabilities, references, dependencies, and branching | Generate sensitivities, calculate margins for cases, compare results |
| Execution provider / engine adapter | An implementation of a capability or execution port | Worker client, company MQ adapter, external HTTP client |
| Analytics input artifact | Immutable bytes required by a particular engine | Existing data-provider ZIP, engine configuration file |

Do not use “provider” ambiguously for both a callable implementation and a ZIP. Prefer `ExecutionProvider` or `EngineAdapter` for the former and `AnalyticsInputArtifact` for the latter.

The generic composition core owns capability discovery, contract validation, typed references, workflow execution boundaries, and view/action binding. Financial domain types and provider-specific formats live in domain modules/adapters. The UI is not the workflow executor, and an engine's function names are not the platform's universal capability catalog.

## 3. Ownership and dependency direction

| Module | Owns | Must not depend on |
|---|---|---|
| Generic composition contracts/runtime | Capability/workflow/view/context contracts; execution abstractions | XVA financial rules, native engine objects, company SDKs |
| Quant domain modules | Rates/XVA/CRIF/IM typed semantics and validation | MudBlazor/ExGrid objects, native pointers, MQ headers |
| Application services | Input acceptance, case expansion, logical dependencies, history | Specific engine constructors or queue-format details |
| Provider adapters | Engine input/output mapping, transport, coverage, provider errors | Page state, Fluxor actions, grid instances |
| Data-source adapters | Counterparty/baseline/trade/market retrieval and fixed versions | User-interface lifecycle assumptions |
| Native Worker | Native loading, engine objects, process-local caches, calculation | Browser state or Web app startup |

Logical boundaries need not be separate deployed services. The native execution boundary is the explicit process boundary required here.

The Web application may reference a managed Worker client and neutral protocol models. It must not transitively reference a package that loads the engine DLL into the Web process. Put native packages and operating-system-specific loader code only in the Worker deployment.

### Public and company-specific code

**Confirmed scope:** reusable OSS is the generic composition layer, including contracts, workflow/context handling, and view/action composition. Company XVA/pricer business logic, entitlements, and model controls remain separate/private. Synthetic examples may demonstrate the framework without copying proprietary business code.

Keep company provider projects/packages in the company codebase. A public build must not require a company package feed, proprietary DLL, credentials, or network access to company services. An internal bootstrap/composition-root project may reference the common platform plus company adapters.

Do not place proprietary DTO definitions into a nominally public “common contracts” project merely to simplify mapping.

## 4. Capability contracts and provider coverage

Define capability contracts independently of engine classes. For each capability version, document:

- Input/output schemas and their financial meanings.
- Supported product and measure types.
- Currency, rate/spread units, signs, valuation dates, and convention identifiers.
- Required fixed input/market/configuration references.
- Allowed execution family: one-shot, durable job, or live subscription.
- Typed unsupported-input, validation, provider, and transient/unknown-outcome errors.
- Output provenance and artifact references.

A provider advertises the versions and subset it implements. Validate the requested product, measures, options, and execution mode before dispatch. Structural schema compatibility is necessary but does not establish financial compatibility.

Examples of explicit semantic rules:

- A spread of 75 basis points is not the decimal value 75; the adapter converts according to the declared unit.
- CDS recovery and the independent CVA recovery override remain distinct fields.
- An IRS fair fixed rate and a cross-currency fair basis spread are different measures.
- Reporting currency, pricing numeraire, and the currency in which a sensitivity is expressed are not interchangeable labels.
- The application's agreed non-positive CVA convention is retained when mapping an engine with a different output convention.
- Missing or unsupported values are not returned as numeric zero.

Use capability-specific types for financial content. Avoid a universal `CalculateAsync(object)` contract or arbitrary string dictionaries that make missing meanings invisible.

### Input authority

For the fixed XVA pricer, the accepted submission is the frozen, committed, validated typed workspace input. Baseline and edit provenance explain it; they do not authorize the adapter to replace it.

The adapter may convert units, identifiers, and documented representations. For example, it may map legal netting scope to the existing engine's `Netting`/Trade ID encoding. It may use the pinned original artifact to supply non-edited source data. It must not fetch a newer source or reapply UI defaults to overwrite a submitted CSA, discount curve, recovery, membership flag, or trade term.

If the target cannot represent a submitted choice, return an explicit unsupported/validation error. Do not silently approximate it.

## 5. Execution families

### One-shot/batch

Suitable for bounded rates calculations against a fixed market snapshot. A representative domain port is:

```csharp
public interface IRatesPricingProvider
{
    Task<RatesBatchResult> PriceBatchAsync(
        RatesBatchInput input,
        CancellationToken cancellationToken);
}
```

This is a contract sketch. Native objects must not appear in `RatesBatchInput` or `RatesBatchResult`.

### Durable jobs

Suitable for existing MQ/HPC execution and independently callable CRIF/IM jobs. The application needs durable submission identity, observed state, output/error collection, and cancellation requests. Persistence of these identities must survive a Web restart.

The app's `AnalyticsRequest` identifies fixed logical engine input. A `DispatchAttempt` records a transmission attempt and its transport/correlation identifiers. Before and After remain independent analytics requests, even when one business case compares both results. A capability may create several child analytics requests; all remain discoverable under its invocation and public Run ID.

The existing company execution service already accepts an `IDataProvider` containing multiple counterparties/netting sets, partitions work per netting set on HPC, and aggregates the outputs. Preserve that service boundary when useful. A company adapter may expose a coarse bulk capability and map its outputs back to explicit case identities. The platform must not require a new HPC dispatcher or one platform invocation per HPC task. `IDataProvider`, its partition rules, and native aggregation details stay outside the generic composition core. Do not invent an `action.csv` or scenario grammar before the company contract is available.

Do not interpret a timed-out wait as proof that the remote calculation failed. Reconciliation and retry rules must reflect what the provider actually supports. Reusing a logical request identifier with different input is an error. A client cancellation token is not evidence of confirmed cancellation in MQ/HPC.

### Live subscriptions

A subscription is not one durable analysis per tick. Use a live adapter or a live coordinator over the batch provider. Pin input/market revisions in each frame, apply bounded work, and discard stale output as specified in [Rates live pricing](07-rates-live.md).

The three families share contracts, provenance, and authorization boundaries. They do not need identical lifetime APIs.

## 6. Provider selection and version pinning

Choose implementations per capability, not with a global assumption that one engine provides rates, XVA, and SIMM.

Illustrative registration in separate composition roots:

```csharp
// Personal environment: a managed client to a separate Worker.
services.AddScoped<IRatesPricingProvider, QuantWorkerRatesProvider>();

// Company environment: use this registration instead, in company code.
services.AddScoped<IRatesPricingProvider, CompanyRatesProvider>();
```

The example shows alternatives, not two registrations to paste into the same startup path. Actual HTTP clients and service lifetimes must match the chosen implementation.

Environment configuration selects only registered providers and supported bindings. Validate required endpoints, configuration profiles, capability versions, and coverage at startup or before accepting work. A company profile must not silently fall back to a fake when a real provider is missing or fails.

Resolve and record a provider binding when a Run is accepted. Preserve that binding through retries and result collection. Changing configuration affects new work; it must not reinterpret an existing logical request using a different engine. An explicit re-execution with another engine is a new Run or clearly identified comparison case.

A binding should identify provider name, adapter version, engine build/version, model/configuration version, and relevant contract versions. A version label alone is not a substitute for retaining the actual fixed inputs or resolvable artifacts.

## 7. Replace data sources as well as analytics

| Port | Personal environment | Company environment |
|---|---|---|
| Counterparty/reference-data source | Synthetic registrations, agreements, CSAs | Company master/reference services |
| Baseline source | Versioned sample portfolios and credit data | Existing data-provider download service |
| Trade source | Sample draft trade definitions | Booking-system draft downloads |
| Market source | Seeded fixed snapshots and live synthetic updates | Company market snapshots/feed |
| Calculation provider | Fake or real OSS Worker | Company service, MQ/HPC, or native Worker |

**Company mapping, PV adjustment (owner, 2026-10-27):** the company `IDataProvider` carries a trade's PV in `/deals/trade_official_pv_table.csv`, and the adjustment applies when `/cfg/xva.csv` registers `xva.enable_pv_adj` as true (`docs/03-xva-domain.md` §2.5). The table is only the engine's intake: any supplied PV, official or model, is written there. The company adapter writes both from the capability's typed inputs; the composition core and the UI never see these files. Their column layout is not yet specified.

Snapshot the outputs or retain immutable version-addressable references. Looking up a source again at Price time must not silently change accepted inputs.

`TradeInput` exists before a Run and represents an immutable downloaded trade version. Workspace trade rows reference it through stable trade-instance identities. Do not make the current Run folder the only owner of a draft trade needed by later Runs.

Not every proprietary product must immediately be converted into a universal portable trade language. Where necessary, retain a typed editable/display subset and a versioned opaque original artifact. Be explicit that another provider may not understand that proprietary artifact; portability requires a supported mapping, not merely an artifact ID.

## 8. Results, intermediate outputs, and diagnostic artifacts

Return neutral typed results or typed references with schema/version, units, currency, producer invocation, and access context. Subsequent workflow steps consume those references without depending on the source provider's transport.

Views are optional consumers. A hidden CRIF, sensitivity set, or market snapshot can be passed to another step and displayed later. A provider must not return only a pre-rendered grid or HTML fragment as its business result.

Preserve provider-native requests/responses as immutable artifacts when needed for investigation. The artifact may contain ZIP or binary data; application-level relationships record its role and association with `AnalyticsRequest`/`DispatchAttempt`/`CapabilityInvocation`.

Persist enough provenance to answer:

- Which accepted business input and fixed source versions were used?
- Which adapter, engine, model/configuration, and capability version ran?
- Which native input was sent and which response was observed?
- Which requests belong to this Before/After case and public Run ID?
- Was the calculation real, simulated, partial, failed, or outcome-unknown?

The common artifact store owns bytes once. Database metadata and canonical result datasets are distinct representations with explicit lineage; they are not independently editable copies of the same authoritative input.

## 9. Replacement proof and tests

### During personal development

1. Implement a deterministic fake and a real Worker adapter for the same rates contract.
2. Run both through the same application service and UI flow.
3. Change only registration/configuration to select the provider.
4. Verify that the Web project builds and starts without loading native engine packages.
5. Keep XVA/CRIF/IM development providers explicitly simulated until a real implementation is connected.

### Contract and mapping tests

- Preserve committed CSA, curve, B/A selection, trade terms, and recovery distinctions.
- Test units/signs and canonical-to-native identifier mappings.
- Return unsupported options explicitly; do not silently discard them.
- Pin the provider and input versions across retries.
- Map delayed, duplicate, and out-of-order MQ replies to the correct persisted request/attempt.
- Record partial/unknown/cancel-requested states honestly.
- Keep raw artifact lineage retrievable by public Run ID.
- Prevent simulated fallback after a real-provider error.

### Company integration tests

Create reference trades and market/configuration fixtures with documented expected conventions. Compare company results with approved references under stated tolerances. Investigate differences due to conventions, models, calibration, and stochastic configuration separately from software transport correctness.

Passing the contract tests proves the integration boundary. It does not prove numerical equivalence between different pricing models.

## 10. Implementation sequence

1. Define the small set of first-release capability contracts and semantic metadata.
2. Implement the common application boundary with deterministic providers and fixed source fixtures.
3. Integrate the real rates Worker without editing UI/workflow consumers.
4. Add company-only data adapters and an initial company calculation adapter.
5. Validate numerical semantics, operational behavior, permissions, and diagnostic retrieval in the company environment.
6. Enable individual providers through configuration while the existing production pricer remains available.

Use the company's existing execution infrastructure where it fits. The composition platform does not require replacing MQ/HPC or rebuilding a financial analytics engine.

## References and attribution

The architecture above is a project-specific proposal constrained by the user's requirements. Background mechanisms are documented in:

- [Microsoft: Common web application architectures](https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures) — dependency inversion and interfaces owned by the application core.
- [Microsoft: .NET dependency injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview) — registration and injection of replaceable implementations.
- [Microsoft: Anti-Corruption Layer pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/anti-corruption-layer) — isolating model/semantic translation at integration boundaries.

Check current supported packages and runtime compatibility when implementing. These references do not select a provider, certify numerical behavior, or require a microservice deployment.
