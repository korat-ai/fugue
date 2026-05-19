---

description: "Task list for Phase 4 — CLI Layout Migration"
---

# Tasks: Migrate Fugue.Cli UI to the Layout Framework

**Input**: Design documents from `/specs/005-cli-layout-migration/`

**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓, quickstart.md ✓

**Tests**: MANDATORY per Constitution Principle I (Test Coverage Discipline). Every migrated render path lands with a snapshot test (visual-parity probe). The new bridge module lands with happy + error path tests. The closed rendering bug (FR-008) lands with a regression test that fails on pre-fix code.

**Organization**: Tasks are grouped by **PR cluster** rather than by individual user story. This feature's user stories are cross-cutting (US1 visual parity + US2 zero direct calls hold across every PR); each PR delivers a self-contained slice of all stories simultaneously. Story labels appear in task descriptions to show which stories each task serves. PR clusters follow `contracts/pr-sequencing.md`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task serves (US1, US2, US3, US4, US5)
- Include exact file paths in descriptions

## Path Conventions

- **F# source**: `src/Fugue.Adapters.Console/` (the framework package) and `src/Fugue.Cli/` (the REPL — primary work area)
- **Tests**: `tests/Fugue.Tests/` (REPL-side tests including snapshot suite) and `tests/Fugue.Adapters.Console.Tests/` (framework-side, mostly unchanged in Phase 4)
- **Snapshots**: `tests/Fugue.Tests/Snapshots/` (new directory, frozen ANSI fixtures)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish the migration baseline. The monorepo is already initialised; setup is minimal — record baseline metrics so progress is measurable per PR.

- [ ] T001 Capture pre-migration baseline metrics in `specs/005-cli-layout-migration/baseline.txt`: (a) Spectre count `rg -c 'Spectre\.Console|AnsiConsole' src/Fugue.Cli/*.fs` ≈ 28; (b) total test count from `dotnet test --no-build --list-tests | wc -l` ≈ 837+; (c) baseline AOT publish size `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64 && du -h src/Fugue.Cli.Aot/bin/Release/net10.0/osx-arm64/publish/fugue-aot`. Commit baseline.txt to the branch.

**Checkpoint**: Baseline captured. Ready for Foundational phase.

---

## Phase 2: Foundational (Blocking Prerequisites — PR P1 prep)

**Purpose**: Create the `RenderBridge` module, snapshot test scaffolding, and the framework-package decoupling. Every subsequent user-story work depends on this phase completing.

**⚠️ CRITICAL**: No PR P2–P5 work can begin until this phase is complete and PR P1 (which contains these tasks plus the Surface.fs migration) has merged.

### Bridge module + framework decoupling

- [ ] T002 Drop `<ProjectReference Include="..\Fugue.Surface\Fugue.Surface.fsproj" />` from `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj` (FR-005 / SC-006).
- [ ] T003 Remove `toDrawOp` function definition from `src/Fugue.Adapters.Console/Renderer.fs` (delete the 4-line function body that maps `Composition → Fugue.Surface.DrawOp.RawAnsi`).
- [ ] T004 Remove `toDrawOp` declaration from `src/Fugue.Adapters.Console/Renderer.fsi` (delete the 4-line val declaration).
- [ ] T005 [P] Create `src/Fugue.Cli/RenderBridge.fsi` per `contracts/render-bridge.fsi`: module signature with `val toDrawOp : RenderContext -> Composition -> Result<DrawOp, RenderError>` and accompanying doc comments.
- [ ] T006 [P] Create `src/Fugue.Cli/RenderBridge.fs` (~6 LOC implementation): `let toDrawOp ctx comp = Renderer.toRawAnsi ctx comp |> Result.map DrawOp.RawAnsi`.
- [ ] T007 Add `<Compile Include="RenderBridge.fsi" />` and `<Compile Include="RenderBridge.fs" />` to `src/Fugue.Cli/Fugue.Cli.fsproj` in the correct ordering (after the adapter dependencies are imported, before `LayoutHost.fs`).

### Snapshot test infrastructure

- [ ] T008 [P] Create directory `tests/Fugue.Tests/Snapshots/` and add `<None Include="Snapshots/**/*.txt" CopyToOutputDirectory="PreserveNewest" />` to `tests/Fugue.Tests/Fugue.Tests.fsproj`.
- [ ] T009 [P] Create `tests/Fugue.Tests/RenderSnapshotTests.fs` scaffold: module declaration, `open` imports, private `readSnapshot` helper that loads `Snapshots/<name>.txt` via `File.ReadAllText`. Add `<Compile Include="RenderSnapshotTests.fs" />` to `tests/Fugue.Tests/Fugue.Tests.fsproj` after the existing test files.

