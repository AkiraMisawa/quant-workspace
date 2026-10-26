# Persistence, normalization, and migration

Status: **PROPOSED implementation starting point**, constrained by the confirmed audit, workspace-reuse, and input-authority requirements. `../contracts/storage.sql` is an executable SQLite schema sketch, not a production-ready migration or a universal financial data model.

## 1. Storage decision

Start the local demonstrator with SQLite for metadata and a directory-backed immutable artifact store behind `IArtifactStore`. Keep the production database/storage choice open until the company deployment, concurrency, backup, and recovery requirements are confirmed. Do not place one writable SQLite file on a network share for multiple application instances.

Each piece of information has one authoritative owner:

| Information | Authoritative owner | Other representations |
| --- | --- | --- |
| Workspace draft, execution relationships, states, access metadata | Database | Client cache and diagnostic exports are projections. |
| Accepted Run input/configuration and provenance | Immutable versioned database record in this starter | Exported JSON is a derived diagnostic representation. If later moved into artifacts, store only the authoritative artifact reference rather than two editable copies. |
| Native provider ZIPs, CRIF, raw replies, large result payloads | Exactly one canonical artifact location | Work folders/download bundles are disposable materializations. |
| Artifact identity, role, hash, location, schema, retention links | Database | Storage object metadata may help integrity checks but does not replace execution relationships. |

Database plus artifact storage is not two independent truths when they own different things. A database BLOB store is an alternative implementation of `IArtifactStore`; using it does not require storing a second canonical filesystem copy. Backups and replicas have explicit recovery roles rather than independent editing authority.

## 2. Separate mutable work from immutable evidence

### Workspace

A Workspace is an editable draft with a revision and typed state. A browser session can reference it, but the browser session does not own its data. New trades are imported before any Run exists.

`WorkspaceTrade` binds a row-level `TradeInstanceId` to an immutable `TradeInputId` and mutable row configuration. Reloading the same booking ID follows an explicit duplicate policy; booking ID alone is not a content version.

In this SQL sketch, `Workspace.draft_json` owns non-row draft configuration; `WorkspaceTrade.row_json` owns the mutable trade rows. Do not independently edit a second complete row collection inside `draft_json`. The service assembles the full typed workspace from these owners. A cached full draft, if introduced, is a rebuildable projection with an explicit revision, not another authority.

### BaselineSnapshot

A snapshot fixes context, source revisions, retrieval metadata, and an original-data artifact. Main context changes select/create a new snapshot; they do not overwrite the old one. The old snapshot remains while referenced by a workspace or retained Run.

A multi-case Run can use several baselines. `CaseBaselineSnapshot` expresses these links. A single mandatory baseline column on Run would incorrectly constrain comparisons across counterparties.

### TradeInput

TradeInput fixes the imported economic input and source provenance. Its original/native payload may be opaque to the generic runtime. The business module defines the typed editable fields and validated translation into engine input.

Refreshing a booking draft creates a new TradeInput version. Existing Runs remain bound to their prior version. A calculation completing does not convert a draft into a booked trade and does not replace Original with After.

### Run and Case

Store the complete accepted typed business submission immutably. Store final case inputs/references after all required selections have been resolved. Retain user edit provenance separately; it explains how the input was reached but is not used to silently reconstruct different pricing input.

`CaseTradeInput` freezes each case's trade input references, B/A membership, and effective row configuration. It deliberately has no live foreign-key dependency on the mutable WorkspaceTrade row.

## 3. What is normalized, and what remains JSON?

Normalize stable identities and relationships that must be queried or constrained:

- Workspace to imported trade instances.
- Run to Cases, invocations, dependencies, requests, attempts, and results.
- Cases to their baselines, trade inputs, and provider item bindings.
- Requests to their native input/output artifacts and correlation mappings.
- Retained owners to artifact references.

Versioned JSON is appropriate for capability-specific financial configurations, accepted input snapshots, view definitions, and provider diagnostics. The generic platform should not acquire tables for every CSA attribute, product-specific option, or in-house model parameter merely to claim normalization.

