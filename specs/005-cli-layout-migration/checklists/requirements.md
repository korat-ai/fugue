# Specification Quality Checklist: Migrate Fugue.Cli UI to the Layout Framework

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-05-19
**Feature**: [spec.md](../spec.md)

## Content Quality

- [X] No implementation details (languages, frameworks, APIs)
- [X] Focused on user value and business needs
- [X] Written for non-technical stakeholders
- [X] All mandatory sections completed

## Requirement Completeness

- [X] No [NEEDS CLARIFICATION] markers remain
- [X] Requirements are testable and unambiguous
- [X] Success criteria are measurable
- [X] Success criteria are technology-agnostic (no implementation details)
- [X] All acceptance scenarios are defined
- [X] Edge cases are identified
- [X] Scope is clearly bounded
- [X] Dependencies and assumptions identified

## Feature Readiness

- [X] All functional requirements have clear acceptance criteria
- [X] User scenarios cover primary flows
- [X] Feature meets measurable outcomes defined in Success Criteria
- [X] No implementation details leak into specification

## Notes

- Initial draft passed validation in one iteration. Four open architectural
  questions (R-1 streaming model, R-2 DrawOp ABI, R-3 visual regression
  detection, R-4 migration sequencing) were resolved as documented Assumptions
  with informed defaults rather than left as NEEDS CLARIFICATION.
- **2026-05-19 amendment after `/speckit-analyze` pass 1**: 10 analyzer findings
  (1 HIGH, 5 MEDIUM, 4 LOW) all addressed via targeted edits.
- **2026-05-19 amendment after `/speckit-analyze` pass 2**: pass-2 surfaced 5
  regression findings (1 HIGH, 2 MEDIUM, 2 LOW) introduced by pass-1
  remediation — all the same root cause: FR text edits weren't propagated to
  narrative sections (User Stories, Acceptance Scenarios, Edge Cases,
  Assumptions). All fixed:
    - **F1** (HIGH, soak-period drift across 5 spec/tasks locations) → all 5
      replaced with "≥ 14 calendar days … per FR-007"; plus 4 additional
      propagation hits in plan.md, research.md, contracts/pr-sequencing.md
      caught and fixed in pass-3.
    - **F2** (MEDIUM, "perceptible stutter" in acceptance/edge) → US1 acceptance
      scenario 2 + Edge Case first bullet now reference FR-004 (a)(b) bounds.
    - **F3** (MEDIUM, T086 ambiguous T079 reference) → T086 clarified that the
      "T079" prefix is a Phase 3 task-id baked into the test name string, NOT
      Phase 4's T079.
    - **F4** (LOW, SC-008 scope vs T080) → SC-008 widened to "all REPL
      render-path files (eight + any added in future)" matching T080's reach.
    - **F5** (LOW, Assumption section stale) → rewritten with concrete 14-day
      rule + rationale for why the original "release cycle" framing was rejected.
- **Pattern**: pass-2 + pass-3 caught 9 total propagation failures across 7
  files. The discipline "edit FR + grep + propagate + re-grep" is now the
  spec maintenance norm. Future amendments should follow it.

Earlier pass-1 findings (kept for traceability):
    - **C1** (HIGH, perf gate gap) → new T086 explicitly runs Phase 3 benchmark
      against post-P4 main; FR-004 (a) bound (10 ms median) made concrete.
    - **C2** (MEDIUM, FR-009 ABI assertion missing) → new T087 adds
      reflection-based `DrawOp` DU shape test.
    - **C3** (MEDIUM, SC-008 unenforceable) → SC-008 rewritten as standing
      invariant (mechanically verifiable via T080 standing test) instead of
      observer-judgement.
    - **A1** (MEDIUM, "perceptible stutter" no threshold) → FR-004 (b) bound
      added: 50 ms p95 streaming-latency; new T088 adds automated probe.
    - **A2** (MEDIUM, "soak period" unmeasurable) → FR-007 now specifies
      ≥ 14 calendar days + zero rendering-regression issues + maintainer
      confirmation; data-model §6 + T070 updated accordingly.
    - **A3** (LOW, "two bridge files" ambiguous) → FR-002 now names both
      (`LayoutHost.fs` + `RenderBridge.fs`).
    - **D1** (LOW, SPECKIT marker cache churn) → acknowledged as accepted cost
      with explicit note in tasks.md Implementation strategy.
    - **U1** (LOW, T012 inventory non-permanent) → T012 now records the
      inventory in `inventory-surface.md` (permanent), not a deleted comment.
    - **I1** (LOW, T080 location ambiguous) → T080 pins location to
      `tests/Fugue.Tests/ZeroSpectreCallTest.fs`.
    - **I2** (LOW, US3 "30 seconds" unverifiable) → US3 Independent Test
      rephrased to "documented as a comment block" — verifiable by file read.
- Note on spec language: the spec deliberately uses neutral phrasing ("the
  underlying terminal-rendering library", "the framework", "the in-repo F#
  adapter") rather than concrete technology names, per the spec template's
  "no implementation details" guidance. Concrete file names appear only in
  Key Entities (as illustrative documented inventory) and the canonical
  scripted-session definition (FR-003), where they are necessary for test
  reproducibility.
