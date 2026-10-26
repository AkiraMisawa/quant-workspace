# API and contract starter

All routes and type names here are proposed. Adapt them to an existing host without changing the domain invariants. `contracts/*.schema.json` describes a bounded starter vocabulary; it is not the company analytics wire protocol.

## Endpoint responsibilities

| Endpoint proposal | Purpose and invariants |
|---|---|
| `GET /api/quant/counterparties?query=...` | Fuzzy authorized search; return registration ID plus CP code, legal entity, name and special flag. |
| `POST /api/quant/workspaces` | Create an editable workspace, not a pricing run. |
| `POST /api/quant/workspaces/{id}/prepare` | Resolve a context change; return immutable source snapshot IDs and dependent data; echo preparation generation/context. |
| `POST /api/quant/trade-inputs/import` | Resolve draft booking IDs to immutable TradeInput versions before a Run exists. |
| `PUT /api/quant/workspaces/{id}/draft` | Optional draft persistence with expected server version; no implicit execution. |
| `POST /api/quant/analysis-runs` | Validate/freeze effective input or a fully bound workflow, persist a Run and enqueue required work. Idempotent. |
| `GET /api/quant/analysis-runs/{runId}` | Run/case/step state, typed result references and links. |
| `POST /api/quant/analysis-runs/{runId}/cancellation` | Record cancellation requested; does not imply provider cancellation succeeded. |
| `GET /api/quant/analysis-runs/{runId}/diagnostics` | Authorized manifest and diagnostic bundle, including legacy locations through the same abstraction. |
| `GET /api/quant/data/{id}/rows` | Authorized projection/filter/sort over a fixed dataset revision. |
| `POST /api/quant/rates/subscriptions` | Open/update a live workload; streaming transport separately bound to subscription ID. |
| `POST /api/quant/rates/subscriptions/{id}/capture` | Persist the exact selected complete frame and its input/market refs. |
| `POST /api/quant/definitions` | Save a validated workflow/view version; execution is separate. |
| `POST /api/quant/agent/...` | Adapter-owned AG-UI endpoint using a pinned verified protocol implementation. |

Endpoints map transport inputs to application services. Tests call those services and pure domain functions without starting Blazor or MQ. Read-only capability discovery may be tailored to the caller's permissions.

## XVA accepted input

`examples/xva-final-input.json` shows a submitted full effective input subset:

- Context and pinned baseline/market versions.
- Effective CDS/proxy and separate recovery settings.
- NS legality, CSA definitions/selection and scenario definitions.
- Every relevant trade row with immutable TradeInput reference, B/A membership and explicit assignments.
- Calculation options and separately stored user/automatic provenance.

`examples/xva-uncollateralized-input.json` covers the optional-CSA case. An explicit `uncollateralized` selection uses the reserved CSA value `nocsa_{ma id}` (for example `nocsa_MA-001`), with no real collateral agreement required. The initial UI's corresponding main selection uses the same marker; its real CSA collection may be empty. Validate the marker against the trade's effective MA; it is not an unresolved choice or a real CSA master record. The default uncollateralized discount curve is configurable, for example `jpy_ois`, and is resolved into the visible typed row during preparation. Price preserves the final row curve, including any later manual override. A source/config version can be recorded through `defaultsProfileRef`.

When `cvaRecoveryOverride` is null/empty, preserve it as null/empty in the company engine request: analytics automatically uses the applicable CDS recovery already supplied in the fixed credit data. No additional `effectiveCvaRecovery` field is required and no explicit override is synthesized. An explicit numeric zero remains zero. Mapping empty wire representation and selecting the applicable CDS record follow the real analytics contract. This documented fallback is part of the accepted semantics, not an excuse to refetch credit data or change user settings.

In iterative mode, `candidateTradeInstanceIds` is a derived consistency projection of all eligible New rows checked for After, unique and nonempty; it cannot independently omit a checked row. The server verifies this equality before case expansion.

The `synthetic` field in these fixtures is an advisory source-data marker only. Real/mock execution provenance and source origin are determined by the server from actual source/provider records, not trusted from client input. A real engine using synthetic markets is still actual computation on synthetic data.

The example uses explicit rows for clarity. A very large portfolio may instead reference an immutable complete dataset revision plus a fixed resolved selection set. This must be logically equivalent and cannot mean only the viewport or a filter evaluated later against mutable data.

