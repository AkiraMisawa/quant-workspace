#!/usr/bin/env python3
"""Validate this handoff bundle, not the future application.

Standard library checks always run. Install jsonschema>=4.23 in a suitable
environment to enable Draft 2020-12 validation and negative-schema fixtures.
Run from any directory: python3 /path/to/tools/validate_handoff.py
"""
from __future__ import annotations
import copy
import json
import math
from pathlib import Path
import re
import sqlite3
import sys

ROOT = Path(__file__).resolve().parents[1]
CHECKS: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    CHECKS.append(message)


def read(relative: str):
    return json.loads((ROOT / relative).read_text(encoding="utf-8"))


def reject(action, message: str) -> None:
    try:
        action()
    except (AssertionError, ValueError, sqlite3.IntegrityError):
        CHECKS.append(message)
        return
    raise AssertionError("Expected rejection: " + message)


def validate_xva(doc) -> None:
    ctx = doc["context"]
    csas = {c["csaId"]: c for c in doc["csas"]}
    nss = {n["nettingSetId"]: n for n in doc["nettingSets"]}
    assert len(csas) == len(doc["csas"])
    assert len(nss) == len(doc["nettingSets"])
    assert ctx["mainNettingSetId"] in nss
    assert [n["nettingSetId"] for n in nss.values() if n["isMain"]] == [ctx["mainNettingSetId"]]
    if ctx["defaultCollateralization"] == "uncollateralized":
        assert ctx["mainCsaId"] == "nocsa_" + ctx["mainMasterAgreementId"]
        assert not any(c["isMain"] for c in csas.values())
    else:
        assert ctx["mainCsaId"] in csas
        assert [c["csaId"] for c in csas.values() if c["isMain"]] == [ctx["mainCsaId"]]
    seen = set()
    for t in doc["trades"]:
        assert t["tradeInstanceId"] not in seen
        seen.add(t["tradeInstanceId"])
        assert t["nettingSetId"] in nss and t["discountCurveId"]
        if t["collateralization"] == "uncollateralized":
            assert t["csaId"] == "nocsa_" + nss[t["nettingSetId"]]["masterAgreementId"]
        else:
            assert t["csaId"] in csas
            assert csas[t["csaId"]]["masterAgreementId"] == nss[t["nettingSetId"]]["masterAgreementId"]
    credit = doc["credit"]
    if credit["cvaRecoveryOverride"] is not None:
        assert 0 <= credit["cvaRecoveryOverride"] <= 1
    calc = doc["calculation"]
    if calc["mode"] == "individualAddition":
        candidates = [t["tradeInstanceId"] for t in doc["trades"] if t["origin"] == "new" and t["includeAfter"]]
        assert candidates and len(set(calc["candidateTradeInstanceIds"])) == len(calc["candidateTradeInstanceIds"])
        assert set(candidates) == set(calc["candidateTradeInstanceIds"])
        assert all(not t["includeBefore"] for t in doc["trades"] if t["tradeInstanceId"] in candidates)
    else:
        assert not calc["candidateTradeInstanceIds"]


def validate_workflow(wf, catalog) -> None:
    caps = {(c["capabilityId"], c["version"]): c for c in catalog["capabilities"]}
    steps = {s["stepId"]: s for s in wf["steps"]}
    assert len(steps) == len(wf["steps"])
    visited, active = set(), set()
    def visit(key):
        assert key in steps and key not in active
        if key in visited:
            return
        active.add(key)
        for dep in steps[key]["dependsOn"]:
            visit(dep)
        active.remove(key)
        visited.add(key)
    for key, step in steps.items():
        visit(key)
        assert (step["capabilityId"], step["capabilityVersion"]) in caps
        cap = caps[(step["capabilityId"], step["capabilityVersion"])]
        assert set(step["bindings"]) == {p["name"] for p in cap["inputPorts"]}
        if "forEach" in step:
            assert step["itemMode"] in {"value", "outcomeValue"}
            if step["itemMode"] == "value":
                assert step["itemKeyPath"].startswith("/")
        bindings = list(step["bindings"].values()) + ([step["forEach"]] if "forEach" in step else [])
        for binding in bindings:
            if binding["kind"] == "output":
                assert binding["stepId"] in step["dependsOn"]
                parent = steps[binding["stepId"]]
                parent_cap = caps[(parent["capabilityId"], parent["capabilityVersion"])]
                assert binding["port"] in {p["name"] for p in parent_cap["outputPorts"]}
    for binding in wf["outputs"].values():
        parent = steps[binding["stepId"]]
        assert binding["port"] in {p["name"] for p in caps[(parent["capabilityId"],parent["capabilityVersion"])]["outputPorts"]}


