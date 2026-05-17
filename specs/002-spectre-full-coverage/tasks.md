---

description: "Task list — Full Spectre.Console F# Coverage (Phase 2 adapter extension)"
---

# Tasks: Full Spectre.Console F# Coverage

**Input**: Design documents from `/specs/002-spectre-full-coverage/`

**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅,
contracts/ ✅, quickstart.md ✅, coverage-matrix.md ✅

**Tests**: MANDATORY for every behavioural change per Principle I of
`.specify/memory/constitution.md` (v1.1.0, Test Coverage Discipline,
NON-NEGOTIABLE). SC-007 mandates at least one property test (happy + Error
paths) and one snapshot or totality test for every new public adapter type.
No "ship without tests" carve-out — not for any user story.

**Organization**: Tasks are grouped by user story (one phase per story) to
enable independent implementation and testing. Each user story is its own PR.
P1 must merge before P2 can land cleanly (P2 primitives rely on the
`Composition.Foreign` internal case introduced by P1).

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel — different file from other `[P]`-marked tasks in
  the same phase, so two writers can work simultaneously without conflict. Does
  NOT imply "no logical dependency". A test task can be `[P]` even when it
  logically depends on the implementation task — the dependency runs through the
  build, not the writer's keyboard.
- **[Story]**: REQUIRED for user-story phase tasks (`[US1]`–`[US5]`). OMIT for
  Setup and Foundational phases. OMIT for the Polish phase.
- Every task description includes the exact file path (repo-relative from repo
  root).

## Path Conventions

- **Adapter library**: `src/Fugue.Adapters.Console/`
- **Test project**:    `tests/Fugue.Adapters.Console.Tests/`
- **Solution file**:   `Fugue.slnx` (repo root)
- **Build script**:    `Justfile` (repo root)

---

## Phase 1: Setup

**Purpose**: Add the new `spectre.console.json` package dependency, extend
`RenderError` with the three Phase 2 error cases, update the existing
totality test for `RenderError` exhaustiveness, add stub `<Compile Include>`
entries for all new Phase 2 paired `.fsi`/`.fs` files, and confirm AOT publish
stays clean. These are shared prerequisites that all five user-story PRs
build on — but they land as individual per-US-PR prefixes, not a single
monolithic PR. The task list captures them here for planning clarity.

- [ ] T001 Add `<PackageReference Include="Spectre.Console.Json" Version="0.49.*" />` to `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj` (data-model.md §0.2 resolution). Confirm `dotnet restore` succeeds and `Spectre.Console.Json.JsonText` is accessible.
- [ ] T002 [P] Extend `RenderError` DU in `src/Fugue.Adapters.Console/Errors.fsi` with three new cases (data-model.md §1): `UserCancelled of operation: string`, `NoInteractiveConsole of operation: string`, `ConcurrentLiveSession of console: string`. Update the matching `.fs` file (`src/Fugue.Adapters.Console/Errors.fs`) identically.
- [ ] T003 [P] Update any existing exhaustiveness match in the existing test files that pattern-matches on `RenderError` to add arms for the three new cases — specifically `tests/Fugue.Adapters.Console.Tests/FoundationTests.fs` and any other file that has a bare `match renderError with ...` without a wildcard. This is the Phase 1 backward-compat guard for SC-005.
- [ ] T004 Add stub `<Compile Include>` entries for all new Phase 2 module pairs to `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj`, in compile order after `Renderer.fsi`/`Renderer.fs`: `Renderable.fsi`, `Renderable.fs`, `DataDisplays.fsi`, `DataDisplays.fs`, `Live.fsi`, `Live.fs`, `Prompts.fsi`, `Prompts.fs`, `Console.fsi`, `Console.fs`. Each stub file contains only the `namespace Fugue.Adapters.Console` declaration so the project builds green before US-phase content fills them in.
- [ ] T005 [P] Verify `dotnet build -c Release` exits zero with zero warnings after T001–T004. Verify `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` exits zero with zero new trim warnings (SC-006 — adapter is JIT-only so the AOT closure is unaffected by construction, but verify to catch accidental reference chain additions).

**Checkpoint**: `dotnet build` green; `dotnet test tests/Fugue.Adapters.Console.Tests` runs and existing 83 tests pass unchanged; AOT publish clean; stub `.fsi`/`.fs` files compile.

---

## Phase 2: Foundational

**Purpose**: The only foundational task for Phase 2 is ensuring the `fsproj`
compile order is stable (done in T004 above). No new cross-cutting design
decisions are pending — all five R-decisions are closed in research.md. The
FACT_DISCOVERY workaround documented in
`tests/Fugue.Adapters.Console.Tests/Helpers.fs` (use `[<Property(MaxTest=1)>]`
returning `bool` as a Fact-equivalent) remains in effect; do NOT migrate it
in this phase.

- [ ] T006 Confirm the FACT_DISCOVERY workaround header comment in `tests/Fugue.Adapters.Console.Tests/Helpers.fs` is still accurate after the Phase 1 `xunit 2.9.3` pin. If the comment references an open tracking issue that is now resolved, update the comment. Otherwise, no code change needed — this task is a verification gate, not a code task.

**Checkpoint**: No additional foundational work; all five user-story phases can start after Phase 1.

---

## Phase 3: User Story 1 — Custom-renderable bridge (P1) 🎯 MVP

**Goal**: Expose an opaque `Renderable` type + `Renderable.fromSpectre` factory
+ `Composition.ofRenderable` embedding function, plus the internal
`Composition.Foreign` case in `Composition.fs`, so contributors can embed any
existing `IRenderable` into a `Composition` tree without leaking the Spectre
interface type. This PR is the blocker for all other Phase 2 user-story PRs
— it must merge first.

**Independent Test**: Take the `assistantFinalComposition` call-site sketched
in `quickstart.md §1`, wrap `MarkdownRender.toRenderable text` via
`Renderable.fromSpectre |> Composition.ofRenderable`, embed in a
`Composition.panel`, render via `Renderer.toRawAnsi`, and assert the output
contains both the panel border character and the inner content. Verify
`rg 'Spectre\.' tests/Fugue.Adapters.Console.Tests/RenderableTests.fs` returns
zero (the test itself has no Spectre references).

### Tests for User Story 1 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**
> A user story without happy-path AND Error/None-path tests cannot ship.

