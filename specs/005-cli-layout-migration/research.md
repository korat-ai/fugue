# Phase 0: Research — Migrate Fugue.Cli UI to the Layout Framework

**Feature**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-05-19

Resolves the four open architectural questions (R-1..R-4) flagged in spec Assumptions. Each follows **Decision / Rationale / Alternatives considered / Mechanics**.

---

## R-1 — Streaming token append vs Layout static-rebuild model

**Question**: Layout API rebuilds the composition tree per frame. Streaming markdown appends tokens incrementally. How do these two models compose without (a) re-rendering the entire markdown buffer on every token, (b) stuttering UX, (c) leaking abstraction (forcing every streaming call site to know about scheduler internals)?

**Decision**: **Use `LayoutScheduler` 60Hz frame-coalescing**, with producer callback closing over a mutable ref to accumulated content. Referential-equality short-circuit suppresses re-render when content hasn't changed since previous tick.

**Rationale**:

- **The scheduler was built for exactly this case.** Phase 3 P5 introduced `LayoutScheduler` precisely so streaming producers wouldn't have to think about frame timing. The scheduler's contract is: producer is called at most once per `frameIntervalMs` tick; if the produced `Composition` is referentially equal to the previous tick's result, `onFrame` is NOT called. That is a coalescing primitive purpose-built for streaming.
- **Streaming markdown ≠ "must render every token immediately"** — the human perceptual threshold for streaming text is ≈ 50 ms (≈ 20 Hz). At 60 Hz scheduler ticks (16 ms per tick), users see at worst one tick of delay between token arrival and on-screen render, which is below perceptual threshold and matches pre-migration Spectre behaviour (Spectre's `Live` display also coalesces at the same order of magnitude).
- **The alternatives all leak abstraction.** Option (b) "introduce `Composition.Append` primitive" requires the framework to know that some compositions are append-only, complicating the renderer and breaking the "Composition is a value" invariant from Phase 3 data-model §1. Option (c) "synchronous render per token" pushes scheduler logic into every streaming call site, making per-call-site reasoning harder and undoing Phase 3 P5's mount-point unification.
- **Referential-equality short-circuit handles the idle case correctly**: when a streaming response completes and no new tokens arrive, the producer keeps returning the same `Composition` reference; the scheduler ticks but `onFrame` doesn't fire; zero ANSI is emitted; the terminal is idle as expected. This was validated by Phase 3 P5 T065 ("scheduler-driven Dock smoke" test).

**Mechanics**:

- Streaming call sites (`MarkdownRender.fs`, `StreamRender.fs`) expose a producer of shape:
  ```fsharp
  let private producer (state: StreamingState) : unit -> Result<Composition, RenderError> =
      fun () -> buildComposition state.AccumulatedMarkdown
  ```
- The producer is mounted via `LayoutHost.mount producer 16` (or whatever `Scheduler` default — Phase 3 baseline is 16 ms).
- New token arrival: token append updates `state.AccumulatedMarkdown` (mutable ref) AND calls `Scheduler.trigger sched` to mark the layout dirty. The trigger is lock-free.
- When stream completes: producer continues to return the same `Composition` reference (state unchanged); scheduler ticks burn no CPU because of ref-equality short-circuit.
- Scheduler `stop` called at end of REPL session (or at end of single streaming response if the design favours one-scheduler-per-stream).

**Alternatives considered**:

- **(b) `Composition.Append` accumulator primitive**: rejected — adds framework complexity for a problem the scheduler already solves.
- **(c) Synchronous per-token render**: rejected — pushes timing concerns into every call site, costs idle CPU when nothing changed.
- **One scheduler instance per streaming response vs one global scheduler**: deferred to implementation; both are workable. The simpler choice is one global scheduler per REPL session, with producers swapping when streaming starts/ends. Implementation surfaces will clarify.

---

## R-2 — DrawOp ABI: extend to accept `Composition` or keep `RawAnsi` flow

**Question**: After migration, `RenderBridge.toDrawOp` maps `Composition → DrawOp.RawAnsi (string)`. Should we instead extend the `DrawOp` discriminated union with a new `DrawOp.Composition of Composition` case, so the Surface actor unpacks it lazily before the actual stdout write?

**Decision**: **Keep the existing `RawAnsi`-constructor-only flow.** Do NOT extend `DrawOp` with a `Composition` case.

**Rationale**:

- **No consumer demand.** The Surface actor's job is to serialise writes to stdout, not to render. Adding `DrawOp.Composition` would mean the actor learns about rendering — that's a responsibility split that buys nothing here. The current shape (`RawAnsi of string`) is exactly what the actor needs: a byte blob to write.
- **Late rendering doesn't enable any optimisation here.** The hypothetical reason to lazy-render at the actor would be to skip rendering for compositions that get superseded before they're written. But the `Scheduler` already coalesces upstream (R-1), so by the time a `Composition` reaches `RenderBridge.toDrawOp`, it has *already* survived the coalescing filter. Re-coalescing at the actor adds complexity without removing work.
- **Keeping `RawAnsi` preserves the Surface actor's value as a pure write-serialiser.** That value is testable, mockable (the existing `MockExecutor` does exactly this), and platform-independent. Adding a render step inside the actor breaks each of those properties.
- **FR-009 explicitly forbids changing the DrawOp interface.** This decision aligns with the spec; documenting it here is the formal "we considered the extension and rejected it" record.

**Mechanics**:

- `Fugue.Surface.DrawOp` remains unchanged.
- `RenderBridge.toDrawOp` signature:
  ```fsharp
  val toDrawOp :
      ctx: RenderContext ->
      composition: Composition ->
          Result<DrawOp, RenderError>
  ```
- Implementation: identical to the current `Renderer.toDrawOp` body in the package, just relocated.

**Alternatives considered**:

- **Extend `DrawOp` with `Composition` case**: rejected for the reasons above.
- **Bypass `DrawOp` entirely** (write directly to stdout from the producer): rejected because it loses the serialisation guarantee the Surface actor provides — concurrent writes would interleave.

---

## R-3 — Visual regression detection mechanism

**Question**: Migration touches 28 call sites. How do we prove no visual regression? Snapshot tests vs PTY-based integration tests vs manual vs combination.

**Decision**: **Per-render-path snapshot tests at unit level + manual smoke against canonical scripted session before each PR merge.** Defer pseudo-terminal-based end-to-end automation to issue #937 (already in backlog).

**Rationale**:

- **Snapshot tests are deterministic at the right layer.** `Renderer.toRawAnsi` is a pure function: given a `Composition` and a `RenderContext`, it produces a deterministic ANSI `string`. Asserting that string against a frozen `.txt` fixture catches any rendering-pipeline change. This is exactly the layer where regressions would manifest.
- **Each migrated file gets its own snapshot test for representative inputs.** Per Constitution Principle I ("happy + error path"), one fixture per render path is sufficient as a regression guard. The 8 migrated files produce ≈ 9 snapshot fixtures (see plan.md Project Structure for the exact list); each fixture is < 5 KB; total snapshot suite under 50 KB — well within reasonable test-suite weight.
- **Manual smoke covers integration-level concerns** that unit snapshots miss: cursor positioning across multiple sequential DrawOps, scroll-region behaviour, terminal resize mid-session, ReadLine prompt interaction. These are exactly the bug classes (#910, #913, #934) that the migration aims to make tractable — so the migration's reviewer is the right person to smoke them.
- **PTY-based automation is significant infrastructure** (#937 estimated as a separate phase). Building it as part of Phase 4 would balloon the scope and delay the dogfooding value. The deferred approach: ship Phase 4 with snapshot + manual; if a regression slips through and is caught later, that becomes the demand-signal that justifies #937 investment.
- **The legacy-render fallback (FR-007) is the safety net** for the brief window when only snapshot+manual is in place. Any regression discovered post-merge can be rolled back via `FUGUE_LEGACY_RENDER=1` while the fix is prepared. The fallback removes in PR P5 only after one full release cycle — the snapshot+manual approach has had time to prove itself.

**Mechanics**:

- **Snapshot test pattern** (in `tests/Fugue.Tests/RenderSnapshotTests.fs`):
  ```fsharp
  [<Fact>]
  let ``surface status bar renders identically to baseline`` () =
      let ctx = RenderContext.create 80 24 true "default" |> Result.unwrap
      let comp = buildStatusBarComposition exampleStatusInputs
      let actual = Renderer.toRawAnsi ctx comp |> Result.unwrap
      let expected = File.ReadAllText "Snapshots/surface_status_bar.txt"
      Assert.Equal(expected, actual)
  ```
- **Fixture creation**: when introducing a new fixture, the developer runs the test, observes the failure (no fixture yet), copies the *actual* output to the `Snapshots/` path, verifies visually that it matches the pre-migration behaviour, commits the fixture. Subsequent test runs assert byte-for-byte.
- **Fixture update process**: any change to a fixture must be reviewed by a human — `git diff` shows the literal ANSI bytes changed. Suspicious changes (loss of colour codes, changed cursor positions) are caught at review.
- **Canonical scripted-session smoke** (manual): each PR's "Test plan" section in the PR body lists the 5 steps to run pre-merge — start binary, type message, observe streaming, run `/turns`, exit. Reviewer ticks them off after manual verification on macOS (primary dev platform) before merging.

**Alternatives considered**:

- **PTY-based end-to-end automation**: deferred to #937 — significant infra, not justified by current evidence.
- **Manual smoke only (no snapshots)**: rejected — gives zero coverage for incremental changes after the initial migration ships; future contributors can break a render path without anyone noticing for months.
- **Visual screenshot diffing**: considered (e.g. capture terminal pixels, run image diff) — rejected because pixel diffs are flaky across terminal-font renderings and don't catch ANSI-level differences that are semantically meaningful but pixel-equivalent.

---

## R-4 — Migration order and PR sequencing

**Question**: 8 files, 28 call sites. How to split? Big-bang (one PR), file-by-file (8 PRs), or grouped clusters?

**Decision**: **5 cohesive PRs grouped by call-site cohesion** (plus an optional final cleanup):

| PR | Files | Sites | Cohesion |
|---|---|---|---|
| **P1 — Surface + RenderBridge** | `Surface.fs` + new `RenderBridge.fs/.fsi` + drop `Renderer.toDrawOp` from package | 13 + (relocation) | Foundation: every other PR depends on `RenderBridge` existing. Also satisfies the deferred Phase 5 SC-006 condition (zero internal refs in the package). |
| **P2 — Boot & diagnostics** | `Render.fs` + `Doctor.fs` | 5 + 1 = 6 | Both render at startup / on `/doctor` invocation. Small cohesive cluster — diagnostic surfaces. |
| **P3 — Streaming markdown** | `MarkdownRender.fs` + `StreamRender.fs` | 3 + 1 = 4 | Both render assistant streaming responses. Together exercise the R-1 scheduler-streaming pattern end-to-end. |
| **P4 — Secondary render surfaces** | `DiffRender.fs` + `StackTraceRender.fs` + `Picker.fs` | 2 + 2 + 1 = 5 | Three small "leaf" render paths. Edit-tool diff, exception display, `/turns` picker. Grouped because each individually is too small for a PR. |
| **P5 — Cleanup** | `Repl.fs` (drop `FUGUE_LEGACY_RENDER` branch) | (0 call sites — removal only) | Cleanup PR. Runs after one full release cycle of soak time on `main` (FR-007 / SC-005). Closes #962. |

**Rationale**:

- **Big-bang (1 PR) is rejected** for the reasons captured in spec FR-013: review burden (28 call sites in one diff is unreviewable in detail), blast radius (any regression unwinds the entire migration), bounded reversion (can't revert one file without reverting all).
- **File-by-file (8 PRs) over-fragments**: e.g. `Picker.fs` has 1 call site — a PR for one call site is procedural overhead with no review value. Phase 3 evidence: PRs that ported < 3 call sites in the Layout framework rollout were trivially merged with no findings (PR review found value at the cluster level).
- **Grouped clusters** balance reviewability (each PR ≤ 13 call sites, most ≤ 6) against fragmentation overhead. Phase 3's PR cadence (P1–P5 + Polish across 5 PRs over 6 days with 6 BLOCKERs caught pre-push) is the calibration baseline: this is the right rhythm for the project's current bandwidth.
- **PR P1 must land first** because it introduces `RenderBridge` (which every later PR consumes) AND it does the framework-package relocation (which is a strictly mechanical change orthogonal to migration logic, easier to review in isolation than mixed with call-site changes).
- **PR P5 must land last and only after soak**, per FR-007. Removing the legacy-render fallback before the migration has proved itself in production is precisely the failure mode the spec wants to avoid.

**Mechanics**:

- Each PR has its own feature branch off `main`: `005-p1-surface-bridge`, `005-p2-boot-diagnostics`, etc.
- Each PR's acceptance gates (from `contracts/pr-sequencing.md`):
  1. All previously passing tests still pass.
  2. New snapshot tests for the PR's migrated paths pass against fresh fixtures.
  3. `rg -c "Spectre\.Console|AnsiConsole" src/Fugue.Cli/*.fs` returns a strictly lower count than the previous PR's post-merge state (progress invariant).
  4. Manual smoke against canonical scripted session — reviewer ticks off in PR body.
  5. AOT publish smoke unaffected (FR-011 / SC-004).
  6. Pre-push review by `engineering-code-reviewer` subagent (per Constitution IX + Phase 3 lesson).
- Each PR's bus-stop checkpoint: after merge, regen the count from gate (3), update CLAUDE.md SPECKIT marker with the running progress (e.g. "Phase 4 PR P2 merged — 9/28 sites remaining").

**Alternatives considered**:

- **Big-bang (1 PR)**: rejected per FR-013 reasoning.
- **File-by-file (8 PRs)**: rejected for over-fragmentation; gives no benefit over grouped clusters for files with 1–2 call sites.
- **Bigger clusters (3 PRs instead of 5)**: considered — `Surface + Boot+Diagnostics` as one PR, `Streaming + Secondary` as another, `Cleanup` as third. Rejected because the first PR would re-introduce review-burden (≥ 19 call sites in one diff) and the second would conflate streaming-pattern complexity with leaf-render simplicity. The 5-PR split is tighter cohesion.

---

## Summary of decisions

| Question | Decision | Spec impact |
|---|---|---|
| R-1: Streaming model | Use existing `LayoutScheduler` with ref-equality short-circuit; producer closes over accumulating state | Confirms FR-003 (visual parity in streaming), FR-004 (no perf regression); no new framework primitive |
| R-2: DrawOp ABI | Keep `RawAnsi`-only flow; do NOT extend `DrawOp` | Confirms FR-009 (no Surface ABI change); `RenderBridge.toDrawOp` is the bridge |
| R-3: Visual regression | Snapshot per render path + manual smoke; PTY deferred to #937 | Implements FR-012 (regression-detection mechanism) with concrete pattern |
| R-4: PR sequencing | 5 cohesive PRs (P1 Surface+Bridge → P2 Boot+Diag → P3 Streaming → P4 Secondary → P5 Cleanup) | Implements FR-013 (≥ 3 PRs); concrete grouping locked |

All four decisions are made through informed analysis with explicit reasoning — none required dual-agent debate. If `/speckit-implement` surfaces contraindicating evidence (e.g. ref-equality short-circuit doesn't work for some streaming case, or snapshot tests turn out non-deterministic across CI OSes), this document is the place to record re-litigation.

## Best-practice references consulted

- **Strangler-fig pattern** (Fowler, 2004): the bridge-module relocation is textbook strangler-fig — the package owns the abstract output, consumers own the mapping to their internal types. Validated against the deferred Phase 5 spec's R-1 decision (independent agreement).
- **Snapshot/approval testing**: well-established pattern in F# (Verify, ApprovalTests.NET, Snapshooter). Custom `File.ReadAllText` + `Assert.Equal` chosen here over a library because the format is trivial (`.txt`) and adding a snapshot library would introduce a dependency for a 9-fixture suite.
- **Phase 3 PR cadence** (in-house): 5 PRs over 6 days with 6 BLOCKERs caught pre-push proved the cluster-sized PR rhythm. Phase 4 follows the same cadence; the calibration is empirical, not theoretical.
- **`MailboxProcessor` ref-equality short-circuit pattern**: Phase 3 P5 `LayoutScheduler` documented this pattern explicitly in `Scheduler.fsi`. Phase 4 consumes it as-is, no rework.
