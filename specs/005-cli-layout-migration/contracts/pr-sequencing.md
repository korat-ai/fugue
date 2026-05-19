# Contract: PR Sequencing & Acceptance Gates

**Feature**: 005-cli-layout-migration | **Status**: design — applied across PR P1–P5

Defines the 5-PR sequence, what each PR contains, and what gates each PR MUST satisfy before merge.

## Sequence (per R-4 decision)

| PR | Branch | Files | Sites | Cohesion |
|---|---|---|---|---|
| **P1** | `005-p1-surface-bridge` | `Surface.fs`, new `RenderBridge.fs/.fsi`, `Renderer.fs/.fsi` (drop `toDrawOp`), `Fugue.Adapters.Console.fsproj` (drop `Fugue.Surface` ref), `RenderBridgeTests.fs`, `Snapshots/surface_*.txt`, `LayoutHost.fs` wiring | 13 + relocation | Foundation: introduces `RenderBridge` + ports the largest render surface. Every later PR depends on `RenderBridge` existing. |
| **P2** | `005-p2-boot-diagnostics` | `Render.fs`, `Doctor.fs`, `Snapshots/render_boot.txt`, `Snapshots/doctor_output.txt` | 5 + 1 = 6 | Boot + diagnostic surfaces. Small cohesive cluster. |
| **P3** | `005-p3-streaming-markdown` | `MarkdownRender.fs`, `StreamRender.fs`, `Snapshots/markdown_streaming_complete.txt` | 3 + 1 = 4 | Streaming surfaces. Exercises R-1 scheduler-streaming pattern end-to-end. |
| **P4** | `005-p4-secondary-render` | `DiffRender.fs`, `StackTraceRender.fs`, `Picker.fs`, `Snapshots/diff_render_edit.txt`, `Snapshots/stacktrace_render_exception.txt`, `Snapshots/picker_turns_basic.txt` | 2 + 2 + 1 = 5 | Three small leaf surfaces. |
| **P5** | `005-p5-legacy-cleanup` | `Repl.fs` (drop `FUGUE_LEGACY_RENDER` branch) + corresponding legacy paths in `Surface.fs` | 0 sites (removal only) | Cleanup. Runs only after one full release cycle of soak time (per FR-007). Closes #962. |

## Per-PR acceptance gates

Every PR P1–P5 MUST satisfy ALL of these gates before merge. Gates are evaluated in order:

### G1 — Build clean

```bash
dotnet build -c Release
```

Zero errors, zero warnings (per Constitution Quality Gate 1). `TreatWarningsAsErrors=true` is in effect on every project; any warning becomes a build failure.

### G2 — Tests pass

```bash
dotnet test -c Release --no-build
```

All existing tests pass (≥ 837 baseline), plus all new tests added by this PR (snapshot tests for the migrated paths, `RenderBridgeTests` for P1, optional regression test for closed bug per FR-008).

Per Constitution Principle I: any newly added `[<Skip = ...>]` MUST link a GitHub issue in the skip reason; otherwise the test MUST be deleted, not silenced.

### G3 — Progress invariant (Spectre count strictly decreases)

```bash
# Post-merge expected count for src/Fugue.Cli/*.fs (excluding RenderBridge.fs):
rg -c 'Spectre\.Console|AnsiConsole' src/Fugue.Cli/*.fs \
    | grep -v 'RenderBridge\.fs' \
    | awk -F: '{ sum += $2 } END { print sum }'
```

| PR | Expected count after merge | Sites still pending |
|---|---|---|
| Pre-Phase-4 baseline | 28 | all 8 files |
| After P1 | 15 | Render, Doctor, MarkdownRender, StreamRender, DiffRender, StackTraceRender, Picker |
| After P2 | 9 | MarkdownRender, StreamRender, DiffRender, StackTraceRender, Picker |
| After P3 | 5 | DiffRender, StackTraceRender, Picker |
| After P4 | **0** | (migration complete) |
| After P5 | 0 (unchanged from P4 — P5 is cleanup) | (LayoutHost.fs and RenderBridge.fs are the only consumers of `open Fugue.Adapters.Console` in Cli) |

Gate fails if count is ≥ previous PR's post-merge state.

### G4 — Snapshot tests for new paths

Every render path migrated in this PR has at least one snapshot fixture under `tests/Fugue.Tests/Snapshots/` and a corresponding test in `RenderSnapshotTests.fs`. The fixture matches the test's actual `Renderer.toRawAnsi` output byte-for-byte.