### Bridge tests (Principle I: happy + error)

- [ ] T010 [P] Create `tests/Fugue.Tests/RenderBridgeTests.fs` with two `[<Fact>]` tests: (T-RB-1 happy) `toDrawOp ctx80 (markerLeaf "OK")` returns `Ok (DrawOp.RawAnsi <string containing "OK">)`; (T-RB-2 error) invalid `RenderContext` (width = 0) propagates `Error (RenderError.DegenerateWidth 0)`. Add `<Compile Include="RenderBridgeTests.fs" />` to `tests/Fugue.Tests/Fugue.Tests.fsproj`.

### Foundation verification

- [ ] T011 Run `dotnet build -c Release` and `dotnet test -c Release --no-build`. Both must be green. Framework package now has zero `Fugue.Surface` references; new bridge module passes its tests. Commit the foundation in a single commit `feat(005/foundation): RenderBridge + snapshot scaffold + framework decoupling`.

**Checkpoint**: Foundation ready. PR P1's remaining tasks (Surface.fs migration) can now proceed in the same branch/PR.

---

## Phase 3: PR P1 — Surface.fs Migration (US1 + US2)

**Goal**: Migrate the largest single render surface (Surface.fs, 13 call sites — the DrawOp executor). Wire `LayoutHost.fs` as the production canonical mount point. Establish the per-PR snapshot pattern. After this PR, the deferred Phase 5 condition 1 ("framework package zero internal refs") is fully satisfied.

**Independent Test**: Run canonical scripted session (data-model.md §4) against the post-PR binary. Status bar, markdown panel, diff panel, prompt area, and toast notifications render visually identically to pre-migration. `rg -c 'Spectre\.Console|AnsiConsole' src/Fugue.Cli/*.fs | grep -v 'RenderBridge\.fs' | awk -F: '{ sum += $2 } END { print sum }'` returns 15.

### Inventory + migration

- [ ] T012 [US1] [US2] Inventory `src/Fugue.Cli/Surface.fs` Spectre call sites using `fslangmcp` semantic queries (`fcs_file_outline` then `workspace_symbol` for `AnsiConsole`, `Markup`, `Panel`, `Table` references). Record the inventory permanently in `specs/005-cli-layout-migration/inventory-surface.md` — one row per call site with: line number, rendering purpose, target adapter primitive, status (pending / migrated). This file survives PR P1 merge as the audit trail; later PRs append their own per-file inventories (`inventory-render.md` etc.).
- [ ] T013 [US1] [US2] Migrate Surface.fs status-bar render path: replace direct Spectre calls with `Composition` built from `Stack` + `Dock` primitives; route through `RenderBridge.toDrawOp` → `Surface.write`.
- [ ] T014 [US1] [US2] Migrate Surface.fs markdown-panel render path: use `Composition.panel Border.Rounded label inner` + appropriate `Style` constructors.
- [ ] T015 [US1] [US2] Migrate Surface.fs diff-panel render path: use `Composition` with `Columns` or `Stack` of styled hunks.
- [ ] T016 [US1] [US2] Migrate Surface.fs toast/notification render path: use `Composition.Leaf (Primitive.Styled (toastStyle, SafeText.ofLiteral msg))`.
- [ ] T017 [US1] [US2] Migrate Surface.fs prompt-area render path: ensure `LayoutHost.mount` is the wiring point; remove any leftover direct `AnsiConsole.Write` calls.
- [ ] T018 [US1] Wire `LayoutHost.mount` as the production canonical mount point in `src/Fugue.Cli/Repl.fs` (replace the placeholder no-op `LayoutHost` call if present; remove pre-Phase-3 direct Surface writes that were left in place during Phase 3 P5).
- [ ] T019 [US1] [US2] Update Surface.fs `open` declarations: remove `open Spectre.Console`, add `open Fugue.Adapters.Console` and (if Stack/Dock used) `open Fugue.Adapters.Console.Layout`.

### Snapshot fixtures + tests (Principle I)

