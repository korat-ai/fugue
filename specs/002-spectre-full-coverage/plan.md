# Implementation Plan: Full Spectre.Console F# Coverage

**Branch**: `002-spectre-full-coverage` | **Date**: 2026-05-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/002-spectre-full-coverage/spec.md`

## Summary

Extend `Fugue.Adapters.Console` from its Phase 1 MVP surface (Markup, Style, Decoration, Color, SafeText, Padder, Panel, Columns, Align, Table, Rule) to **complete coverage** of the public `Spectre.Console 0.49.*` API. Five user-story classes: custom-renderable bridge (P1), data-display primitives (P2), live/streaming displays (P3), interactive prompts (P4), console infrastructure & testing (P5). All additions follow Phase 1 patterns: typed F# primitives, `Result<_, RenderError>`-returning smart constructors, escape-tracked `SafeText`, no exposed `Spectre.Console.*` types in public signatures, per-module `.fsi` sealing. PR strategy: one PR per user story (P1 first — it sets the pattern). After Phase 2 lands, the codebase is ready for Phase 3 (Fugue-specific Layout framework built atop these primitives).

## Technical Context

**Language/Version**: F# 9 on .NET 10 SDK (matches the rest of the Fugue solution).

**Primary Dependencies**:
- `Spectre.Console 0.49.*` — the target of the wrapping (existing dependency).
- `Spectre.Console.Json 0.49.*` — NEW in Phase 2 — separate NuGet package containing the `JsonText` widget; resolved per data-model.md §0.2 punt resolution.
- `Spectre.Console.Testing 0.49.*` — needed for `TestConsole` wrapper in P5 (already in test fsproj).
- `FsCheck.Xunit 3.*` — existing, property tests on smart constructors.
- `xunit 2.9.3` + `xunit.runner.visualstudio 2.8.2` (pinned per the FACT_DISCOVERY workaround in `tests/Fugue.Adapters.Console.Tests/Helpers.fs`; adapter test project remains on the older runner because 3.x loses FsCheck `[<Property>]` discovery in this assembly — see Neftedollar/FsLangMCP#100 and PR #944).

**Storage**: N/A — library, no persisted state.

**Testing**: xUnit + FsUnit + FsCheck.Xunit + Spectre.Console.Testing. Existing 83 adapter tests stay green; ≥250 total by end of Phase 2.

**Target Platform**: macOS arm64, Linux x64, Windows x64 — same as parent `Fugue.Cli` (JIT) closure. NOT included in the headless `Fugue.Cli.Aot` closure.

**Project Type**: F# library inside an existing solution (`Fugue.Adapters.Console.fsproj`). No new project — Phase 2 extends the existing one.

**Performance Goals**: Adapter test suite total runtime ≤ 5 seconds (currently 130 ms with 83 tests; ≥250 tests must still fit in 5s). Individual primitive `Renderer.toRawAnsi` call: ≤ 5 ms for typical-sized composition (matches Phase 1 perf budget tightened by review #15).

**Constraints**:
- Zero `Spectre.*` types in any adapter `.fsi`.
- 100% backward compatibility with Phase 1 API.
- Adapter stays JIT-only (no AOT-friendliness work).
- `engineering-fsharp-developer` / `engineering-fsharp-autotester` subagents do most implementation work (Principle III).
- One PR per user story (P1 first; others follow).

**Scale/Scope**:
- ~30 new public Spectre types to wrap (Tree, Json, BarChart, BreakdownChart, Calendar, FigletText, Grid, TextPath, Live, LiveDisplay, Status, Progress, ProgressTask, Spinners, TextPrompt, SelectionPrompt, MultiSelectionPrompt, ConfirmationPrompt, AnsiConsole, IAnsiConsole, AnsiConsoleSettings, TestConsole, IRenderable bridge, exception rendering helpers; plus an enumerated coverage matrix of every public type in `Spectre.Console.dll`).
- ~170 new tests (83 → ≥250). Mix: smart-constructor property tests, totality tests per new DU case, snapshot tests for byte-stable output, perf-smoke tests for new Live primitives.
- Estimated effort: 5 PRs × 1-2 days each ≈ 5-10 working days, mostly subagent-driven.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verified against `.specify/memory/constitution.md` v1.1.0:

- [X] **Principle I — Test Coverage Discipline**: Every new wrapper type, smart constructor, and primitive in Phase 2 lands with at least one happy-path property test and one `Error`-path test. Covered by FR-019 (coverage-matrix-driven test enumeration) and SC-007 (per-type test mandate). No `Skip` tests.
- [X] **Principle II — C#/F# Interop Sanitization**: Phase 2 IS a Principle-II sanitization layer for Spectre.Console — the canonical example. FR-001..FR-004 codify "no exposed C# types, no raw exceptions across boundary". Integration tests (FR-019 + Phase 1 pattern) hit the real Spectre library, no mocks.
- [X] **Principle III — Subagent-Driven Development**: All implementation work delegated to `engineering-fsharp-developer` (one subagent per user-story PR), `engineering-fsharp-autotester` consulted for test-suite design. Cross-cutting design decisions (Live UI lifetime API, prompt cancellation model) go through dual-subagent debate before commitment.
- [X] **Principle IV — F# Purity at Source**: No `.cs` files. New code is F# under `src/Fugue.Adapters.Console/`. Spectre.Console stays as a NuGet dependency, never as source.
- [X] **Principle V — AOT-Closure vs JIT Discipline**: Adapter is JIT-only by construction (`Fugue.Adapters.Console.fsproj` is not in `Fugue.Cli.Aot` reference closure). Phase 2 additions inherit that. Interactive prompts, Live UI, dynamic Spectre features stay in JIT — they would never be acceptable in the AOT closure. Verified by SC-006.
- [X] **Principle VI — Prompt-First Slash Commands**: Not applicable — Phase 2 adds adapter primitives, not slash commands. Downstream Phase 3 (Layout framework) and any new slash commands built atop it WILL be subject to VI.
- [X] **Principle VII — Decision Hygiene & Merge Strategy**: PRs merged with `--merge` (no squash). Per-user-story splitting means clean linear history. Semver bumps on the adapter per FR-006 (new `RenderError` cases = MINOR; breaking removals = MAJOR — neither expected since FR-004 forbids breaking changes).
- [X] **Principle VIII — Tool Contract Discipline**: Not directly applicable — Phase 2 does not add or modify LLM-tool dispatch. Adapter is a render-layer library, not part of the tool pipeline. The downstream porting of REPL render-sites onto the adapter does not change tool semantics either.
- [X] **Principle IX — Runtime Policy ≠ Prompt Policy**: Not directly applicable — Phase 2 adds rendering and prompt primitives, no safety policy. Interactive prompts (US4) DO surface as code (not prompt-side instruction), which IS the principle's pattern.
- [X] **Principle X — Untrusted Content Boundary**: Partially applicable — `Json` primitive (P2) and `Tree` primitive (P2) will render externally-sourced data. FR-007 (SafeText for user-supplied strings) is the principle's hook for this layer; any content rendered as `Json` payload or `Tree` node label MUST go through `SafeText` at the smart-constructor boundary. Documented in data-model.md when the entity table is written.
- [X] **Principle XI — Context as Engineered Artifact**: Not applicable — adapter has no system-prompt surface, no context-window concerns. (Phase 3's Layout framework will likely touch this, not Phase 2.)
- [X] **Quality Gates**: Plan does not regress build/test/AOT-publish gates. SC-005 (Phase 1 backward-compat) and SC-006 (AOT clean) are the explicit guards.

**Gate verdict**: PASS — no violations, no Complexity Tracking entries required.

## Project Structure

### Documentation (this feature)

```text
specs/002-spectre-full-coverage/
├── plan.md                    # This file (/speckit-plan output)
├── spec.md                    # Feature spec (already written)
├── research.md                # Phase 0 — design decisions (R1..R5)
├── data-model.md              # Phase 1 — entity table for all new types
├── coverage-matrix.md         # FR-018 — every public Spectre type with status
├── quickstart.md              # Phase 1 — per-user-story port mapping
├── contracts/                 # Phase 1 — .fsi sketches per US
│   ├── Renderable.fsi         # P1 — IRenderable opaque bridge
│   ├── DataDisplays.fsi       # P2 — Tree, Json, BarChart, etc.
│   ├── Live.fsi               # P3 — Live, Status, Progress, Spinners
│   ├── Prompts.fsi            # P4 — TextPrompt, SelectionPrompt, etc.
│   └── Console.fsi            # P5 — AnsiConsole/TestConsole/exception
├── checklists/
│   └── requirements.md        # Spec quality checklist (already passed)
└── tasks.md                   # Phase 2 — generated by /speckit-tasks (NOT this command)
```

### Source Code (repository root)

The adapter library extends the existing single-project layout from Phase 1 — no new fsproj, no relocation. Each user-story PR adds 1-3 paired `.fsi`/`.fs` files plus tests.

```text
src/Fugue.Adapters.Console/
├── Errors.fsi          (existing)
├── Errors.fs           (existing — Phase 2 adds 3 cases: UserCancelled, NoInteractiveConsole, ConcurrentLiveSession)
├── Style.fsi           (existing)
├── Style.fs            (existing)
├── SafeText.fsi        (existing)
├── SafeText.fs         (existing)
├── Context.fsi         (existing)
├── Context.fs          (existing)
├── Primitive.fsi       (existing — unchanged in Phase 2; renderable bridge lives in new Composition.Foreign internal DU case)
├── Primitive.fs        (existing — unchanged in Phase 2; renderable bridge lives in new Composition.Foreign internal DU case)
├── Composition.fsi     (existing — unchanged in Phase 2)
├── Composition.fs      (existing — unchanged in Phase 2)
├── Renderer.fsi        (existing — Phase 2 may add new public functions per Live/Console US)
├── Renderer.fs         (existing — extended to handle new Primitive cases)
│
├── Renderable.fsi      (NEW — P1: opaque IRenderable bridge module)
├── Renderable.fs       (NEW — P1)
│
├── DataDisplays.fsi    (NEW — P2: Tree, Json, BarChart, BreakdownChart, Calendar, FigletText, Grid, TextPath)
├── DataDisplays.fs     (NEW — P2)
│
├── Live.fsi            (NEW — P3: Live, Status, Progress, Spinners)
├── Live.fs             (NEW — P3)
│
├── Prompts.fsi         (NEW — P4: TextPrompt, SelectionPrompt, MultiSelectionPrompt, ConfirmationPrompt)
├── Prompts.fs          (NEW — P4)
│
├── Console.fsi         (NEW — P5: AnsiConsole wrapper, TestConsole, exception rendering)
├── Console.fs          (NEW — P5)
│
└── Fugue.Adapters.Console.fsproj  (existing — Phase 2 adds new <Compile Include> entries per PR)