- [ ] T007 [P] [US1] Create `tests/Fugue.Adapters.Console.Tests/RenderableTests.fs`. Write a property test (`[<Property(MaxTest=1)>]`) that calls `Renderable.fromSpectre` with a real `Spectre.Console.Text` instance (a simple non-null `IRenderable`) and asserts the returned value is non-null and can be passed to `Composition.ofRenderable` without throwing. Happy path.
- [ ] T008 [P] [US1] In `tests/Fugue.Adapters.Console.Tests/RenderableTests.fs`, write a property test for the exception-boundary behaviour (FR-009): construct a custom `IRenderable` implementation (inline object expression) whose `Render` method raises an exception; call `Renderer.toRawAnsi ctx (Composition.ofRenderable (Renderable.fromSpectre throwingRenderable))` and assert the result is `Error (RenderFailed (typeName, _))` — not a propagated exception. Confirm `typeName` is non-empty (the captured type name).
- [ ] T009 [US1] In `tests/Fugue.Adapters.Console.Tests/RenderableTests.fs`, write a composition embedding test: build a `Composition.panel Border.Rounded None (Composition.ofRenderable (Renderable.fromSpectre inner))` where `inner` is a real `Spectre.Console.Text`; render via `Renderer.toRawAnsi`; assert the output contains both the rounded-border corner character (`╭`) and the inner text. This is the acceptance-scenario 1.1 gate.
- [ ] T010 [P] [US1] In `tests/Fugue.Adapters.Console.Tests/RenderableTests.fs`, write a totality test: assert that rendering a `null`-wrapping renderable (construct via `Renderable.fromSpectre null`) produces `Error (RenderFailed _)` and does NOT throw (data-model.md §2.2 null-acceptance rule).

### Implementation for User Story 1

- [ ] T011 [US1] Write `src/Fugue.Adapters.Console/Renderable.fsi` per `contracts/Renderable.fsi` contract sketch. Public surface: `type Renderable` (sealed); `module Renderable` with `val fromSpectre : Spectre.Console.Rendering.IRenderable -> Renderable`; `module Composition` with `val ofRenderable : Renderable -> Composition`. This is the SINGLE intentional Spectre type in a public `.fsi` (FR-008).
- [ ] T012 [US1] Write `src/Fugue.Adapters.Console/Renderable.fs`. Implement the private record `type Renderable = private { Backend: Spectre.Console.Rendering.IRenderable; TypeName: string }`. Implement `Renderable.fromSpectre` (captures `r.GetType().FullName` into `TypeName`; null argument accepted, `TypeName` = `"null"`). Implement `Composition.ofRenderable` (wraps into the internal `Composition.Foreign` case — see T013). Add `internal` accessor helpers `Renderable.backend` and `Renderable.typeName` for use by `Renderer.fs`.
- [ ] T013 [US1] Add the `internal` `Foreign of Renderable` case to the `Composition` DU in `src/Fugue.Adapters.Console/Composition.fs` (data-model.md §0.3). Do NOT add this case to `src/Fugue.Adapters.Console/Composition.fsi` — it must remain invisible to external pattern matches (no exhaustiveness warnings at call sites that cannot see the case). Add `Composition.ofRenderable : Renderable -> Composition` implementation (one-liner: `Foreign r`).
- [ ] T014 [US1] Extend `src/Fugue.Adapters.Console/Renderer.fs` `toRenderable` private helper with a new arm for `Composition.Foreign r`. The arm: calls `Renderable.backend r`; wraps the actual `IRenderable.Render` call in a `try/with` catching all exceptions; on exception returns `Error (RenderFailed (Renderable.typeName r, ex.Message))`. On success, returns `Ok` wrapping the backend `IRenderable` directly (no re-wrapping needed — the backend IS the `IRenderable` Spectre expects).
- [ ] T015 [US1] Run `just regen-fsi` (or the equivalent recipe from PR #942) to confirm the generated `.fsi` for `Composition.fsi` does NOT include the `Foreign` case. If the recipe exposes `Foreign`, narrow it by hand in `Composition.fsi`. Confirm `rg 'Spectre\.' src/Fugue.Adapters.Console/Composition.fsi` returns zero hits.
- [ ] T016 [US1] Verify SC-004: run `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` from the repo root and confirm exactly ONE match: the `IRenderable` argument in `Renderable.fsi`. Document the exact match line in the PR description.

**Checkpoint**: US1 PR shippable. `Renderable.fromSpectre` + `Composition.ofRenderable` work end-to-end; exception safety verified; 83 existing Phase 1 tests still green; new RenderableTests.fs green; SC-004 verified.

---

## Phase 4: User Story 2 — Data-display primitives (P2)

**Goal**: Expose typed F# wrappers for all eight data-display widgets (`Tree`,
`Json`, `BarChart`, `BreakdownChart`, `Calendar`, `FigletText`, `Grid`,
`TextPath`) with `Result`-returning smart constructors, all lowering into the
adapter's `Composition` tree via the `Composition.Foreign` path established by
US1. No new renderer arms are needed — the `Foreign` arm handles all eight.

**Independent Test**: For each primitive, one property test verifies the
`create` smart constructor returns `Ok` for a valid representative input and
the rendered `Composition.of<Widget>` produces non-empty ANSI output. A
separate property test verifies that each degenerate input (empty tree,
malformed JSON, negative chart value, ragged Grid rows) returns
`Result.Error InvalidArgument` without entering `Renderer.toRawAnsi`.

**Dependency**: Requires US1 (Phase 3) to be merged first — `Composition.Foreign`
and the `Renderable` bridge are how all eight widgets lower into compositions.