- [ ] T020 [P] [US1] Create snapshot fixture `tests/Fugue.Tests/Snapshots/surface_status_bar.txt` per `contracts/snapshot-fixture-shape.md` Lifecycle § Creation procedure: build composition for known status-bar input, capture `Renderer.toRawAnsi` output, visually verify against pre-migration binary, commit the bytes.
- [ ] T021 [P] [US1] Create snapshot fixture `tests/Fugue.Tests/Snapshots/surface_markdown_basic.txt` (markdown panel with 1 heading + 2 paragraphs + 1 code block).
- [ ] T022 [P] [US1] Create snapshot fixture `tests/Fugue.Tests/Snapshots/surface_diff_basic.txt` (3-line diff: 1 added, 1 removed, 1 context).
- [ ] T023 [P] [US1] Add three `[<Fact>]` snapshot tests to `tests/Fugue.Tests/RenderSnapshotTests.fs` — one per fixture (T020–T022). Each test builds the same composition the fixture was created from, calls `Renderer.toRawAnsi ctx80`, asserts byte-equality against the fixture.

### Per-PR gates (per `contracts/pr-sequencing.md` G1–G6)

- [ ] T024 [US1] [US2] Local gate verification: run `dotnet build -c Release`, `dotnet test -c Release --no-build`, `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64`. All green. Spectre count expected = 15.
- [ ] T025 [US1] Manual smoke against canonical scripted session (data-model.md §4) on macOS. Tick off 9 session steps in upcoming PR body.

### Review + ship

- [ ] T026 Dispatch `engineering-code-reviewer` subagent on the staged P1 diff per `contracts/pr-sequencing.md` G7. Brief: focus on visual-parity preservation, `SafeText` discipline (Principle X), zero accidental `Fugue.Surface` re-introduction in the package, and snapshot-fixture sensibility. Address any BLOCKER findings.
- [ ] T027 Push branch `005-cli-layout-migration` (current). Open PR with title `feat(005-P1): RenderBridge + Surface.fs migration (13/28 sites, foundation)` and body filled per `contracts/pr-sequencing.md` PR body template.
- [ ] T028 Wait for CI green (G8) on all 3 OS. Merge with `gh pr merge <PR#> --merge --delete-branch`. Sync local main: `git checkout main && git pull --ff-only`.
- [ ] T029 Update CLAUDE.md SPECKIT marker per `data-model.md §8` to "Phase 4 PR P1 merged — Surface.fs + RenderBridge done; 15/28 sites remaining". Commit + push to main.

**Checkpoint**: PR P1 merged. Foundation + Surface.fs done. 15/28 sites remain. PR P2 can now branch off main.

---

## Phase 4: PR P2 — Boot + Diagnostics Migration (US1 + US2)

**Goal**: Migrate `Render.fs` (5 sites — boot banner, version, `/doctor` output) + `Doctor.fs` (1 site — `/doctor` interactive details). Smallest cluster after P1.

**Independent Test**: Run `fugue --version`, `fugue` (boot banner displays), `fugue` then `/doctor` (diagnostic output). Visual parity vs pre-P2. Spectre count = 9.

### Branch setup

- [ ] T030 Create branch `005-p2-boot-diagnostics` off latest `main`: `git checkout main && git pull --ff-only && git checkout -b 005-p2-boot-diagnostics`.

### Migration

- [ ] T031 [US1] [US2] Inventory + migrate `src/Fugue.Cli/Render.fs` 5 Spectre call sites following the `quickstart.md` per-file procedure. Use `Composition.Leaf` + `Style` + `SafeText` for boot/version output; `DataDisplays.Grid` if any tabular content present.
- [ ] T032 [US1] [US2] Inventory + migrate `src/Fugue.Cli/Doctor.fs` 1 Spectre call site. Likely a `DataDisplays.Grid` for the provider/model check table.
- [ ] T033 [US1] [US2] Update `open` declarations in both files: remove `open Spectre.Console`, add `open Fugue.Adapters.Console` (+ `.Layout` if needed).

### Snapshots + tests

- [ ] T034 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/render_boot.txt` (boot banner + version line for a fixed version string `0.0.0-test`).
- [ ] T035 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/doctor_output.txt` (one provider listed as OK, one as FAIL with a deterministic error string).
- [ ] T036 [P] [US1] Add two `[<Fact>]` snapshot tests to `RenderSnapshotTests.fs` — one per fixture.

### Per-PR gates + ship

- [ ] T037 [US1] [US2] Local gates G1–G6: build, test, Spectre count = 9, AOT publish clean.
- [ ] T038 Dispatch `engineering-code-reviewer` on the staged P2 diff.
- [ ] T039 Push, open PR `feat(005-P2): Render.fs + Doctor.fs migration (6 sites; 9/28 remaining)`, wait CI, merge.
- [ ] T040 Sync main, update CLAUDE.md SPECKIT marker post-P2.