The distinction is deliberate: relational integrity covers lifecycle and identity; a schema validator covers typed business payloads. A JSON field is not a license to accept unvalidated arbitrary objects. Record its schema ID/version and validate before accepting it.

For heavily used history filters, add explicit indexed projection columns or dedicated read models. Derive them from immutable accepted input and document the rebuild procedure. Do not make those projections a second editable source.

### Examples of avoided duplication

- Do not copy the same original ZIP into every Run folder; reference the immutable artifact.
- Do not overwrite a TradeInput row when a booking draft changes; create a new version.
- Do not store the counterparty autocomplete display string as the only identity. Preserve the registration/relationship identity including the legal entity.
- Do not infer a request's role from a filename. Store side, configuration, scenario, and bindings.
- Do not duplicate the full raw response inside several database columns and files as independently mutable data. Store an artifact reference and a typed derived result.

### Normalization in this particular model

An initial flat record might store a Run plus `request_zip_1`, `request_zip_2`, repeated trade descriptions and repeated counterparty display names. Adding another analytics call changes its columns, and correcting a source description can create inconsistent copies. Represent repeatable calls as AnalyticsRequest rows and preserve relationships through keys instead.

- **First normal form:** use repeatable child rows for requests/case bindings, instead of numbered repeating columns or comma-separated IDs that must be searched as text.
- **Second normal form:** in a relation with a composite key, facts belong to the whole relationship. For example, source trade content belongs to TradeInputId; it is not a new fact for every `(CaseId, TradeInstanceId)` binding. The case binding stores its own inclusion/configuration and references that input.
- **Third normal form:** keep facts determined by another entity with that entity. An analytics request references its invocation; a mutable copy of the provider's descriptive configuration on every request is not its master record. Immutable historical execution configuration, however, is intentionally captured evidence, not a competing mutable master.

Useful dependencies are `TradeInputId -> immutable source version/content`, `AnalyticsRequestId -> fixed logical input and invocation`, and `(endpoint scope, correlation ID) -> logical request`. Counterparty code alone does not determine the selected legal-entity registration.

Versioned JSON deliberately preserves bounded financial documents and immutable historical snapshots. The package does not claim that every JSON member is fully normalized relationally, or that third normal form requires putting ZIP bytes in a database. Query patterns, integrity, immutability and a clear owner for each fact determine the practical split.

## 4. Artifact lifecycle and consistency

Suggested write protocol:

1. Write bytes to a new temporary object/path while computing the exact byte hash and length.
2. Complete the storage write and verify the expected length/hash.
3. Atomically publish the immutable object where the backend supports it, or finish its committed object upload.
4. In the database, insert the available Artifact and its owning references, then mark execution work ready.
5. If the database transaction fails, the unreferenced artifact is an orphan eligible for delayed cleanup. If a referenced artifact is missing, fail integrity checks rather than returning an empty payload.

This is not a cross-store ACID transaction. Define orphan cleanup, write retries, and crash recovery. Never dispatch a request whose input artifact is still incomplete. An artifact's hash identifies bytes, not business equality; native ZIP serialization may differ for semantically identical inputs.

Retention uses reachability from retained Runs, active/restorable Workspaces, pinned studies, and other protected owners. It must account for shared baseline/trade/result references. Cleanup removes unreferenced objects only after the retention policy and grace period allow it. Deleting a UI view must not delete data still used by a workflow or another view.

Store immutable result payloads as artifacts and expose typed ResultDataset references. A result can be intermediate and have no view. A SavedView references result IDs; editing its presentation does not modify the original dataset.

## 5. Diagnostic retrieval by Run ID

Implement a service such as `GetRunDiagnosticsAsync(runId)` that resolves all available relationships and generates a manifest/bundle. Treat a generated ZIP folder layout as an export format, not as the database schema.

The manifest records:

- Public Run ID, source, user, workspace revision, and timestamps.
- Cases and frozen input/provenance completeness.
- Snapshot and TradeInput references.
- Invocation graph and case/provider-item bindings.
- Request/attempt identifiers, correlation mappings, state, and errors.
- Artifact roles, byte hashes, sizes, and missing/unavailable status.
- Provider/adapter/engine/model/data versions where known.

