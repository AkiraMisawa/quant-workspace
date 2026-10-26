# Execution, identity, and history

Status: implementation specification. **CONFIRMED** denotes user requirements; **PROPOSED** denotes a concrete starting policy that can be changed without rewriting the product. The names below are the canonical names for this handoff; older documents used inconsistent combinations of analysis groups, sessions, and pricing runs.

## 1. Execution is independent of a page

**CONFIRMED:** Fixed pricers, the AI interface, and composed workflows call the same capabilities. AI execution must not navigate the XVA page, dispatch Fluxor actions, or manipulate a grid. One workflow may have several views, and a workflow step may have no view. Intermediate results are available through typed references even when they are not rendered.

The reusable composition layer owns generic execution concepts. XVA, CSA, Before/After valuation meaning, proprietary `IDataProvider` formats, company authorization rules, and model governance belong to business/company modules. They must not become dependencies of the generic OSS runtime.

Use an execution-runtime port so the application is not tied to a particular workflow engine. A bounded reference executor is sufficient for the demonstrator. Elsa and Temporal are candidates mentioned in prior discussions, **not selected dependencies**. Durable production semantics below remain requirements regardless of the runtime used.

## 2. Canonical identity model

| Identity | Meaning and lifetime | Rule |
| --- | --- | --- |
| `UiSessionId` | An ephemeral browser-tab/page visit, useful for diagnostics | It is not a data owner, authorization identity, or durable analysis group. |
| `WorkspaceId` | A reusable editable working context | It outlives individual calculations. Draft trades can exist here before any Run. Persisted restoration is a proposed default. |
| `WorkspaceRevision` | Version of the complete accepted draft state | Increment on semantic changes. Freeze the submitted revision. |
| `RunId` (`AnalysisRun`) | One business submission | One Price operation, one submitted AI multi-case analysis, or one captured/manual rates analysis. A retry of the same submission does not create a second Run. |
| `CaseId` (`AnalysisCase`) | One comparison item inside a Run | Examples: one counterparty/CSA alternative or one individual added trade. |
| `InvocationId` (`CapabilityInvocation`) | One logical capability execution | A shared preparatory invocation can be bound to multiple cases. |
| `AnalyticsRequestId` | One immutable logical request at an analytics-provider boundary | Fixes input/configuration/data versions. Before and After are independent requests. |
| `AttemptId` (`DispatchAttempt`) | One transport submission attempt | A retry gets a new attempt, not a modified logical request. |
| `ArtifactId` | One immutable byte payload | Original providers, native request/response ZIPs, CRIF, logs, and exports reference these. |
| `ResultId` (`ResultDataset`) | A typed, immutable logical result | May be a scalar, table, structured object, CRIF, or another declared result type; not every result is a table. |
| `AnalysisGroupId` | Optional named grouping of several Runs | Suitable for a later saved study/quote investigation; not mandatory for an ordinary multi-case request. |
| `SubscriptionId` | A live rates subscription | It is not a Run. Do not create a Run for every tick. |

Use opaque IDs. The prototype may use UUIDs; no domain meaning should be parsed from their strings. Provider IDs and legacy IDs are mapped explicitly rather than reused as interchangeable application IDs.

### Examples

- One fixed-XVA Price click with five individual additions: **one Run, five Cases**, and the required independent Before/After requests for each case/configuration/scenario.
- One AI request comparing SIMM for ten counterparties: **one Run, ten Cases**, plus one or more preparation invocations. A later "change B's CSA and recalculate" submission creates another Run; an optional analysis-group or parent-run link connects them.
- A second Price click from the same browser tab: the same Workspace may be reused, but a new Run freezes the new input revision.
- A connection retry after an uncertain HTTP response: retry the same submission key and recover the existing Run, not a new business submission.

## 3. Submission and authoritative input

**CONFIRMED:** The fixed XVA pricer submits the entire committed, validated typed input represented by its workspace, including B/A membership and per-trade edits. It does not submit only a patch for the server to apply to newly fetched defaults. This supersedes the older authoritative `baseline + patch -> server reconstruction` proposal.

Submission sequence:

1. Commit active editors, including a focused cell whose blur has not yet fired.
2. Finish required context loads and automatic-follow operations; reject an incomplete or stale context revision.
3. Validate the whole relevant dataset, including rows outside the virtualized viewport.
4. Freeze the typed input and referenced immutable data versions. User edit provenance is attached separately.
5. Send a client-generated submission/idempotency key with the frozen input.
6. The application service authorizes, validates references and financial constraints, and computes its own canonical input hash. It must not trust a client-provided hash.
7. In one database transaction, reserve the Run and its immutable accepted input plus the initial execution plan. A repeated key with identical accepted input returns the existing Run; a repeated key with different input returns a conflict.
8. Persist the native input artifact before marking the corresponding dispatch as ready. Dispatch workers execute committed work.

The server may normalize representation (units, native IDs, serialized field ordering) under a versioned contract. It must preserve the requested financial meaning. Unsupported CSA, product, model, or curve options cause explicit validation errors; they are not silently dropped or replaced by defaults.

**Hash separation:** `accepted_input_hash` hashes the versioned canonical semantic input, including resolved data references and relevant options. `Artifact.sha256` hashes exact stored bytes. ZIP timestamps, JSON property order, and numerical formatting can change byte hashes without changing financial meaning. Never interchange these hashes. The canonicalization algorithm/version must be specified before hashes are used for deduplication; the prototype can compare stored canonical serialization directly. Do not use a semantic hash to deduplicate calculation results in the first release.

## 4. Dependencies, bulk providers, and Before/After

**CONFIRMED company integration context:** An existing company execution service already accepts a multi-counterparty/netting-set data provider, partitions it into per-netting-set jobs on HPC, and aggregates the results. Reuse this service through an adapter. Building a replacement HPC scheduler is not a prerequisite.

Separate the application's logical graph from the provider's physical parallelization. A single invocation may cover several Cases. A provider request may be a bulk Before or bulk After envelope with explicit case-to-provider-item bindings. Its internal HPC jobs may remain provider-owned; retain provider job references when exposed. Bulk batching does not imply reusing the computed value of one case as another case's result.

Example logical dependencies for an MVA-related analysis:

| Step | Input | Output | Dependency |
| --- | --- | --- | --- |
| Generate CRIF | Frozen new-trade inputs and market/model versions | Typed CRIF result/artifact | No XVA request is prepared from an unavailable CRIF. |
| Prepare XVA inputs | Accepted case input plus the required CRIF reference | Native input ZIPs/configurations | Explicitly binds the correct CRIF to the relevant trade set. |
| Execute Before | Before membership, configuration, scenario, fixed data | Before result | Independent MQ request. |
| Execute After | After membership, configuration, scenario, fixed data | After result | Independent MQ request. |
| Compare | Successful compatible Before and After results | Difference/result dataset | Never treats a missing side as zero. |

The exact CRIF/IM/MVA dependency belongs to the business workflow and engine contract; do not assume every provider requires the same preprocessing. CRIF generation and IM/SIMM calculation must also be callable independently.

FVA versus other XVA configurations and base versus risk/scenario runs produce the required distinct logical requests. Do not rely on filenames such as `before.zip` to identify them. Record role, configuration, scenario, side, and case bindings explicitly.

### Individual-addition iterative mode

**CONFIRMED initial mode:** each selected new trade is added independently to the same explicitly defined comparison baseline. It is not cumulative addition. If there are five individual additions, case 3 is not implicitly "trades 1 + 2 + 3". Package/grouped additions are future scope.

The business module must declare how existing manually selected B/A membership is converted to the common comparison baseline; see the XVA domain document. An ambiguous mixture is rejected or resolved explicitly before execution.

### Shared-Before optimization

Deferred. Model bindings may permit one result to be referenced by several cases, but the first release must not introduce result reuse. Future eligibility requires equality of the complete effective portfolio, calculation/engine/model settings, market and reference-data versions, scenarios, conventions, and applicable stochastic settings, together with an explicit reuse policy. Same trade IDs, same displayed PV, or same baseline ID alone are insufficient. Record the reused result's originating invocation and bindings.

## 5. Durable state, transport, and recovery

The database execution record is authoritative for job lifecycle; an in-memory `Task`, a Blazor circuit, or an open MQ consumer is not the durable record.