**Checkpoint**: PR P2 merged. 9/28 sites remain.

---

## Phase 5: PR P3 — Streaming Markdown Migration (US1 + US2)

**Goal**: Migrate `MarkdownRender.fs` (3 sites — markdown response render) + `StreamRender.fs` (1 site — per-token append). Exercises the R-1 scheduler-streaming pattern end-to-end. Most likely to surface streaming-smoothness regressions if R-1 assumption is wrong.

**Independent Test**: Start REPL, send a message that produces a multi-paragraph markdown response, observe streaming-token append smoothness matches pre-migration. Spectre count = 5.

### Branch setup

- [ ] T041 Create branch `005-p3-streaming-markdown` off latest `main`.

### Streaming pattern implementation

- [ ] T042 [US1] [US2] Implement `StreamingProducer` type and producer factory in `src/Fugue.Cli/StreamRender.fs` per `data-model.md §5`: closure over mutable `StreamingState` ref, returns `Composition` built from current accumulated markdown text.
- [ ] T043 [US1] [US2] Mount streaming producer via `LayoutHost.mount producer 16` in `src/Fugue.Cli/Repl.fs` at the right lifecycle point (stream-begin → mount; stream-end → keep scheduler running, producer just stops mutating state; ref-equality short-circuits subsequent ticks).
- [ ] T044 [US1] [US2] Migrate `MarkdownRender.fs` 3 Spectre call sites: replace direct `AnsiConsole.Markup` calls with Markdig-output-to-`Composition` mapper (lift Markdig AST to `Composition.panel` / `Composition.Leaf` per-block-element).
- [ ] T045 [US1] [US2] Migrate `StreamRender.fs` 1 token-arrival call site: token append updates `StreamingState.AccumulatedMarkdown`, calls `Scheduler.trigger sched`. Remove any direct `AnsiConsole.Write` for incremental tokens.
- [ ] T046 [US1] [US2] Update `open` declarations in both files.

### Snapshots + tests + property test for streaming