Enforce permission checks in the service and artifact retrieval endpoints, including downloads initiated from AI or result-row actions. A Run ID is a lookup key, not authorization.

## 6. Legacy folders: index first, migrate safely

**CONFIRMED requirement:** developers must continue to find historical requests/responses by the user's Run ID. It does not require physically relocating every historical file before the new UI can ship.

### Phase A — read-only inventory

Index existing session/run folder locations and artifact roles. Preserve legacy Run ID, session ID, source server/environment, relative folder locator, observed file names, lengths, and hashes where feasible. A `LegacyLocator` maps legacy lookup to a new history record. Old files remain read-only in their original storage.

Do not fabricate audit evidence. The current system may have overwritten Original or persisted only effective Before/After data. If the exact original or sequence of user changes was not retained, mark it `legacy_partial` or `unavailable`. A computed diff is inferred provenance and must be labelled as such; it cannot be presented as the user's recorded intent.

### Phase B — dual visibility, single owner

The new history/query service reads both indexed legacy Runs and new Runs. New execution writes through the new persistence services only. Existing production pricer behavior continues during the developer-only release.

If old and new execution paths coexist, route replies and artifacts explicitly. Do not introduce a second consumer on the same reply queue without an ownership/routing design.

### Phase C — optional verified relocation

Copy an artifact to its new location, verify byte length/hash, and atomically update the authoritative locator. During migration, a verified copy is a transfer replica; metadata still identifies one authoritative location. Retain the old location for a controlled rollback period, then remove it under the agreed retention procedure.

Resume by artifact ID/manifest entry so interruption does not create duplicate history. Never remove the source merely because a copy command completed.

## 7. SQLite sketch: scope and implementation notes

The included DDL creates 24 tables plus indexes and immutability guards. It uses TEXT identifiers, UTC timestamp strings, foreign keys, CHECK constraints, and SQLite JSON functions. Confirm JSON-function support (`SELECT json_valid('{}')`) before applying it. Enable `PRAGMA foreign_keys = ON` on every connection, not just in the migration.

This combined demonstrator schema contains generic runtime tables and private pricer-domain extensions. In particular, BaselineSnapshot, TradeInput, WorkspaceTrade, CaseBaselineSnapshot, and CaseTradeInput belong to the business module; their financial meaning must not be hard-coded into the OSS composition core. The production company integration adds its own policy and provider metadata privately.

`CaseTradeInput` membership/configuration and `ViewResultBinding` are relational integrity/retention projections of the corresponding immutable case input or view definition. Maintain them in the same transaction and validate consistency; they are not independently editable copies of economic input.

It is a deliberately explicit sketch. Before production use, provide migrations, transactions, busy handling, backups/restores, tenant/user authorization integration, retention jobs, canonical serialization, lease/dispatch recovery, and schema validation. It has no generated financial schemas and does not validate business semantics by itself.

Cross-row invariants that the application/runtime must enforce include:

1. Bound cases, invocations, requests, results, and views are authorized and belong to compatible Runs/contexts.
2. The invocation dependency graph is acyclic; all required typed bindings are satisfied.
3. Accepted JSON conforms to its declared schema and matches its server-computed canonical hash.
4. A reply can transition only the correlated request, with duplicate/conflicting terminal outcomes handled explicitly.
5. Status transitions and cancellation semantics are legal and aggregated consistently.
6. An artifact marked available has verified bytes; referenced input/output artifacts have the expected media/schema.
7. A pending dispatch is claimed with a lease/transaction before transport work.
8. Provider bulk output is mapped through explicit provider-item keys to the correct Cases.
9. Complete new Runs have the required input metadata; legacy partial records never masquerade as complete.
10. Canonical artifacts cannot be removed while protected references remain.

The SQL is a starting model for implementation review. Evolve it through versioned migrations rather than relying on `CREATE TABLE IF NOT EXISTS` as a migration strategy.