### Tests for User Story 2 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T017 [P] [US2] Create `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`. Write property tests for `Tree` smart constructor: (a) valid single-leaf tree returns `Ok` and renders non-empty output; (b) `Tree.create` of a root with depth > 1000 returns `Error (InvalidArgument ("Tree", _))`.
- [ ] T018 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `Json`: (a) valid JSON string (`"{}"`) returns `Ok` and renders non-empty output; (b) malformed JSON string returns `Error (InvalidArgument ("Json", _))`; (c) payload > 10 MiB returns `Error (InvalidArgument ("Json", _))` (use a 10 MiB + 1 byte string of spaces wrapped in quotes).
- [ ] T019 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `BarChart`: (a) valid item + chart (positive value, non-empty list, positive width) returns `Ok` and renders non-empty output; (b) `BarChartItem.create` with negative value returns `Error (InvalidArgument ("BarChartItem", _))`; (c) `BarChart.create` with empty items list returns `Error (EmptyComposition "BarChart: at least one item required")`.
- [ ] T020 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `BreakdownChart`: (a) valid items + width returns `Ok` and renders; (b) empty items list returns `Error (EmptyComposition _)`; (c) negative item value returns `Error (InvalidArgument ("BreakdownChartItem", _))`.
- [ ] T021 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `Calendar`: (a) valid year/month (e.g. 2026, 5) with empty events returns `Ok` and renders; (b) month = 0 returns `Error (InvalidArgument ("Calendar", _))`; (c) month = 13 returns `Error (InvalidArgument ("Calendar", _))`.
- [ ] T022 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `FigletText`: (a) `FigletFont.default_` + `FigletText.create` with short text returns `Ok` and renders non-empty output; (b) text of length 101 returns `Error (InvalidArgument ("FigletText", _))`.
- [ ] T023 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `Grid`: (a) 2 columns with 2 rows of exactly 2 cells each returns `Ok` and renders; (b) 2 columns with a row of 1 cell returns `Error (InvalidArgument ("Grid", _))` containing "ragged rows"; (c) empty columns list returns `Error (EmptyComposition _)`.
- [ ] T024 [P] [US2] In `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs`, write property tests for `TextPath`: (a) any non-null `SafeText` path value returns `Ok` and renders; (b) `null` path (passed as `SafeText.ofLiteral null` — degenerate) is handled gracefully (either `Ok` with empty content or `Error (InvalidArgument _)`, per implementation choice — document the chosen behaviour in a comment).
- [ ] T024a [P] [US2] Add parametric test in `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs` that for each new widget (`Tree`, `JsonText`, `BarChart`, `BreakdownChart`, `Calendar`, `FigletText`, `Grid`, `TextPath`) renders the same value at `colourEnabled=true` and `colourEnabled=false` through `Renderer.toRawAnsi` and asserts the colour-off output is identical to the colour-on output with all `"\x1b["` sequences stripped. Verifies edge case "Colour downgrade" per spec.md.
- [ ] T025 [US2] Write one snapshot test per widget in `tests/Fugue.Adapters.Console.Tests/DataDisplaysTests.fs` using the programmatic pattern from Phase 1 `CorpusSnapshotTests.fs` (not Verify.Xunit, due to the known FACT_DISCOVERY issue): for each widget, render at width 80 twice and assert the two outputs are byte-identical (determinism gate). One test covers all eight widgets sequentially — name it `` ``all data-display primitives render deterministically at fixed width`` ``.

### Implementation for User Story 2

- [ ] T026 [US2] Write `src/Fugue.Adapters.Console/DataDisplays.fsi` per `contracts/DataDisplays.fsi` contract sketch (data-model.md §3). Declares all eight opaque sealed types (`TreeNode`, `Tree`, `Json`, `BarChartItem`, `BarChart`, `BreakdownChartItem`, `BreakdownChart`, `CalendarEvent`, `Calendar`, `FigletFont`, `FigletText`, `GridColumn`, `Grid`, `TextPath`) and their companion modules. No Spectre types in any signature. RENDER-LAYER INVARIANT: every smart constructor accepting user-supplied content (`Tree` node labels, `JsonText` payload, `BarChartItem` labels, `Calendar` event titles, `FigletText` text, `Grid` cell content, `TextPath` segments) MUST take `SafeText`, not raw `string`. Verified by `.fsi` review and by FR-007.
- [ ] T027 [P] [US2] Implement `TreeNode` and `Tree` in `src/Fugue.Adapters.Console/DataDisplays.fs`: private record `TreeNode`, `leaf`/`branch` smart constructors (non-validating at construction; validation deferred to `Tree.create`). `Tree.create` validates depth ≤ 1000 via a DFS counter; returns `Error (InvalidArgument ("Tree", ...))` on depth overflow. Lowers to Spectre's `Spectre.Console.Tree` via `Renderable.fromSpectre` then `Composition.ofRenderable`. Implement `Composition.ofTree`.
- [ ] T028 [P] [US2] Implement `Json` in `src/Fugue.Adapters.Console/DataDisplays.fs`: `Json.create` parses via `System.Text.Json.JsonDocument.Parse` (catches `JsonException`), enforces 10 MiB limit. Stores a `Spectre.Console.Json.JsonText` value internally. Lowers via `Renderable.fromSpectre >> Composition.ofRenderable`. Implement `Composition.ofJson`.
- [ ] T029 [P] [US2] Implement `BarChartItem` and `BarChart` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.3. Validation rules as specified. Lowers to `Spectre.Console.BarChart` via `Renderable.fromSpectre >> Composition.ofRenderable`. Implement `Composition.ofBarChart`.
- [ ] T030 [P] [US2] Implement `BreakdownChartItem` and `BreakdownChart` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.4. Same validation pattern as `BarChart`. Lowers to `Spectre.Console.BreakdownChart`. Implement `Composition.ofBreakdownChart`.
- [ ] T031 [P] [US2] Implement `CalendarEvent` and `Calendar` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.5. Year/month range validation. Lowers to `Spectre.Console.Calendar`. Implement `Composition.ofCalendar`.
- [ ] T032 [P] [US2] Implement `FigletFont` and `FigletText` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.6. `FigletFont.default_` returns the Spectre default font. `FigletFont.fromFile` wraps `Spectre.Console.FigletFont` file-load with try/with. `FigletText.create` enforces length ≤ 100. Lowers via `Renderable.fromSpectre >> Composition.ofRenderable`. Implement `Composition.ofFigletText`.
- [ ] T033 [P] [US2] Implement `GridColumn` and `Grid` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.7. `GridColumn.create` clamps negative padding to 0 (no `Result` — cosmetic). `Grid.create` validates non-empty columns and equal row widths. Lowers to `Spectre.Console.Grid`. Implement `Composition.ofGrid`.
- [ ] T034 [P] [US2] Implement `TextPath` in `src/Fugue.Adapters.Console/DataDisplays.fs` per data-model.md §3.2.8. Tolerant constructor (no path-existence check). Lowers to `Spectre.Console.TextPath`. Implement `Composition.ofTextPath`.
- [ ] T035 [US2] Update `src/Fugue.Adapters.Console/Composition.fsi` to add the eight new `Composition.of<Widget>` factory signatures. Ensure the `DataDisplays.fsi` types (not Spectre types) appear as parameter types. Confirm `rg 'Spectre\.' src/Fugue.Adapters.Console/DataDisplays.fsi` returns zero.