def validate_view(view, wf, dataset) -> None:
    fields = {c["field"] for c in dataset["columns"]}
    assert len(fields) == len(dataset["columns"])
    for component in view["components"]:
        if component["data"]["kind"] == "workflowOutput":
            assert component["data"]["name"] in wf["outputs"]
        for col in component.get("columns", []):
            assert col["field"] in fields
            if "currencyField" in col:
                assert col["currencyField"] in fields
        if component["kind"] == "chart":
            assert component["xField"] in fields
            assert set(component["yFields"]) <= fields
        for action in component.get("actions", []):
            for binding in action["bindings"].values():
                assert binding["field"] in fields
    for row in dataset["rows"]:
        assert set(row) == fields
        if row["status"] == "Succeeded":
            assert math.isclose(row["after"] - row["before"], row["incremental"], abs_tol=1e-9)


def validate_sql():
    db = sqlite3.connect(":memory:")
    db.executescript((ROOT / "contracts/storage.sql").read_text())
    names = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    check(len(names) == 24, "SQLite DDL creates 24 tables")
    check(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "SQLite integrity check")
    check(db.execute("PRAGMA foreign_key_check").fetchall() == [], "SQLite foreign key check")
    now, digest = "2026-10-24T00:00:00Z", "a" * 64
    db.execute("INSERT INTO Artifact(artifact_id,storage_kind,storage_locator,media_type,sha256,byte_length,availability,created_at) VALUES(?,?,?,?,?,?,?,?)", ("a", "local", "fixture/a", "application/zip", digest, 1, "available", now))
    db.execute("INSERT INTO BaselineSnapshot VALUES(?,?,?,?,?,?,?)", ("base", "1.0.0", "{}", "{}", "a", now, "demo"))
    for rid in ["r1", "r2"]:
        db.execute("INSERT INTO AnalysisRun(run_id,owner_id,source,submission_key,accepted_input_schema_id,accepted_input_schema_version,accepted_input_json,canonicalization_version,accepted_input_hash,input_completeness,status,submitted_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)", (rid,"demo","workflow",rid,"demo.input","1.0.0","{}","fixture-v1",digest,"complete","accepted",now))
        db.execute("INSERT INTO AnalysisCase(case_id,run_id,case_key,title,case_input_schema_id,case_input_schema_version,case_input_json,case_input_hash,status) VALUES(?,?,?,?,?,?,?,?,?)", ("c"+rid,rid,"case","Fixture","demo.case","1.0.0","{}",digest,"pending"))
    db.execute("INSERT INTO CapabilityInvocation(invocation_id,run_id,step_key,capability_id,capability_version,input_schema_id,input_schema_version,input_json,input_hash,provider_key,adapter_version,status,created_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)", ("inv","r1","step/case/before","demo.cap","1.0.0","demo.input","1.0.0","{}",digest,"demo","1.0.0","pending",now))
    db.commit()
    reject(lambda: db.execute("UPDATE AnalysisRun SET accepted_input_json = ? WHERE run_id='r1'", ('{"changed":true}',)), "SQL rejects mutation of accepted input")
    reject(lambda: db.execute("INSERT INTO InvocationCase VALUES(?,?,?,?)", ("r1","inv","cr2","input")), "SQL rejects cross-Run case binding")
    reject(lambda: db.execute("DELETE FROM Artifact WHERE artifact_id='a'"), "SQL protects a referenced baseline artifact")
    db.execute("UPDATE AnalysisRun SET status='running' WHERE run_id='r1'")
    check(db.execute("SELECT status FROM AnalysisRun WHERE run_id='r1'").fetchone()[0] == "running", "SQL permits operational status update")
    db.close()


# Installed packages and build output are not the handoff's documents.
SKIPPED = {"node_modules", "bin", "obj", "dist", "test-results", "playwright-report", ".git"}


def documents(pattern):
    return [f for f in ROOT.rglob(pattern) if not SKIPPED.intersection(f.relative_to(ROOT).parts)]


