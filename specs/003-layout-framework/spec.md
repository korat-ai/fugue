# Feature Specification: Phase 3 — Fugue Layout Framework

**Feature Branch**: `003-layout-framework`

**Created**: 2026-05-18

**Status**: Draft

**Input**: User description: "Phase 3: высокоуровневый Layout-фреймворк для Fugue TUI поверх Phase 2 примитивов (Composition tree). Архитектура: layout-узлы — собственные F# DU/sealed types (НЕ wrap Spectre.Console.Layout, который остаётся excluded в Phase 2 coverage matrix), компилируются в дерево Composition через lowering функции. MVP scope — 4 примитива: Stack (vertical/horizontal flow с gap + align опциями), Dock (top/bottom/left/right/fill edge-pinned regions), Grid (rows × columns с fixed/auto/star sizes, WPF-подобная модель), Flex (CSS flexbox-like, grow/shrink/basis по оси). Все 4 — opaque sealed types, smart constructors возвращают Result<_, RenderError>, никаких exposed C# типов в .fsi (SC-004 продолжается), SafeText для пользовательского контента. Целевой первый port — REPL prompt + streaming markdown area в src/Fugue.Cli/Repl.fs: input region внизу, scrollable streaming markdown сверху, support для incremental updates (новые токены не пересчитывают весь layout). Те же принципы что Phase 2: typed primitives, Result smart constructors, per-module .fsi sealing, JIT-only (не в AOT closure). Открытые архитектурные вопросы для research phase: (1) static-rebuild vs reactive-diff модель обновлений (streaming требует partial updates), (2) namespace collision Layout.Grid vs Phase 2 widget Grid — переименовать Phase 2 Grid в Table или различать через namespace, (3) интеграция с существующей Surface.DrawOp моделью (Phase 3 Layout строится ПОВЕРХ Surface или ЗАМЕНЯЕТ её, или вырождает в DrawOp последовательности). Constitution v1.1.0 продолжает применяться: пишем тесты на всё, субагенты постоянно, обязательный код-ревью перед push (lesson из 6 BLOCKER'ов в Phase 2), follow-up issue если найдены новые архитектурные вопросы."

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Stack layout primitive (Priority: P1) MVP

A Fugue contributor builds a vertical or horizontal flow of child compositions — for example the REPL's main column (assistant turn, status divider, next prompt) — by stating "stack these children, this orientation, this gap, this alignment", and the framework computes the resulting placement and produces a ready-to-render `Composition` tree without the contributor reasoning about pixel coordinates or cursor positions.

**Why this priority**: Stack is the simplest layout primitive with the highest leverage. It maps directly to the REPL's natural layout (assistant turns flow downward), to the StatusBar (icons + text laid out horizontally), and to most informational panels. Closing Stack first proves the architecture (independent F# DU compiling to Composition) end-to-end on the smallest possible surface. Every other layout (Dock/Grid/Flex) reuses the Stack lowering path internally.

**Independent Test**: Construct a vertical Stack containing three `Composition` children (e.g. a Panel, a Markup, a Rule), render it through `Renderer.toRawAnsi` with a fixed RenderContext (width=80), assert the output (a) contains all three children's content in order, (b) honours the requested gap (specified blank lines between children), (c) respects the requested cross-axis alignment (left/center/right). Construct a horizontal Stack with the same three children and assert the output places them side-by-side in single-line rows with the requested gap (specified blank columns between).

**Acceptance Scenarios**:

1. **Given** a vertical Stack with children `[panel1; panel2; panel3]`, gap=1, align=Start, **When** rendered at width 80, **Then** output contains all three panels stacked top-to-bottom with exactly 1 blank line between consecutive panels and each panel left-aligned to column 0.
2. **Given** a horizontal Stack with children `[icon; text; spacer]`, gap=2, align=Center, **When** rendered at width 80, **Then** output contains all three children side-by-side on one row with 2 blank columns between consecutive children and the whole row vertically centred within its allocated height.
3. **Given** an empty Stack (no children), **When** smart constructor `Stack.create` is called, **Then** result is `Error (EmptyComposition "Stack: at least one child required")` — Stack rejects degenerate input at construction, BEFORE render.

