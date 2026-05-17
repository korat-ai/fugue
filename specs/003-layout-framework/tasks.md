---

description: "Task list — Phase 3 Fugue Layout Framework"
---

# Tasks: Phase 3 — Fugue Layout Framework

**Input**: Design documents from `/specs/003-layout-framework/`

**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅,
contracts/ ✅, quickstart.md ✅

**Tests**: MANDATORY for every behavioural change per Principle I of
`.specify/memory/constitution.md` (v1.1.0, Test Coverage Discipline,
NON-NEGOTIABLE). SC-001 + FR-013 mandate at least one happy-path property
test AND one Error-path test for every new public layout type / smart
constructor. No "ship without tests" carve-out — not for any user story.
Phase 2 review lesson: 6 BLOCKERs / 5 PRs caught at pre-push review, all
of class "missing test → silent divergence". Phase 3 reviewer brief MUST
cite this inventory.

**Organization**: Tasks are grouped by user story (one phase per story) to
enable independent implementation and testing. Each user story is its own
PR. US1 (Stack) is the MVP-blocker — it must merge before US2/US3/US4 can
land cleanly (those depend on the shared `Orientation`/`CrossAxisAlignment`
DUs and the lowering pattern established by Stack).

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel — different file from other `[P]`-marked tasks
  in the same phase, so two writers can work simultaneously without conflict.
  Does NOT imply "no logical dependency". A test task can be `[P]` even when
  it logically depends on the implementation task — the dependency runs
  through the build, not the writer's keyboard.
- **[Story]**: REQUIRED for user-story phase tasks (`[US1]`–`[US5]`). OMIT
  for Setup, Foundational, and Polish phases.
- Every task description includes the exact file path (repo-relative from
  repo root).

## Path Conventions

- **Adapter library**: `src/Fugue.Adapters.Console/Layout/` (new sub-folder
  per R-3 decision in research.md §R-3)
- **REPL port target**: `src/Fugue.Cli/Repl.fs` + new `src/Fugue.Cli/LayoutHost.fs`
- **Test project**: `tests/Fugue.Adapters.Console.Tests/Layout/`
- **Solution file**: `Fugue.slnx` (repo root)

---

## Phase 1: Setup

**Purpose**: Add the new `Layout/` sub-folder structure to `Fugue.Adapters.Console.fsproj`,
extend `RenderContext` with the `Height` field (FR-009), extend `RenderError` with
the new `LayoutOverflow` case (data-model.md §8), update the existing
`RenderContext` call-sites for the new field, add stub `<Compile Include>` entries
for all new Phase 3 paired `.fsi`/`.fs` files, and confirm AOT publish stays clean.
These are shared prerequisites that all five user-story PRs build on — they land as
the prefix of the US1 PR.

- [ ] T001 Extend `RenderContext` in `src/Fugue.Adapters.Console/Context.fsi` and `.fs` with a `Height: int` field per data-model.md §1. Default value `System.Int32.MaxValue` (unconstrained sentinel) preserves backward compat for existing call-sites that omit the field. Update the smart constructor / record literals accordingly. NOTE: this is a real breaking change — see T002 for the existing-call-site sweep.
- [ ] T002 Update every existing `RenderContext` construction in `src/Fugue.Adapters.Console/` and `tests/Fugue.Adapters.Console.Tests/` to include `Height` (use the sentinel `System.Int32.MaxValue` where height was previously implicit). Estimated ~12 test call-sites per data-model.md §1 audit. Run `rg 'RenderContext\.' src/ tests/` to enumerate first; spot-check that no missed site falls back to the default record value silently.
- [ ] T002a Add smart constructor validation in `src/Fugue.Adapters.Console/Context.fs`: `RenderContext.create` (or equivalent constructor function) rejects non-positive dimensions with `Error (InvalidArgument ("Renderer", "non-positive dimensions: width=W or height=H"))` per spec edge case "Negative or zero terminal dimensions" (FR-009 supporting). Add corresponding test in `tests/Fugue.Adapters.Console.Tests/FoundationTests.fs`: `RenderContext.create 0 24` and `RenderContext.create 80 0` and `RenderContext.create -1 24` each return `Error (InvalidArgument _)`. Closes finding G6.
- [ ] T003 Extend `RenderError` DU in `src/Fugue.Adapters.Console/Errors.fsi` and `.fs` with one new case (data-model.md §8): `LayoutOverflow of layoutKind: string * dimension: string`. Update any existing exhaustiveness match — specifically `tests/Fugue.Adapters.Console.Tests/FoundationTests.fs` (and grep for other call-sites that match `RenderError` exhaustively without wildcards).
- [ ] T004 [P] Add stub `<Compile Include>` entries for all Phase 3 module pairs to `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj`, in compile order after `Console.fs` (last Phase 2 module): `Layout/Layout.fsi`, `Layout/Layout.fs`, `Layout/Stack.fsi`, `Layout/Stack.fs`, `Layout/Dock.fsi`, `Layout/Dock.fs`, `Layout/LayoutGrid.fsi`, `Layout/LayoutGrid.fs`, `Layout/Flex.fsi`, `Layout/Flex.fs`, `Layout/LayoutComposition.fsi`, `Layout/LayoutComposition.fs`, `Layout/Scheduler.fsi`, `Layout/Scheduler.fs`. Each stub file contains only the `namespace Fugue.Adapters.Console.Layout` declaration so the project builds green before US-phase content fills them in.
- [ ] T005 [P] Verify `dotnet build -c Release` exits zero with zero warnings after T001–T004. Verify `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` exits zero with zero new trim warnings (SC-008 — Phase 3 is JIT-only by construction; verify to catch accidental reference chain additions).

**Checkpoint**: `dotnet build` green; `dotnet test tests/Fugue.Adapters.Console.Tests` runs and existing 232 tests pass unchanged (after T002 call-site updates); AOT publish clean; stub `.fsi`/`.fs` files compile.

---

## Phase 2: Foundational

**Purpose**: Common Layout module that holds the shared DUs (`Orientation`, `CrossAxisAlignment`, `Overflow`) and any cross-primitive helpers. Implemented BEFORE US1 because all four primitives depend on these types.

