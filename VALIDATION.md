# Handoff validation

Validated on 2026-10-24. This report concerns the **documentation package**, its starter contracts/examples and its HTML reader. No application implementation, real pricing engine, real AI runtime or company connection is included or claimed to be tested.

## Automated document and contract checks

- Parsed all 12 JSON files.
- Checked three Draft 2020-12 schemas and their applicable examples using Python jsonschema 4.23.0. Provider profile configuration is illustrative JSON and is not covered by those three schemas.
- Verified collateralized and uncollateralized XVA example relationships and the derived individual-addition candidate set.
- Verified final manual CSA/curve values remain in the submitted example.
- Verified empty CVA recovery remains empty for the analytics' CDS fallback, while explicit zero remains an override.
- Verified the uncollateralized example uses `nocsa_MA-001` and the configured `jpy_ois` curve.
- Checked registry IDs, workflow DAG/dependencies/ports, map modes and planned-versus-result case identity.
- Checked view field/action bindings and synthetic comparison arithmetic.
- Checked failure outcomes contain no invented numeric value.
- Exercised negative fixtures for invalid recovery/CSA shape, omitted selected candidates, a workflow cycle and an unknown displayed column.
- Checked UTF-8 text, Markdown code fences and local Markdown links.

These checks validate the bounded examples; they are not a complete financial semantic validator or an implemented workflow runtime.

## SQLite sketch

Executed `contracts/storage.sql` in an in-memory SQLite 3.53.1 database:

- 24 tables created successfully.
- `PRAGMA integrity_check` returned `ok`.
- `PRAGMA foreign_key_check` returned no issues.
- Accepted input mutation was rejected.
- Cross-Run case binding was rejected.
- A referenced baseline artifact could not be deleted.
- An operational status update was permitted.

The schema remains an illustrative starting point. Production migrations, authorization, recovery and operational storage are future implementation work.

## HTML reader

Headless Chromium checks passed at:

| Viewport | Input configuration | Result |
|---|---|---|
| 1440 × 1000 | Desktop | Passed |
| 390 × 844 | Mobile layout with touch emulation | Passed |
| 320 × 740 | Narrow mobile layout with touch emulation | Passed |

Checked page overflow, search/clear, light/dark toggle, native chapter opening, open/close-all, keyboard search focus, internal anchors, script errors and external network requests. There was no page-level horizontal overflow, no broken internal anchors, no script errors and no external network request. Wide tables scroll within their own containers.

Also verified native chapter reading with JavaScript disabled. Desktop, dark and mobile screenshots were visually inspected. The HTML contains its font and diagrams and has no Mermaid/CDN dependency.

These are **document-reader** checks in Chromium, not physical iPhone/Safari testing and not verification of ExGrid's mobile support.

## Reproduce the portable checks

Run `python3 tools/validate_handoff.py`. Standard-library document/relationship/SQLite checks run without additional packages. To include JSON Schema checks, use a Python environment with `jsonschema>=4.23` installed. The script explicitly reports when the optional schema validation was not run.

## Review corrections incorporated

Independent design reviews identified and corrected generic-versus-domain dataset metadata, mapped success/error propagation, stable expanded invocation keys, optional collateral handling and the existing .NET8/.NET10 host-compatibility gate. The owner's latest clarification then superseded the draft recovery/no-CSA representation and was applied consistently to documents, schemas, fixtures and acceptance criteria.
