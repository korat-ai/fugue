# Implementation Plan: Phase 3 — Fugue Layout Framework

**Branch**: `003-layout-framework` | **Date**: 2026-05-18 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `specs/003-layout-framework/spec.md`

## Summary

Phase 3 extends `Fugue.Adapters.Console` with a **high-level Layout framework** that
sits on top of the Phase 2 `Composition` IR. Four opaque sealed layout primitives —
`Stack`, `Dock`, `Grid` (layout-grid, namespace-disambiguated from Phase 2 widget Grid),
and `Flex` — are constructed via `Result`-returning smart constructors and lower into
`Composition` trees via per-primitive factories. After the framework lands, the REPL
loop (`src/Fugue.Cli/Repl.fs`) is ported off direct `AnsiConsole` calls onto the
layout primitives, with streaming markdown supported via an incremental-update API.

The three open architectural decisions (reactive-vs-static update model, Surface.DrawOp
integration, layout-Grid namespace) are resolved in Phase 0 research.md before any code
lands. Same Phase 2 principles continue: typed primitives, `Result` smart constructors,
escape-tracked `SafeText` for user content, per-module `.fsi` sealing, JIT-only (not
referenced by `Fugue.Cli.Aot`), no Spectre.* types in adapter `.fsi` signatures
(continues SC-004 grep gate).

## Technical Context

**Language/Version**: F# 9 on .NET 10 SDK (matches Phase 2 baseline).

**Primary Dependencies**: Existing — `Spectre.Console 0.49.*`, `Spectre.Console.Json 0.49.*`,
`Spectre.Console.Testing 0.49.*` (already in `Fugue.Adapters.Console.fsproj`). No new
NuGet packages anticipated for Phase 3 MVP. Optional: a Unicode-display-width helper
package (e.g. `Wcwidth.Net` or similar) may be added to satisfy FR-014 if the
existing Phase 2 `SafeText` width computation proves insufficient — defer to research.md
R-4 if surfaced during P1 Stack implementation.

**Storage**: N/A (in-memory layout tree only; no persistence layer).

**Testing**: xUnit + FsCheck.Xunit + Spectre.Console.Testing (matches Phase 2 baseline).
FACT_DISCOVERY workaround (`[<Property(MaxTest=1)>]` returning `bool` as Fact-equivalent)
continues per Phase 2's documented xunit 2.9.3 pin.

**Target Platform**: macOS-arm64 + Linux-x64 + Windows (3-OS CI matrix per Constitution).
JIT-only — Phase 3 code is NOT referenced from `Fugue.Cli.Aot`.

**Project Type**: F# library extension to existing `Fugue.Adapters.Console.fsproj`
(JIT-only adapter). Layout primitives live either in the existing fsproj (likely under
a sub-namespace `Fugue.Adapters.Console.Layout`) or, if research R-3 chooses, in a new
sibling fsproj `Fugue.Adapters.Console.Layout.fsproj`. Either way: JIT, never AOT.

**Performance Goals**:
- SC-005: 100 update events/sec sustained throughput without dropping final state.
- SC-006: < 5ms layout latency for typical Fugue UI tree (depth ≤ 10, nodes ≤ 100).
- SC-007: < 16ms (one frame) from SIGWINCH to next-render.

**Constraints**:
- SC-003: Test suite stays under 5s per OS leg as it grows from 232 → ≥ 350 tests.
- SC-004: Zero `Spectre.*` references in new `.fsi` files (grep gate continues).
- SC-008: AOT publish stays clean (Phase 3 is JIT-only by construction; verify nothing
  in `Fugue.Cli.Aot` reference chain pulls in Phase 3 modules).