- [ ] T006 Write `src/Fugue.Adapters.Console/Layout/Layout.fsi` per data-model.md §2: declare `Orientation = Vertical | Horizontal`, `CrossAxisAlignment = Start | Center | End | Stretch`, `Overflow = Clip | Strict | WrapNext`. All closed DUs (no `Custom`). Document the relationship to Phase 2 `Composition.Alignment` (if present) — reuse if shape matches; otherwise add a one-line doc-comment justifying the new `CrossAxisAlignment` over reusing `Alignment`.
- [ ] T007 [P] Implement `src/Fugue.Adapters.Console/Layout/Layout.fs` — DU definitions matching the `.fsi`. No functions, just types.
- [ ] T008 [P] Write `tests/Fugue.Adapters.Console.Tests/Layout/CommonTypesTests.fs`: exhaustive-match smoke test on each DU asserting every case is reachable. This catches any accidental `_ ->` wildcard arms that would mask a forgotten case at a future Phase 4 amendment.
- [ ] T009 Confirm Constitution check still passes: `mcp__fslangmcp__workspace_diagnostics` returns clean; `dotnet build` zero warnings; `dotnet test` 232 + new T008 tests pass.

**Checkpoint**: Common DUs in place. All five user-story phases can start after Phase 2.

---

## Phase 3: User Story 1 — Stack layout primitive (P1) 🎯 MVP

**Goal**: Expose an opaque sealed `Stack` type + `Stack.create` smart constructor +
`Composition.ofStack` lowering. Stack is the simplest layout primitive and the
template for the other three. Closing Stack first proves the architecture end-to-end
on the smallest possible surface; subsequent primitives reuse the same lowering pattern.
This PR is the blocker for US2/US3/US4 (they all depend on the Stack pattern as
reference + may delegate sub-flow to Stack internally).

**Independent Test**: Construct `Stack.create Vertical 1 Start [panel1; panel2; panel3]`,
render via `Renderer.toRawAnsi` at width=80 height=24, assert the output contains
all three panels stacked vertically with exactly 1 blank row between them and each
panel left-aligned to column 0 (per spec US1 acceptance scenario 1). Verify
`rg 'Spectre\.' src/Fugue.Adapters.Console/Layout/Stack.fsi` returns zero.

### Tests for User Story 1 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T010 [P] [US1] Create `tests/Fugue.Adapters.Console.Tests/Layout/StackTests.fs`. Property test: `Stack.create Vertical 1 Start [validComp1; validComp2; validComp3]` returns `Ok stack` and `Composition.ofStack stack` lowered + `Renderer.toRawAnsi` produces non-empty output containing all three children's marker strings (use `"STACK-CHILD-N"` markers per Phase 2 PR-952 lesson — assert markers appear in stripped-ANSI output, not just `Length > 0`).
- [ ] T011 [P] [US1] In StackTests.fs: error-path — `Stack.create Vertical 1 Start []` returns `Error (EmptyComposition "Stack: at least one child required")`. Synchronous, no render.
- [ ] T012 [P] [US1] In StackTests.fs: error-path — `Stack.create Vertical -1 Start [anyChild]` returns `Error (InvalidArgument ("Stack", "gap must be non-negative"))`. Synchronous.
- [ ] T013 [P] [US1] In StackTests.fs: horizontal flow — `Stack.create Horizontal 2 Center [child1; child2]` rendered at width=80 places children side-by-side with 2-col gap, centred vertically in their allocated row (per US1 acceptance scenario 2).
- [ ] T014 [P] [US1] In StackTests.fs: cross-axis alignment — separate test per `CrossAxisAlignment` case (Start/Center/End/Stretch) asserts the lowered Composition reflects the requested alignment. 4 sub-tests in one `[<Property(MaxTest=1)>]` function (or 4 separate functions).
- [ ] T015 [P] [US1] In StackTests.fs: colour-toggle parametric test (Phase 2 T024a pattern) — render a Stack at `colourEnabled=true` and `=false` through `Renderer.toRawAnsi`, assert colour-off output equals colour-on output with `\x1b[...]` sequences stripped via `Regex @"\x1b\[[0-?]*[ -/]*[@-~]"`.
- [ ] T015a [P] [US1] In StackTests.fs: CJK / wide-character width test (FR-014 verification per research R-4 transitive-Spectre scope) — construct a Vertical Stack containing `SafeText.ofLiteral "日本語"` rendered at fixed width=10; assert the layout treats the 3-char string as 6 display columns (CJK each = 2 cells), not 3. Document in test comment that ZWJ family emoji are an explicit known gap (research R-4 + follow-up T082) and not covered here. Closes finding G3.
- [ ] T016 [P] [US1] In StackTests.fs: determinism — render the same Stack twice via two separate `RenderContext` instances at width=80 height=24; assert byte-identical output (Phase 2 T025 pattern).
- [ ] T017 [P] [US1] In StackTests.fs: depth-limit — construct a Stack containing a Stack containing... nested 101 deep; assert smart constructor (or `ofStack` lowering) returns `Error (InvalidArgument ("Layout", "depth exceeds 100"))` per spec edge case.

### Implementation for User Story 1

