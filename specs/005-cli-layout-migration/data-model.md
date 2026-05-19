# Phase 1: Data Model — Migrate Fugue.Cli UI to the Layout Framework

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Research**: [research.md](./research.md) | **Date**: 2026-05-19

Models the data/structural artefacts the migration produces or consumes. Phase 4 is a refactoring feature, so most entities are *code artefacts* (modules, signatures, fixtures) rather than runtime domain types.

## §1 — Call-site inventory (the work to be done)

The 28 Spectre.Console call sites that this migration replaces. Counted on `main` at SHA `52c7853` (post-Phase-3-Polish merge).

| # | File | Sites | What it renders | Target adapter primitive(s) |
|---|---|---|---|---|
| 1 | `src/Fugue.Cli/Surface.fs` | 13 | DrawOp executor: markdown panels, diff panels, status bar, toast notifications, prompt area | `Composition` (Stack + Dock for status bar; Panel for content); `LayoutHost.mount` wiring |
| 2 | `src/Fugue.Cli/Render.fs` | 5 | Boot banner, `/doctor` summary output, version display | `Composition.Leaf` + `Style` + `SafeText` |
| 3 | `src/Fugue.Cli/Doctor.fs` | 1 | `/doctor` interactive details (provider list, model checks) | `Composition` (Stack + DataDisplays.Grid for tabular checks) |
| 4 | `src/Fugue.Cli/MarkdownRender.fs` | 3 | Streaming markdown rendering (Markdig → terminal) | Pure rendering of Markdig output via `Composition.panel` + Markdig-to-Composition mapper |
| 5 | `src/Fugue.Cli/StreamRender.fs` | 1 | Per-token append during streaming | `Composition` producer + `Scheduler.trigger` (see R-1) |
| 6 | `src/Fugue.Cli/DiffRender.fs` | 2 | Edit tool result diff (old/new side-by-side or unified) | `Composition` (Columns or Stack of styled hunks) |
| 7 | `src/Fugue.Cli/StackTraceRender.fs` | 2 | Exception display (type / message / frames / inner) | `Composition` (Stack + `Console.renderException` adapter wrapper) |
| 8 | `src/Fugue.Cli/Picker.fs` | 1 | `/turns` interactive picker (arrow-key navigation) | `Composition` (Stack with selected-row Style highlight) |
|   | **TOTAL** | **28** | | |

**State transitions per site**:

1. **Pre-migration**: `open Spectre.Console` + direct `AnsiConsole.Write(...)` / `AnsiConsole.MarkupLine(...)` / Spectre widget construction in-line.
2. **Post-migration**: `open Fugue.Adapters.Console` (and `.Layout` where Stack/Dock/Grid/Flex used); construct a `Composition` value; render via `Renderer.toRawAnsi ctx comp` or post `DrawOp` via `RenderBridge.toDrawOp` then `Surface.write`.

**Validation rule** (per FR-002 / SC-001): after PR P4 merges, the invariant
```
rg -c 'Spectre\.Console|AnsiConsole' src/Fugue.Cli/*.fs | grep -v ':0$'
```
must return ZERO lines (i.e. no file has a non-zero count), *except* `LayoutHost.fs` and `RenderBridge.fs` which legitimately `open Fugue.Adapters.Console` (matched by the same regex via the namespace prefix — adjust the regex to exclude `Adapters\.Console`).

## §2 — `Fugue.Cli.RenderBridge` module (the new bridge)

A single new module relocating `Renderer.toDrawOp` out of the framework package. Owns the one place where the framework's abstract output is mapped to Fugue's internal `DrawOp`.

**Module signature** (final form in `contracts/render-bridge.fsi`):

```fsharp
module Fugue.Cli.RenderBridge

open Fugue.Adapters.Console
open Fugue.Surface

/// Render a Composition to ANSI bytes wrapped as DrawOp.RawAnsi, suitable
/// for posting to the Fugue Surface actor.
///
/// This is the single bridge from the framework's abstract output to Fugue's
/// internal draw-op representation. The framework package owns the abstract
/// Composition; consumers (like Fugue) own their mapping into local types.
val toDrawOp :
    ctx: RenderContext ->
    composition: Composition ->
        Result<DrawOp, RenderError>
```

**Implementation** (≈ 6 LOC):

```fsharp
module Fugue.Cli.RenderBridge

open Fugue.Adapters.Console
open Fugue.Surface

let toDrawOp (ctx: RenderContext) (composition: Composition) : Result<DrawOp, RenderError> =
    Renderer.toRawAnsi ctx composition
    |> Result.map DrawOp.RawAnsi
```