---

### User Story 2 — Dock layout primitive (Priority: P2)

A Fugue contributor pins children to specific edges of a region — for example the StatusBar's "elapsed time pinned to bottom-right, model name pinned to bottom-left, fill empty middle" — by stating which edge each child docks to and which child fills the remaining space, and the framework computes the resulting region split without the contributor manually subtracting heights or columns.

**Why this priority**: Dock is the natural model for any TUI with status bars, sidebars, or header/footer regions. Fugue's StatusBar and the planned multi-pane layout (assistant output | thinking | tool diffs) need Dock. Lower priority than Stack only because Dock can be expressed via nested Stacks for simple cases — Dock makes the common case clean, but Stack provides the escape hatch.

**Independent Test**: Construct a Dock layout: `Dock.create [DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp]`, render at width=80 height=24, assert the status comp appears in the bottom-most row(s) and the main comp fills rows 0..(height - statusHeight - 1). Assert the same Dock with `DockEdge.Top` instead places the status comp at the top.

**Acceptance Scenarios**:

1. **Given** `Dock.create [DockEdge.Bottom, oneLineStatus; DockEdge.Fill, scrollableArea]`, **When** rendered at 80×24, **Then** the status appears at row 23 and the scrollableArea fills rows 0..22.
2. **Given** `Dock.create [DockEdge.Left, sidebar; DockEdge.Right, metadata; DockEdge.Fill, main]` with sidebar of width 20 and metadata of width 30, **When** rendered at 100×24, **Then** sidebar occupies columns 0..19, main occupies columns 20..69, metadata occupies columns 70..99.
3. **Given** a Dock with two children both requesting `DockEdge.Fill`, **When** smart constructor `Dock.create` is called, **Then** result is `Error (InvalidArgument ("Dock", "exactly one Fill child permitted"))`.

---

### User Story 3 — LayoutGrid primitive (Priority: P3)

A Fugue contributor lays out children in a 2D grid with explicit row and column sizing (fixed cells, auto-size to content, or star-share remaining space proportionally) — for example a diff viewer with two equal-width columns, a Git status panel with three columns of widths (auto, auto, star), or a settings page with label-input pairs — by stating column and row size specifications and a 2D array of child compositions, and the framework computes the cell rectangles and renders each child into its assigned cell.

**Why this priority**: LayoutGrid is the most expressive layout primitive but also the most demanding to specify (sizing model, ragged-row handling, span behaviour). Fugue's diff viewer use case is the main current driver. Lower priority than Stack/Dock because most current Fugue panels don't need a true 2D grid — they get by with nested Stacks. Including LayoutGrid in MVP per user requirement (multi-select answer in scope discussion).

**NOTE on name collision (resolved per research.md R-3)**: Phase 2 already shipped a `Grid` widget (table-without-borders type in `Fugue.Adapters.Console.DataDisplays`). Phase 3 Layout primitive is a **structurally different concept** (2D layout with sizing model, not a tabular widget). Resolution: Phase 3 type literally named `LayoutGrid` (not `Grid`) and lives under `Fugue.Adapters.Console.Layout` sub-namespace. Phase 2 widget Grid keeps its name unchanged. No breaking change.

**Independent Test**: Construct `LayoutGrid.create columns=[Star 1; Star 1] rows=[Auto; Auto] cells=[[left1; right1]; [left2; right2]]`, render at width=80, assert columns are 40 cells wide each, rows size to taller of left/right child, cells render their children into the correct rectangle. Construct a LayoutGrid with ragged rows (a row whose child count ≠ column count), assert smart constructor returns `Error (InvalidArgument ("LayoutGrid", "row N has M cells, expected K"))`.

**Acceptance Scenarios**:

1. **Given** `LayoutGrid.create columns=[Fixed 20; Star 1] rows=[Auto] cells=[[label; value]]`, **When** rendered at width=100, **Then** label occupies columns 0..19, value occupies columns 20..99.
2. **Given** `LayoutGrid.create columns=[Star 1; Star 2] rows=[Star 1; Star 1] cells=[[a; b]; [c; d]]`, **When** rendered at 90×24, **Then** column 1 is 30 cells wide, column 2 is 60 cells wide, rows are 12 cells tall each; children a/b/c/d render into their respective cells.
3. **Given** `LayoutGrid.create columns=[Star 1] rows=[] cells=[[]]`, **When** smart constructor `LayoutGrid.create` is called, **Then** result is `Error (EmptyComposition "LayoutGrid: at least one row required")`.