- [ ] T047 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/markdown_streaming_complete.txt` (complete streamed markdown — same content as `surface_markdown_basic` but built through the streaming producer path, asserting equivalent final state).
- [ ] T048 [P] [US1] Add `[<Fact>]` snapshot test for streaming completion in `RenderSnapshotTests.fs`.
- [ ] T049 [P] [US1] Add `[<Property(MaxTest=1)>]` test in `RenderSnapshotTests.fs`: streaming-idle-state ref-equality short-circuit. Per `data-model.md §5` validation rule: after N idle ticks (no token arrival), producer is called exactly N+1 times but `onFrame` is called once. Implement by mocking `LayoutScheduler` callback collection and asserting ref-equality coalescing fires.

### Per-PR gates + ship

- [ ] T050 [US1] [US2] Local gates G1–G6. Spectre count = 5. Manual smoke: focus on streaming-smoothness perception (key risk for this PR).
- [ ] T051 Dispatch `engineering-code-reviewer` on P3 diff. Brief: emphasise R-1 correctness — does the producer hold ref-equality when state unchanged? Are there any `new Composition` allocations on every call that would defeat the short-circuit?
- [ ] T052 Push, open PR `feat(005-P3): MarkdownRender + StreamRender migration with scheduler streaming (4 sites; 5/28 remaining)`, wait CI, merge.
- [ ] T053 Sync main, update CLAUDE.md SPECKIT marker post-P3.

**Checkpoint**: PR P3 merged. 5/28 sites remain. Streaming pattern dogfooded.

---

## Phase 6: PR P4 — Secondary Surfaces + Close One Bug (US1 + US2 + US4)

**Goal**: Migrate three small leaf render surfaces (`DiffRender.fs`, `StackTraceRender.fs`, `Picker.fs`) AND auto-close or substantially simplify ≥ 1 of the three target rendering bugs (#910, #913, #934 per FR-008 / SC-007).

**Independent Test**: Run REPL, trigger an Edit tool (diff renders), trigger an exception (stack trace renders), run `/turns` (picker renders with arrow nav). Spectre count = 0. The selected rendering bug either no longer reproduces OR is fixed in this PR with ≤ 30 LOC of change.

### Branch setup

- [ ] T054 Create branch `005-p4-secondary-render` off latest `main`.

### Migration (3 small files)

- [ ] T055 [US1] [US2] Migrate `src/Fugue.Cli/DiffRender.fs` 2 Spectre call sites. Likely shape: `Composition` with `Columns` (side-by-side hunks) or `Stack` (unified hunks) of styled lines.
- [ ] T056 [US1] [US2] Migrate `src/Fugue.Cli/StackTraceRender.fs` 2 Spectre call sites. Use `Console.renderException settings ex` (already adapter-wrapped in Phase 2 P5).
- [ ] T057 [US1] [US2] Migrate `src/Fugue.Cli/Picker.fs` 1 Spectre call site. `Stack` with selected-row `Style` highlight (e.g. `Style.background Colour.dim`).
- [ ] T058 [US1] [US2] Update `open` declarations in all three files.

### Snapshots + tests

- [ ] T059 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/diff_render_edit.txt` (3-line edit diff: 1 added "+ new line", 1 removed "- old line", 1 context "  context").
- [ ] T060 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/stacktrace_render_exception.txt` (synthetic `ArgumentNullException` with deterministic message + 2 stack frames).
- [ ] T061 [P] [US1] Create snapshot `tests/Fugue.Tests/Snapshots/picker_turns_basic.txt` (5 turns, 3rd selected).
- [ ] T062 [P] [US1] Add three `[<Fact>]` snapshot tests in `RenderSnapshotTests.fs`.

### Close one rendering bug (US4 / FR-008)

- [ ] T063 [US4] Pick the most-likely-auto-closed bug: **#910 scroll-region reset**. Hypothesis: Layout doesn't issue `Console.Clear()`; if Surface.fs migration in P1 already eliminated that call path, the bug is gone. Reproduce on pre-migration binary, then attempt to reproduce on post-P3 binary (current state). If gone → write regression test in `tests/Fugue.Tests/Bug910RegressionTests.fs` that asserts no `\x1b[2J` (clear-screen escape) appears in a multi-redraw sequence. If still present → file a fix ≤ 30 LOC in `src/Fugue.Cli/Surface.fs`, ship in this PR.
- [ ] T064 [US4] If #910 doesn't yield, try **#913 model description overlap** next (similar hypothesis: centralised status-bar Dock primitive should fix positioning). If neither yields, try **#934 multi-line corruption**. SC-007 requires only 1 of 3 closed; do not let perfect block good.
- [ ] T065 [US4] Add regression test for the closed bug per Principle I (fails on pre-fix code; passes on post-fix code).

### Per-PR gates + ship

- [ ] T066 [US1] [US2] Local gates G1–G6. Spectre count = **0** (migration complete). AOT publish clean.
- [ ] T067 Dispatch `engineering-code-reviewer` on P4 diff. Brief: this is the last migration PR; verify SC-001 invariant holds (zero direct calls except `RenderBridge.fs`), verify the closed-bug evidence is convincing.
- [ ] T068 Push, open PR `feat(005-P4): secondary surfaces migration + close #910 (5 sites; 0/28 remaining — migration COMPLETE)`, wait CI, merge.
- [ ] T069 Sync main, update CLAUDE.md SPECKIT marker post-P4 — "Phase 4 migration COMPLETE — 0/28 direct Spectre calls. Awaiting soak period for PR P5 (legacy fallback removal)."

**Checkpoint**: PR P4 merged. Migration COMPLETE (0/28 direct Spectre calls in `src/Fugue.Cli/*.fs` except `RenderBridge.fs` and `LayoutHost.fs`). Awaiting ≥ 14 calendar days of soak time on `main` with zero rendering-regression issues before P5 (per spec FR-007).

---

## Phase 7: PR P5 — Legacy Fallback Cleanup (US5) — AFTER SOAK PERIOD

**Goal**: Remove the `FUGUE_LEGACY_RENDER` env-var fallback from `Repl.fs` and corresponding legacy paths from `Surface.fs`. Closes #962.

**Independent Test**: `rg 'FUGUE_LEGACY_RENDER|legacyRender' src/` returns zero matches. Setting the env var to any value has no effect on REPL behaviour.

**Gate**: This phase MUST NOT begin until ≥ 14 calendar days have elapsed since PR P4 merge AND zero rendering-regression issues have been filed against the post-P4 binary during that window AND maintainer has explicitly approved removal in writing (per spec FR-007 + CEO escalation per Constitution VII).

### Soak verification + branch setup

- [ ] T070 [US5] Verify soak conditions per spec FR-007: (a) **≥ 14 calendar days** have elapsed since PR P4's merge commit date — verify with `git log --format='%aI' <P4_merge_sha> -1` and a date-arithmetic check; (b) `gh issue list -R korat-ai/fugue --search "rendering regression in:title,body created:>=<P4_merge_date>" --state all` returns zero hits; (c) explicit user (CEO) confirmation requested and granted in writing. Document the verification (the three concrete checks + their outcomes + the user's confirmation text) in `specs/005-cli-layout-migration/soak-verification.md`.
- [ ] T071 [US5] Create branch `005-p5-legacy-cleanup` off latest `main`.

