# Start here

Prepared 2026-10-24. Working name: **Quant Workbench**. This name is a placeholder, not a branding decision.

## Objective

Build a platform in which users can combine versioned quant/data capabilities, reuse workflows, and attach new input/result views without adding a bespoke Razor page for every analysis. A fixed XVA pricer and a live Rates pricer provide fast expert workflows. An AI workspace can resolve context, propose a valid workflow, ask for missing economically material choices, execute through the same services, and render structured results with ExGrid.

The main architectural asset is the independence of **Capability / Workflow / View / Context**, together with typed intermediate references and action rebinding. This is broader than an XVA rewrite or a chat interface for SIMM.

## What is in this package

- A short root `CLAUDE.md` for persistent implementation guidance.
- Decisions, detailed business rules, architecture and a phased implementation backlog.
- Draft wire schemas and consistent synthetic examples.
- An illustrative SQLite schema, not a production migration.
- Acceptance cases, a company-integration checklist and ready-to-paste coding prompts.

No application source, installed NuGet dependencies, real pricing service, production configuration or actual company data is included.

## Use it

1. Create a new working repository, or place these files in an existing repository after inspecting its instructions.
2. Keep `CLAUDE.md` at the repository root. Merge rather than overwrite an existing instruction file.
3. Start Claude Code in that root and paste `prompts/01-start.md`.
4. Let the agent inspect the environment, record implementation choices and complete the first runnable slice.
5. Use `prompts/02-review.md` for an independent review and `prompts/03-company-integration.md` when company interfaces become available.

The initial runnable slice proves generic composition with synthetic capabilities and the actual ExGrid. It must then lead directly into the XVA input-to-history flow. Rates and real AI are separate subsequent slices, not omitted requirements.

## Precedence

Latest explicit owner instructions take precedence. Within this package: `docs/00-decisions.md` records status and supersessions; detailed domain documents specify behavior; schemas/examples illustrate and constrain the starter subset; illustrative SQL is an implementation aid. Resolve a contradiction before implementing the affected behavior and update its evidence, rather than silently choosing whichever example is easiest.

The schemas deliberately cover a bounded starting subset. They are not claims that every company product, financial convention or analytics file format has been modeled.
