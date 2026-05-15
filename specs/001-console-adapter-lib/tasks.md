---

description: "Task list — F# Console Adapter Library"
---

# Tasks: F# Console Adapter Library

**Input**: Design documents from `/specs/001-console-adapter-lib/`

**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: MANDATORY for every behavioural change per Principle I of
`.specify/memory/constitution.md` (v1.1.0, Test Coverage Discipline,
NON-NEGOTIABLE). Integration tests against real Spectre.Console are also
MANDATORY per Principle II (C#/F# Interop Sanitization, NON-NEGOTIABLE).
No "ship without tests" carve-out.

**Organization**: Tasks are grouped by user story to enable independent
implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel — see semantics paragraph below
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Setup / Foundational / Polish phases carry no `[Story]` label
- Every task has an exact file path

**`[P]` semantics (canonical)**: `[P]` means "**different file** from
other `[P]`-marked tasks in the same phase, so two writers can edit at
the same time without conflict". It does NOT mean "no dependencies at
all". A test task can be `[P]` even when it logically depends on its
implementation task's code being present at compile time — the
dependency runs through the build, not through the writer's keyboard.
Practical reading: if you're scheduling a contributor for an evening's
work, `[P]`-marked tasks in the current phase can be claimed in any
order; same-phase non-`[P]` tasks (like T006 R2 debate, T025 Renderer
core) are blockers the rest of the phase waits on.

## Path Conventions

- **Adapter library**: `src/Fugue.Adapters.Console/`
- **Test project**:    `tests/Fugue.Adapters.Console.Tests/`
- **Solution file**:   `Fugue.slnx` (repo root)
- **Build script**:    `Justfile` (repo root)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Scaffold the two new projects, wire references, ensure
`dotnet build` is green before any logic is written.

- [ ] T001 Create `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj` targeting `net10.0`, `TreatWarningsAsErrors=true`, `WarningLevel=5`. Library output type. Add `PackageReference` to `Spectre.Console` Version `0.49.*` and `ProjectReference` to `../Fugue.Surface/Fugue.Surface.fsproj` (for `DrawOp` interop per R4 of research.md).
- [ ] T002 [P] Create `tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj` targeting `net10.0`. Add `PackageReference` to `xunit`, `xunit.runner.visualstudio`, `FsUnit.xUnit`, `Verify.Xunit` (snapshot tests — see research.md R3), `FsCheck.Xunit` (property tests — see data-model.md §3). Add `ProjectReference` to `../../src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj`.
- [ ] T003 Add both new projects to `Fugue.slnx`. Confirm `dotnet build` at the repo root succeeds with zero warnings.
- [ ] T004 [P] Create `tests/Fugue.Adapters.Console.Tests/VisualParity/.gitkeep` so the empty snapshot directory is committed (Verify writes `.received.txt` next to `.verified.txt`; the directory must exist before any test runs).
- [ ] T005 [P] Add a `verify-spectre-version` script to `Justfile`: prints the resolved `Spectre.Console` version from the lock file. Used during upgrade workflow (US3 deliverable, but the script itself is shared infra).

**Checkpoint**: `dotnet build` green; `dotnet test tests/Fugue.Adapters.Console.Tests` runs (zero tests is fine at this point).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Resolve the R2 architecture decision and land the
shape-invariant foundation types (`RenderError`, `RenderContext`,
`Style`, `SafeText`) that every primitive and composition depend on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T006 **Run the R2 three-way subagent debate** for API shape. Research.md §R2 surfaces THREE candidate shapes (A. record-DU primitives, B. computation expression, C. builder functions on opaque type) — Principle III mandates the debate cover all surfaced candidates, not a pre-pruned subset. Dispatch THREE `engineering-fsharp-developer` subagents in parallel — one arguing FOR record-DU (A), one FOR computation expression (B), one FOR builder functions (C). Each subagent gets the same context: spec.md FR-002, FR-003, FR-004, data-model.md type sketches, research.md §R2 trade-off table, inventory addendum. 3–5 rounds of cross-rebuttal (each round each subagent reads the other two's latest positions and counters). Final escalation to user: present three position-summaries (≤200 words each), the single strongest counter-argument that each side could not fully rebut, and any common ground emerged. **Block until the user selects a shape.** Record the chosen shape in `specs/001-console-adapter-lib/research.md` §R2 as a new "Decision" line (replaces the current "DEFERRED" status). Subsequent implementation tasks use the chosen shape; this task gates Phases 3, 4, 5.
- [ ] T007 [P] Define `RenderError` DU in `src/Fugue.Adapters.Console/Errors.fs` per data-model.md §1. All five cases (`InvalidMarkup`, `InvalidStyleSpec`, `DegenerateWidth`, `EmptyComposition`, `RenderFailed`). No catch-all.
- [ ] T008 [P] Define `Colour`, `Decoration`, `Style` types + `Style` smart constructors in `src/Fugue.Adapters.Console/Style.fs` per data-model.md §2 and contracts/Fugue.Adapters.Console.fsi. `Style` MUST be a private-field record (`[<Sealed>]` per the contract). Smart constructors `create`, `withFg`, `withBg`, `withDeco`, `ofMarkupHint` return `Result`.
- [ ] T009 [P] Define `SafeText` type + `ofUser`/`ofLiteral`/`unwrap` smart constructors in `src/Fugue.Adapters.Console/SafeText.fs` per data-model.md §3. Private DU constructor; `ofUser` applies `Markup.Escape` exactly once.
- [ ] T010 [P] Define `RenderContext` record + `probe`/`create` constructors in `src/Fugue.Adapters.Console/Context.fs` per data-model.md §6. `probe` is the ONLY function in the adapter that reads `System.Console`; comment that contract.
- [ ] T011 [P] [Test] Property test for `SafeText.ofUser` idempotence in `tests/Fugue.Adapters.Console.Tests/SafeTextPropertyTests.fs` using `FsCheck.Xunit`. Test body matches the snippet in data-model.md §3.
- [ ] T012 [P] [Test] Unit tests for `Style.create` happy and error paths (invalid theme slot, bad rgb byte, conflicting decoration set) in `tests/Fugue.Adapters.Console.Tests/StyleTests.fs`. xUnit + FsUnit.
- [ ] T013 [P] [Test] Unit tests for `RenderContext.create` and a smoke test that `probe()` returns a valid record (width ≥ 0, theme = some string) in `tests/Fugue.Adapters.Console.Tests/ContextTests.fs`.
- [ ] T013a [P] [Test] Unit tests for `Style.ofMarkupHint` happy path (parses `"bold #ff0000"` → expected `Style`) and Error path (malformed hint `"bold #zz"` → `Result.Error (RenderError.InvalidStyleSpec _)`) in `tests/Fugue.Adapters.Console.Tests/StyleTests.fs`. Added 2026-05-16 per `/speckit-analyze` M2 (closes coverage gap on the markup-hint bridge function).

**Checkpoint**: R2 chosen; `RenderError`, `Style`, `SafeText`, `RenderContext` compile; foundation tests green. User story phases can now start in parallel.

---

## Phase 3: User Story 1 — Port existing rendering site without ceremony (P1) 🎯 MVP

**Goal**: Deliver enough adapter surface that a Fugue contributor can pick
one existing Spectre call-site and rewrite it against the adapter, with
visible terminal output identical to before (Acceptance Scenarios 1.1,
1.2, 1.3 of spec.md).

**Independent Test**: Take ONE existing rendering call-site (suggested:
the user-message render in `Render.fs::userMessage`, or the diff-line
output in `DiffRender.fs`), rewrite it using only `Primitive.Styled` +
`Primitive.LineBreak` + `Renderer.render`, build, run REPL, confirm
output is byte-identical to the pre-port output (verified by adding a
Verify snapshot for that specific call-site).

### Tests for User Story 1 (MANDATORY — Principle I, NON-NEGOTIABLE) ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation.**
> A user story without happy-path AND Error/None-path tests cannot ship.

- [ ] T014 [P] [US1] Integration test: `Primitive.Styled` with `Style.empty` and an English `SafeText` produces the same bytes as Spectre's `Markup(Markup.Escape text)`. File: `tests/Fugue.Adapters.Console.Tests/PrimitiveTests.fs`. Test uses real Spectre (no mocks — Principle II).
- [ ] T015 [P] [US1] Integration test: `Primitive.Styled` with red `Colour.Theme "error"` + `Decoration.Bold` produces the same bytes as Spectre's `Markup "[red bold]text[/]"`. File: same as T014.
- [ ] T016 [P] [US1] Integration test: `Primitive.Markup` (bridge case) with valid markup matches Spectre; with malformed markup (`"[red][unclosed"`) returns `Result.Error (RenderError.InvalidMarkup _)` rather than throwing. File: same as T014.
- [ ] T017 [P] [US1] Integration test: `Primitive.LineBreak` emits exactly `"\r\n"`. File: same as T014.
- [ ] T018 [P] [US1] Integration test: `Primitive.RawAnsi` passes its byte string through untouched (no escaping, no styling injection). File: same as T014.
- [ ] T019 [P] [US1] Integration test: `Primitive.Rule` with `Style.empty` matches Spectre's `Rule()` byte stream at width 80 and at width 40. File: same as T014.
- [ ] T020 [P] [US1] Verify snapshot test: render each primitive with a stable `RenderContext { Width = 80; ColourEnabled = true; ThemeName = "default" }` and snapshot the byte output to `tests/Fugue.Adapters.Console.Tests/VisualParity/primitive_*.verified.txt`. File: `tests/Fugue.Adapters.Console.Tests/PrimitiveSnapshotTests.fs` + accompanying `ModuleInitializer.fs` configuring Verify.
- [ ] T021 [P] [US1] Integration test for FR-007 (`ColourEnabled = false` → no colour escapes): `Primitive.Styled` with a red foreground renders with NO `\x1b[31m` / `\x1b[38;...m` sequences when colour is off; the rest of the structure is preserved. File: `tests/Fugue.Adapters.Console.Tests/ColourDisabledTests.fs`.
- [ ] T022 [P] [US1] Integration test for FR-008 (degenerate width): `Primitive.Styled` with `Width = 0` and `Width = -5` returns `Result.Ok` with clipped/empty content, NEVER throws. File: `tests/Fugue.Adapters.Console.Tests/DegenerateWidthTests.fs`.
- [ ] T022a [P] [US1] Integration test for `Renderer.render` actor delivery: using a MockExecutor-style Surface stub (pattern from `tests/Fugue.Tests/ReadLineActorRoutingTests.fs`), assert `Renderer.render ctx (Composition.Leaf Primitive.LineBreak)` posts exactly one `DrawOp.RawAnsi "\r\n"` to the stub's WriteLog and returns `Result.Ok ()`. Confirms the side-effecting render entry-point actually delivers to the actor (no orphaned async, no double-post). File: `tests/Fugue.Adapters.Console.Tests/RendererActorTests.fs`. Added 2026-05-16 per `/speckit-analyze` M3.

### Implementation for User Story 1

- [ ] T023 [P] [US1] Define the `Primitive` DU in `src/Fugue.Adapters.Console/Primitive.fs` per data-model.md §4 (5 cases). Shape per R2 outcome (T006).
- [ ] T024 [P] [US1] Define minimal `Composition` shape — `Leaf of Primitive` only — in `src/Fugue.Adapters.Console/Composition.fs` per data-model.md §5. Remaining cases (Stack, Padded, Panel, Columns, Aligned, Table) deferred to US2.
- [ ] T025 [US1] Implement `Renderer.toRawAnsi` for the `Leaf` branch in `src/Fugue.Adapters.Console/Renderer.fs` per data-model.md §7. Internally uses Spectre's ephemeral `AnsiConsole.Create` + `StringWriter` pattern (the `Surface.fs::spectre` helper, absorbed into the adapter per R4). Returns `Result<string, RenderError>`. Depends on T023, T024.
- [ ] T026 [US1] Implement `Renderer.toDrawOp` and `Renderer.render` in the same file. `toDrawOp` is `toRawAnsi |> Result.map DrawOp.RawAnsi`; `render` posts to `Fugue.Surface` via `Surface.batch`. Depends on T025.
- [ ] T027 [US1] Make `Renderer.toRawAnsi` honour `RenderContext.ColourEnabled = false` (FR-007) — when false, configure the ephemeral `AnsiConsole` profile to `ColorSystem.NoColors`. Verified by T021. Depends on T025.
- [ ] T028 [US1] Make `Renderer.toRawAnsi` tolerate `RenderContext.Width ≤ 0` (FR-008) — clamp to a minimum of 1 internally, never throw. Verified by T022. Depends on T025.
- [ ] T029 [US1] Theme slot resolver: implement `Colour.Theme slot` lookup inside `Renderer.toRawAnsi`. Slot table is a private `Map<string * string, Spectre.Console.Color>` keyed on `(themeName, slot)` (private internal type — does NOT leak to public surface, so FR-003 is unaffected). Implement EXACTLY the canonical slot list in `data-model.md` §2 "Canonical theme slots (v1)" (7 slots × 2 themes = 14 entries: `default` and `nocturne`). Unknown slot returns `Colour.Default` (no error — slots are open-ended; per data-model). Depends on T025.
- [ ] T030 [US1] Public signature file `src/Fugue.Adapters.Console/Console.fsi` exposing exactly the surface in contracts/Fugue.Adapters.Console.fsi (US1 portion: `RenderError`, `Colour`, `Decoration`, `Style` + module, `SafeText` + module, `Primitive`, `Composition.Leaf`, `RenderContext` + module, `Renderer.toRawAnsi`/`toDrawOp`/`render`). Verified by `dotnet build` (FS errors if internal types leak).
- [ ] T031 [US1] **Verification — exemplar parity snapshot**: pick ONE concrete existing call-site (proposed: `Render.fs::assistantLive` — the simplest IRenderable in the inventory) and add a single Verify snapshot test that produces the byte stream of that call-site **through the adapter**, asserting byte-identical match against a baseline captured from direct Spectre. The existing `Render.fs` source MUST NOT be modified by this task (port PRs are a separate workstream per spec Assumption). Concrete deliverables: (a) `tests/Fugue.Adapters.Console.Tests/VisualParity/exemplar_assistantLive.verified.txt` — baseline byte stream captured via direct Spectre call against representative input; (b) `tests/Fugue.Adapters.Console.Tests/ExemplarParityTests.fs` — one `[Fact]` that builds the adapter-side `Composition` for the same input and asserts via Verify; (c) one row appended to `quickstart.md` Appendix A "Port log" naming `assistantLive` as the proven exemplar with the snapshot path. Sharpens 2026-05-16 per `/speckit-analyze` M5.

**Checkpoint**: User Story 1 fully functional. Adapter can stand in for direct primitive Spectre calls. MVP shippable as a standalone PR.

---

## Phase 4: User Story 2 — Add a new rendering site without learning the C# library (P2)

**Goal**: Adapter covers composition (Panel, Padded, Columns, Rows,
Aligned, Table) so a contributor can build a NEW rendering function
using only the adapter, with no `open Spectre.Console`.

**Independent Test**: A contributor writes a new function — e.g., a
two-column notification renderer — using only the adapter; the resulting
diff contains zero references to `Spectre.Console`. Verified by `rg "Spectre"` over the new file returning zero hits.

### Tests for User Story 2 (MANDATORY) ⚠️

- [ ] T032 [P] [US2] Integration test: `Composition.stack [Leaf a; Leaf b]` produces the same bytes as Spectre's `Rows [|a; b|]`. File: `tests/Fugue.Adapters.Console.Tests/CompositionTests.fs`.
- [ ] T033 [P] [US2] Integration test: `Composition.padded 2 4 0 0 inner` produces the same bytes as Spectre's `Padder(inner).PadLeft(2).PadRight(4)`. Test that `padded -1 0 0 0 inner` returns `Result.Error (RenderError.RenderFailed _)` (or similar — negative padding rejected at build time per data-model.md §5). File: same.
- [ ] T034 [P] [US2] Integration test: `Composition.panel Border.Rounded (Some header) inner` matches Spectre's `Panel(inner, Border = BoxBorder.Rounded, Header = PanelHeader(header))`. File: same.
- [ ] T035 [P] [US2] Integration test: `Composition.columns [(0.3, a); (0.7, b)]` matches Spectre column layout. Test that ratios summing to > 1.0 (e.g., `[(0.6, a); (0.6, b)]`) return `Result.Error (RenderError.RenderFailed _)`. File: same.
- [ ] T036 [P] [US2] Integration test: `Composition.aligned Alignment.RightAlign inner` matches Spectre's `Align(inner, HorizontalAlignment.Right)`. File: same.
- [ ] T037 [P] [US2] Integration test: `Composition.table headers rows` happy-path with 2 columns × 3 rows matches Spectre's `Table` byte output. Ragged rows (a row with the wrong column count) returns `Result.Error`. File: same.
- [ ] T038 [P] [US2] Verify snapshot tests for each composition primitive at stable `RenderContext` (Width 80, colour enabled, default theme). Snapshots in `tests/Fugue.Adapters.Console.Tests/VisualParity/composition_*.verified.txt`. File: `tests/Fugue.Adapters.Console.Tests/CompositionSnapshotTests.fs`.
- [ ] T039 [P] [US2] Property test: stacking a single-element list is equivalent to the element itself: `Composition.stack [c] ≡ c` (under render-output equality). File: `tests/Fugue.Adapters.Console.Tests/CompositionPropertyTests.fs`.

### Implementation for User Story 2

- [ ] T040 [P] [US2] Define `Border`, `Alignment` DUs in `src/Fugue.Adapters.Console/Composition.fs` per data-model.md §5.
- [ ] T041 [P] [US2] Extend the `Composition` DU with `Stack`, `Padded`, `Panel`, `Columns`, `Aligned`, `Table` cases. Same file, alongside `Leaf` from T024.
- [ ] T042 [P] [US2] Implement smart constructors `Composition.stack`, `panel`, `aligned` (non-validating, since they take well-typed inputs). Same file.
- [ ] T043 [P] [US2] Implement smart constructors `Composition.padded`, `columns`, `table` returning `Result<Composition, RenderError>`. Reject: negative padding (`padded`), ratios summing > 1.0 (`columns`), ragged rows (`table`). Same file.
- [ ] T044 [US2] Extend `Renderer.toRawAnsi` with branches for `Stack`, `Padded`, `Panel`, `Columns`, `Aligned`, `Table`. Each branch builds the corresponding Spectre `IRenderable` and feeds it through the existing ephemeral `AnsiConsole` path. Depends on T040–T043 and T025.
- [ ] T045 [US2] Update `src/Fugue.Adapters.Console/Console.fsi` to expose all `Composition` cases and smart constructors per contracts/Fugue.Adapters.Console.fsi.

**Checkpoint**: A contributor can write `Composition.panel Border.Rounded (Some header) (Composition.stack [Leaf …; Leaf …])` and it renders correctly. US2 Acceptance Scenarios 2.1 and 2.2 satisfied.

---

## Phase 5: User Story 3 — Catch regressions when the C# dependency upgrades (P3)

**Goal**: A maintainer bumping `Spectre.Console 0.49.x → 0.49.y` can run
the test suite and either get a clean pass (cleared to merge) or a
specific diff naming the affected primitive (cleared to investigate).
SC-007: regression catch < 1 developer-minute.

**Independent Test**: Deliberately seed a regression (e.g., change
`RenderContext.create`'s default width to a wrong value), run
`dotnet test tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj`, observe failing tests within 30 s naming the affected primitives (per SC-003 + SC-007).

### Tests for User Story 3 (MANDATORY) ⚠️

- [ ] T046 [P] [US3] Property test: `Renderer.toRawAnsi` is total (no exceptions) over arbitrary `Style` × `SafeText` input. Use FsCheck.Xunit with shrinking. File: `tests/Fugue.Adapters.Console.Tests/RendererTotalityTests.fs`.
- [ ] T047 [P] [US3] Property test: `RenderContext.create width c t` with negative `width` produces the same output as `width = 1` (idempotent clamp). File: same.
- [ ] T048 [P] [US3] Verify snapshot test for the *combined* primitive corpus — a single multi-primitive composition rendered once, snapshotted as `tests/Fugue.Adapters.Console.Tests/VisualParity/combined_corpus.verified.txt`. This is the canary that catches subtle Spectre-version drift in cross-primitive interactions. File: `tests/Fugue.Adapters.Console.Tests/CorpusSnapshotTests.fs`.
- [ ] T049 [P] [US3] Smoke test that the integration suite finishes in < 30 s (SC-003). File: `tests/Fugue.Adapters.Console.Tests/SuiteDurationSmoke.fs` (single test reading `Stopwatch.GetTimestamp` deltas across `xunit` collection events — or simpler, a `[Fact]` that runs a representative corpus and asserts elapsed < 30 s).

### Implementation for User Story 3

- [ ] T050 [P] [US3] Add a `regenerate-baselines` recipe to `Justfile`. Concrete body (Verify ≥ v22 honours the `DiffEngine_Disabled` env-var and the `--autoaccept` xunit arg; adjust if Verify upgrades change the mechanism — see the upgrade-workflow doc T051): `find tests/Fugue.Adapters.Console.Tests/VisualParity -name '*.verified.txt' -delete && DiffEngine_Disabled=true dotnet test tests/Fugue.Adapters.Console.Tests/Fugue.Adapters.Console.Tests.fsproj -- VerifyTests.autoAcceptReceived=true`. Recipe is marked `# DESTRUCTIVE — regenerates baselines, only run after intentional Spectre upgrade` in the Justfile header comment. NOT auto-invoked. NOT part of `just ci` or `just test`.
- [ ] T051 [P] [US3] Write `specs/001-console-adapter-lib/upgrade-workflow.md` — a one-page guide for maintainers bumping the Spectre version. Steps: bump in `Fugue.Adapters.Console.fsproj`, run tests, read failing snapshots, decide intentional-vs-regression, regenerate baselines (T050) if intentional, commit baselines with the bump. Cross-link from `quickstart.md` §11 and from CLAUDE.md SPECKIT marker.
- [ ] T052 [US3] **Verification of the regression-catch workflow**: deliberately seed a regression (e.g., change theme-slot `"error"` colour from red to blue in T029's resolver), run `dotnet test`, confirm at least one Verify snapshot fails AND names the affected primitive within 30 s. Revert the seeded regression. Document the dry-run result in `upgrade-workflow.md` Appendix.

**Checkpoint**: SC-007 verified. The adapter is now self-defending against silent Spectre-upgrade regressions.

---

## Success Criteria explicitly out of scope for this feature

- **SC-006** ("After the first wave of ports lands, zero F# source files
  in the REPL surface contain an `open` on the underlying C# library
  except inside the adapter project itself") is **post-feature**. Per
  spec.md Assumptions, port PRs land *after* this feature ships v1; the
  zero-`open` invariant is therefore an exit criterion of the **port
  wave**, not of the adapter library. Each port PR re-applies it via
  the plan-template Constitution Check (Principle II — "every C# NuGet
  boundary wrapped in adapter; no `open` on the wrapped library outside
  it"). Tracked in `quickstart.md` Appendix A — Port log column "Bridges
  remaining" surfaces drift. No task in this feature attempts to verify
  SC-006 because the population it measures (post-port codebase) does
  not exist yet.

(Acknowledged 2026-05-16 per `/speckit-analyze` M4. SC-001 — 30-min
onboarding — is an outcome metric measured by user feedback after the
quickstart ships, not a buildable task; T053 supports it indirectly.
That is the analyzer's "outcome-only acceptable" classification.)

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Tighten the surface, satisfy SC-005 deliverable (mapping
doc), confirm Constitution Check still holds post-implementation, leave
the repo merge-ready.

- [ ] T053 [P] Finalise `quickstart.md` — all `[shape-pending]` markers resolved with the R2-chosen syntax (T006 outcome). Add 1-3 concrete worked examples per section. Verify 100% coverage of the call patterns observed in the inventory addendum of research.md (per SC-005).
- [ ] T054 [P] Update CLAUDE.md SPECKIT marker block — replace the planning-stage reference with a status note: "Phase: tasks complete, implementation in progress on branch `001-console-adapter-lib`." Once the feature merges, drop the marker block content back to a one-liner pointing at the constitution.
- [ ] T055 [P] Run the post-implementation Constitution Check re-evaluation: walk all 11 principles in `.specify/memory/constitution.md` v1.1.0 against the delivered code. Document any new violations (expected: none) in `plan.md` "Complexity Tracking". Particular scrutiny: Principle II (every public primitive has its integration test), Principle V (no AOT regressions — confirm `dotnet publish src/Fugue.Cli.Aot` still 0 warnings), Principle VIII (every `Renderer.*` returns a `Result` — no orphaned async calls).
- [ ] T056 [P] Confirm Quality Gates pass: `dotnet build -c Release` zero warnings; `dotnet test -c Release` full suite green; `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64` zero new trim warnings (the new adapter is JIT-only and not in the headless closure — Principle V — but the publish must still succeed).
- [ ] T057 [P] Inventory-driven coverage check: count the existing call-sites (research.md inventory: ~522 sites across 17 types) that COULD be ported with the v1 adapter surface. Document the figure in `quickstart.md` Appendix A header. Target: ≥ 80% (SC-002). If < 80%, list the gap primitives in an "out-of-scope edge cases" section with reasons (not implemented this round).
- [ ] T058 Open follow-up issue `TODO(TOOL_AUDIT_VIII)` referenced in constitution v1.1.0 Sync Impact Report — track separately, do not block this feature's merge.

---

## Dependencies

```
Phase 1 Setup (T001–T005)
   ↓
Phase 2 Foundational (T006 R2 debate BLOCKS T023+; T007–T013a parallel)
   ↓
Phase 3 US1 P1 (T014–T031, incl. T022a) ───┐
                                           │ Phase 4 US2 P2 (T032–T045)
                                           ↓     ↑
                                      Phase 5 US3 P3 (T046–T052)
                                           ↓
Phase 6 Polish (T053–T058)
```

**Inter-phase notes**:
- T006 (R2 dual-debate) is the single hard blocker on the whole project. Until the user picks an API shape, no implementation task in Phases 3–5 can start.
- T007–T013a are foundation types/tests; T014+ (US1 implementation) depends on them.
- US1, US2, US3 *implementation* phases are nominally independent — but US2 builds on the renderer scaffolding from US1, so the actual order is US1 → US2 sequential; US3 (mostly tests + maintenance infra) can run in parallel with US2 once US1 is done.
- Phase 6 (Polish) runs only after all three user stories merge.

## Parallel Execution Examples

**Within Phase 2 (Foundational)** — dispatch concurrently after T006 lands:
```
T007 (RenderError.fs)
T008 (Style.fs)            — parallel
T009 (SafeText.fs)         — parallel
T010 (Context.fs)          — parallel
T011 (SafeTextPropertyTests.fs)  — parallel
T012 (StyleTests.fs)       — parallel
T013 (ContextTests.fs)     — parallel
T013a (StyleTests.fs — ofMarkupHint cases)  — parallel
```

**Within Phase 3 (US1 tests, T014–T022 + T022a)** — primitive tests in one `PrimitiveTests.fs` / sibling files; T022a's actor-delivery test in `RendererActorTests.fs` (distinct file → parallel). If grouped by file, run as a single test job. Each implementation task T023, T024 is in a distinct file, so they parallelise.

**Cross-phase (after US1 lands)** — Phase 4 implementation (T040–T045) and Phase 5 tests (T046–T049) can run as two parallel work-streams.

## Implementation Strategy

**MVP scope**: User Story 1 (Phases 1 + 2 + 3) — ship as the first PR.
That alone unlocks the port wave; US2 and US3 follow as separate PRs.

**Incremental delivery**:
1. **PR #1 — Foundation + US1 (MVP)**: T001–T031 (includes T013a in
   Phase 2 foundation tests and T022a in Phase 3 US1 tests; 33 tasks
   total). Adapter handles primitives only; `Composition` is just
   `Leaf`. Ship-criterion: can replace ONE existing Spectre primitive
   call-site with byte-identical output. MERGEABLE as a standalone
   deliverable.
2. **PR #2 — US2**: T032–T045. Adapter handles full composition tree.
   Ship-criterion: a contributor writes a brand-new rendering function
   using zero `open Spectre.Console`.
3. **PR #3 — US3**: T046–T052. Regression-catch infrastructure +
   upgrade workflow doc. Ship-criterion: seeded-regression dry-run fails
   loudly within 30 s.
4. **PR #4 — Polish**: T053–T058. quickstart finalisation + Constitution
   re-check + coverage figures. Ship-criterion: SC-005 (100% inventory
   coverage in mapping doc), SC-002 (≥ 80% portable). May be merged
   into PR #1 if T053 is doable up-front (often it is).

**Do NOT** attempt to ship US1 + US2 + US3 as one PR. The constitution's
Principle VII (Decision Hygiene & Merge Strategy) is fine with merge
commits, but each user story is independently testable per spec.md — the
incremental delivery is the right granularity for review and bisection.

---

## Constitution Check (v1.1.0) — Per-Phase Verification

These echo plan.md's Constitution Check, expressed as per-task acceptance:

| Principle | Where checked | Acceptance |
|---|---|---|
| I  Test Coverage | every US task — T011, T012, T013, T013a, T014–T022, T022a, T032–T039, T046–T049 | each public function has happy + Error/None test |
| II C#/F# Sanitisation | T014–T022, T032–T037 are integration tests against real Spectre (no mocks) | confirmed in test bodies |
| III Subagent-Driven | T006 dispatches three-way debate over the R2 candidates; research.md was authored after a background `Explore` subagent inventory | logged in research.md |
| IV F# Purity | T001, T002 — only `.fs` / `.fsi` / `.fsproj` files | grep `*.cs` in PR diff returns nothing |
| V AOT vs JIT | T056 confirms `Fugue.Cli.Aot` publish still 0 warnings | adapter project not referenced from the headless closure |
| VI Prompt-First Slash | N/A — no slash commands added | — |
| VII Decision Hygiene | PR strategy above uses merge commits, not squash; CEO escalation for any release/push | reviewer enforces |
| VIII Tool Contract | N/A for v1 — adapter is a library, not a tool. Will apply when adapter is wired into Fugue's tool layer in future ports. | deferred to port PRs |
| IX Runtime ≠ Prompt | N/A — adapter introduces no safety policy | — |
| X Untrusted Content | N/A — adapter doesn't read external content | — |
| XI Context Engineering | N/A — adapter doesn't touch prompt/context assembly | — |

**Note on VIII–XI**: They're new in v1.1.0 and runtime-shaped — they apply
when the *port wave* moves Fugue's real rendering through the adapter,
not when the adapter library itself is built. Each future port PR
re-applies them via plan-template's Constitution Check.