**Tests** (`tests/Fugue.Tests/RenderBridgeTests.fs`):

- **T-RB-1 (happy)**: `toDrawOp ctx80 (markerLeaf "OK")` → `Ok (DrawOp.RawAnsi <non-empty string containing 'OK'>)`.
- **T-RB-2 (error)**: invalid context (e.g. width = 0) flows through; verify `Error` case propagates correctly (`RenderError.DegenerateWidth 0`).

**Relationships**:

- Imports `Fugue.Adapters.Console` (the framework, which after this migration drops its own `Fugue.Surface` reference).
- Imports `Fugue.Surface` (the internal actor; bridge knows about both — that's its job).
- Consumed by `LayoutHost.fs` (existing, ≈ 41 LOC) and any other Fugue REPL code that needs a `DrawOp` from a `Composition`.

## §3 — Snapshot fixture format (the regression-detection artefact)

Frozen `.txt` files under `tests/Fugue.Tests/Snapshots/` containing raw ANSI bytes. One per render path.

**File naming convention**:

```
<source_file_basename>_<scenario>.txt
```

Examples:

- `surface_status_bar.txt`
- `surface_markdown_basic.txt`
- `surface_diff_basic.txt`
- `render_boot.txt`
- `doctor_output.txt`
- `markdown_streaming_complete.txt`
- `diff_render_edit.txt`
- `stacktrace_render_exception.txt`
- `picker_turns_basic.txt`

**Content format**: raw bytes as captured from `Renderer.toRawAnsi ctx80 comp` for a deterministic input `comp`. ANSI escape sequences (CSI codes) are preserved literally (`\x1b[31m` etc.). Newlines are LF (not CRLF) — fixtures are stored canonically; comparison uses literal byte equality.

**Fixture creation procedure** (one-time per fixture):

1. Developer writes the test with `expected = ""` (empty).
2. Test fails; the test's failure message includes the actual ANSI output.
3. Developer inspects the output visually (in a terminal that renders ANSI — `printf '%s' "$(cat actual.txt)"` or similar).
4. Developer verifies it matches the pre-migration Spectre rendering for the same logical input (manual comparison — open both binaries side-by-side).
5. Developer copies the actual output to the fixture path; commits.
6. Subsequent runs assert byte-for-byte.

**Fixture update procedure** (when the migration *intentionally* changes a fixture — e.g. the closed bug from FR-008):

1. Test fails after the intentional change.
2. PR diff shows the changed fixture bytes; reviewer inspects.
3. Reviewer either approves (intentional, behaviour change is desired) or rejects (unintentional regression, revert the change).

**Determinism preconditions** (must hold for snapshot tests to be reliable):

- `RenderContext` is fixed (80 cols × 24 rows, colour ON, theme = "default" in the test).
- `Composition` input is built from literal values (no `DateTime.Now`, no random IDs).
- `Renderer.toRawAnsi` is itself deterministic — confirmed by Phase 3 SC-006 benchmark and Phase 1+2 test suite (260+ render-evidence tests assert exact byte sequences).

**Cross-platform consideration**: if `Renderer.toRawAnsi` output differs across macOS / Linux / Windows (e.g. due to underlying Spectre platform-detection), fixtures live under per-OS subdirectories (`Snapshots/macos/`, `Snapshots/linux/`, etc.). Implementation surfaces will determine whether this is needed; Phase 3 tests are currently OS-uniform, so the default is one fixture per render path, shared across OSes.

## §4 — Canonical scripted session DSL (the integration-level probe)

A fixed sequence of REPL inputs used for manual smoke (per R-3 decision) and potentially for future PTY-harness automation (#937).

**Session shape**:

```
1. fugue (start REPL, expect boot banner — covers Render.fs paths)
2. <user message>: "hello"  (expect assistant streaming response — covers MarkdownRender + StreamRender + Surface)
3. <user message>: "list files in src/"  (expect tool call + result — covers DiffRender via Edit / Bash output)
4. /turns  (interactive picker — covers Picker)
5. <up arrow> <up arrow> <enter>  (selects historical turn)
6. <user message>: ""  (empty — expect graceful no-op)
7. <Ctrl+L>  (clear screen — covers status bar re-render in Surface)
8. <user message>: "throw an exception please"  (expect tool error → StackTraceRender)
9. /exit  (clean shutdown)
```

**Acceptance**: each PR's reviewer runs this sequence against the post-merge binary. Visual parity vs the pre-PR baseline = acceptance.

**Future automation** (#937): the DSL above is exactly the input the PTY harness will replay. Reserving the structure now means the eventual PTY work just adds the harness, not the test cases.

## §5 — Per-render-path producer signature (streaming paths)

For streaming-capable render paths (currently `MarkdownRender.fs` + `StreamRender.fs`), the migration introduces a uniform producer shape that the `LayoutScheduler` consumes:

```fsharp
/// A streaming producer closes over mutable state that grows as tokens arrive.
/// Each call returns the current full Composition; ref-equality short-circuit
/// in the scheduler suppresses re-render when state hasn't changed.
type StreamingProducer = unit -> Result<Composition, RenderError>
```

**State machine for a streaming response lifecycle**:

```
[idle]
  ↓ stream-begin
[accumulating] ──(token-arrival)──→ [accumulating]   (each arrival updates ref, triggers scheduler)
  ↓ stream-end (or stream-error)
[final] ──(scheduler-tick × N, ref-equal)──→ [final]   (no further work, no further renders)
  ↓ next-stream-begin or REPL-exit
[idle]
```

**Validation rule**: in `[final]` state, the producer's returned `Composition` reference MUST remain equal across ticks. Tested by a property: "after N idle ticks, producer is called exactly once more (then short-circuited N-1 times)".

## §6 — Legacy-render fallback state (the removal target)

Existing artefact, scheduled for removal in PR P5 per FR-007.

**Current shape** (in `src/Fugue.Cli/Repl.fs`):

- Environment variable read: `FUGUE_LEGACY_RENDER` (string, accepted truthy values: `"1"`, `"true"`, case-insensitive).
- Branch: when truthy, render paths bypass the new adapter and call directly into the pre-Phase-3 Spectre code path.
- Estimated footprint: ~50 LOC in `Repl.fs` + corresponding "legacy" code paths in `Surface.fs` (these are removed alongside).

**Post-PR-P5 state**: zero matches for `FUGUE_LEGACY_RENDER` in the source tree. Setting the env var has no effect.

**Removal precondition** (gate before P5 PR is merged):

- One full Fugue release cycle has passed since PR P4 merge (the migration is complete on `main`).
- No rendering-regression issue has been filed against the post-P4 binary during that cycle.
- Maintainer explicitly approves the removal (CEO escalation per Constitution VII).

## §7 — Target rendering bug (the SC-007 commitment)

One of three open bugs must be auto-closed by the migration OR become fixable in ≤ 30 LOC (FR-008).

| Bug | Current pre-Phase-4 symptom | Hypothesis: migration impact |
|---|---|---|
| **#910** scroll-region reset on every refresh | Surface.fs's `Console.Clear()`-then-redraw destroys terminal scrollback | Migration routes through Layout which doesn't issue `Clear` — likely auto-closes |
| **#913** model description and input prompt overlap after heartbeat | Status bar render races with ReadLine cursor positioning | Migration centralises status bar via Dock primitive; predictable positioning — likely simplifies fix |
| **#934** multi-line input/output corrupts ReadLine prompt area | Multiple sequential ANSI emissions interleave with ReadLine's cursor management | Migration sequences all writes through Surface actor (single serialised channel) — likely simplifies fix |

**Implementation tactic** (decided in `/speckit-implement` per PR): the cluster doing the relevant migration (P1 Surface for #910/#934, P3 streaming for #913) attempts to close the bug as part of the PR. If the bug auto-closes, write a regression test (per Constitution I) and link the issue. If the bug requires a separate fix, file a follow-up issue and ship the migration without the fix (SC-007 still demands ≥ 1 closed before Phase 4 declares complete; up to two follow-ups allowed).

## §8 — CLAUDE.md SPECKIT marker (the status-reporting artefact)

Updated at end of each PR cluster to reflect progress.

**Progression**:

- After PR P1 merge: "Phase 4 PR P1 merged — Surface.fs + RenderBridge done; 15/28 sites remaining (Render.fs, Doctor.fs, MarkdownRender.fs, StreamRender.fs, DiffRender.fs, StackTraceRender.fs, Picker.fs)."
- After PR P2 merge: "Phase 4 PR P2 merged — Boot + Doctor done; 9/28 sites remaining."
- After PR P3 merge: "Phase 4 PR P3 merged — Streaming markdown done; 5/28 sites remaining."
- After PR P4 merge: "Phase 4 migration COMPLETE — 0/28 direct Spectre calls in src/Fugue.Cli/*.fs (except RenderBridge.fs which legitimately bridges to adapter). Awaiting soak period for PR P5 (FUGUE_LEGACY_RENDER removal)."
- After PR P5 merge: "Phase 4 COMPLETE — legacyRender fallback removed; deferred Phase 5 condition 1 satisfied (gh-965 condition 1 ✓ — package fully dogfooded, can be unblocked when conditions 2+3 also met)."