**Checkpoint**: US2 PR shippable. All eight data-display primitives compile; smart constructors validate degenerate inputs; `Composition.of<Widget>` produces renderable compositions; 83 + US1 tests still green; new DataDisplaysTests.fs green.

---

## Phase 5: User Story 3 — Live/streaming displays (P3)

**Goal**: Expose callback-scoped session APIs for `Live`, `Status`, and
`Progress` (per research.md §R2 decision C), the `Spinner` closed DU (70+
named cases + `Custom`), per-console concurrent-session enforcement via
`ConditionalWeakTable`, and non-TTY fallback behaviour (FR-016). Session
handles are opaque and cannot escape the callback frame.

**Independent Test**: Port the "spinner while LLM is thinking" pattern from
`quickstart.md §3.1` against `Console.test` (Phase 5 / US5 dependency):
assert spinner start/update/stop completes without exception; cancellation
via a pre-cancelled token returns `Error (UserCancelled "live")`; a second
concurrent `Live.run` against the same console returns
`Error (ConcurrentLiveSession _)`.

**NOTE on US3 / US5 sequencing**: `Live.run`, `Status.run`, and
`Progress.run` accept `Console` as their first parameter (research.md §R5).
`Console` is defined in US5 (`Console.fsi`/`Console.fs`). For implementation
purposes, US3 and US5 can be implemented in the same PR or US3 can be stubbed
against a forward-declared `Console` type. The simplest path: implement US5
(Console type) first within the same PR as US3, or have US3 depend on US5.
The `[P]` markers in this phase assume they ship together; if split, remove
`[P]` from tasks that depend on `Console`.

**Dependency**: Requires US1 (Phase 3) merged.

### Tests for User Story 3 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T036 [P] [US3] Create `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`. Write a property test for `Status.run` happy path: use `Console.test 80 false` (non-interactive), pass a pre-completed `CancellationToken`, body immediately returns `Ok 42`. Assert result is `Ok 42`. (Non-interactive console is used so no stdin is needed — `Status` degrades gracefully per FR-016.)
- [ ] T037 [P] [US3] In `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`, write a cancellation test: create a `CancellationTokenSource`, cancel it before calling `Status.run`; assert the result is `Error (UserCancelled "status")`.
- [ ] T038 [P] [US3] In `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`, write a concurrent-session rejection test: with the same `Console.test` instance, nest two `Live.run` calls (outer body starts inner before returning); assert the inner call returns `Error (ConcurrentLiveSession _)` without the outer failing.
- [ ] T039 [P] [US3] In `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`, write a non-TTY degradation test for `Progress.run`: use `Console.test 80 false`, run one `ProgressSession.addTask` + `ProgressTask.complete` inside the body; assert `Ok` and no exception. Verifies FR-016 (non-TTY progress falls back gracefully).
- [ ] T040 [P] [US3] In `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`, write a `LiveSession.update` test: inside a `Live.run` body, call `LiveSession.update` with a `Composition.stack []`; assert `Ok`. Then call `LiveSession.refresh`; assert `Ok`. Tests that update/refresh do not throw on a live session.
- [ ] T041 [P] [US3] In `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`, write a `Spinner.Custom` validation test: verify that using `Spinner.Custom ([], 100)` (empty frames) inside `Status.run` returns `Error (InvalidArgument ("Spinner", _))` (lazy validation at point of use, per data-model.md §4.2). Use `Console.test 80 false`.

### Implementation for User Story 3

- [ ] T042 [US3] Write `src/Fugue.Adapters.Console/Live.fsi` per `contracts/Live.fsi` contract sketch and data-model.md §4. Types: `Spinner` DU (all 70+ cases from data-model.md §4.2 + `Custom`), `LiveSession`, `StatusSession`, `ProgressSession`, `ProgressTask` (all sealed). Modules: `LiveSession`, `Live`, `StatusSession`, `Status`, `ProgressTask`, `ProgressSession`, `Progress`. No Spectre types in any signature.
- [ ] T043 [US3] Implement `Spinner` DU in `src/Fugue.Adapters.Console/Live.fs`: enumerate all `Spinner.Known.*` static fields as DU cases (70+ cases per data-model.md §4.2). Add a private `spinnerToSpectre : Spinner -> Spectre.Console.Spinner` mapping function; for `Custom (frames, interval)`: validate non-empty frames and positive interval (return `Error (InvalidArgument ("Spinner", _))` from the callsite — the DU case itself is non-validating). For `Custom` with invalid args, propagate the error from the `Status.run` / `Live.run` entry point.
- [ ] T044 [US3] Implement the per-console session guard in `src/Fugue.Adapters.Console/Live.fs`: a `private let sessionGuard = System.Runtime.CompilerServices.ConditionalWeakTable<Spectre.Console.IAnsiConsole, obj>()` shared across `Live.run`, `Status.run`, `Progress.run`. At entry, attempt `sessionGuard.TryAdd(backend, obj())`; on failure return `Error (ConcurrentLiveSession (backend.GetHashCode().ToString("x8")))`. Release in `finally` via `sessionGuard.Remove`. Use the `IAnsiConsole` from the `Console` opaque type via an `internal` accessor (see T049).
- [ ] T045 [US3] Implement `LiveSession`, `Live.run` in `src/Fugue.Adapters.Console/Live.fs` per data-model.md §4.3. `LiveSession` wraps `Spectre.Console.LiveDisplayContext`. `Live.run` calls Spectre's `AnsiConsole.Live(initial).StartAsync(fun ctx -> task { ... })` internally; provides the typed `LiveSession` to the user body; catches `OperationCanceledException` → `Error (UserCancelled "live")`; calls the session guard (T044). `LiveSession.update` runs `Renderer.toRawAnsi` on the new composition and calls `ctx.UpdateTarget(renderable)` + `ctx.Refresh()`. `LiveSession.refresh` calls `ctx.Refresh()` directly.
- [ ] T046 [US3] Implement `StatusSession`, `Status.run` in `src/Fugue.Adapters.Console/Live.fs` per data-model.md §4.4. `StatusSession` wraps `Spectre.Console.StatusContext`. Non-TTY fallback (FR-016): when `Console.isInteractive = false`, degrade to writing the message once to the console output (no redraw loop). `StatusSession.updateMessage` and `updateSpinner` update the Spectre context fields.
- [ ] T046a [US3] Cursor-restore assertion in `tests/Fugue.Adapters.Console.Tests/LiveTests.fs`: after a cancelled `Live.run` / `Status.run` / `Progress.run`, assert that `Console.cursorState` (via `Console.test` fixture) returns to `Visible` + row0 baseline. Verifies FR-011 + edge case "Cancellation mid-Live".
- [ ] T047 [US3] Implement `ProgressSession`, `ProgressTask`, `Progress.run` in `src/Fugue.Adapters.Console/Live.fs` per data-model.md §4.5. `ProgressTask.increment`, `setValue`, `complete`, `value`, `maxValue` wrap the Spectre `ProgressTask` methods. Non-TTY fallback: emit a final tally line. `ProgressSession.addTask` calls `Spectre.Console.ProgressContext.AddTask`.