def main():
    json_files = documents("*.json")
    for f in json_files:
        json.loads(f.read_text(encoding="utf-8"))
    check(True, f"Parse {len(json_files)} JSON files")
    for f in documents("*.md"):
        content = f.read_text(encoding="utf-8")
        assert len(re.findall(r"^```", content, re.M)) % 2 == 0, str(f)
        for target in re.findall(r"(?<!!)\[[^\]]*\]\(([^)]+)\)", content):
            if not re.match(r"[a-z]+:", target) and not target.startswith("#"):
                assert (f.parent / target.split("#")[0]).exists(), (f, target)
    check(True, "UTF-8, Markdown fences and local Markdown links")
    schema_status = "not run: install optional jsonschema package"
    try:
        from jsonschema import Draft202012Validator, FormatChecker
        validators = {}
        for f in (ROOT / "contracts").glob("*.schema.json"):
            schema = json.loads(f.read_text())
            Draft202012Validator.check_schema(schema)
            validators[f.name] = Draft202012Validator(schema, format_checker=FormatChecker())
        for f in (ROOT / "examples").glob("*.json"):
            doc = json.loads(f.read_text())
            if doc["documentType"] == "providerProfiles":
                continue
            name = {"xvaSubmission":"xva-submission.schema.json", "simmComparisonInput":"simm-comparison-input.schema.json"}.get(doc["documentType"], "composition.schema.json")
            validators[name].validate(doc)
        for mutation in ["invalidCollateral", "invalidRecovery"]:
            doc = read("examples/xva-uncollateralized-input.json")
            if mutation == "invalidCollateral": doc["trades"][0]["csaId"] = "INVALID"
            else: doc["credit"]["cvaRecoveryOverride"] = 1.5
            check(not validators["xva-submission.schema.json"].is_valid(doc), "Schema rejects " + mutation)
        schema_status = "passed: Draft 2020-12 schemas, examples and two negative fixtures"
        check(True, schema_status)
    except ImportError:
        pass
    for name in ["xva-final-input.json", "xva-uncollateralized-input.json"]:
        validate_xva(read("examples/"+name))
    check(True, "Collateralized and uncollateralized XVA fixture invariants")
    empty_recovery = read("examples/xva-uncollateralized-input.json")
    check(empty_recovery["credit"]["cvaRecoveryOverride"] is None and "effectiveCvaRecovery" not in empty_recovery["credit"], "Empty CVA recovery retained for analytics CDS fallback")
    zero_recovery = copy.deepcopy(empty_recovery)
    zero_recovery["credit"]["cvaRecoveryOverride"] = 0
    validate_xva(zero_recovery)
    check(zero_recovery["credit"]["cvaRecoveryOverride"] == 0, "Explicit zero recovery remains an override")
    check(all(t["csaId"] == "nocsa_MA-001" and t["discountCurveId"] == "jpy_ois" for t in empty_recovery["trades"]), "Uncollateralized fixture retains nocsa marker and configured curve")
    doc = read("examples/xva-final-input.json")
    check(doc["trades"][2]["csaId"] == "CSA-A" and doc["trades"][2]["discountCurveId"] == "CURVE-USD-MANUAL", "Manual row values remain in final input")
    bad = copy.deepcopy(doc); bad["calculation"]["candidateTradeInstanceIds"].pop()
    reject(lambda: validate_xva(bad), "Semantic validation rejects omitted After-selected candidate")
    wf, cat = read("examples/simm-comparison.workflow.json"), read("examples/capability-catalog.json")
    validate_workflow(wf, cat); check(True, "Workflow registry, DAG, dependencies, ports and map modes")
    bad = copy.deepcopy(wf); bad["steps"][0]["dependsOn"] = ["compare"]
    reject(lambda: validate_workflow(bad, cat), "Workflow cycle rejected")
    view, data = read("examples/simm-comparison.view.json"), read("examples/simm-comparison.dataset.json")
    planned = read("examples/simm-comparison.input.json")
    check({c["caseId"] for c in planned["cases"]} == {r["caseId"] for r in data["rows"]}, "Planned SIMM cases match comparison result identities")
    validate_view(view, wf, data); check(True, "View fields/actions and synthetic comparison arithmetic")
    bad = copy.deepcopy(view); bad["components"][0]["columns"][0]["field"] = "unknown"
    reject(lambda: validate_view(bad, wf, data), "Unknown dataset column rejected")
    outcomes = read("examples/simm-side-outcomes.json")["items"]
    check(all("value" not in r for r in outcomes if r["status"] != "succeeded"), "Failed outcomes have no invented numeric value")
    validate_sql()
    print(json.dumps({"status":"passed", "checks":CHECKS, "json_schema":schema_status, "sqlite_version":sqlite3.sqlite_version, "application_tests":"not run; no application is implemented"}, indent=2))


if __name__ == "__main__":
    main()
