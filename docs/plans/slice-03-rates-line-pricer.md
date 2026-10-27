# Slice 03: the Rates line pricer

Status: plan, 2026-10-27. Implements the start of P4 (`docs/12-implementation-plan.md`) with the scope
the owner added on 2026-10-27 (C37 to C39 in `docs/00-decisions.md`). Built on slice 02's branch.

Every figure is synthetic until a real engine is selected. The first provider is a deterministic fake
labelled `simulated calculation`; the market is a seeded synthetic source labelled `synthetic`.

## Goal

A user types an interest-rate swap, a cross-currency swap or a swaption on one ExGrid row, sees the
standard cashflow schedule generated from it in the row's detail panel, customises cashflows there, and
gets PV, the par rate (or the fair spread with its target leg) and risk, both on a manual Price and
ticking live against the synthetic market.

## Built first (does not depend on an OPEN convention)

- The live subscription's rules (`Workbench.Samples.Rates/RatesLive.cs`): one calculation in flight, the
  newest market update waiting behind it, frames shown only for the current input revision and a newer
  market, stop keeping the last frame (RT03, RT04, RT05 part). Done, with xUnit tests.
- Next: the live service that drives those rules with a bounded worker pool and a provider, the seeded
  synthetic market source, the Rates workspace on the generic edit log, and the history projection.

## Waiting on the owner

Questions of 2026-10-27, recorded as OPEN in C37 to C39: swaption product scope and volatility model;
which risk measures and bumps; the first currencies and indices and the one-line syntax; the first
cross-currency structure; which cashflow fields can be customised; and what happens to customised
cashflows when the one-line terms change. No convention, measure definition or schedule rule is
invented in the meantime.

## Acceptance

RT03 to RT07 and PR01 to PR04 (`docs/11-acceptance.md`) with the fake provider first; RT01 and RT02 need
the real worker. Keyboard, touch (after ExGrid's touch release, C36), dark and light, and a narrow layout
with the detail panel.