**Checkpoint**: US3 PR shippable (together with US5 Console type). `Live.run`, `Status.run`, `Progress.run` all compile; session guard works; cancellation flows correctly; non-TTY fallback tested; 83 + US1 + US2 tests still green; new LiveTests.fs green.

---

## Phase 6: User Story 4 — Interactive prompts (P4)

**Goal**: Expose typed F# wrappers for `TextPrompt`, `SelectionPrompt`,
`MultiSelectionPrompt`, and `ConfirmationPrompt` (per research.md §R3
decisions A+X+Q+M): `Async<Result<'T, RenderError>>` return, native
`CancellationToken` passed to Spectre's `ShowAsync`, `Composition`-typed
choice labels lowered to plain text via `Prompts.projectLabel` (data-model.md
§0.1), and typed `string -> Result<'T, string>` validators for `TextPrompt`.

**Independent Test**: Construct a `SelectionPrompt.ask` against
`Console.test 80 false` (non-interactive) and assert it returns
`Error (NoInteractiveConsole "selection")` without blocking. Construct a
`TextPrompt.ask` with a validator that rejects empty strings, pass a
pre-cancelled token, and assert `Error (UserCancelled "text-prompt")`.

**Dependency**: Requires US1 (Phase 3) and US5 (`Console` type). In the
simplest split, US4 lands after US3+US5 so `Console.test` is available
for tests. If split differently, US4 can stub the `Console` type via an
`open` alias.

### Tests for User Story 4 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**
> NOTE: Actual interactive prompt testing requires scripted stdin input,
> which Phase 2 defers. Tests below verify constructor validity, non-TTY
> short-circuit, and cancellation — NOT full interactive round-trips.

- [ ] T048 [P] [US4] Create `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`. Write a property test for `TextPrompt.ask` non-TTY short-circuit: use `Console.test 80 false` (isInteractive = false); assert result is `Error (NoInteractiveConsole "text-prompt")`. Verify no blocking stdin read occurred (test must complete in < 100 ms).
- [ ] T049 [P] [US4] In `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`, write a cancellation test for `TextPrompt.ask`: use `Console.test 80 false`, pass a pre-cancelled `CancellationToken`; assert result is `Error (UserCancelled "text-prompt")`.
- [ ] T050 [P] [US4] In `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`, write a non-TTY test for `SelectionPrompt.ask`: choices `["a", comp1; "b", comp2]` against non-interactive `Console.test`; assert `Error (NoInteractiveConsole "selection")`.
- [ ] T051 [P] [US4] In `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`, write a validator pre-flight test for `SelectionPrompt.ask`: pass an empty choices list; assert `Error (EmptyComposition _)` is returned synchronously (no `ShowAsync` invoked), per data-model.md §5.3.
- [ ] T052 [P] [US4] In `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`, write a validator pre-flight test for `MultiSelectionPrompt.ask`: empty choices list → `Error (EmptyComposition _)`.
- [ ] T053 [P] [US4] In `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs`, write a non-TTY test for `ConfirmationPrompt.ask`: non-interactive console → `Error (NoInteractiveConsole "confirmation")`.
- [ ] T054 [US4] Add TODO markers in `tests/Fugue.Adapters.Console.Tests/PromptsTests.fs` for full IO round-trip tests (full interactive tests require scripted stdin — deferred). Comment: `// TODO: full interactive round-trips pending scripted TestConsole.Input support (Phase 3 follow-up)`.

### Implementation for User Story 4

- [ ] T055 [US4] Write `src/Fugue.Adapters.Console/Prompts.fsi` per `contracts/Prompts.fsi` contract sketch and data-model.md §5. Types: `Validator<'T>` type alias. Modules: `TextPrompt`, `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`. All `ask` functions return `Async<Result<'T, RenderError>>`. No Spectre types in any signature.
- [ ] T056 [US4] Implement `Prompts.fs`: private `projectLabel : RenderContext -> Composition -> string` helper (data-model.md §0.1) — forks `ctx` with `ColourEnabled = false`, calls `Renderer.toRawAnsi`, returns the string or empty string on error (render error is caught; label projection cannot return `Result`). Pre-flight checks all labels at prompt construction time (return `Error (InvalidArgument ("SelectionPrompt", _))` if any label fails).
- [ ] T057 [US4] Implement `TextPrompt.ask` in `src/Fugue.Adapters.Console/Prompts.fs` per data-model.md §5.2 and research.md §R3. Non-TTY short-circuit: check `Console.isInteractive`; if false, return `Error (NoInteractiveConsole "text-prompt")` immediately. Use `Spectre.Console.TextPrompt<string>` internally; wire user validator `v : Validator<'T>` as Spectre's `Validate` hook; on `ShowAsync` success run `v input` a second time to get `'T`. Catch `OperationCanceledException` / `TaskCanceledException` → `Error (UserCancelled "text-prompt")`.
- [ ] T058 [US4] Implement `SelectionPrompt.ask` in `src/Fugue.Adapters.Console/Prompts.fs` per data-model.md §5.3. Validate non-empty choices; pre-flight all labels via `projectLabel`; build `Spectre.Console.SelectionPrompt<'T>` with `Converter` set to the projected labels; call `ShowAsync` via `Async.AwaitTask`; catch cancellation → `Error (UserCancelled "selection")`; non-TTY → `Error (NoInteractiveConsole "selection")`.
- [ ] T059 [US4] Implement `MultiSelectionPrompt.ask` in `src/Fugue.Adapters.Console/Prompts.fs` per data-model.md §5.4. Same structure as `SelectionPrompt.ask`; returns `'T list`; empty selection → `Ok []`.
- [ ] T060 [US4] Implement `ConfirmationPrompt.ask` in `src/Fugue.Adapters.Console/Prompts.fs` per data-model.md §5.5. Simpler — no choices, no validator. Non-TTY → `Error (NoInteractiveConsole "confirmation")`; cancellation → `Error (UserCancelled "confirmation")`.