### Removal

- [ ] T072 [US5] Remove `FUGUE_LEGACY_RENDER` environment variable read from `src/Fugue.Cli/Repl.fs`. Locate via `rg "FUGUE_LEGACY_RENDER" src/Fugue.Cli/Repl.fs`; delete the read and any conditional branching it controls.
- [ ] T073 [US5] Remove legacy code paths from `src/Fugue.Cli/Surface.fs` (the branches that the `legacyRender` flag selected when truthy). Identify via `rg "legacyRender" src/Fugue.Cli/Surface.fs`.
- [ ] T074 [US5] Verify removal: `rg "FUGUE_LEGACY_RENDER|legacyRender" src/` returns zero matches. Spectre count remains 0 (no accidental re-introduction).

### Per-PR gates + ship

- [ ] T075 [US5] Local gates G1–G3, G6, G7, G8 (G4–G5 N/A — no render-path changes). Spectre count still 0.
- [ ] T076 Dispatch `engineering-code-reviewer` on P5 diff. Brief: focus on completeness (no orphaned references to the removed env var elsewhere in source or docs).
- [ ] T077 Push, open PR `chore(005-P5): remove FUGUE_LEGACY_RENDER fallback (closes #962)`, wait CI, merge.
- [ ] T078 Sync main, update CLAUDE.md SPECKIT marker — "Phase 4 COMPLETE — legacyRender fallback removed; deferred Phase 5 condition 1 satisfied (gh-965 condition 1 ✓)."
- [ ] T079 [US5] Close GitHub issue #962 with a comment referencing the merged PR.

**Checkpoint**: PR P5 merged. Phase 4 fully complete. Deferred Phase 5 condition 1 satisfied — Phase 5 can be reopened when conditions 2+3 also met.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Final SC verification, gh-965 update, dogfooding documentation, and any cleanup that surfaces only after the full migration is in place.

