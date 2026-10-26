-- Quant Workbench: illustrative local-demo SQLite schema, revision 1.
-- Design scaffold; not a complete production migration or authorization model.
-- Requires SQLite JSON functions. Enable foreign_keys on EVERY connection.
-- All timestamps are application-written UTC ISO-8601 strings.
-- This demonstrator schema includes both generic runtime tables and PRIVATE
-- pricer-domain extensions (BaselineSnapshot, TradeInput, WorkspaceTrade,
-- CaseBaselineSnapshot, CaseTradeInput). Do not publish the whole schema as OSS.
PRAGMA foreign_keys = ON;
BEGIN;

CREATE TABLE SchemaVersion (
    version INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at TEXT NOT NULL
);

CREATE TABLE Artifact (
    artifact_id TEXT PRIMARY KEY,
    storage_kind TEXT NOT NULL CHECK (storage_kind IN ('local','object','database','legacy')),
    storage_locator TEXT NOT NULL,
    media_type TEXT NOT NULL,
    schema_id TEXT,
    schema_version TEXT,
    sha256 TEXT CHECK (sha256 IS NULL OR length(sha256) = 64),
    byte_length INTEGER CHECK (byte_length IS NULL OR byte_length >= 0),
    availability TEXT NOT NULL CHECK (availability IN ('available','legacy_unverified','missing','quarantined')),
    created_at TEXT NOT NULL,
    CHECK (availability <> 'available' OR (sha256 IS NOT NULL AND byte_length IS NOT NULL)),
    UNIQUE (storage_kind, storage_locator)
);
CREATE INDEX ix_artifact_hash ON Artifact(sha256);

CREATE TABLE BaselineSnapshot (
    baseline_snapshot_id TEXT PRIMARY KEY,
    context_schema_version TEXT NOT NULL,
    context_json TEXT NOT NULL CHECK (json_valid(context_json)),
    source_versions_json TEXT NOT NULL CHECK (json_valid(source_versions_json)),
    original_artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    retrieved_at TEXT NOT NULL,
    created_by TEXT NOT NULL
);

CREATE TABLE TradeInput (
    trade_input_id TEXT PRIMARY KEY,
    source_system TEXT NOT NULL,
    source_key TEXT,
    source_revision TEXT,
    schema_id TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    original_artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    metadata_json TEXT NOT NULL CHECK (json_valid(metadata_json)),
    imported_at TEXT NOT NULL,
    imported_by TEXT NOT NULL
);
CREATE INDEX ix_trade_input_source ON TradeInput(source_system, source_key, source_revision);