---

### User Story 4 — Flex layout primitive (Priority: P4)

A Fugue contributor builds a flexbox-like layout where children declare grow / shrink / basis sizing along a main axis (vertical or horizontal) and the framework distributes available space proportionally — for example an input prompt where label is fixed-width, input box flexes to fill, and submit button is fixed-width — by stating the axis, the per-child Flex sizing, and the framework solves the distribution and renders into the resulting rectangles.

**Why this priority**: Flex is the most expressive sizing model but also the most complex to specify (grow ratios, shrink ratios, basis, cross-axis alignment, wrap behaviour). Most current Fugue use cases can be served by Stack + Grid. Including Flex in MVP per user requirement; if research determines complexity outweighs payoff, can be deferred to a Phase 3.1 follow-up via tasks.md re-grouping.

**Independent Test**: Construct `Flex.create axis=Horizontal items=[Flex.fixed 10 label; Flex.grow 1 input; Flex.fixed 8 submit]`, render at width=80, assert label occupies columns 0..9, input occupies columns 10..71 (62 = 80 − 10 − 8), submit occupies columns 72..79. Construct a Flex with three `grow 1` children at width=90, assert each gets 30 columns.

**Acceptance Scenarios**:

1. **Given** `Flex.create axis=Horizontal items=[Flex.fixed 10 label; Flex.grow 1 input; Flex.fixed 8 submit]`, **When** rendered at width=80, **Then** widths are [10, 62, 8] and the children appear in three abutting horizontal regions.
2. **Given** `Flex.create axis=Vertical items=[Flex.grow 1 a; Flex.grow 2 b; Flex.grow 1 c]`, **When** rendered at height=20, **Then** heights are [5, 10, 5] (1:2:1 ratio).
3. **Given** `Flex.create axis=Horizontal items=[Flex.fixed 100 a]` at width=80, **When** rendered, **Then** the framework either (a) clips child a to 80 columns (overflow=Hidden) or (b) returns `Error (InvalidArgument ("Flex", "fixed children exceed available width: requested 100, have 80"))` — overflow policy resolved in research.

---

### User Story 5 — REPL prompt + streaming markdown port (Priority: P5) Proof of concept

A Fugue end-user runs the interactive REPL and sees: streaming markdown output in a scrollable area filling most of the terminal, the next-turn input prompt pinned to the bottom three rows, the status bar pinned to the last row, and as new LLM tokens arrive the markdown updates incrementally without redrawing the whole screen or moving the input cursor. Before this story lands, the REPL uses direct `AnsiConsole` writes + `Surface.DrawOp` cursor manipulation; after, the REPL uses Phase 3 Layout primitives end-to-end.

**Why this priority**: This is the **integration story** — the test of whether Stack/Dock/Grid/Flex are correctly designed for real use. Without it the layout primitives are theoretically nice but unproven. The current REPL render loop (`src/Fugue.Cli/Repl.fs`) is the most complex UI surface in the codebase: streaming output, scrolling, partial updates, cursor coordination with input. If the Layout framework cleanly handles this, it handles the rest of Fugue trivially.