**Checkpoint**: US4 PR shippable. All four prompt wrappers compile; non-TTY short-circuit verified; cancellation tested; validator pre-flight tested; 83 + US1 + US2 + US3 tests still green; new PromptsTests.fs green.

---

## Phase 7: User Story 5 — Console infrastructure & testing (P5)

**Goal**: Expose the opaque `Console` type with `default_` (production
singleton) and `test` (fresh per call) factories; `Console.write`, `capture`,
`cursorState`, `isInteractive`; the `CursorState` DU; `ExceptionFormat`
record + `ExceptionFormat.default_`/`verbose`/`compact` named values; and
`Exceptions.render`/`renderWith`. This is the final missing piece that makes
all `Live.*` and `Prompts.*` functions independently testable without a real
terminal.

**Independent Test**: Migrate the `Surface.fs` `AnsiConsole.Write` pattern to
`Console.write`. Use `Console.test 40 true` in a unit test; call
`Console.write test comp`; call `Console.capture test comp`; assert the
captured string contains the expected content. Assert `Console.cursorState`
on a test console returns a typed `CursorState` value (not `UnknownState`
where known). Assert `Console.isInteractive (Console.test 80 true)` is
`false`.

**NOTE on US3/US5 co-dependency**: `Live.run`, `Status.run`, `Progress.run`
need `Console`. If US5 ships in the same PR as US3, stub compile-order is
fine. If shipped separately, US3 PR must forward-declare a minimal `Console`
type stub first, then US5 replaces it. The simplest production path: ship
US3 + US5 in one PR.

### Tests for User Story 5 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T061 [P] [US5] Create `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`. Write a property test for `Console.test` + `Console.write` + `Console.capture`: create `Console.test 40 true`; write a `Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "hello"))` via `Console.write`; capture via `Console.capture`; assert captured string contains `"hello"`. This is the acceptance-scenario 5.2 gate.
- [ ] T062 [P] [US5] In `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`, write a determinism test for `Console.test`: render the same composition twice via two separate `Console.test` instances at the same width; assert the two captured strings are equal.
- [ ] T063 [P] [US5] In `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`, write a `Console.isInteractive` test: assert `Console.isInteractive (Console.test 80 true) = false` (test consoles are not interactive in Phase 2 — no scripted stdin, per data-model.md §6.2).
- [ ] T064 [P] [US5] In `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`, write a `Console.cursorState` test on a test console: after a `Console.write`, call `Console.cursorState`; assert the result is NOT `UnknownState` (test consoles track cursor position, per data-model.md §6.3).
- [ ] T065 [P] [US5] In `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`, write an `Exceptions.render` test: create `Console.test 80 true`, call `Exceptions.render test (System.Exception "test-error")`; assert result is `Ok ()`; call `Console.capture test (Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "")))` and assert captured output is non-empty (exception text was written to the console buffer). Acceptance-scenario 5.3 gate.
- [ ] T066 [P] [US5] In `tests/Fugue.Adapters.Console.Tests/ConsoleTests.fs`, write an `ExceptionFormat` test: assert `ExceptionFormat.compact.ShortenTypes = true`, `ExceptionFormat.verbose.ShortenTypes = false`, `ExceptionFormat.default_.ShowLinks = false`. Guards the named presets.

### Implementation for User Story 5

- [ ] T067 [US5] Write `src/Fugue.Adapters.Console/Console.fsi` per `contracts/Console.fsi` contract sketch and data-model.md §6. Types: `Console` (sealed), `CursorState` DU (3 cases), `ExceptionFormat` record. Modules: `ExceptionFormat`, `Console`, `Exceptions`. No Spectre types in any signature.
- [ ] T068 [US5] Implement `src/Fugue.Adapters.Console/Console.fs`. Private discriminated union `type ConsoleMode = Production | Test of Spectre.Console.Testing.TestConsole`. Private record `type Console = private { Backend: Spectre.Console.IAnsiConsole; Mode: ConsoleMode }`. Add `internal` accessor `Console.backend : Console -> Spectre.Console.IAnsiConsole` for use by `Live.fs` and `Prompts.fs` (session guard and `ShowAsync` targets).
- [ ] T069 [US5] Implement `Console.default_` in `src/Fugue.Adapters.Console/Console.fs`: singleton via `lazy` (`AnsiConsole.Console` wrapped as `Production` mode). Implement `Console.test`: creates fresh `Spectre.Console.Testing.TestConsole(width, colourEnabled)`, wraps as `Test tc`. Width clamped `[1, 1000]`.
- [ ] T070 [US5] Implement `Console.isInteractive`, `Console.write`, `Console.capture`, `Console.cursorState` in `src/Fugue.Adapters.Console/Console.fs` per data-model.md §6.2. `isInteractive`: `Production` → `not System.Console.IsInputRedirected`; `Test _` → `false`. `write`: for `Production` calls the existing `Renderer.toRawAnsi` path and writes to `backend`; for `Test tc` appends to `tc`'s buffer. `capture`: for `Production` spins up a transient `TestConsole` mirroring width/colour; for `Test tc` returns `tc.Output` (does NOT clear — see data-model.md §6.5 note). `cursorState`: maps Spectre's cursor state to the `CursorState` DU.
- [ ] T071 [US5] Implement `ExceptionFormat` presets (`default_`, `verbose`, `compact`) and `Exceptions.render` / `Exceptions.renderWith` in `src/Fugue.Adapters.Console/Console.fs` per data-model.md §6.4. Maps `ExceptionFormat` record to Spectre's `ExceptionFormats` `[<Flags>]` enum via a private converter. Calls `backend.WriteException(ex, flags)`. Catches exceptions from the formatter itself → `Error (RenderFailed ("Exceptions", ex.Message))`.