- [ ] T018 [US1] Write `src/Fugue.Adapters.Console/Layout/Stack.fsi` per `contracts/Stack.fsi` contract sketch and data-model.md §3. Declares opaque sealed `Stack` type and `module Stack` with `create : Orientation -> int -> CrossAxisAlignment -> Composition list -> Result<Stack, RenderError>`. RENDER-LAYER INVARIANT: no Spectre.* types in `.fsi` signature. No `SafeText` parameter (Stack composes existing Compositions, no new text-input point).
- [ ] T019 [US1] Implement `src/Fugue.Adapters.Console/Layout/Stack.fs`: private record `StackData { Orientation: Orientation; Gap: int; Align: CrossAxisAlignment; Children: Composition list }`, sealed wrapper type `Stack`, smart constructor `create` performs validation (non-empty children → EmptyComposition; negative gap → InvalidArgument; recursive depth check via tree traversal → InvalidArgument).
- [ ] T020 [US1] Implement `Composition.ofStack : Stack -> Composition` in `src/Fugue.Adapters.Console/Layout/LayoutComposition.fs` per data-model.md §3 lowering algorithm: for `Vertical` use `Composition.Stack` (existing Phase 1 DU case) with gap-padding inserted between children; for `Horizontal` use `Composition.Columns` (existing) with gap-padding. Cross-axis alignment maps to per-child padding/spacing in the perpendicular axis.
- [ ] T021 [US1] Update `src/Fugue.Adapters.Console/Layout/LayoutComposition.fsi` to add the `Composition.ofStack` factory signature. Confirm `rg 'Spectre\.' src/Fugue.Adapters.Console/Layout/Stack.fsi src/Fugue.Adapters.Console/Layout/LayoutComposition.fsi` returns zero. Add `Layout` module entry to project compile order if not already covered by T004 stubs.

**Checkpoint**: US1 PR shippable. `Stack.create` validates; `Composition.ofStack` lowers correctly; 232 + ~8 new tests green; `rg 'Spectre\.' src/Fugue.Adapters.Console/Layout/*.fsi` returns zero; AOT publish clean.

---

## Phase 4: User Story 2 — Dock layout primitive (P2)

**Goal**: Expose opaque sealed `Dock` type + `DockEdge` DU + `Dock.create` + `Composition.ofDock` per data-model.md §4. Dock handles edge-pinned regions (top/bottom/left/right + fill) — the natural model for status bars, sidebars, header/footer.

**Independent Test**: Construct `Dock.create [DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp]`, render at 80×24, assert status appears at row 23 with marker text `"STATUS-MARKER"` present in row 23 of stripped output, and `"MAIN-MARKER"` present in rows 0–22 (per spec US2 acceptance scenario 1).

**Dependency**: Requires US1 (Phase 3) — Dock lowering uses the same `Composition` IR pattern and depth-check infrastructure introduced in US1.

### Tests for User Story 2 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T022 [P] [US2] Create `tests/Fugue.Adapters.Console.Tests/Layout/DockTests.fs`. Property test: `Dock.create [DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp]` returns `Ok dock`; `Composition.ofDock` rendered at 80×24 places `"STATUS-MARKER"` content in the last row and `"MAIN-MARKER"` content in rows 0..22.
- [ ] T023 [P] [US2] In DockTests.fs: multi-edge — `Dock.create [DockEdge.Left, sidebar; DockEdge.Right, metadata; DockEdge.Fill, main]` with sidebar-of-width-20 and metadata-of-width-30 at width=100 assigns columns correctly per US2 acceptance scenario 2.
- [ ] T024 [P] [US2] In DockTests.fs: top-pin — `Dock.create [DockEdge.Top, header; DockEdge.Fill, body]` renders header in row 0, body in rows 1..(height-1).
- [ ] T025 [P] [US2] In DockTests.fs: error-path — two `Fill` children → `Error (InvalidArgument ("Dock", "exactly one Fill child permitted"))` synchronous.
- [ ] T026 [P] [US2] In DockTests.fs: error-path — empty children list → `Error (EmptyComposition "Dock: at least one child required")` synchronous.
- [ ] T027 [P] [US2] In DockTests.fs: error-path — zero Fill children → `Error (InvalidArgument ("Dock", "exactly one Fill child permitted"))`.
- [ ] T028 [P] [US2] In DockTests.fs: colour-toggle parametric (mirror Phase 2 T024a pattern + Phase 3 T015).
- [ ] T028a [P] [US2] In DockTests.fs: depth-limit — nest Docks 101 levels deep; assert `Error (InvalidArgument ("Layout", "depth exceeds 100"))` per spec edge case "Recursive nesting" (mirrors T017 for Stack). Closes finding G4 for Dock.
- [ ] T029 [P] [US2] In DockTests.fs: determinism — render same Dock twice via separate RenderContext, byte-identical.

### Implementation for User Story 2

