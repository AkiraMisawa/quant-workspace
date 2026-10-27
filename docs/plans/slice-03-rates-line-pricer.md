# Slice 03: the Rates line pricer

Status: plan, 2026-10-27. Implements the start of P4 (`docs/12-implementation-plan.md`) with the scope
the owner added on 2026-10-27 (C37 to C42 in `docs/00-decisions.md`). Built on slice 02's branch.

Every figure is synthetic until a real engine is selected. The first provider is a deterministic fake
labelled `simulated calculation`; the market is a seeded synthetic source labelled `synthetic`.

## Goal

A user types an interest-rate swap or a cross-currency swap on one ExGrid row, sees the
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

Answered on 2026-10-27: no swaptions for now; risk is the analytics' automatic-differentiation
sensitivities; the basic row terms (C40); JPY, USD, EUR, GBP (C41); the first cross-currency structure
(C42); the carry check for customised cashflows (C39). Still OPEN: the EUR and GBP indices, each
currency's conventions, the risk factors sensitivities are reported against, and the levels of terms
beyond the basic one. No convention is invented meanwhile: the schedule generator takes every
convention from an explicit profile, and only confirmed profiles are registered.

## Acceptance

RT03 to RT07 and PR01 to PR04 (`docs/11-acceptance.md`) with the fake provider first; RT01 and RT02 need
the real worker. Keyboard, touch (after ExGrid's touch release, C36), dark and light, and a narrow layout
with the detail panel.