`workspaceRevision` identifies the locally visible accepted edit revision. If draft persistence is enabled, use a separate server ETag/expected-version check; do not assume a client counter alone prevents concurrent writes. Price may atomically persist the submitted complete draft and its Run. A conflict returns a structured error; it does not merge economic fields silently.

`schemaVersion` is the contract version, not an engine version. The server computes the authoritative accepted-input digest using a documented canonical encoding. A raw file SHA-256 is a separate byte-integrity digest. Client digests are consistency hints, not trusted proof. Preserve semantically significant ordering and units.

## Typed reference

Use a data ID, immutable revision and explicit contract identifier/version. Resolve contents through authorized services. `dataId` is not a file path, and knowledge of it grants no access. For artifacts, preserve actual media type, schema/format version, byte digest and producing invocation. Small typed values may be embedded instead of stored separately.

## Workflow starter vocabulary

`examples/simm-comparison.input.json` supplies the resolved case/MA/CSA/curve and fixed source bindings used by the Workflow. Its domain-specific schema is separate from the generic composition schema. A real source adapter resolves these example references; this bundle contains no company snapshots.

The example workflow uses `input`, `context`, `constant`, `output` and `item` bindings. `forEach` maps a step over a typed collection with bounded concurrency. Every mapped output is lifted to `Outcome<T>[]`: each envelope contains a stable `itemKey`, status, domain identity metadata, and either a value or an error. Consumers cannot infer identity from completion order.

With `itemMode: value`, the source is a plain input collection and `itemKeyPath` supplies its unique stable expansion key. The CRIF step uses this mode. With `itemMode: outcomeValue`, the source is a prior mapped output; the runtime binds `item` to the successful envelope's `value`. Failed/blocked upstream envelopes do not invoke the downstream capability; they produce a blocked outcome with the same item identity. The SIMM step uses this mode. The comparison step consumes the complete `qwb.simm-side-outcome` collection plus the original planned comparison, so cases with both sides failed remain present with unavailable values. `collect` does not mean errors disappear.

The runtime records expanded invocation keys such as `crif/case-A/before` and `simm/case-A/before`, distinct from the definition's `stepId`. Their construction is stable across retries. In the SQL sketch, `step_key` stores this expanded key, so map iterations do not collide under the per-Run uniqueness constraint.

`dependsOn` is explicit and must agree with output bindings. `outputs` names stable workflow output ports. Bindings use JSON Pointer paths within declared contracts; no executable expressions are permitted.

Generic dataset schemas contain column/row structure and generic execution/data-origin provenance. Domain basis metadata is a versioned extension; the SIMM fixture's value date, market, methodology and currency live there. The generic kernel does not interpret those financial fields.

The sample CRIF generation occurs per prepared portfolio side. A real domain adapter may compute deltas or reuse common sensitivities if mathematically valid and represented by compatible lineage; this optimization is not assumed by the sample.

## Numerical result semantics

Return values together with currency/unit, measure definition, status, provider ID/version, engine/model/config version, value date, data versions and input/result references. A numeric null with failure/unavailable status is not zero. Compare results only when their bases are compatible.

The app's agreed CVA convention is `CVA <= 0`, with `V_hat = V + CVA`; movement is `after - before`. A trader charge or charge ratio is a separately named business measure, not an implicit sign reversal. Rates results distinguish PV, par rate and fair spread with target leg/convention.

## Common error envelope

Proposed fields: `code`, `message`, `fieldErrors`, `retryable`, `operationId`, `detailsRef`. Useful codes include `ValidationFailed`, `ContextChanged`, `SourceVersionUnavailable`, `UnsupportedProduct`, `UnsupportedOption`, `ProviderUnavailable`, `PermissionDenied`, `IdempotencyConflict` and `ExecutionOutcomeUnknown`.

Do not put raw company credentials, connection strings or unfiltered engine dumps in end-user errors. Authorized diagnostic artifacts may contain detailed engine messages.

## Versioning

Pin Capability contract, Workflow definition, View definition, provider/engine configuration and source data revisions per execution. Additive schema changes need compatibility tests; changed financial meaning requires a new semantic version or explicit migration. Existing Runs are not edited in place when adapters change.

## Files versus production compatibility

The JSON Schemas verify shape and limited enumerations. Cross-reference resolution, catalog compatibility, B/A case construction, authorization, curve/CSA compatibility and economic semantics require additional validators. `storage.sql` is an illustrative local persistence starting point. No example claims to reproduce company ZIP/CSV/XML formats.
