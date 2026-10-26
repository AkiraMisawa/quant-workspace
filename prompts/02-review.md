Independently review the implementation against this handoff. Read `docs/00-decisions.md`, the relevant domain documents, `docs/11-acceptance.md`, and `IMPLEMENTATION_STATUS.md` before reviewing code.

Prioritize concrete defects and unsupported completion claims. Inspect these boundaries:

1. Final committed XVA input survives main-following and later manual edits, active editors, stale downloads, validation and server translation.
2. Generic composition has no XVA/CSA/QuantLib/company policy dependency; Workflow and View are independent; retained intermediates and unsaved rebind work.
3. Counterparty registration identity includes legal entity; B/A membership is dataset-wide; iterative addition is non-cumulative.
4. Draft inputs and immutable baselines survive workspace reuse, while prior Runs cannot change.
5. Job/request/attempt/correlation identity survives restarts and duplicate/late responses; missing results do not become zero.
6. Live Rates has bounded work, correct input/market revisions, coherent capture and no Run per tick.
7. Provider/source replacement leaves UI and kernel unchanged; Web has no native dependency; company-only packages are absent from standalone builds.
8. The actual ExGrid is used. Keyboard, touch and target-browser claims have real evidence.
9. AI calls registered application services, resolves material ambiguity and uses validated view definitions. Scripted UI text is not reported as real agent integration.
10. Existing company bulk/HPC execution is reused, with explicit case mapping and no accidental duplicate scheduler.

Run the smallest relevant checks that resolve concrete risks. Report findings by severity with file locations, failing scenarios and recommended fixes. Separate verified defects from open product decisions. Fix authorized reversible defects if implementation work is in scope, then recheck. Do not add unrelated frameworks or expand the product scope during review.