- [ ] T080 [US2] Add SC-001 / SC-008 verification test in `tests/Fugue.Tests/ZeroSpectreCallTest.fs` (new file — REPL-side standing invariant; deliberately NOT in `tests/Fugue.Adapters.Console.Tests/` because that project is framework-side and shouldn't reach into `src/Fugue.Cli/*.fs` paths). The test uses `File.ReadAllText` + regex to walk every `.fs` file under `src/Fugue.Cli/` and assert total `Spectre.Console` / `AnsiConsole` reference count = 0, excluding `LayoutHost.fs` and `RenderBridge.fs` (the two designated bridge files per spec FR-002). This is the standing invariant gate — future regressions get caught at test time, not at review time. Add `<Compile Include="ZeroSpectreCallTest.fs" />` to `tests/Fugue.Tests/Fugue.Tests.fsproj`.
- [ ] T081 [US3] Add a one-paragraph "Production examples" section to `src/Fugue.Adapters.Console/README.md` (if README exists from a future Phase 5; if not, add a comment block at the top of `src/Fugue.Cli/Surface.fs` pointing future contributors at it as the canonical multi-region layout example). Per `data-model.md §8` and US3 acceptance.
- [ ] T082 [US3] Verify SC-006: framework package contains no internal references. `rg "Fugue\.(Surface|Cli|Core|Tools|Agent)" src/Fugue.Adapters.Console/` returns zero matches (`Layout/` and the rest of the adapter, after T002–T004, should be clean). Update `specs/002-spectre-full-coverage/coverage-matrix.md` to note Phase 4 dogfooding complete.
- [ ] T083 Comment on `gh-965` (deferred Phase 5 tracking issue): "Condition 1 — dogfooding — is now satisfied as of Phase 4 PR P5 merge. Conditions 2 (≥ 2 external demand signals) and 3 (bandwidth honesty) remain open. Spec/research/plan from Phase 5 deferred state remain valid as the baseline."
- [ ] T084 Final SC verification across the suite: SC-001 ✓ (zero count — T080 standing test), SC-002 ✓ (visual parity confirmed across manual smoke + snapshots), SC-003 ✓ (≥ 837 tests pass on all 3 OS), SC-004 ✓ (AOT publish clean on all 3 runtimes), SC-005 ✓ (legacyRender gone), SC-006 ✓ (zero internal refs in package), SC-007 ✓ (≥ 1 bug closed), SC-008 ✓ (standing invariant test enforces zero direct underlying-library refs — T080), SC-009 ✓ (5 PRs merged). Document the verification in `specs/005-cli-layout-migration/sc-verification.md`.

### Coverage-gap remediation tasks (added 2026-05-19 after /speckit-analyze)

- [ ] T086 [US1] **FR-004 perf gate (was C1 gap)**: in Phase 8 Polish, run the prior phase's existing performance benchmark against post-P4 main: `dotnet test --filter "Category=perf"`. The relevant test lives in `tests/Fugue.Adapters.Console.Tests/Layout/BenchmarkTests.fs` and is named in source as `T079 — SC-006: typical Fugue UI layout tree renders in under 5 ms` (the `T079` prefix is the Phase 3 task-id baked into the test name string — NOT a reference to Phase 4's T079). Capture the median + p95 latencies, assert median ≤ 10 ms (the spec FR-004 (a) ceiling). Document the measurement in `specs/005-cli-layout-migration/perf-verification.md` alongside the Phase 3 baseline for comparison. If median > 10 ms, this is a FR-004 violation — file a follow-up issue and do NOT declare Phase 4 complete until resolved.
- [ ] T087 [US2] **FR-009 DrawOp ABI assertion (was C2 gap)**: add `[<Fact>]` test `DrawOp ABI is unchanged from Phase 4 baseline` in `tests/Fugue.Tests/DrawOpAbiTests.fs` (new file). Use `Microsoft.FSharp.Reflection.FSharpType.GetUnionCases<DrawOp>` to enumerate every union case, assert the case names exactly equal the pre-migration baseline list (hard-coded list in the test reflecting the `DrawOp` DU shape at SHA `52c7853`). If a future PR adds or removes a `DrawOp` case, this test fails — forcing an explicit conversation about whether the ABI change is intentional. Add `<Compile Include="DrawOpAbiTests.fs" />` to `tests/Fugue.Tests/Fugue.Tests.fsproj`.
- [ ] T088 [US1] **FR-004 streaming-latency probe (was A1 gap)**: add a property test `streaming-token-to-render p95 latency` in `tests/Fugue.Tests/StreamingLatencyTests.fs` (new file, or co-located with `RenderSnapshotTests.fs`). Test scenario: drive `StreamingProducer` with 200 synthetic tokens at 10 ms intervals, capture timestamps for (a) token enqueued, (b) `onFrame` invoked with the composition containing that token, compute per-token latencies, assert p95 ≤ 50 ms (spec FR-004 (b)). This is the automated probe for "no perceptible stutter" — turns observer-judgement into a quantitative bound.

### Final task

- [ ] T085 Mark Phase 4 COMPLETE in CLAUDE.md SPECKIT marker. Move Phase 4 section under `---` divider (demoted like Phase 3 was after its completion). Active Spec Kit plan field becomes empty pending the next feature.

---

## Dependencies

```
T001 (setup baseline)
  ↓
Phase 2 Foundation (T002–T011) — RenderBridge + Snapshots + framework decoupling
  ↓
Phase 3 PR P1 (T012–T029) — Surface.fs migration, ships RenderBridge to production
  ↓                                                                      ↓
Phase 4 PR P2 (T030–T040)                                                |
  ↓                                                                      |
Phase 5 PR P3 (T041–T053)                                                | (PRs ship sequentially;
  ↓                                                                      |  within each PR many tasks
Phase 6 PR P4 (T054–T069) — closes ≥ 1 rendering bug                     |  can run in parallel)
  ↓                                                                      |
[SOAK ≥ 14 calendar days per FR-007]                                     |
  ↓                                                                      |
Phase 7 PR P5 (T070–T079) — legacy cleanup                               |
  ↓                                                                      |
Phase 8 Polish (T080–T085) — SC verification + cross-cutting             ↓
```

## Parallel execution opportunities

Within each PR phase, the following tasks can run in parallel (marked `[P]` above):