**Independent Test**: Port `src/Fugue.Cli/Repl.fs` to use a top-level `Dock` (Bottom = input region, Bottom = status bar, Fill = streaming output) where the Fill child is a Stack of assistant turns and each streaming assistant turn updates in place as tokens arrive. Validate: (a) the input cursor stays at the correct row/column even as the markdown area redraws above it, (b) `rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero matches (all rendering goes through the layout/adapter), (c) typing during a streaming response does not flicker or duplicate characters, (d) terminal resize (`SIGWINCH`) is handled — the layout re-computes against the new dimensions.

**Acceptance Scenarios**:

1. **Given** the REPL is in an idle state with the input prompt visible at the bottom, **When** the user types a query and presses Enter, **Then** the input area clears, a new assistant turn appears in the streaming area, and as tokens arrive the assistant turn grows downward without disturbing the input cursor position.
2. **Given** the REPL is streaming a long response that exceeds the terminal height, **When** the streaming area scrolls upward to make room for new content, **Then** the input region and status bar remain pinned to the bottom and the cursor in the input region (if any) is preserved.
3. **Given** the user resizes the terminal mid-stream, **When** `SIGWINCH` fires, **Then** the Layout framework re-computes against the new width/height, the streaming area reflows its markdown, and the input region and status bar reposition correctly within one render frame.

---

### Edge Cases

- **Empty layout**: any layout primitive constructor called with zero children (or zero rows/columns for Grid) returns `Error (EmptyComposition _)` synchronously. No render path attempted.
- **Layout overflow**: when children's requested space exceeds the available terminal dimension, the framework either (a) clips to terminal bounds (default), (b) returns `Error (InvalidArgument _)` if explicit overflow=Strict is set, or (c) wraps to a new row/column where the axis permits. Default policy resolved in research.md.
- **Negative or zero terminal dimensions**: width=0 or height=0 from the terminal (rare but possible during resize transients) returns `Error (InvalidArgument ("Renderer", "non-positive dimensions: width=0 or height=0"))` — render is refused, the previous frame remains visible.
- **Recursive nesting**: a Stack inside a Stack inside a Grid inside a Flex (etc.) is permitted and tested up to depth 100. Depth > 100 returns `Error (InvalidArgument ("Layout", "depth exceeds 100"))` — protects against unbounded recursion that would stack-overflow Spectre's renderer.
- **Mixed sizing in Grid**: `columns=[Fixed 20; Auto; Star 1; Star 2]` — Fixed allocated first, Auto sized to content, remaining space split among Star children by ratio. Tested with edge cases where Auto content exceeds available, where Star children sum to fractional pixels, where all columns are Fixed and total < available (extra space is left blank).
- **Streaming concurrent modification**: a streaming update arrives while the layout is being computed for a redraw. The framework must serialise updates per-layout-tree (no concurrent computation on the same tree) and either drop / coalesce / queue updates per the chosen reactive model. Resolution in research.md.
- **Terminal resize during render**: `SIGWINCH` fires mid-render. The current frame finishes with the previous dimensions; the next frame uses the new dimensions. No torn frame.
- **Streaming token rate burst**: 1000 tokens/sec spike (cache hit on local LLM). Layout framework must coalesce updates to terminal refresh rate (~60 Hz) without dropping content from the final view.
- **Zero-width characters in content**: emoji, combining characters, ANSI escape sequences in user-supplied SafeText. Width calculation must use display columns (East Asian Width-aware), not byte length or UTF-16 code units.
- **Right-to-left languages**: out of scope for MVP. If RTL content (Arabic, Hebrew) appears in a layout, it renders left-to-right as if LTR. Documented assumption; future Phase 4 if needed.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST expose four opaque sealed layout primitive types in `Fugue.Adapters.Console.Layout`: `Stack`, `Dock`, `LayoutGrid` (literally named `LayoutGrid` to disambiguate from Phase 2 widget `Grid` in `DataDisplays`; Phase 2 widget keeps its name), and `Flex`. Each type is constructed only via its smart constructor.
- **FR-002**: System MUST provide smart constructors for each layout primitive that return `Result<'Layout, RenderError>`. Constructors validate degenerate inputs (empty children list, illegal sizing combinations, depth > 100) BEFORE any render call.
- **FR-003**: System MUST provide per-primitive `Composition.of<Layout>` factories that lower a Layout value into the existing Phase 2 `Composition` tree. After lowering, layouts are renderable via the existing `Renderer.toRawAnsi` path. No new top-level rendering entry point.
- **FR-004**: System MUST accept `SafeText` (not raw `string`) for any user-supplied textual content embedded in layout children. The Layout framework does not introduce new string-acceptance points; user content travels through Phase 1/2 primitives and is escaped at Phase 1 boundaries.
- **FR-005**: System MUST NOT expose any Spectre.* type in the public `.fsi` signatures of the Layout modules (continues SC-004 from Phase 2). The one intentional bridge from Phase 2 (`Renderable.fromSpectre`) is the sole permitted leak; Phase 3 adds none.
- **FR-006**: System MUST NOT wrap `Spectre.Console.Layout`, `Spectre.Console.Region`, or `Spectre.Console.VerticalAlignment`. These three types remain `excluded` in the coverage matrix per Phase 2 decision. Phase 3 Layout is an independent implementation.
- **FR-007**: System MUST coalesce streaming-driven layout updates to a frame-rate budget so that streaming token arrival (e.g. assistant turns appending new markdown 100×/sec) does not back-pressure the rendering pipeline. Internal model is static-rebuild + scheduler-driven coalescing per research.md R-1 (whole-tree rebuilds are sub-millisecond; the "incremental" intent is satisfied via frame-rate coalescing rather than partial subtree mutation). Verified by SC-005 (100 upd/s throughput) and SC-006 (< 5 ms per-render latency).
- **FR-008**: System MUST produce deterministic output: the same Layout value rendered twice at the same RenderContext (width, height, colour enabled) produces byte-identical output. No internal mutation between renders of an immutable Layout value.
- **FR-009**: System MUST handle terminal resize: when the caller supplies new RenderContext dimensions, the Layout re-computes against the new dimensions on the next render. No stale-dimension caching. Implementation extends `RenderContext` with a `Height: int` field (Phase 1+2 had width-only); backward compat preserved via sentinel default `System.Int32.MaxValue` meaning "unconstrained height". Existing Phase 1+2 call-sites that omit height get the sentinel (zero behavioural change); new Layout consumers supply real terminal height.
- **FR-010**: System MUST integrate with the existing `Fugue.Surface.DrawOp` model in one of three specified ways (research.md decides): (a) Layout is **above** Surface — compiles to Composition, which becomes ANSI strings, which Surface writes; (b) Layout **replaces** Surface in `Fugue.Cli` — `Repl.fs` no longer uses DrawOp; (c) Layout **lowers to** DrawOp — Layout produces a DrawOp sequence that Surface executes. The choice is internal-implementation; the user-facing API is the same in all three.
- **FR-011**: System MUST provide a single-step migration path for `src/Fugue.Cli/Repl.fs`: after the port, `rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero matches and `rg "Spectre.Console" src/Fugue.Cli/Repl.fs` returns zero matches (the adapter is the only Spectre consumer in Repl.fs).
- **FR-012**: System MUST enforce per-module `.fsi` sealing for every Phase 3 module pair (one `.fs` + one `.fsi` per logical unit). Same discipline as Phase 1 + Phase 2.
- **FR-013**: System MUST publish a coverage assertion test analogous to Phase 2's `CoverageMatrixTests.fs`: every public Layout type and every public smart constructor has at least one happy-path property test AND one error-path test (Principle I, NON-NEGOTIABLE).
- **FR-014**: System MUST handle East Asian Wide characters (CJK ideographs), zero-width characters (combining marks, ZWJ), and emoji (including ZWJ sequences) when computing display width for layout calculations. Width = display columns, not byte length or UTF-16 code units.
- **FR-015**: System MUST coalesce streaming updates that arrive faster than the terminal can redraw (target 60 Hz). When N updates arrive within one refresh interval, only the most recent state is rendered to the terminal; intermediate states are dropped from the output but reflected in the final state.
- **FR-016**: System MUST remain JIT-only — Phase 3 additions go into `src/Fugue.Adapters.Console/` (existing fsproj) or a new `src/Fugue.Adapters.Console.Layout/` JIT project. Phase 3 code MUST NOT be referenced from `src/Fugue.Cli.Aot/`. Verified by `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` producing zero new trim/AOT warnings after Phase 3 merges.

### Key Entities *(include if feature involves data)*

- **Stack**: Vertical or horizontal flow of child Compositions. Holds: orientation (V/H), gap (≥ 0), cross-axis alignment (Start/Center/End/Stretch), children (non-empty `Composition list`).
- **Dock**: Edge-pinned region split. Holds: a list of `(DockEdge * Composition)` pairs where exactly one entry uses `DockEdge.Fill`. `DockEdge` is a closed DU: Top / Bottom / Left / Right / Fill.
- **Grid (Layout-Grid)**: 2D tabular layout. Holds: column sizing list (`ColumnSize list`), row sizing list (`RowSize list`), cells (2D `Composition` matrix conforming to row × column shape). `ColumnSize` / `RowSize` are closed DUs: `Fixed of int` (cells wide) / `Auto` (size to content) / `Star of int` (proportional share of remaining space, ratio).
- **Flex**: Flexbox-like axis distribution. Holds: axis (V/H), items (non-empty `FlexItem list`). `FlexItem` carries grow ratio, shrink ratio, basis (initial size), and the child Composition. Cross-axis alignment (Stretch by default).
- **RenderContext**: existing Phase 2 type, extended (if needed) with height (currently width-only). Layout primitives consume `RenderContext.width` and `RenderContext.height` during lowering.
- **LayoutSession** (only if reactive-diff model wins research.md): an opaque handle to a mounted layout tree that supports `updateChild` / `redraw` operations without rebuilding the whole tree. If static-rebuild model wins, this entity does not exist.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of the four Phase 3 layout primitives (Stack, Dock, Grid, Flex) have happy-path + error-path property tests covering at minimum: empty input, single child, multi-child, degenerate sizing, max-depth boundary. Verified by the coverage-assertion test (FR-013).
- **SC-002**: `src/Fugue.Cli/Repl.fs` after the Phase 3 REPL port satisfies: `rg "AnsiConsole" src/Fugue.Cli/Repl.fs` returns zero matches AND `rg "Spectre\.Console" src/Fugue.Cli/Repl.fs` returns zero matches. (The Phase 2 SC-002 — REPL-wide elimination — graduates here from "deferred" to "satisfied for Repl.fs"; other call-sites in `Surface.fs`, `Render.fs` may follow in Phase 4.)
- **SC-003**: Adapter test suite grows from 232 tests (Phase 2 close) to ≥ 350 tests by Phase 3 close. Suite still runs under 5 seconds per OS leg (continues Phase 2's SC-003 budget).
- **SC-004**: Zero `Spectre\.` references appear in any new `.fsi` file added by Phase 3. `rg 'Spectre\.' src/Fugue.Adapters.Console/Layout/*.fsi` (or equivalent path) returns zero matches. Continues Phase 2 SC-004 enforcement on the new module pairs.
- **SC-005**: Streaming token-arrival throughput: the framework handles 100 update events per second (typical LLM streaming rate) without dropping content from the final rendered state. Measured by injecting 100 token events over 1 second into a mock streaming layout and asserting the final rendered output contains all 100 token strings concatenated.
- **SC-006**: Layout calculation latency: a typical Fugue UI Layout tree (depth ≤ 10, total nodes ≤ 100) computes its rendered output in under 5 milliseconds on the project CI matrix per a benchmark test. Distinct from SC-005 (which measures throughput) — SC-006 measures per-render latency.
- **SC-007**: Terminal resize responsiveness: after a `SIGWINCH` event, the layout re-computes and the next render completes within one frame (~16 ms) of the resize event. Measured by integration test that fires a synthetic resize and times to next-render.
- **SC-008**: AOT closure remains clean — `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` produces zero new trim/AOT warnings after Phase 3 merges. Continues Phase 2 SC-006.
- **SC-009**: Pre-push code review catches at minimum two blocker-class defects per non-trivial PR (lesson from Phase 2's 6 BLOCKERS / 5 PRs ≈ 1.2 blockers/PR). Measured by reviewing the local-review reports across Phase 3 PRs; if any PR ships with a post-merge revert due to a defect that pre-push review missed, the SC fails for that PR and is documented as a lesson in the next-Phase constitution amendment.

## Assumptions

- **A1 (Grid namespace)**: Phase 2 widget `Grid` (`Fugue.Adapters.Console.DataDisplays.Grid`) keeps its current name. Phase 3 layout-Grid uses a distinct namespace such as `Fugue.Adapters.Console.Layout.Grid` (or a separate `Fugue.Adapters.Console.Layout.fsproj`). No rename of Phase 2 Grid (would be breaking change to merged code). Final namespace choice deferred to `/speckit-plan` research.md.
- **A2 (Composition is IR)**: Phase 2's `Composition` tree is the **intermediate representation** of all rendered output. Phase 3 Layout primitives lower into `Composition` (possibly via new `internal` `Composition` cases such as `Composition.LayoutLowered`) and use the existing `Renderer.toRawAnsi` path. No new top-level rendering pipeline; no parallel renderer.
- **A3 (Reactive vs static — RESEARCH)**: The choice between static-rebuild (every update rebuilds the layout tree from scratch) and reactive-diff (layout tree is mutable, updates touch only changed subtrees) is the **single biggest architectural decision** for Phase 3. The user-facing API (FR-007 `update child`) is designed to work with either; the choice affects performance, complexity, and the existence of `LayoutSession`. Deferred to `/speckit-plan` research.md as decision R-1.
- **A4 (Surface.DrawOp integration — RESEARCH)**: FR-010 enumerates three integration models (Layout above Surface / Layout replaces Surface / Layout lowers to DrawOp). The choice affects how `src/Fugue.Surface/` evolves alongside Phase 3. Deferred to `/speckit-plan` research.md as decision R-2.
- **A5 (Streaming rate)**: Layout coalescing assumes LLM token arrival up to 100 tokens/sec sustained and bursts up to 1000 tokens/sec. These rates cover typical cloud LLM streaming (Anthropic ~50 tok/sec, OpenAI ~60 tok/sec, Ollama local ~100 tok/sec) and burst case (LLM cache hits). If a future provider exceeds 1000 tok/sec sustained, FR-015 coalescing strategy is re-evaluated.
- **A6 (Terminal size minimum)**: Layout primitives assume terminal dimensions ≥ 20 columns × 5 rows. Below this, smart constructors / render path may return `Error (InvalidArgument ("Renderer", "dimensions below minimum: 20x5"))`. 20×5 is below any reasonable interactive terminal (smallest practical xterm is 80×24).
- **A7 (JIT-only)**: All Phase 3 additions go into JIT-only projects (`Fugue.Adapters.Console` or new `Fugue.Adapters.Console.Layout`). Layout will never be referenced by `Fugue.Cli.Aot` (the headless binary doesn't need TUI layout). Verified by SC-008.
- **A8 (Subagent delegation continues)**: Phase 3 PRs delegate implementation to `engineering-fsharp-developer` subagent in isolated worktrees, with pre-push review by `engineering-code-reviewer`. Lesson from Phase 2: trust-but-verify caught 6 BLOCKERS across 5 PRs that would otherwise have shipped silent-divergence bugs.
- **A9 (Mandatory pre-push review)**: Every Phase 3 PR runs through `engineering-code-reviewer` BEFORE `git push`. Reviewer brief cites prior-PR blocker patterns (Phase 2 inventory: typeName regression, silent overload pick, Mode-ignoring, silent render-error swallow, error sentinel collision, missing-test coverage) so the reviewer knows the failure modes. This is now a project rule, not a per-PR decision.
- **A10 (Constitution v1.1.0 governs)**: All Constitution principles continue to apply (Test Coverage Discipline NON-NEGOTIABLE, Tool Contract Discipline, Adapter Boundary, etc.). Phase 3 introduces no new constitution amendments; if research surfaces a needed amendment, it goes through `/speckit-constitution` separately.
- **A11 (Follow-up issue tradition)**: Phase 3 PRs open follow-up GitHub issues for deferred work — same pattern as Phase 2's #951 (FR-017 test migration). Examples likely to surface: full Flex spec corner cases, Phase 4 RTL support, Surface.fs / Render.fs port (if A4 chooses "Layout above Surface" rather than "Layout replaces Surface").
- **A12 (PR splitting by user story)**: Phase 3 PRs follow Phase 2's pattern — one PR per user story (or per pair where co-dependent). Initial estimate: 5 PRs (P1 Stack, P2 Dock, P3 Grid, P4 Flex, P5 REPL port) + Polish PR (coverage tests + SC verification). Tasks.md will confirm the split after `/speckit-tasks`.
- **A13 (Spec Kit feature directory)**: `specs/003-layout-framework/`. Branch `003-layout-framework`. Number 003 per sequential numbering.