Created fixtures MUST be human-verified before commit (procedure: `contracts/snapshot-fixture-shape.md` § Lifecycle § Creation).

### G5 — Manual smoke against canonical scripted session

Reviewer runs the canonical scripted session (data-model.md §4) against the post-PR binary on macOS (primary dev platform). PR body's "Test plan" section ticks off the 9 session steps after manual verification.

Acceptable degradation: nothing. Visual parity is the constraint (FR-003). Any observed difference is either a regression (must be fixed before merge) or intentional behaviour change (must be documented in PR body and linked to an issue).

### G6 — AOT publish smoke

```bash
dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64
dotnet publish src/Fugue.Cli.Aot -c Release -r linux-x64
dotnet publish src/Fugue.Cli.Aot -c Release -r win-x64
```

All three runtime identifiers publish cleanly. Zero new trim/AOT warnings beyond the allow-listed set (`IL2026`, `IL3050`, `IL3053`, `IL2104`).

This gate exists even though Phase 4 work is entirely in the JIT closure — it is a regression guard against accidentally pulling JIT-only code into the AOT closure via a transitive dependency change.

### G7 — Pre-push review by `engineering-code-reviewer` subagent

Per Constitution Principle IX (Trust-but-Verify) and the Phase 3 lesson (6 BLOCKERs caught pre-push across 5 PRs).

Reviewer subagent reads the full diff and reports findings. Any BLOCKER finding MUST be addressed (fix or explicit justification with reviewer agreement) before merge. Non-blocker findings (suggestions, follow-ups) MAY be deferred to follow-up issues.

### G8 — CI green on all 3 OS

Standard CI matrix: Ubuntu, macOS, Windows. All checks COMPLETED + SUCCESS. `mergeStateStatus` must be `CLEAN` before `gh pr merge`.

### G9 (P5 only) — Soak period satisfied

Applies only to PR P5 ("legacy-cleanup"). The PR MAY NOT merge until:

- PR P4 has been on `main` through ≥ 1 full Fugue release cycle.
- Zero rendering-regression issues filed against the post-P4 binary during that cycle.
- Maintainer explicitly approves removal (CEO escalation per Constitution VII).

This gate is a documentation gate — reviewer confirms in PR body that the soak period is satisfied; the merger (user) confirms the approval.

## PR body template

Every PR uses this body shape:

```markdown
## Summary

- <one-bullet summary of what this PR does in the migration sequence>
- Closes <Spectre count> direct call sites: <list of files migrated>
- Adds snapshots for: <fixture list>

## Migration progress

- Pre-PR count: <N> sites
- Post-PR count: <N-K> sites (target: <expected>)

## Test plan

- [ ] G1 `dotnet build -c Release` clean
- [ ] G2 `dotnet test -c Release` ≥ 837 tests pass (specifically: <new test count> added)
- [ ] G3 Spectre count strictly decreases (verified by `rg` invocation above)
- [ ] G4 Snapshot fixtures for migrated paths exist and assert correctly
- [ ] G5 Canonical scripted session smoke run on macOS — no visual regression
- [ ] G6 AOT publish smoke clean on osx-arm64 / linux-x64 / win-x64
- [ ] G7 Pre-push review by `engineering-code-reviewer` — no BLOCKER findings
- [ ] G8 CI green on all 3 OS
- [ ] (P5 only) G9 Soak period ≥ 1 release cycle + maintainer approval
```

## Rollback path

If a regression is discovered post-merge for any of P1–P4:

1. **Immediate**: user sets `FUGUE_LEGACY_RENDER=1` in their shell; falls back to pre-migration rendering for the affected REPL session.
2. **Short-term**: file an issue describing the regression with reproduction steps and a snapshot diff.
3. **Fix forward**: prepare a follow-up PR that updates the relevant snapshot fixture intentionally (per `contracts/snapshot-fixture-shape.md` § Update) or fixes the regressed code path. Do NOT revert prior migration PRs — that re-introduces 28 call sites and unwinds the dogfooding.
4. **PR P5 blocker**: any open regression issue automatically gates PR P5 (the legacy-cleanup) until resolved.

Rollback path is the value the `FUGUE_LEGACY_RENDER` fallback provides — its removal in PR P5 is conditional on the migration having proved itself stable.