- Constitution Principle I — every public Layout type + smart constructor has happy + error tests
  (NON-NEGOTIABLE; lesson from Phase 2's 6 BLOCKERS).

**Scale/Scope**:
- 4 layout primitives + ~6 supporting types (DockEdge, ColumnSize, RowSize, FlexItem,
  Orientation, Alignment if not already in Phase 2).
- ~8-12 new module pairs (`.fsi` + `.fs`).
- Single REPL port target: `src/Fugue.Cli/Repl.fs` (~2800 LOC currently; subset rewrites
  the render loop, not the whole file).
- Initial estimate per A12: 5-6 PRs (one per user story P1-P5 + Polish).

**Open architectural questions** (resolved in Phase 0 research.md, NOT NEEDS CLARIFICATION
at spec level):
- **R-1**: Reactive-diff vs static-rebuild update model (per spec Assumption A3).
- **R-2**: Surface.DrawOp integration — above / replaces / lowers-to (per spec Assumption A4).
- **R-3**: Layout-Grid namespace — sub-namespace inside existing fsproj vs new sibling fsproj
  (per spec Assumption A1).
- **R-4 (possible)**: Unicode display-width helper — existing SafeText sufficient vs add
  NuGet helper (per FR-014). Determined during P1 Stack scaffolding.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verified against `.specify/memory/constitution.md` (v1.1.0). Each gate maps to a numbered Principle.

- [X] **Principle I — Test Coverage Discipline**: Every Phase 3 public type, smart constructor, and DU case will have a happy-path + error-path test (FR-013 enforces via coverage-assertion test). Phase 2 review pattern (engineering-code-reviewer flags untested public APIs as BLOCKER) continues per Assumption A9.
- [X] **Principle II — C#/F# Interop Sanitization**: Phase 3 introduces NO new C# NuGet boundary (no new packages anticipated; existing Spectre packages already wrapped by Phase 1/2). Integration tests against Phase 1/2's real Spectre boundary continue. If R-4 adds `Wcwidth.Net` or similar, that boundary gets its own adapter + integration test.
- [X] **Principle III — Subagent-Driven Development**: Per A8 — Phase 3 PRs delegate implementation to `engineering-fsharp-developer` in isolated worktrees; pre-push review by `engineering-code-reviewer`. R-1/R-2 architectural decisions resolved via dual-subagent debate pattern in research.md (CLAUDE.md "CEO escalation" rule).
- [X] **Principle IV — F# Purity at Source**: Phase 3 adds only `.fs` and `.fsi` files under `src/`. Zero `.cs` files. New NuGet dependencies (if R-4 surfaces) ship as packages.
- [X] **Principle V — AOT-Closure vs JIT Discipline**: Phase 3 code lives in `Fugue.Adapters.Console` (existing JIT-only fsproj) or a new sibling JIT fsproj. NEVER referenced by `Fugue.Cli.Aot`. Verified by SC-008 (AOT publish remains clean).
- [X] **Principle VI — Prompt-First Slash Commands**: N/A — Phase 3 adds no slash commands. Layout primitives are library code, not user-invokable commands.
- [X] **Principle VII — Decision Hygiene & Merge Strategy**: Phase 3 PR cascade explicitly uses `gh pr merge <N> --merge --delete-branch` (NEVER `--squash`) per tasks.md Implementation Strategy. CEO escalation required before each push (per A9 mandatory pre-push review + per-PR user confirmation pattern from Phase 2). Semver: Phase 3 user-stories are new features → minor bump for any release-tagging. No `--no-verify` / `--no-gpg-sign`.
- [X] **Principle VIII — Tool Contract Discipline**: N/A — Phase 3 adds no tools to the agent surface. Layout primitives are rendering library code, not LLM-callable tools.
- [X] **Principle IX — Runtime Policy ≠ Prompt Policy**: N/A — Phase 3 introduces no safety properties (no destructive-shell guard, no approval mode, no secret redaction). It's a pure rendering library.
- [X] **Principle X — Untrusted Content Boundary**: SafeText (Phase 1) continues to escape any user-supplied textual content embedded in layout children (FR-004). Streaming markdown content arrives via existing `MarkdownRender` path, which already runs through SafeText boundary.
- [X] **Principle XI — Context as Engineered Artifact**: Phase 3 does not modify the system prompt, tool-schema preamble, or compaction code. Adds an entry to `CLAUDE.md` `<!-- SPECKIT -->` marker pointing at this plan (per Phase 1 step 3).
- [X] **Quality Gates**: `dotnet build`, `dotnet test`, and `dotnet publish src/Fugue.Cli.Aot` MUST stay green after every Phase 3 PR. Same gates that caught Phase 2 issues continue.

**Result**: **11/11 PASS** (Principles I, II, III, IV, V, VI, VII, VIII, IX, X, XI all verified). No Constitution violations; no Complexity Tracking entries needed.

## Project Structure

### Documentation (this feature)

```text
specs/003-layout-framework/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output — resolves R-1, R-2, R-3 + best-practices
├── data-model.md        # Phase 1 output — per-primitive entity design
├── quickstart.md        # Phase 1 output — REPL port walkthrough
├── contracts/           # Phase 1 output — per-primitive .fsi sketches
│   ├── Stack.fsi
│   ├── Dock.fsi
│   ├── LayoutGrid.fsi   # (or Grid.fsi under Layout namespace per R-3)
│   ├── Flex.fsi
│   └── LayoutComposition.fsi   # Composition.of<Layout> factories
├── checklists/
│   └── requirements.md  # Phase 0 spec quality checklist (already created)
└── tasks.md             # Phase 2 output (/speckit-tasks command — not created here)
```

### Source Code (repository root)

```text
# Existing Phase 1 + Phase 2 adapter (will gain Phase 3 modules per R-3):
src/Fugue.Adapters.Console/
├── Errors.fs / Errors.fsi              # Phase 1
├── Style.fs / Style.fsi                # Phase 1
├── SafeText.fs / SafeText.fsi          # Phase 1
├── Context.fs / Context.fsi            # Phase 1 (RenderContext)
├── Primitive.fs / Primitive.fsi        # Phase 1
├── Renderable.fs / Renderable.fsi      # Phase 1/P1 (IRenderable bridge)
├── Composition.fs / Composition.fsi    # Phase 1
├── Renderer.fs / Renderer.fsi          # Phase 1
├── DataDisplays.fs / DataDisplays.fsi  # Phase 2/P2 (incl. widget Grid)
├── Live.fs / Live.fsi                  # Phase 2/P3+P5
├── Prompts.fs / Prompts.fsi            # Phase 2/P4
├── Console.fs / Console.fsi            # Phase 2/P3+P5
└── Layout/                             # NEW Phase 3 sub-folder (R-3 final choice)
    ├── Layout.fs / Layout.fsi          # Common: Orientation, Alignment, Overflow
    ├── Stack.fs / Stack.fsi            # P1 Stack
    ├── Dock.fs / Dock.fsi              # P2 Dock
    ├── LayoutGrid.fs / LayoutGrid.fsi  # P3 layout-Grid
    ├── Flex.fs / Flex.fsi              # P4 Flex
    └── LayoutSession.fs / LayoutSession.fsi  # P5 reactive handle (if R-1 chooses reactive)

# Existing Phase 2 test project:
tests/Fugue.Adapters.Console.Tests/
├── ... (existing 232 tests)
├── Layout/                             # NEW Phase 3 test sub-folder
│   ├── StackTests.fs                   # P1
│   ├── DockTests.fs                    # P2
│   ├── LayoutGridTests.fs              # P3
│   ├── FlexTests.fs                    # P4
│   ├── LayoutSessionTests.fs           # P5 (if reactive)
│   └── ReplPortIntegrationTests.fs     # P5 integration tests
└── CoverageMatrixTests.fs              # Phase 2; extended with Phase 3 type-coverage assertions

# REPL port target (P5):
src/Fugue.Cli/
├── Repl.fs            # MODIFIED — render loop ported to Layout (FR-011)
└── ... (other existing files unchanged)

# Surface integration (per R-2 final choice):
src/Fugue.Surface/
├── Domain.fs          # DrawOp DU — UNCHANGED if R-2 = "Layout above Surface"
│                      # MODIFIED if R-2 = "Layout lowers to DrawOp"
│                      # DEPRECATED if R-2 = "Layout replaces Surface"
└── ... (Mailbox, etc., subject to R-2)
```

**Structure Decision**:
- Phase 3 modules go under a NEW `Layout/` sub-folder inside the existing
  `Fugue.Adapters.Console.fsproj` (per R-3 preliminary; final confirmation in
  research.md). Reuses the existing fsproj's NuGet references + InternalsVisibleTo
  setup; no new fsproj overhead unless research surfaces a hard reason.
- Test files mirror under `tests/Fugue.Adapters.Console.Tests/Layout/`.
- REPL port (`src/Fugue.Cli/Repl.fs`) is the only file outside the adapter that
  changes. `src/Fugue.Surface/` evolution depends on R-2.
- Phase 1+2 modules remain UNCHANGED — Phase 3 is purely additive at the adapter
  level. The Phase 2 `Grid` widget keeps its name; Phase 3 layout-Grid is a
  distinct type in the `Layout` sub-namespace (per Assumption A1).

## Complexity Tracking

> Fill ONLY if Constitution Check has violations that must be justified

Phase 3 has **zero** Constitution violations. All 11 principles pass on the first check.
No complexity-tracking entries required.

## Post-Implementation Constitution Re-Check

*To be filled in after Phase 1 design (data-model.md + contracts/ + quickstart.md complete).
Re-run the 11-principle check against the concrete design artifacts and document any
discoveries that warrant revisiting the Phase 0 decisions.*

**Pending Phase 1 completion**.

---

## Phase 0 / Phase 1 Status

- [X] Phase 0 (Outline & Research) — `research.md` generated; R-1 (static-rebuild + `LayoutScheduler`), R-2 (Layout above Surface, option A), R-3 (sub-namespace `Fugue.Adapters.Console.Layout.*`), R-4 (no new Unicode dep) all resolved.
- [X] Phase 1 (Design & Contracts) — `data-model.md` (10 sections), `contracts/Stack.fsi` + `Dock.fsi` + `LayoutGrid.fsi` + `Flex.fsi` + `LayoutComposition.fsi`, `quickstart.md` (6 sections including REPL port preview) — all generated. `CLAUDE.md` `<!-- SPECKIT -->` marker updated to point at this plan.
- [ ] Post-Implementation Constitution Re-Check — to be filled in `tasks.md` and again at Phase 3 close after all PRs merge.