tests/Fugue.Adapters.Console.Tests/
├── Helpers.fs                       (existing — FACT_DISCOVERY workaround)
├── (existing Phase 1 test files)    (unchanged)
├── RenderableTests.fs               (NEW — P1)
├── DataDisplaysTests.fs             (NEW — P2; one test module per primitive recommended)
├── LiveTests.fs                     (NEW — P3)
├── PromptsTests.fs                  (NEW — P4)
├── ConsoleTests.fs                  (NEW — P5)
└── CoverageMatrixTests.fs           (NEW — FR-019 enforcement: every covered type has render evidence)
```

**Structure Decision**: Single-project F# library extension. Each user-story PR ships one `<Module>.fsi`+`<Module>.fs` pair plus one matching test file, ordered by US priority. New `Compile Include` entries go at the end of `Fugue.Adapters.Console.fsproj` (F# compile order matters; new modules depend on existing primitives, never vice versa). The existing `Renderer.fsi` may grow new public functions per user story (notably for Live and Console infrastructure); when it does, the change is signature-additive only (no removed or renamed members — FR-004).

## Complexity Tracking

> Constitution Check passed cleanly — no violations to justify. Section retained for any post-design re-check that surfaces an unavoidable deviation.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| (none)    |            |                                      |

## Post-Implementation Constitution Re-Check

To be performed after Phase 1 (design) and at the close of each user-story PR:

1. **Principle II re-check** — once `Console.fsi` (P5) lands, run `rg 'Spectre\.' src/Fugue.Adapters.Console/*.fsi` and confirm zero matches. The adapter is the only legitimate place `Spectre.Console` namespace lives.
2. **Principle X re-check** — once `DataDisplays.fsi` (P2) ships `Json` and `Tree`, confirm every public smart constructor that takes user content goes through `SafeText` (FR-007).
3. **SC-002 re-check** — only meaningful AFTER the downstream REPL port wave begins (Phase 3 prerequisite). At Phase 2 PR close, this SC is "structurally enabled" but not measurably true yet.
   **SC-002 note (T077 Polish)**: `rg 'Spectre\.Console' src/Fugue.Cli/ src/Fugue.Surface/` SHOULD return zero only after the downstream REPL port wave (Phase 3 per plan.md), NOT after Phase 2 closes. This SC is a **Phase 3 gate**, not a Phase 2 gate. No code change required at Phase 2 close — the adapter provides full coverage but the REPL call-sites have not yet been migrated. Running the `rg` check at Phase 2 close will show non-zero results (expected); the Phase 3 kickoff task list must include SC-002 verification as a required gate before Phase 3 PR merges.
4. **SC-001 (coverage matrix complete)** — must be true at the merge of the LAST Phase 2 PR (P5). Earlier PRs leave `pending` entries that close one-by-one as user-stories land.