- [ ] T030 [US2] Write `src/Fugue.Adapters.Console/Layout/Dock.fsi` per `contracts/Dock.fsi`: declare `DockEdge = Top | Bottom | Left | Right | Fill`, opaque sealed `Dock`, `module Dock` with `create : (DockEdge * Composition) list -> Result<Dock, RenderError>`.
- [ ] T031 [US2] Implement `src/Fugue.Adapters.Console/Layout/Dock.fs`: private record + sealed wrapper + smart constructor with validation per data-model.md §4 (non-empty, exactly-one-Fill).
- [ ] T032 [US2] Implement `Composition.ofDock : Dock -> Composition` in `src/Fugue.Adapters.Console/Layout/LayoutComposition.fs` per data-model.md §4 lowering — two-pass geometry: edges measured for intrinsic size via Spectre `IRenderable.Measure` (transitively, per R-4); Fill takes remainder. Use the lazy-IRenderable pattern (data-model.md §4 + surprise #3 from Phase 1 subagent report) — wrap result in `Composition.Foreign (Renderable.fromSpectre ...)` so geometry defers to render time.
- [ ] T033 [US2] Update `src/Fugue.Adapters.Console/Layout/LayoutComposition.fsi` to add `Composition.ofDock` signature. Verify SC-004 gate.

**Checkpoint**: US2 PR shippable. Dock validates; lowering correct; 232 + US1 + ~8 new tests green; SC-004 still 1 match.

---

## Phase 5: User Story 3 — LayoutGrid primitive (P3)

**Goal**: Expose opaque sealed `LayoutGrid` type + `ColumnSize` + `RowSize` DUs + `LayoutGrid.create` + `Composition.ofLayoutGrid` per data-model.md §5. Note the **type literally named `LayoutGrid`** (not `Grid`) to avoid F# unqualified-resolution ambiguity with Phase 2 `DataDisplays.Grid` widget (per R-3 decision).

**Independent Test**: Construct `LayoutGrid.create [Fixed 20; Star 1] [Auto] [[labelComp; valueComp]]` at width=100; assert label occupies columns 0..19 (Fixed) and value occupies columns 20..99 (Star fills remainder), per US3 acceptance scenario 1. Construct a ragged-row Grid and assert `Error (InvalidArgument ("LayoutGrid", _))`.

**Dependency**: Requires US1 (Phase 3) for the Composition lowering pattern. Independent of US2 + US4.

### Tests for User Story 3 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T034 [P] [US3] Create `tests/Fugue.Adapters.Console.Tests/Layout/LayoutGridTests.fs`. Property test: `LayoutGrid.create [Fixed 20; Star 1] [Auto] [[label; value]]` at width=100 lowers and renders with `"LABEL-MARKER"` in cols 0..19 and `"VALUE-MARKER"` in cols 20..99.
- [ ] T035 [P] [US3] In LayoutGridTests.fs: Star ratio — `LayoutGrid.create [Star 1; Star 2] [Star 1; Star 1] [[a; b]; [c; d]]` at 90×24 produces 30-col + 60-col columns and 12-row + 12-row rows; all four children appear in their assigned cells.
- [ ] T036 [P] [US3] In LayoutGridTests.fs: Auto sizing — `LayoutGrid.create [Auto] [Auto] [[shortComp]]` sizes to content; render output exactly contains the child content with no extra padding beyond the child's intrinsic size.
- [ ] T036a [P] [US3] In LayoutGridTests.fs: mixed sizing — `LayoutGrid.create [Fixed 20; Auto; Star 1; Star 2] [Auto] [[fixedComp; autoComp; star1Comp; star2Comp]]` at width=100; assert Fixed allocated first (cols 0..19), Auto sized to its content intrinsic, remainder split 1:2 among Star children. Plus three sub-cases: (a) Auto content exceeds available; (b) Star ratios sum to fractional pixels (e.g. width=83 with two Star 1); (c) all-Fixed with total < available width — extra space left blank to the right. Closes finding G5.
- [ ] T037 [P] [US3] In LayoutGridTests.fs: error-path — empty columns → `Error (EmptyComposition "LayoutGrid: at least one column required")`.
- [ ] T038 [P] [US3] In LayoutGridTests.fs: error-path — empty rows → `Error (EmptyComposition "LayoutGrid: at least one row required")`.
- [ ] T039 [P] [US3] In LayoutGridTests.fs: error-path — ragged row → `Error (InvalidArgument ("LayoutGrid", "row 0 has 1 cells, expected 2"))` per US3 acceptance scenario "ragged rows rejected".
- [ ] T040 [P] [US3] In LayoutGridTests.fs: error-path — `Fixed 0` (zero width column) → `Error (InvalidArgument ("LayoutGrid", "Fixed size must be ≥ 1"))`.
- [ ] T041 [P] [US3] In LayoutGridTests.fs: error-path — `Star 0` ratio → `Error (InvalidArgument ("LayoutGrid", "Star ratio must be ≥ 1"))`.
- [ ] T042 [P] [US3] In LayoutGridTests.fs: colour-toggle parametric + determinism (mirror US1/US2 patterns).
- [ ] T042a [P] [US3] In LayoutGridTests.fs: depth-limit — nest LayoutGrids 101 levels deep (one cell per level wraps the next); assert `Error (InvalidArgument ("Layout", "depth exceeds 100"))` per spec edge case "Recursive nesting" (mirrors T017 for Stack). Closes finding G4 for LayoutGrid.

### Implementation for User Story 3

- [ ] T043 [US3] Write `src/Fugue.Adapters.Console/Layout/LayoutGrid.fsi` per `contracts/LayoutGrid.fsi`: `ColumnSize = Fixed of int | Auto | Star of int`, `RowSize = Fixed of int | Auto | Star of int` (same shape, distinct types for clarity), opaque sealed `LayoutGrid`, `module LayoutGrid` with `create`. **Type literally named `LayoutGrid` per R-3 + data-model.md §5** — NOT `Grid` (Phase 2's widget keeps that name).
- [ ] T044 [US3] Implement `src/Fugue.Adapters.Console/Layout/LayoutGrid.fs`: private record + sealed wrapper + smart constructor validating non-empty columns/rows, equal-length rows, Fixed ≥ 1, Star ≥ 1. Depth-check via tree traversal.
- [ ] T045 [US3] Implement `Composition.ofLayoutGrid : LayoutGrid -> Composition` in `src/Fugue.Adapters.Console/Layout/LayoutComposition.fs` per data-model.md §5 two-pass sizing algorithm (Fixed allocated → Auto measures content → Star distributes remainder by ratio). Uses lazy-IRenderable pattern (deferred geometry).
- [ ] T046 [US3] Update `src/Fugue.Adapters.Console/Layout/LayoutComposition.fsi` to add `Composition.ofLayoutGrid` signature. Verify SC-004 gate.

**Checkpoint**: US3 PR shippable. LayoutGrid validates; sizing algorithm correct; 232 + US1 + US2 + ~9 new tests green.

---

## Phase 6: User Story 4 — Flex layout primitive (P4)

**Goal**: Expose opaque sealed `Flex` + opaque `FlexItem` + `Flex.create` / `createWith` + smart constructors `Flex.fixed`/`grow`/`shrink` + `Composition.ofFlex` per data-model.md §6. Implements CSS flexbox-like distribution along an axis (basis → free-space → flex distribution per research.md B-1 pseudocode).

**Independent Test**: Construct `Flex.create Horizontal [Flex.fixed 10 label; Flex.grow 1 input; Flex.fixed 8 submit]` at width=80; assert widths are [10, 62, 8] per US4 acceptance scenario 1 (label in cols 0–9, input in cols 10–71, submit in cols 72–79).

**Dependency**: Requires US1 (Phase 3) for the lowering pattern. Independent of US2/US3.

### Tests for User Story 4 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**

- [ ] T047 [P] [US4] Create `tests/Fugue.Adapters.Console.Tests/Layout/FlexTests.fs`. Property test: `Flex.create Horizontal [Flex.fixed 10 label; Flex.grow 1 input; Flex.fixed 8 submit]` at width=80 places "LABEL-MARKER" in cols 0..9, "INPUT-MARKER" in cols 10..71, "SUBMIT-MARKER" in cols 72..79.
- [ ] T048 [P] [US4] In FlexTests.fs: grow-ratio distribution — `Flex.create Vertical [Flex.grow 1 a; Flex.grow 2 b; Flex.grow 1 c]` at height=20 produces heights [5; 10; 5] (1:2:1 ratio) per US4 acceptance scenario 2.
- [ ] T049 [P] [US4] In FlexTests.fs: overflow Clip (default) — `Flex.create Horizontal [Flex.fixed 100 a]` at width=80 clips child a to 80 columns; output contains child a content up to col 80 (no error).
- [ ] T050 [P] [US4] In FlexTests.fs: overflow Strict — `Flex.createWith Horizontal [Flex.fixed 100 a] Strict` at width=80 returns `Error (LayoutOverflow ("Flex", "width: 100 requested, 80 available"))` per US4 acceptance scenario 3 option (b).
- [ ] T051 [P] [US4] In FlexTests.fs: error-path — empty items → `Error (EmptyComposition "Flex: at least one item required")`.
- [ ] T052 [P] [US4] In FlexTests.fs: error-path — `Flex.grow -1 child` (negative grow) returns `Error (InvalidArgument ("FlexItem", "grow must be non-negative"))`.
- [ ] T053 [P] [US4] In FlexTests.fs: shrink behaviour — at width=20 with `Flex.fixed 30 a + Flex.fixed 30 b` both with non-zero shrink, items proportionally shrink to fit. (Concrete distribution algorithm per research.md B-1.)
- [ ] T054 [P] [US4] In FlexTests.fs: colour-toggle + determinism parametric.
- [ ] T054a [P] [US4] In FlexTests.fs: depth-limit — nest Flex inside Flex 101 levels deep; assert `Error (InvalidArgument ("Layout", "depth exceeds 100"))` per spec edge case "Recursive nesting" (mirrors T017 for Stack). Closes finding G4 for Flex.

### Implementation for User Story 4

- [ ] T055 [US4] Write `src/Fugue.Adapters.Console/Layout/Flex.fsi` per `contracts/Flex.fsi`: `Overflow` DU (or import from Layout.fsi if T006 placed it there), opaque `FlexItem`, opaque sealed `Flex`, `module Flex` with `fixed`/`grow`/`shrink`/`item`/`create`/`createWith`.
- [ ] T056 [US4] Implement `src/Fugue.Adapters.Console/Layout/Flex.fs`: private records, sealed wrappers, smart constructors. `FlexItem.fixed N comp = { Grow=0; Shrink=0; Basis=N; Child=comp }`. `FlexItem.grow N comp = { Grow=N; Shrink=0; Basis=0; Child=comp }`. `FlexItem.shrink N comp = { Grow=0; Shrink=N; Basis=N; Child=comp }`. Smart constructor `Flex.create` validates non-empty items, non-negative grow/shrink/basis per item.
- [ ] T057 [US4] Implement the flexbox solver in `src/Fugue.Adapters.Console/Layout/Flex.fs` private function `solveDistribution : int -> FlexItem list -> int list` per research.md B-1 pseudocode: (1) sum basis values; (2) compute free space = available − total basis; (3) if free > 0 distribute by grow ratios; if free < 0 distribute negative space by shrink ratios; (4) return per-item final sizes. Pure function — directly unit-testable, mark `module internal Internal` for cross-file test access (mirror Phase 2 P4 `preflightLabels` pattern).
- [ ] T058 [US4] Implement `Composition.ofFlex : Flex -> Composition` in `src/Fugue.Adapters.Console/Layout/LayoutComposition.fs` per data-model.md §6 lowering: invoke `solveDistribution`, place each child in its computed rectangle via `Composition.padded`. Uses lazy-IRenderable pattern.
- [ ] T059 [US4] Update `src/Fugue.Adapters.Console/Layout/LayoutComposition.fsi` to add `Composition.ofFlex` signature. SC-004 gate.
- [ ] T060 [P] [US4] Add direct unit tests for `solveDistribution` in `tests/Fugue.Adapters.Console.Tests/Layout/FlexTests.fs` via the `Internal` accessor — happy path (basic 1:2:1 distribution), edge (all fixed, no free space), edge (negative free space with shrink), edge (zero grow + zero shrink + sum of basis < available → trailing space empty). 4 sub-tests minimum.

**Checkpoint**: US4 PR shippable. Flex solver correct; overflow policies tested; 232 + US1 + US2 + US3 + ~14 new tests green.

---

## Phase 7: User Story 5 — REPL port + LayoutScheduler (P5) 🎯 Integration proof

**Goal**: Implement `LayoutScheduler` (R-1 outcome — MailboxProcessor frame-coalescing drain-loop) AND port `src/Fugue.Cli/Repl.fs` to use Phase 3 Layout primitives end-to-end (`rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero per FR-011). Per A12 + dependency note in plan.md, US5 ships as a single combined PR (Scheduler + REPL port codependent — REPL port can't ship without Scheduler driving its render cycle).

**Independent Test**: After port, run `fugue --print "hello"` and assert output is identical to pre-port (smoke). Run interactive REPL, type a query, observe streaming markdown updates without flickering or cursor displacement. `rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero matches; `rg "Spectre\.Console" src/Fugue.Cli/Repl.fs` returns zero matches.

**Dependency**: Requires US1 + US2 (Stack + Dock are the layout primitives Repl.fs's top-level layout uses). US3 (LayoutGrid) and US4 (Flex) NOT required for the basic REPL port — they may surface in Phase 4 follow-ups (e.g. diff viewer).

### Tests for User Story 5 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **Write these tests FIRST, ensure they FAIL before implementation.**
> NOTE: REPL port integration tests require either a headless mock console driver
> or `Console.test` from Phase 2 P5. Use `Console.test` where possible; defer full
> interactive round-trip tests to a follow-up issue (mirror Phase 2 P4 T054 pattern).

- [ ] T061 [P] [US5] Create `tests/Fugue.Adapters.Console.Tests/Layout/LayoutSessionTests.fs` (renamed from data-model spec since R-1 means it's `Scheduler` not `Session` — file name `SchedulerTests.fs` is more accurate). Property test: `LayoutScheduler.create producerFn 16` returns scheduler; `Scheduler.trigger` invokes producer at most once per 16ms tick; multiple rapid `trigger` calls coalesce to ONE producer invocation per tick.
- [ ] T062 [P] [US5] In SchedulerTests.fs: referential-equality short-circuit — producer returns the same `Composition` value twice; scheduler skips the second render (cheap referential comparison).
- [ ] T062a [P] [US5] In SchedulerTests.fs: burst-rate stress — inject 1000 `Scheduler.trigger` calls in a tight loop within 1 second; assert producer invoked ≤ 62 times (16ms tick × 60fps ≈ 62 ticks/sec) AND final rendered state matches the layout produced by the last trigger. Verifies spec edge case "Streaming token rate burst 1000 tok/sec" + FR-015 coalescing intent. Closes finding G7.
- [ ] T063 [P] [US5] In SchedulerTests.fs: stop semantics — `Scheduler.stop` cleanly shuts down the Mailbox; subsequent `trigger` calls are no-ops (no exception, no error).
- [ ] T064 [P] [US5] In SchedulerTests.fs: error path — producer function returns `Error e` from internal layout build; scheduler surfaces via error callback (or dedicated error channel — design choice in data-model.md §7).
- [ ] T064a [P] [US5] In SchedulerTests.fs: concurrent-trigger thread safety — spawn N (e.g. 8) threads each calling `Scheduler.trigger` simultaneously in a tight loop for 500ms; assert no exception, no torn state, producer invoked at most once per frame interval across all threads (MailboxProcessor's inherent serialization is the contract). Verifies spec edge case "Streaming concurrent modification". Closes finding G8.
- [ ] T065 [P] [US5] Create `tests/Fugue.Adapters.Console.Tests/Layout/ReplPortIntegrationTests.fs`. Smoke test: port a simplified REPL render-loop (Dock with Bottom = input region, Bottom = status, Fill = streaming output) against `Console.test`; trigger 10 sequential streaming updates; assert final captured output contains all 10 token strings concatenated (small-scale precursor to T065a SC-005 measurement).
- [ ] T065a [P] [US5] In ReplPortIntegrationTests.fs: SC-005 throughput measurement — inject 100 streaming update events over 1 second into a `Scheduler`-driven layout against `Console.test`; assert final captured output contains all 100 token strings concatenated AND scheduler producer invoked ≤ 62 times (60fps cap). Distinct from T065 (10-event smoke) and T062a (1000-event burst stress); this is the binding SC-005 verification. Closes finding G1.
- [ ] T066 [P] [US5] In ReplPortIntegrationTests.fs: SIGWINCH simulation with timing — fire a synthetic resize event (change `RenderContext` dimensions mid-stream); assert next render uses new dimensions and layout reflows correctly. **Add `Stopwatch` measurement** from synthetic-SIGWINCH event to next render completion; assert elapsed < 16 ms (with jitter tolerance — accept up to 32 ms for CI flakiness, fail above that). Tests SC-007 + spec edge case "terminal resize during render". Closes finding G2.
- [ ] T067 [P] [US5] In ReplPortIntegrationTests.fs: SC-002 verification via pure-F# file inspection (NOT `rg` shell-out, to avoid CI-environment dependency): `File.ReadAllText "src/Fugue.Cli/Repl.fs"` then assert the string does NOT contain `"AnsiConsole"` AND does NOT contain `"Spectre.Console"`. Resolve the path via `AppContext.BaseDirectory` walk-up + repo-relative join (or via a project-root environment variable set by the test harness). Closes finding L1.

### Implementation for User Story 5

- [ ] T068 [US5] Write `src/Fugue.Adapters.Console/Layout/Scheduler.fsi` per data-model.md §7: opaque sealed `LayoutScheduler`, `Scheduler.create : (unit -> Result<Composition, RenderError>) -> int -> LayoutScheduler` (producer + frame interval ms), `Scheduler.trigger`, `Scheduler.stop`. No Spectre.* types in signature.
- [ ] T069 [US5] Implement `src/Fugue.Adapters.Console/Layout/Scheduler.fs`: MailboxProcessor with message DU `SchedulerMsg = Trigger | Tick | Stop`. Drain-loop pattern (mirror `Fugue.Surface.RealExecutor` per R-1): on `Trigger` set dirty flag; periodic `Tick` (System.Threading.Timer) fires every `frameIntervalMs`; if dirty since last tick, invoke producer, send result to host callback. Referential-equality short-circuit per T062.
- [ ] T070 [US5] Write `src/Fugue.Cli/LayoutHost.fsi` and `.fs` (new file): host shim per R-2 outcome. Takes a `LayoutScheduler` + a callback that writes rendered ANSI to `Fugue.Surface` via `DrawOp.RawAnsi`. ~50 LOC per research.md R-2 estimate. Add Compile Include entry to `src/Fugue.Cli/Fugue.Cli.fsproj` BEFORE `Repl.fs`.
- [ ] T071 [US5] Port `src/Fugue.Cli/Repl.fs` render-loop per quickstart.md §5: replace existing `AnsiConsole.Write` / direct `DrawOp` calls in the render path with `LayoutHost`-mediated layout updates. Build the top-level layout via `Dock.create [Bottom, inputRegion; Bottom, statusBar; Fill, streamingOutput]` where `streamingOutput` is a `Stack.create Vertical 0 Start assistantTurns`. Wire `LayoutScheduler.trigger` to existing token-arrival callbacks. CRITICAL: this is a high-risk port — keep the existing render code visible in a `private let legacyRender = ...` branch initially, gate via env var, run side-by-side; only delete `legacyRender` after smoke + integration tests pass.
- [ ] T072 [US5] Verify FR-011 gate: `rg 'AnsiConsole' src/Fugue.Cli/Repl.fs` AND `rg 'Spectre\.Console' src/Fugue.Cli/Repl.fs` both return zero. Run full `dotnet test` + interactive REPL smoke (`fugue` binary, type a query, observe streaming).
- [ ] T073 [US5] Hook SIGWINCH detection per research.md B-2: poll `System.Console.WindowWidth` / `WindowHeight` on every render tick; if changed since last tick, update RenderContext + re-trigger scheduler. Cross-platform safe (Windows + macOS + Linux). Verify by running tests on local + CI matrix.

**Checkpoint**: US5 PR shippable (combined Scheduler + REPL port). `Repl.fs` ports cleanly; layout primitives integrate end-to-end; 232 + US1 + US2 + US3 + US4 + ~13 new tests green; manual REPL smoke shows no regressions vs pre-port.

---

## Phase 8: Polish & Cross-Cutting

**Purpose**: SC verification gates, coverage-matrix test extensions, follow-up issue creation, `just regen-fsi` drift check.

- [ ] T074 Extend `tests/Fugue.Adapters.Console.Tests/CoverageMatrixTests.fs` with Phase 3 type-coverage assertions: enumerate all Phase 3 public types via reflection on `Fugue.Adapters.Console.Layout.*` namespace; assert each appears in either coverage-matrix.md OR a Phase 3 coverage section. SC-001 + FR-013 gate. Bonus: assert every Phase 3 smart constructor has a property test of name pattern `*Tests.fs` referencing it.
- [ ] T075 [P] Add per-Phase-3-primitive render assertion to CoverageMatrixTests.fs (extend Phase 2 T073 registry pattern): for `Stack`, `Dock`, `LayoutGrid`, `Flex`, `LayoutScheduler` build a representative value via smart constructor and assert lowered + rendered output contains marker text. Mirror Phase 2's marker-string discipline (PR-952 lesson).
- [ ] T076 [P] Verify SC-002: `rg 'AnsiConsole' src/Fugue.Cli/Repl.fs` AND `rg 'Spectre\.Console' src/Fugue.Cli/Repl.fs` both return zero. (Redundant with T072 but consolidated for the Polish PR's verification report.) Document the verification in the PR description.
- [ ] T077 [P] Verify SC-003: `dotnet test tests/Fugue.Adapters.Console.Tests -c Release` test count ≥ 350 and suite duration < 5 seconds per OS leg. If below 350 but all mandatory tests present, document the delta. If suite > 5s, split slow tests into `[<Trait("Category", "slow")>]` excluded from default run.
- [ ] T078 [P] Verify SC-004: `rg 'Spectre\.' src/Fugue.Adapters.Console/Layout/*.fsi` returns ZERO matches. (The 1 intentional match in `Renderable.fsi` is Phase 2 carry-over and outside the Layout sub-folder.) Document in PR description.
- [ ] T078a [P] Verify FR-004 (SafeText discipline) — assert no Layout primitive `.fsi` accepts raw `string` parameters (only `Composition` or DUs). Run `rg ': string( -|$| ->)' src/Fugue.Adapters.Console/Layout/*.fsi` and confirm zero matches (or limited to documented exceptions if any surface during implementation). Catches future contributor accidentally adding a raw-string parameter that would bypass SafeText escaping. Closes finding U1.
- [ ] T079 [P] Verify SC-006: benchmark test in `tests/Fugue.Adapters.Console.Tests/Layout/BenchmarkTests.fs` (NEW): build a typical Fugue UI Layout tree (depth ≤ 10, ~100 nodes — mirror research.md §R-1 measurement methodology), invoke `Composition.ofX` + `Renderer.toRawAnsi`, time with `System.Diagnostics.Stopwatch`, assert elapsed < 5 ms. Mark `[<Trait("Category", "perf")>]` so it can be excluded from the strict 5s suite budget per T077.
- [ ] T080 [P] Verify SC-008: `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` produces zero new trim warnings. Phase 3 is JIT-only by construction; this should hold by reference-chain isolation. Document in PR description.
- [ ] T081 Run `just regen-fsi` (Phase 2 T078 pattern) on all new Phase 3 `Layout/*.fs` files. Confirm no drift between generated `.fsi` content and hand-authored. Commit any drift corrections separately.
- [ ] T082 [P] Open GitHub follow-up issues for deferred Phase 3 work:
  - FR-014 ZWJ family-emoji width fix — implement `SafeText.displayWidth` API + replace transitive Spectre call where needed. Reference research.md §R-4 for context.
  - SC-002 Phase 4 completion — extend SC-002 from "Repl.fs" to "src/Fugue.Cli/* + src/Fugue.Surface/*" (currently SC-002 only gates Repl.fs after Phase 3). Track as Phase 4 work.
  - Surface.fs / Render.fs port to Layout — remaining direct-AnsiConsole call-sites in Surface.fs and Render.fs (if any) per the R-2 "option A" trade-off note (we chose Layout above Surface, not Layout replaces Surface — so non-REPL UI surfaces may still use Surface directly).
  - Full interactive round-trip tests for REPL port — currently T065-T067 use Console.test smoke; full tests need scripted stdin. Reference Phase 2 P4 T054 pattern.
  - **`legacyRender` removal** — T071 keeps the pre-port render loop in `private let legacyRender = ...` gated by env var as a safety rollback. After PR P5 ships + 1 week soak time on main with no rollback triggers, open a cleanup PR to delete `legacyRender` from `src/Fugue.Cli/Repl.fs`. Removes dead code; commits to the new path. Closes finding L2.
- [ ] T083 Run full test suite `dotnet test` from repo root one final time. Confirm all projects pass: `Fugue.Tests` (existing 573 tests), `Fugue.Adapters.Console.Tests` (target ≥ 350). Document final pass/fail count in PR description.
- [ ] T084 Polish: update `CLAUDE.md` SPECKIT marker post-Phase-3-merge to point at "Phase 3 COMPLETE" status (mirror what Phase 2 close did — update Status section, link the merged PRs). Will be done by orchestrator after the Polish PR merges, NOT in this PR.

---

## Dependencies

```
Phase 1 Setup (T001–T005)
   │
   ▼
Phase 2 Foundational (T006–T009) — common DUs in Layout.fs
   │
   ▼
Phase 3 US1 P1 (T010–T021) ── MVP blocker for US2/US3/US4
   │
   ├──────────────────────────────────────┐
   ▼                                      ▼
Phase 4 US2 P2 (T022–T033)            Phase 5 US3 P3 (T034–T046)
   │                                      │
   │                                      │
   └──────────────────────────────┐       │
                                  ▼       │
                          Phase 6 US4 P4 (T047–T060)
                                  │       │
                                  └───────┴──────────┐
                                                     ▼
                                          Phase 7 US5 P5 (T061–T073) — Scheduler + REPL port
                                                     │
                                                     ▼
                                          Phase 8 Polish (T074–T084)
                                          [runs after all US phases complete]
```

**Inter-phase notes**:
- T001–T005 (Setup) are global prerequisites; they run in the US1 PR prefix.
- T002 (RenderContext.Height call-site sweep) is a real breaking change — must precede any US-phase PR.
- T006–T009 (Foundational) — common DUs blockable across all US phases. Land in US1 PR alongside Setup.
- US1 (Phase 3) is the single hard blocker: the Composition lowering pattern + depth-check infrastructure introduced there is reused by all subsequent US phases.
- US2 (Phase 4), US3 (Phase 5), US4 (Phase 6) depend on US1 only; mutually independent — can be parallelized across contributors / interleaved across PR cycle.
- US5 (Phase 7) depends on US1 + US2 minimum (Dock for top-level Repl layout). LayoutGrid (US3) and Flex (US4) not strictly required for the basic REPL port — but if US3/US4 are merged first they're available for richer layouts.
- Phase 8 (Polish) runs only after all five user-story PRs are merged.

**PR strategy** (per spec.md Assumption A12, mirror Phase 2 cascade):
- **PR P1**: Setup (T001–T005) + Foundational (T006–T009) + US1 (T010–T021) — MVP blocker
- **PR P2**: US2 (T022–T033)
- **PR P3**: US3 (T034–T046)
- **PR P4**: US4 (T047–T060)
- **PR P5**: US5 Scheduler + REPL port (T061–T073) — codependent, combined
- **PR Polish**: Phase 8 (T074–T084)

Total: **6 PRs**, ~84 tasks.

---

## Parallel Execution Examples

**Within Phase 3 (US1) tests** — T010–T017 all write to `StackTests.fs`; assign to one contributor but note they are logically independent assertions. Implementation tasks T018–T021 span Stack.fs/.fsi and LayoutComposition.fs/.fsi — partial parallelism possible if writer A drafts Stack while writer B drafts LayoutComposition signatures.

**Within Phase 4 (US2) tests** — T022–T029 all write to `DockTests.fs` and are `[P]` (logically independent sections of the same test file). Implementation T030–T033 sequential per file.

**Within Phase 5 (US3) tests** — T034–T042 are `[P]` sections of `LayoutGridTests.fs`. T043–T046 sequential per file.

**Within Phase 6 (US4) tests** — T047–T054 are `[P]` in `FlexTests.fs`; T055–T060 — solver implementation (T057) must complete before lowering (T058) consumes it.

**Within Phase 7 (US5)** — T061–T064 (Scheduler tests) `[P]` in `SchedulerTests.fs`; T065–T067 (REPL port integration tests) `[P]` in `ReplPortIntegrationTests.fs`. Implementation T068–T073 has internal dependencies (Scheduler T068–T069 before LayoutHost T070 before Repl port T071–T072).

**Within Phase 8 (Polish)** — T074 (coverage test extension) blocks T075 (registry). All other Polish tasks are `[P]`.

---

## Implementation Strategy

**MVP first** (US1 — Stack) — independently shippable; proves architecture; unblocks all subsequent user stories.

**Incremental delivery** per Phase 2 cascade:
1. US1 PR — Setup + Foundational + Stack — establishes Layout sub-folder, common DUs, lowering pattern. Reviewer brief cites Phase 2 BLOCKER inventory.
2. US2 PR — Dock — adds edge-pinning.
3. US3 PR — LayoutGrid — adds 2D tabular layout with WPF-like sizing.
4. US4 PR — Flex — adds CSS flexbox-like axis distribution (most complex algorithm).
5. US5 PR — Scheduler + REPL port — the integration story. Proves the framework solves real Fugue UI problems.
6. Polish PR — SC verification gates + follow-up issues.

**Per-PR pattern (Phase 2 lesson)**:
1. Delegate implementation to `engineering-fsharp-developer` subagent (isolated worktree).
2. Pre-push review via `engineering-code-reviewer` with brief citing Phase 2's 6 BLOCKER patterns.
3. Apply review findings via fsharp-developer subagent.
4. Push + open PR with detailed body.
5. Immediately schedule wakeup (270s) to check CI.
6. If CI green: merge with `--merge --delete-branch` (NEVER `--squash`).
7. Sync main + delete local branch.
8. Move to next PR.

**Trust-but-verify is the norm.** Every subagent-produced output is verified by the orchestrator (build + tests + SC-004 grep + diff inspection) BEFORE proceeding. Phase 2 caught 6 BLOCKERs this way that would otherwise have shipped silent-divergence bugs.

---

## Constitution carry-over

All 11 Principles from `.specify/memory/constitution.md` (v1.1.0) continue to apply unchanged. No new amendments expected. Specifically:

- **Principle I (Test Coverage Discipline)** — every public Layout type + smart constructor has happy + error tests. NON-NEGOTIABLE.
- **Principle III (Subagent-Driven Development)** — US1–US5 PRs delegate via subagents.
- **Principle V (AOT vs JIT)** — Phase 3 is JIT-only; T080 verifies.
- **Principle XI (Context as Engineered Artifact)** — `CLAUDE.md` SPECKIT marker updated per Phase 1 step 3 (done in /speckit-plan).

If research surfaces a needed amendment during implementation (unexpected), it goes through `/speckit-constitution` separately, NOT inline.