CREATE TABLE Workspace (
    workspace_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    baseline_snapshot_id TEXT REFERENCES BaselineSnapshot(baseline_snapshot_id),
    draft_schema_version TEXT NOT NULL,
    draft_json TEXT NOT NULL CHECK (json_valid(draft_json)),
    lifecycle TEXT NOT NULL CHECK (lifecycle IN ('active','archived','expired')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX ix_workspace_owner ON Workspace(owner_id, lifecycle, updated_at);

CREATE TABLE WorkspaceTrade (
    workspace_id TEXT NOT NULL REFERENCES Workspace(workspace_id),
    trade_instance_id TEXT NOT NULL,
    trade_input_id TEXT NOT NULL REFERENCES TradeInput(trade_input_id),
    sort_position INTEGER NOT NULL,
    row_schema_version TEXT NOT NULL,
    row_json TEXT NOT NULL CHECK (json_valid(row_json)),
    PRIMARY KEY (workspace_id, trade_instance_id)
);
CREATE INDEX ix_workspace_trade_input ON WorkspaceTrade(trade_input_id);

CREATE TABLE AnalysisGroup (
    analysis_group_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    title TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE AnalysisRun (
    run_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    workspace_id TEXT REFERENCES Workspace(workspace_id),
    workspace_revision INTEGER CHECK (workspace_revision IS NULL OR workspace_revision >= 0),
    analysis_group_id TEXT REFERENCES AnalysisGroup(analysis_group_id),
    parent_run_id TEXT REFERENCES AnalysisRun(run_id),
    source TEXT NOT NULL CHECK (source IN ('fixed_xva','rates_manual','rates_capture','ai','workflow','legacy')),
    submission_key TEXT,
    accepted_input_schema_id TEXT,
    accepted_input_schema_version TEXT,
    accepted_input_json TEXT CHECK (accepted_input_json IS NULL OR json_valid(accepted_input_json)),
    canonicalization_version TEXT,
    accepted_input_hash TEXT CHECK (accepted_input_hash IS NULL OR length(accepted_input_hash) = 64),
    edit_provenance_json TEXT CHECK (edit_provenance_json IS NULL OR json_valid(edit_provenance_json)),
    input_completeness TEXT NOT NULL CHECK (input_completeness IN ('complete','legacy_partial','unavailable')),
    status TEXT NOT NULL CHECK (status IN ('accepted','running','succeeded','partially_succeeded','failed','cancel_requested','cancelled','unknown')),
    submitted_at TEXT NOT NULL,
    completed_at TEXT,
    CHECK (input_completeness <> 'complete' OR (
        accepted_input_schema_id IS NOT NULL AND accepted_input_schema_version IS NOT NULL
        AND accepted_input_json IS NOT NULL AND accepted_input_hash IS NOT NULL
        AND canonicalization_version IS NOT NULL)),
    CHECK (source = 'legacy' OR (submission_key IS NOT NULL AND input_completeness = 'complete')),
    UNIQUE (owner_id, submission_key)
);
CREATE INDEX ix_run_history ON AnalysisRun(owner_id, submitted_at DESC);
CREATE INDEX ix_run_workspace ON AnalysisRun(workspace_id, submitted_at DESC);
CREATE INDEX ix_run_group ON AnalysisRun(analysis_group_id, submitted_at);
CREATE INDEX ix_run_status ON AnalysisRun(status, submitted_at);

CREATE TABLE AnalysisCase (
    case_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES AnalysisRun(run_id),
    case_key TEXT NOT NULL,
    title TEXT NOT NULL,
    case_input_schema_id TEXT NOT NULL,
    case_input_schema_version TEXT NOT NULL,
    case_input_json TEXT NOT NULL CHECK (json_valid(case_input_json)),
    case_input_hash TEXT NOT NULL CHECK (length(case_input_hash) = 64),
    status TEXT NOT NULL CHECK (status IN ('pending','running','succeeded','failed','blocked','cancel_requested','cancelled','unknown')),
    error_json TEXT CHECK (error_json IS NULL OR json_valid(error_json)),
    UNIQUE (run_id, case_key),
    UNIQUE (run_id, case_id)
);

CREATE TABLE CaseBaselineSnapshot (
    case_id TEXT NOT NULL REFERENCES AnalysisCase(case_id),
    baseline_snapshot_id TEXT NOT NULL REFERENCES BaselineSnapshot(baseline_snapshot_id),
    binding_role TEXT NOT NULL,
    PRIMARY KEY (case_id, baseline_snapshot_id, binding_role)
);
CREATE INDEX ix_case_baseline ON CaseBaselineSnapshot(baseline_snapshot_id);

CREATE TABLE CaseTradeInput (
    case_id TEXT NOT NULL REFERENCES AnalysisCase(case_id),
    trade_instance_id TEXT NOT NULL,
    trade_input_id TEXT NOT NULL REFERENCES TradeInput(trade_input_id),
    include_before INTEGER NOT NULL CHECK (include_before IN (0,1)),
    include_after INTEGER NOT NULL CHECK (include_after IN (0,1)),
    effective_row_json TEXT NOT NULL CHECK (json_valid(effective_row_json)),
    PRIMARY KEY (case_id, trade_instance_id)
);
CREATE INDEX ix_case_trade_input ON CaseTradeInput(trade_input_id);

CREATE TABLE CapabilityInvocation (
    invocation_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES AnalysisRun(run_id),
    step_key TEXT NOT NULL,
    capability_id TEXT NOT NULL,
    capability_version TEXT NOT NULL,
    input_schema_id TEXT NOT NULL,
    input_schema_version TEXT NOT NULL,
    input_json TEXT NOT NULL CHECK (json_valid(input_json)),
    input_hash TEXT NOT NULL CHECK (length(input_hash) = 64),
    provider_key TEXT NOT NULL,
    adapter_version TEXT NOT NULL,
    provider_execution_id TEXT,
    engine_version TEXT,
    model_versions_json TEXT CHECK (model_versions_json IS NULL OR json_valid(model_versions_json)),
    status TEXT NOT NULL CHECK (status IN ('pending','running','succeeded','failed','blocked','cancel_requested','cancelled','unknown')),
    created_at TEXT NOT NULL,
    completed_at TEXT,
    error_json TEXT CHECK (error_json IS NULL OR json_valid(error_json)),
    UNIQUE (run_id, step_key),
    UNIQUE (run_id, invocation_id)
);

CREATE TABLE InvocationCase (
    run_id TEXT NOT NULL,
    invocation_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    binding_role TEXT NOT NULL,
    PRIMARY KEY (invocation_id, case_id, binding_role),
    FOREIGN KEY (run_id, invocation_id) REFERENCES CapabilityInvocation(run_id, invocation_id),
    FOREIGN KEY (run_id, case_id) REFERENCES AnalysisCase(run_id, case_id)
);

CREATE TABLE InvocationDependency (
    run_id TEXT NOT NULL,
    predecessor_invocation_id TEXT NOT NULL,
    successor_invocation_id TEXT NOT NULL,
    binding_json TEXT NOT NULL CHECK (json_valid(binding_json)),
    PRIMARY KEY (predecessor_invocation_id, successor_invocation_id),
    CHECK (predecessor_invocation_id <> successor_invocation_id),
    FOREIGN KEY (run_id, predecessor_invocation_id) REFERENCES CapabilityInvocation(run_id, invocation_id),
    FOREIGN KEY (run_id, successor_invocation_id) REFERENCES CapabilityInvocation(run_id, invocation_id)
);
CREATE INDEX ix_dependency_successor ON InvocationDependency(successor_invocation_id);

CREATE TABLE AnalyticsRequest (
    analytics_request_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    invocation_id TEXT NOT NULL,
    logical_key TEXT NOT NULL,
    input_schema_version TEXT NOT NULL,
    effective_input_json TEXT NOT NULL CHECK (json_valid(effective_input_json)),
    effective_input_hash TEXT NOT NULL CHECK (length(effective_input_hash) = 64),
    -- Side/configuration/scenario metadata is module-provided, not interpreted by generic runtime.
    request_role TEXT NOT NULL,
    native_input_artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    native_output_artifact_id TEXT REFERENCES Artifact(artifact_id),
    status TEXT NOT NULL CHECK (status IN ('prepared','dispatch_pending','submitted','running','succeeded','failed','cancel_requested','cancelled','unknown')),
    created_at TEXT NOT NULL,
    completed_at TEXT,
    error_json TEXT CHECK (error_json IS NULL OR json_valid(error_json)),
    FOREIGN KEY (run_id, invocation_id) REFERENCES CapabilityInvocation(run_id, invocation_id),
    UNIQUE (invocation_id, logical_key),
    UNIQUE (run_id, analytics_request_id)
);
CREATE INDEX ix_request_status ON AnalyticsRequest(status, created_at);

CREATE TABLE RequestCaseBinding (
    run_id TEXT NOT NULL,
    analytics_request_id TEXT NOT NULL,
    case_id TEXT NOT NULL,
    provider_item_key TEXT NOT NULL,
    binding_role TEXT NOT NULL,
    PRIMARY KEY (analytics_request_id, case_id, binding_role),
    FOREIGN KEY (run_id, analytics_request_id) REFERENCES AnalyticsRequest(run_id, analytics_request_id),
    FOREIGN KEY (run_id, case_id) REFERENCES AnalysisCase(run_id, case_id),
    UNIQUE (analytics_request_id, provider_item_key, binding_role)
);

CREATE TABLE CorrelationBinding (
    endpoint_scope TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    analytics_request_id TEXT NOT NULL REFERENCES AnalyticsRequest(analytics_request_id),
    created_at TEXT NOT NULL,
    PRIMARY KEY (endpoint_scope, correlation_id),
    UNIQUE (endpoint_scope, correlation_id, analytics_request_id)
);
CREATE INDEX ix_correlation_request ON CorrelationBinding(analytics_request_id);

CREATE TABLE DispatchAttempt (
    attempt_id TEXT PRIMARY KEY,
    analytics_request_id TEXT NOT NULL REFERENCES AnalyticsRequest(analytics_request_id),
    attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
    endpoint_scope TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    transport_message_id TEXT,
    status TEXT NOT NULL CHECK (status IN ('pending','sending','accepted','rejected','unknown')),
    lease_owner TEXT,
    lease_expires_at TEXT,
    created_at TEXT NOT NULL,
    sent_at TEXT,
    acknowledged_at TEXT,
    error_json TEXT CHECK (error_json IS NULL OR json_valid(error_json)),
    FOREIGN KEY (endpoint_scope, correlation_id, analytics_request_id)
        REFERENCES CorrelationBinding(endpoint_scope, correlation_id, analytics_request_id),
    UNIQUE (analytics_request_id, attempt_number)
);
CREATE INDEX ix_dispatch_ready ON DispatchAttempt(status, lease_expires_at, created_at);

CREATE TABLE InboxMessage (
    inbox_message_id TEXT PRIMARY KEY,
    endpoint_scope TEXT NOT NULL,
    deduplication_key TEXT NOT NULL,
    correlation_id TEXT,
    analytics_request_id TEXT REFERENCES AnalyticsRequest(analytics_request_id),
    payload_artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    received_at TEXT NOT NULL,
    processing_status TEXT NOT NULL CHECK (processing_status IN ('received','matched','processed','quarantined')),
    processed_at TEXT,
    error_json TEXT CHECK (error_json IS NULL OR json_valid(error_json)),
    UNIQUE (endpoint_scope, deduplication_key)
);
CREATE INDEX ix_inbox_correlation ON InboxMessage(endpoint_scope, correlation_id);
CREATE INDEX ix_inbox_pending ON InboxMessage(processing_status, received_at);

CREATE TABLE ResultDataset (
    result_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES AnalysisRun(run_id),
    invocation_id TEXT,
    result_key TEXT NOT NULL,
    result_kind TEXT NOT NULL CHECK (result_kind IN ('tabular','scalar','structured','document','trade_set','risk_cube','crif')),
    schema_id TEXT NOT NULL,
    schema_version TEXT NOT NULL,
    payload_artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    metadata_json TEXT NOT NULL CHECK (json_valid(metadata_json)),
    row_count INTEGER CHECK (row_count IS NULL OR row_count >= 0),
    created_at TEXT NOT NULL,
    FOREIGN KEY (run_id, invocation_id) REFERENCES CapabilityInvocation(run_id, invocation_id),
    UNIQUE (run_id, result_key)
);

-- Optional auxiliary diagnostics. Native request/output and result payload references above
-- already have their authoritative role; do not duplicate those bindings here.
CREATE TABLE ArtifactLink (
    artifact_link_id TEXT PRIMARY KEY,
    artifact_id TEXT NOT NULL REFERENCES Artifact(artifact_id),
    artifact_role TEXT NOT NULL,
    run_id TEXT REFERENCES AnalysisRun(run_id),
    invocation_id TEXT REFERENCES CapabilityInvocation(invocation_id),
    analytics_request_id TEXT REFERENCES AnalyticsRequest(analytics_request_id),
    attempt_id TEXT REFERENCES DispatchAttempt(attempt_id),
    CHECK ((run_id IS NOT NULL) + (invocation_id IS NOT NULL)
        + (analytics_request_id IS NOT NULL) + (attempt_id IS NOT NULL) = 1)
);
CREATE UNIQUE INDEX ux_artifact_run_link ON ArtifactLink(artifact_id, artifact_role, run_id) WHERE run_id IS NOT NULL;
CREATE UNIQUE INDEX ux_artifact_invocation_link ON ArtifactLink(artifact_id, artifact_role, invocation_id) WHERE invocation_id IS NOT NULL;
CREATE UNIQUE INDEX ux_artifact_request_link ON ArtifactLink(artifact_id, artifact_role, analytics_request_id) WHERE analytics_request_id IS NOT NULL;
CREATE UNIQUE INDEX ux_artifact_attempt_link ON ArtifactLink(artifact_id, artifact_role, attempt_id) WHERE attempt_id IS NOT NULL;

CREATE TABLE SavedView (
    view_id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL,
    view_revision INTEGER NOT NULL CHECK (view_revision > 0),
    definition_schema_version TEXT NOT NULL,
    definition_json TEXT NOT NULL CHECK (json_valid(definition_json)),
    result_bindings_json TEXT NOT NULL CHECK (json_valid(result_bindings_json)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

-- Relational retention/index projection of the view's typed result references.
-- Update bindings transactionally with SavedView; validate against its JSON.
CREATE TABLE ViewResultBinding (
    view_id TEXT NOT NULL REFERENCES SavedView(view_id),
    binding_name TEXT NOT NULL,
    result_id TEXT NOT NULL REFERENCES ResultDataset(result_id),
    PRIMARY KEY (view_id, binding_name)
);
CREATE INDEX ix_view_result_retention ON ViewResultBinding(result_id);

CREATE TABLE LegacyLocator (
    legacy_locator_id TEXT PRIMARY KEY,
    run_id TEXT REFERENCES AnalysisRun(run_id),
    source_instance TEXT NOT NULL,
    legacy_session_id TEXT,
    legacy_run_id TEXT NOT NULL,
    folder_locator TEXT NOT NULL,
    inventory_artifact_id TEXT REFERENCES Artifact(artifact_id),
    migration_status TEXT NOT NULL CHECK (migration_status IN ('indexed','copying','verified','migrated','missing')),
    discovered_at TEXT NOT NULL,
    UNIQUE (source_instance, legacy_run_id),
    UNIQUE (source_instance, folder_locator)
);
CREATE INDEX ix_legacy_run_lookup ON LegacyLocator(legacy_run_id);

-- Immutability examples. Retention deletion is a separate authorized operation;
-- foreign keys prevent removal while referenced. Additional business projections must
-- be updated in the same transaction and tested against the accepted snapshot.
CREATE TRIGGER baseline_snapshot_immutable
BEFORE UPDATE ON BaselineSnapshot
BEGIN SELECT RAISE(ABORT, 'BaselineSnapshot is immutable; create a new snapshot'); END;

CREATE TRIGGER trade_input_immutable
BEFORE UPDATE ON TradeInput
BEGIN SELECT RAISE(ABORT, 'TradeInput is immutable; import a new version'); END;

CREATE TRIGGER accepted_run_input_immutable
BEFORE UPDATE OF accepted_input_schema_id, accepted_input_schema_version,
    accepted_input_json, canonicalization_version, accepted_input_hash,
    edit_provenance_json, input_completeness, workspace_revision ON AnalysisRun
WHEN NEW.accepted_input_schema_id IS NOT OLD.accepted_input_schema_id
    OR NEW.accepted_input_schema_version IS NOT OLD.accepted_input_schema_version
    OR NEW.accepted_input_json IS NOT OLD.accepted_input_json
    OR NEW.canonicalization_version IS NOT OLD.canonicalization_version
    OR NEW.accepted_input_hash IS NOT OLD.accepted_input_hash
    OR NEW.edit_provenance_json IS NOT OLD.edit_provenance_json
    OR NEW.input_completeness IS NOT OLD.input_completeness
    OR NEW.workspace_revision IS NOT OLD.workspace_revision
BEGIN SELECT RAISE(ABORT, 'Accepted Run input and provenance are immutable'); END;

CREATE TRIGGER case_input_immutable
BEFORE UPDATE OF run_id, case_key, case_input_schema_id,
    case_input_schema_version, case_input_json, case_input_hash ON AnalysisCase
WHEN NEW.run_id IS NOT OLD.run_id OR NEW.case_key IS NOT OLD.case_key
    OR NEW.case_input_schema_id IS NOT OLD.case_input_schema_id
    OR NEW.case_input_schema_version IS NOT OLD.case_input_schema_version
    OR NEW.case_input_json IS NOT OLD.case_input_json
    OR NEW.case_input_hash IS NOT OLD.case_input_hash
BEGIN SELECT RAISE(ABORT, 'Accepted Case input is immutable'); END;

CREATE TRIGGER case_trade_input_immutable
BEFORE UPDATE ON CaseTradeInput
BEGIN SELECT RAISE(ABORT, 'CaseTradeInput is immutable'); END;

CREATE TRIGGER request_input_immutable
BEFORE UPDATE OF run_id, invocation_id, logical_key, input_schema_version,
    effective_input_json, effective_input_hash, request_role,
    native_input_artifact_id ON AnalyticsRequest
WHEN NEW.run_id IS NOT OLD.run_id OR NEW.invocation_id IS NOT OLD.invocation_id
    OR NEW.logical_key IS NOT OLD.logical_key
    OR NEW.input_schema_version IS NOT OLD.input_schema_version
    OR NEW.effective_input_json IS NOT OLD.effective_input_json
    OR NEW.effective_input_hash IS NOT OLD.effective_input_hash
    OR NEW.request_role IS NOT OLD.request_role
    OR NEW.native_input_artifact_id IS NOT OLD.native_input_artifact_id
BEGIN SELECT RAISE(ABORT, 'AnalyticsRequest input is immutable; create a new request'); END;

CREATE TRIGGER result_dataset_immutable
BEFORE UPDATE ON ResultDataset
BEGIN SELECT RAISE(ABORT, 'ResultDataset is immutable; create a derived result'); END;

INSERT INTO SchemaVersion(version, description, applied_at)
VALUES (1, 'Illustrative Quant Workbench local schema', '2026-10-24T00:00:00Z');

COMMIT;