**Phase 2 Foundation**:
- T005 (RenderBridge.fsi) ∥ T006 (RenderBridge.fs) — different files
- T008 (Snapshots dir setup) ∥ T009 (RenderSnapshotTests scaffold) ∥ T010 (RenderBridgeTests) — all different files; can be dispatched as three concurrent subagent tasks if desired

**Phase 3 PR P1** (after T012–T019 migration is done):
- T020 (status_bar fixture) ∥ T021 (markdown_basic fixture) ∥ T022 (diff_basic fixture) ∥ T023 (snapshot tests) — fixtures are different files

**Phase 4 PR P2** (after T031–T033 migration done):
- T034 (boot fixture) ∥ T035 (doctor fixture) ∥ T036 (tests)

**Phase 5 PR P3** (after T042–T046 migration done):
- T047 (streaming fixture) ∥ T048 (streaming test) ∥ T049 (property test)

**Phase 6 PR P4** (after T055–T058 migration done):
- T059 (diff fixture) ∥ T060 (stacktrace fixture) ∥ T061 (picker fixture) ∥ T062 (tests)

## Implementation strategy

**MVP slice = Phase 2 + Phase 3** (PR P1 alone). After T029, the framework is dogfooded for the largest single render surface (Surface.fs) AND the framework package is decoupled from Fugue-internal code. This is enough to satisfy the deferred Phase 5 condition 1 partially (foundation in place) and to validate the migration pattern before committing to the remaining ~15 sites.

**Incremental delivery**: PRs P2 → P3 → P4 ship in order, each independently reviewable and revertable. Each PR's "Independent Test" is the same canonical scripted session — failing the test on any PR halts further work until the regression is resolved.

**Soak gate before P5**: the FUGUE_LEGACY_RENDER fallback removal is deliberately delayed (≥ 14 calendar days per spec FR-007); this is not slack, it is intentional risk management. The fallback exists precisely as the rollback path for any unforeseen regression P1–P4 might introduce.

**CLAUDE.md SPECKIT marker updates**: tasks T029, T040, T053, T069, T078 each update the CLAUDE.md SPECKIT marker post-PR-merge — 5 edits total over the migration. Each edit busts the prompt cache for that session per Constitution Principle XI ("cacheable prefix"). This is **knowingly accepted cost**: Phase 3 followed the identical 5-update pattern with no observed problem; per-PR status visibility in CLAUDE.md is more valuable than the cache-hit savings of batching. If a future phase finds this cost more painful, batching to 1 update per 2 PRs is a valid alternative — document the change in CLAUDE.md when made.

## Subagent dispatch suggestions (per Constitution Principle III)

Each PR phase is a candidate for delegating to a specialist subagent:

- **PR P1 (Phase 3)**: `engineering-fsharp-developer` — large file, foundational; main agent monitors, runs gates locally before push.
- **PR P2 (Phase 4)**: `engineering-fsharp-developer` — small cluster, can dispatch with full quickstart.md as context.
- **PR P3 (Phase 5)**: `engineering-fsharp-developer` — emphasise R-1 streaming pattern correctness in brief; this PR's failure mode is subtle (ref-equality short-circuit broken).
- **PR P4 (Phase 6)**: `engineering-fsharp-developer` — also wears the bug-investigator hat for T063–T065; brief includes the three target bug links and FR-008 commitment.
- **PR P5 (Phase 7)**: trivial — main agent can execute inline; no subagent needed.

For every dispatch: include the project's F# tool discipline rules (use `fslangmcp` for semantic queries; `rg` for text-search; mandatory UX report on `fslangmcp` at end per CLAUDE.md).

## Tests overview

Per Constitution Principle I, the following test artefacts land with this feature:

| Test file | Purpose | Phase |
|---|---|---|
| `RenderBridgeTests.fs` | Happy + error path for new bridge module | Phase 2 (T010) |
| `RenderSnapshotTests.fs` | Per-render-path snapshot assertions (9 tests total) | Phases 3–6, incrementally |
| `Snapshots/*.txt` | 9 frozen ANSI fixtures | Phases 3–6, incrementally |
| `Bug910RegressionTests.fs` (or chosen bug's file) | Regression test for the closed rendering bug | Phase 6 (T065) |
| `ZeroSpectreCallTest.fs` | Standing invariant: SC-001 enforced at test time | Phase 8 (T080) |

Total new test count: ≈ 13 tests + 1 property test = 14 tests added, ≈ 20 KB of fixtures.