**Checkpoint**: US5 PR shippable (or merged with US3). `Console.test` is available for all P3 + P4 tests; `Console.write` + `Console.capture` tested; `Exceptions.render` tested; 83 + US1 + US2 + US3 + US4 tests still green; new ConsoleTests.fs green.

---

## Phase 8: Polish & Cross-Cutting

**Purpose**: FR-019 coverage-matrix enforcement test; SC-004 verification
(`rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi`); SC-006 AOT publish
clean; SC-003 test count ≥ 250 and suite ≤ 5s; SC-002 note; `just regen-fsi`
drift check.

- [ ] T072 Create `tests/Fugue.Adapters.Console.Tests/CoverageMatrixTests.fs`. Implement a `CoverageMatrix` test-internal module that parses `specs/002-spectre-full-coverage/coverage-matrix.md` — extracts the `Type` column (column 1, 0-indexed) from every `| ... |` row. Implement `` ``Every public Spectre type is in the coverage matrix`` `` test: enumerate `typeof<Spectre.Console.AnsiConsole>.Assembly.GetExportedTypes()`, append `typeof<Spectre.Console.Json.JsonText>.Assembly.GetExportedTypes()`, append `typeof<Spectre.Console.Testing.TestConsole>.Assembly.GetExportedTypes()`; assert each type's simple name appears in the parsed matrix set. This is the SC-001 gate and FR-019 enforcement.
- [ ] T073 [P] In `tests/Fugue.Adapters.Console.Tests/CoverageMatrixTests.fs`, implement `` ``Every covered type produces non-empty render output`` `` (FR-019 second half): a hand-rolled registry mapping each `covered` type name to a `unit -> Result<string, RenderError>` function that builds a representative value and calls `Renderer.toRawAnsi`. One entry per covered type (60 entries). Assert every entry returns `Ok output` where `output.Length > 0`. Mark entries for Live/Status/Progress/Prompts session types as `"not applicable to static render"` and skip those (they require `Async` + `Console`; static coverage is verified by LiveTests.fs, PromptsTests.fs).
- [ ] T074 [P] Verify SC-004: run `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` and confirm exactly ONE match — the `IRenderable` argument in `Renderable.fsi`. Document the match in a comment at the top of `Renderable.fsi`. If additional matches appear (from stub or implementation drift), fix before declaring SC-004 satisfied.
- [ ] T075 [P] Verify SC-006: run `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` and confirm zero new trim/AOT warnings. The adapter is JIT-only and is NOT in the headless closure — this should hold by construction. Document the publish output in the PR description.
- [ ] T076 [P] Verify SC-003: run `dotnet test tests/Fugue.Adapters.Console.Tests -c Release` and confirm (a) test count ≥ 250, (b) total elapsed time < 5 seconds. If test count is below 250 but all mandatory tests are present, document the delta and note which test-gaps exist. If suite exceeds 5s, profile and split the slowest tests into a `[<Trait("Category", "slow")>]` category excluded from the main suite.
- [ ] T077 [P] Note on SC-002: `rg 'Spectre\.Console' src/Fugue.Cli/ src/Fugue.Surface/` SHOULD return zero after the downstream REPL port wave (Phase 3 per plan.md), NOT after Phase 2 closes. Add a comment to `specs/002-spectre-full-coverage/plan.md` §"Post-Implementation Constitution Re-Check" confirming this SC is a Phase 3 gate, not a Phase 2 gate. No code change — documentation only.
- [ ] T078 Run `just regen-fsi` (or the recipe from PR #942) on all new Phase 2 source files. Confirm no drift between generated `.fsi` content and the hand-authored ones. Commit any drift corrections. Ensures `.fsi` sealing is accurate for the final PR.
- [ ] T079 [P] Update `specs/002-spectre-full-coverage/coverage-matrix.md` if any `pending` rows were created during implementation. Confirm the count at the top of the matrix reads `pending | 0`. SC-001 satisfied.
- [ ] T080 [P] Run the full test suite `dotnet test` one final time from the repo root. Confirm all projects pass: `Fugue.Tests` (existing), `Fugue.Adapters.Console.Tests` (≥ 250 tests), any other projects in `Fugue.slnx`. Document the final pass/fail count in the PR description.
- [ ] T081 Polish: open GitHub follow-up issue tracking migration of Phase 1's 83 existing adapter tests to use the new `Console.test` wrapper (FR-017 SHOULD; not required for Phase 2 PR close but flagged for visibility). The follow-up issue body should reference PR #942 baseline, the T067 `Console.test` factory, and link back to this `tasks.md` entry.

---

## Dependencies

```
Phase 1 Setup (T001–T005)
   │
   ▼
Phase 2 Foundational (T006 — verification only; parallel with Phase 1)
   │
   ▼
Phase 3 US1 P1 (T007–T016) ── blocker for all other user stories
   │
   ├──────────────────────────────────────┐
   ▼                                      ▼
Phase 4 US2 P2 (T017–T035)            Phase 7 US5 P5 (T067–T071)
   │                                      │
   │                              ┌───────┘
   │                              ▼
   │                      Phase 5 US3 P3 (T036–T047)
   │                      [US3 + US5 may ship as one PR]
   │                              │
   │                              ▼
   │                      Phase 6 US4 P4 (T048–T060)
   │
   └──────────────────────────────┐
                                  ▼
                          Phase 8 Polish (T072–T080)
                          [runs after all US phases complete]
```

**Inter-phase notes**:
- T001–T005 (Setup) are global prerequisites; they run in the P1 PR.
- T004 (stub compile entries) must precede any US-phase PR to keep fsproj
  compile order stable across all five PRs.
- US1 (Phase 3) is the single hard blocker: the `Composition.Foreign` internal
  case it introduces is required by all subsequent user stories' lowering path.
- US2 (Phase 4) depends on US1 only. It is independent of US3, US4, US5.
- US5 (Phase 7) is required by US3 and US4 (both take `Console` parameter).
  Ship US5 with US3 in one PR, or ship US5 first as a standalone PR.
- US3 (Phase 5) depends on US1 + US5.
- US4 (Phase 6) depends on US1 + US5 (for `Console.test` in tests).
- Phase 8 (Polish) runs only after all five user-story PRs are merged.

**PR strategy** (per spec.md assumption "PR splitting follows user-story
boundaries"):
- **PR P1**: Setup (T001–T005) + US1 (T007–T016) — MVP blocker
- **PR P2**: US2 (T017–T035)
- **PR P3+P5**: US3 + US5 in one PR (T036–T047 + T061–T071), since US3 needs
  `Console` which is US5
- **PR P4**: US4 (T048–T060)
- **PR Polish**: Phase 8 (T072–T080)

---

## Parallel Execution Examples

**Within Phase 3 (US1) tests** — all test tasks touch the same file
`RenderableTests.fs`; assign to one contributor but note they are logically
independent assertions. Implementation tasks T011–T016 span different files
and can be assigned in sequence.

**Within Phase 4 (US2) tests** — T017–T025 all write to `DataDisplaysTests.fs`
and are `[P]` (logically independent sections of the same test file). The
implementation tasks T027–T034 are strongly `[P]` — each widget is its own
isolated code block in `DataDisplays.fs`:
```
T027 (Tree)         — parallel
T028 (Json)         — parallel
T029 (BarChart)     — parallel
T030 (BreakdownChart) — parallel
T031 (Calendar)     — parallel
T032 (FigletText)   — parallel
T033 (Grid)         — parallel
T034 (TextPath)     — parallel
```

**Within Phase 5 (US3) tests** — T036–T041 are `[P]` sections of
`LiveTests.fs`; implementation T043–T047 have internal dependencies (T044
session guard must exist before T045 `Live.run` uses it).

**Within Phase 6 (US4) tests** — T048–T054 are `[P]` in `PromptsTests.fs`;
implementation T057–T060 are parallel per-prompt-type.

**Within Phase 7 (US5) tests** — T061–T066 are `[P]` in `ConsoleTests.fs`;
implementation T067–T071 are sequential (record first, then factories, then
module functions).

**Within Phase 8 (Polish)** — T072 + T073 (coverage matrix tests) must be
written before T076 (SC-003 test count gate). All other Polish tasks are `[P]`.

---

## Implementation Strategy

### MVP Scope: User Story 1 + Setup alone

US1 (Phase 3) is the minimum shippable unit. It delivers the bridge pattern
that unblocks ALL downstream REPL porting. Without it, no nontrivial REPL site
can move onto the adapter. With it, contributors can incrementally port
existing `IRenderable`-returning functions by wrapping them at the callsite
while leaving the inner implementation unchanged.

**Ship criterion for MVP**: `Renderable.fromSpectre` + `Composition.ofRenderable`
work end-to-end; exception safety (FR-009) verified; 83 existing tests green.

### Incremental Delivery

1. **PR P1 — Setup + US1 (MVP)**: T001–T006 + T007–T016 (21 tasks). Delivers
   the bridge. Ship as standalone, merge immediately.
2. **PR P2 — US2**: T017–T035 + T024a (20 tasks). Delivers all 8 data-display widgets.
   Independent of US3/US4/US5. Can merge before or after P3+P5.
3. **PR P3+P5 — US3 + US5**: T036–T047 + T046a + T067–T071 (24 tasks). Delivers Live
   sessions + Console infrastructure. These ship together because US3 takes
   `Console` as a parameter (US5).
4. **PR P4 — US4**: T048–T060 (13 tasks). Delivers interactive prompts.
   Depends on `Console` type (US5 in PR P3+P5).
5. **PR Polish**: T072–T081 (10 tasks). Coverage matrix enforcement, SC gates,
   suite timing, regen-fsi drift check, follow-up issue tracking. Merge last.

### Do NOT ship US1 + US2 + US3 + US4 + US5 as one PR

Per Principle VII (Decision Hygiene & Merge Strategy) and spec.md §Assumptions:
one PR per user story enables independent review, bisection, and incremental
porting. The bridge (US1) is the one true blocker; every other story can land
in any order once US1 is merged.

---

## Format Validation

All task IDs follow the `T001`–`T081` sequential numbering in execution order
within their phase. Checklist format compliance:

| Requirement | Verified |
|---|---|
| Every task starts with `- [ ] T0xx` | ✅ |
| `[P]` included only for tasks in different files from other `[P]` tasks | ✅ |
| `[US1]`–`[US5]` labels on all user-story phase tasks | ✅ |
| Setup and Foundational tasks have no `[Story]` label | ✅ |
| Polish tasks have no `[Story]` label | ✅ |
| Every task description includes exact file path | ✅ |
| Tasks sequential T001–T081; no gaps (suffix form `TxxxA` allowed for inserted tasks, e.g. T024a, T046a) | ✅ |

---

## Constitution Check (v1.1.0) — Per-Phase Verification

| Principle | Where checked | Acceptance |
|---|---|---|
| I — Test Coverage | T007–T010 (US1 tests), T017–T025 (US2), T036–T041 (US3), T048–T054 (US4), T061–T066 (US5), T072–T073 (matrix tests) | Every public type has happy + Error test; totality test (T009, T025); no `[Skip]` |
| II — C#/F# Sanitisation | All test tasks hit real Spectre library — no mocks; SC-004 (T074) verifies zero Spectre types in `.fsi` except bridge | Integration tests only |
| III — Subagent-Driven | Implementation tasks T011–T016, T026–T035, T042–T047, T055–T060, T067–T071 delegated to `engineering-fsharp-developer` subagent | One subagent per US PR |
| IV — F# Purity | No `.cs` files; all new files under `src/Fugue.Adapters.Console/` and `tests/` are `.fsi`/`.fs` | `rg '\.cs$' src/ tests/` in PR diff returns nothing |
| V — AOT vs JIT | T005 + T075 verify `Fugue.Cli.Aot` publish stays clean; adapter is JIT-only by construction | AOT publish gate |
| VI — Prompt-First Slash | Not applicable — Phase 2 adds adapter primitives, not slash commands | — |
| VII — Decision Hygiene | PRs merged with `--merge` per CLAUDE.md conventions; per-US-story split | Reviewer enforces |
| VIII — Tool Contract | Not applicable to the adapter library layer | — |
| IX — Runtime ≠ Prompt | Prompt wrappers are code (US4), not prompt-side instruction | Pattern correct |
| X — Untrusted Content | `Json.create` validates + size-limits user JSON (T028); `Tree` labels go through `SafeText` (T027); `FigletText` length-bounded (T032) | Smart constructors enforce FR-007 |
| XI — Context Engineering | Not applicable | — |