| Situation | Required behavior |
| --- | --- |
| Accepted work has not been sent | Durable pending/dispatch record can be resumed after restart. |
| Send succeeds but acknowledgement is lost | Mark the outcome unknown and reconcile. Do not automatically execute the same economic calculation again unless provider idempotency or authoritative rejection makes the retry safe. |
| A result has not arrived before the UI timeout | Report timeout/unknown according to evidence. Timeout is not proof of engine failure. |
| Cancel is requested | Record `cancel_requested`. Use `cancelled` only after cancellation is confirmed or the work is known not to have started. |
| HTTP request or browser connection is cancelled | Stop that wait/stream; it does not by itself confirm cancellation of remote work. |
| Duplicate or late MQ response arrives | Persist/deduplicate it, correlate it, and apply a monotonic state transition. Do not overwrite a newer workspace or downgrade a completed Run. |
| One case fails | Preserve successful cases and their artifacts. Aggregate status may be `partially_succeeded`; required downstream steps for the failed case become blocked. |
| Old and new web versions coexist | Message routing and correlation determine ownership. Do not let both consumers race for the same unpartitioned reply queue. |

**PROPOSED** aggregate statuses: `accepted`, `running`, `succeeded`, `partially_succeeded`, `failed`, `cancel_requested`, `cancelled`, `unknown`. Case/invocation/request states can be more detailed. Document transition rules in code; a generic enum does not enforce a state machine.

Use a durable inbox for received messages and an outbox/dispatch record committed with the execution state. This provides recoverable work and at-least-once delivery handling; it does not create exactly-once execution in a non-idempotent external engine. Claim/lease dispatch records if several workers can process them.

### Correlation IDs

Using `AnalyticsRequestId` as the MQ correlation ID is acceptable if the transport encoding/length and routing rules support it. The adapter owns any encoding or mapping.

The important invariant is a durable mapping from `(provider endpoint/environment, correlation ID)` to exactly one immutable logical request. A new calculation input gets a new logical request ID. A replay cannot reuse an ID with different bytes or settings.

**PROPOSED:** use distinct attempt correlations when the provider supports them, while retaining the logical request ID in metadata. If the provider echoes the same logical correlation across retries, several attempts can map to one request; do not pretend the response uniquely identifies the attempt. Do not retry an unknown outcome merely to obtain a fresh correlation.

## 6. Intermediate results and independent views

Expose a `TypedDataRef` containing a result ID, schema identifier/version, immutable artifact reference, and relevant shape/metadata. Large results remain in the result/artifact service; the agent and browser receive bounded summaries or queried projections.

- Later steps bind to typed result references, not grid row positions or natural-language summaries.
- Intermediate CRIF/sensitivity data can remain hidden while being available to downstream steps or later inspection.
- A saved View Definition can bind to an existing result without rerunning the calculation.
- A row action starts a new validated command/workflow using stable row identifiers and result references.
- Changing a view's grouping or chart type does not create a calculation Run. A derived dataset transformation may be a separately recorded capability when it changes data, has cost, or requires provenance.
- Type/schema compatibility, context compatibility, and authorization are validated when references are bound or rebound. A structurally valid reusable workflow still requires fresh data-dependent validation.

## 7. History and diagnostics

History first groups by the business Run and then by Cases. It may also filter/group by user, workspace, capability, date, counterparty registration, status, or an optional named analysis group. Browser-tab sessions are diagnostic filters only.

Every saved result should expose provider identity, adapter version, engine/model/configuration version when available, market/reference snapshot IDs, units, and actual-versus-simulated provenance. Missing legacy metadata is marked unknown, not fabricated.

**CONFIRMED:** authorized developers can retrieve a diagnostic bundle using Run ID. Include a machine-readable manifest, accepted input/provenance where available, baseline/draft references, invocation graph, case bindings, native request/response artifacts, versions, status, and relevant logs. Include hashes and a list of missing files. The retrieval API resolves through metadata and storage abstraction; callers do not need a server-local folder path. Apply authorization to the Run and each referenced artifact/result.

## 8. Acceptance obligations

1. Two Price submissions from one workspace produce immutable distinct Runs; subsequent edits do not mutate either.
2. Retrying the same submission key after an interrupted HTTP response recovers the same Run. Changing the payload with that key is rejected.
3. Before/After, FVA/non-FVA, and scenario requests are independently identified and correctly bound to cases.
4. A provider bulk request can map outputs to all relevant cases without inventing per-case transport sends.
5. Process restart after request persistence and before send recovers work. Restart after uncertain send does not blindly duplicate remote calculation.
6. Duplicate/late/unmatched replies are handled deterministically; unmatched data is inspectable, not silently discarded.
7. Missing/failed results remain missing/failed; no synthetic zero enters a difference.
8. Hidden intermediate data can feed another capability and can be shown by a new view without changing the original workflow.
9. Run-ID diagnostics work for new and indexed legacy Runs, with explicit completeness metadata.
10. Live market ticks do not generate analysis-history rows unless the user/workflow explicitly captures an analysis.
