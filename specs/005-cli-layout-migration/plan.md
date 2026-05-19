# Implementation Plan: Migrate Fugue.Cli UI to the Layout Framework

**Branch**: `005-cli-layout-migration` | **Date**: 2026-05-19 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/005-cli-layout-migration/spec.md`

## Summary

Replace 28 direct `Spectre.Console` call sites across 8 files in `src/Fugue.Cli/` with calls into the in-repo F# adapter (`Fugue.Adapters.Console` primitives + Phase 3 Layout API). The Fugue REPL becomes its own first production consumer of the framework it built — closing the "framework built but not used" gap surfaced during the Phase 5 deferral analysis.

The migration ships as 4–5 cohesive pull requests grouped by call-site cohesion (per FR-013), each with a green CI run, independently reviewable, independently revertable. A new bridge module `Fugue.Cli.RenderBridge` (≈ 20 LOC + `.fsi`) becomes the only place where the framework's abstract output (`Composition` → `string` ANSI) is mapped to Fugue's internal `DrawOp.RawAnsi` representation. As a side effect, the framework package's `Renderer.toDrawOp` is relocated out of the package (satisfying the deferred Phase 5 condition that the package contain zero Fugue-internal references).

Visual parity is the hardest constraint. Approach: per-render-path snapshot tests at unit level (capture `toRawAnsi` output against frozen fixtures), supplemented by manual smoke against the canonical scripted session before each PR merge. Full automated end-to-end (PTY-harness) regression detection is deferred to a follow-up (#937 already in backlog).

A legacy-render env-var fallback installed in Phase 3 P5 (`FUGUE_LEGACY_RENDER`) is removed in the final PR after one full release cycle of soak time (FR-007 / SC-005).

At least one of three open rendering bugs (#910 scroll-region reset, #913 model description overlap, #934 multi-line corruption) is either auto-closed by the migration or becomes fixable in ≤ 30 LOC (FR-008 / SC-007).

## Technical Context

**Language/Version**: F# 9 (.NET 10) — JIT closure (`Fugue.Cli`), no AOT constraints

**Primary Dependencies**:

- Existing: `Fugue.Adapters.Console` (in-tree, Phase 1–3 product); `Fugue.Surface` (in-tree, DrawOp actor); `Markdig 0.41` (markdown parser, unchanged); `Spectre.Console 0.49.*` (transitively via the adapter only after migration)
- No new NuGet packages introduced

**Storage**: N/A (REPL is stateless across runs except for `~/.fugue/config.json` and `~/.fugue/history` which are unchanged by this work)

**Testing**:

- Existing: 264+ adapter tests (`tests/Fugue.Adapters.Console.Tests/`), 573+ core tests (`tests/Fugue.Tests/`) — both unchanged in baseline
- New: snapshot tests for each migrated render path (per FR-012); regression test for the closed rendering bug (FR-008); happy + error tests for the new `RenderBridge` module (FR-006)
- Toolkit: xUnit + FsUnit + FsCheck.Xunit; snapshot tests stored as `.txt` fixtures under `tests/Fugue.Tests/Snapshots/` and asserted against `Renderer.toRawAnsi` output

**Target Platform**: Cross-platform .NET 10. The migration is platform-agnostic; CI runs on `ubuntu-latest`, `macos-14`, `windows-latest` continue to apply.

**Project Type**: Interactive CLI / TUI refactoring within an existing F# monorepo

**Performance Goals**:

- Streaming token append latency: within pre-migration envelope (no regression — FR-004); concretely, per-tick render budget of the Phase-3 baseline (≤ 10 ms p50 on typical UI tree, established in SC-006 of `specs/003-layout-framework/`)
- Full-frame redraw rate: bounded by the Layout `Scheduler`'s frame interval (16 ms / ≈ 60 Hz default) with referential-equality short-circuit suppressing identical-tree re-renders
- Memory: no growth attributable to retained `Composition` trees or scheduler queue accumulation — verified by a long-session smoke test

**Constraints**:

- Visual parity (FR-003): no observable change in colours, spacing, cursor positioning, or scroll behaviour against the canonical scripted session
- DrawOp ABI stable (FR-009): no changes to the `Fugue.Surface.DrawOp` discriminated union — all migrated paths flow through the existing `RawAnsi` constructor
- JIT-only: all new and modified code lives in `Fugue.Cli` (JIT closure) — no AOT regressions on `src/Fugue.Cli.Aot` (FR-011 / SC-004)
- One coupling-removal precondition: `Renderer.toDrawOp` MUST leave the package (FR-005) before legacyRender fallback can be removed (orderly dependency)

**Scale/Scope**:

- 8 source files migrated, 28 direct Spectre call sites replaced
- 4–5 pull requests (per FR-013 / SC-009)
- New files: `src/Fugue.Cli/RenderBridge.fsi`, `src/Fugue.Cli/RenderBridge.fs`, `tests/Fugue.Tests/RenderBridgeTests.fs`, `tests/Fugue.Tests/Snapshots/*.txt` (≈ 6 fixtures), `tests/Fugue.Tests/RenderSnapshotTests.fs`
- Modified files: 8 in `src/Fugue.Cli/` (rendering call sites), `src/Fugue.Adapters.Console/Renderer.fs` + `.fsi` (relocated `toDrawOp`), `src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj` (drop `Fugue.Surface` ProjectReference), `src/Fugue.Cli/Repl.fs` (drop legacyRender branch in final PR), `src/Fugue.Cli/Fugue.Cli.fsproj` (add RenderBridge entries)
- Estimated LOC: ≈ +200 (new bridge + tests + snapshots), ≈ –50 (legacyRender removal), net ≈ +150 — but the replaced call sites trade Spectre verbosity for adapter terseness, so net per-file LOC in `src/Fugue.Cli/` may decrease slightly

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verified against `.specify/memory/constitution.md` (v1.1.0). Unlike the deferred Phase 5 (where most gates were trivially N/A — a red flag I flagged at the time), Phase 4 actively engages with several principles. Each gate below is reasoned, not stamped.

- [X] **Principle I — Test Coverage Discipline**: Three new test categories land with this feature: (a) `RenderBridgeTests.fs` — happy + error path for the new bridge module (FR-006); (b) `RenderSnapshotTests.fs` — per-render-path snapshot assertions for each migrated path (FR-012); (c) regression test for the closed rendering bug (FR-008). No `Skip = "..."` annotations introduced. No `result.IsOk`-only tests — snapshot tests inspect actual ANSI payloads byte-for-byte.

- [X] **Principle II — C#/F# Interop Sanitization**: This feature *strengthens* this principle, not weakens it. Today the REPL has 28 direct call sites into a C# library; after the feature, those go through the existing typed F# adapter (which itself satisfies II's adapter-with-integration-tests requirement, established in Phase 1+2). The migration is a deliberate enlargement of the safe sanitized surface. No new C# boundary introduced.

- [X] **Principle III — Subagent-Driven Development**: 4–5 cohesive PRs map naturally to 4–5 subagent dispatches via `engineering-fsharp-developer` (per PR) + `engineering-code-reviewer` (pre-push, per Principle IX trust-but-verify). The R-3 visual-regression-detection question (snapshot vs PTY vs combination) was resolvable with a clear default (snapshot + manual) without dual-debate — but if implementation surfaces evidence that snapshot is insufficient (e.g. ANSI output is non-deterministic across runs), dual-debate fires then on "snapshot vs PTY priority".

- [X] **Principle IV — F# Purity at Source**: No `.cs` files introduced. New module `Fugue.Cli.RenderBridge` is pure F#. Snapshot fixtures are `.txt`, not source.

- [X] **Principle V — AOT-Closure vs JIT Discipline**: All work in `Fugue.Cli` (JIT closure). No code lands in `Fugue.Core` / `Fugue.Tools` / `Fugue.Agent` / `Fugue.Cli.Aot`. The relocation of `Renderer.toDrawOp` from the package into `Fugue.Cli.RenderBridge` *removes* a `Fugue.Surface` reference from the adapter package — strictly closure-tightening, not closure-broadening. AOT publish for `Fugue.Cli.Aot` MUST continue to be clean (verified in SC-004); this is straightforward because the AOT closure is untouched.

- [X] **Principle VI — Prompt-First Slash Commands**: N/A. No slash commands added or removed. Existing slash commands' rendering paths are migrated mechanically; their *behaviour* is unchanged.

- [X] **Principle VIII — Tool Contract Discipline**: N/A directly — no new tools. *Tangentially*: the migration touches `DiffRender.fs` (renders the `Edit` tool's result) and `Surface.fs` (renders all tool results via DrawOp). The migration MUST preserve every tool's existing result-rendering path (no tool's result MAY be silently dropped because the rendering path changed). This is a regression-guard constraint already covered by snapshot tests for tool-result rendering paths.

- [X] **Principle IX — Runtime Policy ≠ Prompt Policy**: N/A. No safety policies introduced. The `FUGUE_LEGACY_RENDER` env-var fallback is *not* a safety policy — it is a rollback safety net for a refactor; removing it under FR-007 is a code-cleanup, not a policy change.

- [X] **Principle X — Untrusted Content Boundary**: SUBTLY APPLICABLE. The migration's `MarkdownRender.fs` and `StreamRender.fs` paths render assistant-produced markdown, which may transitively contain content that originated in tool results (file reads, shell stdout). The migration MUST preserve current treatment: rendered text is rendered *as data*, never interpreted as adapter-level commands or framework instructions. The framework's `SafeText` abstraction (already used by Phase 1–3) is the enforcement mechanism — every user-content string flowing through the migrated paths MUST land in a `SafeText.ofLiteral` or equivalent, never spliced raw into a `Style` constructor or border-character slot. Snapshot tests covering rendered tool-result content will catch any escape that bypasses `SafeText`.

- [X] **Principle XI — Context as Engineered Artifact**: N/A. No changes to system prompt, `CLAUDE.md` cacheable prefix, tool-schema preamble, or conversation compaction. The CLAUDE.md SPECKIT marker update at end of Phase 4 is a one-line edit to a stable section — same cache impact as any other status update.

- [X] **Quality Gates**: All gates preserved — `dotnet build`, `dotnet test`, `dotnet publish src/Fugue.Cli.Aot`, headless smoke (`fugue-aot --version` etc.), interop adapter integration tests (Phase 1+2's existing suite of 264+ tests). No gate is weakened; snapshot tests are additive within the `dotnet test` budget (each is < 10 ms — well under the suite's overall budget).

**Result**: 11/11 gates pass. Engagement levels vary: most actively engaged are Principle I (new test categories), II (sanitization surface enlargement), VIII (tangential — tool-result render preservation), X (`SafeText` discipline at migrated paths). No Complexity Tracking entries required.

## Project Structure

### Documentation (this feature)

```text
specs/005-cli-layout-migration/
├── plan.md              # This file
├── research.md          # Phase 0: resolves R-1 (streaming model), R-2 (DrawOp ABI), R-3 (visual regression mechanism), R-4 (PR sequencing)
├── data-model.md        # Phase 1: call-site inventory per file, RenderBridge signature, snapshot fixture shape, canonical scripted session DSL
├── quickstart.md        # Phase 1: how to port one file end-to-end (template procedure for each subagent dispatch)
├── contracts/
│   ├── render-bridge.fsi         # exact RenderBridge module signature
│   ├── snapshot-fixture-shape.md # snapshot file format + naming convention
│   └── pr-sequencing.md          # PR cluster definitions + acceptance gates per cluster
└── tasks.md             # Phase 2 — created by /speckit-tasks
```

### Source Code (repository root)

```text
src/Fugue.Adapters.Console/
├── Fugue.Adapters.Console.fsproj    # ⨯ DROP ProjectReference to Fugue.Surface (FR-005)
├── Renderer.fs                       # ⨯ REMOVE toDrawOp (relocated to Fugue.Cli.RenderBridge)
├── Renderer.fsi                      # ⨯ REMOVE toDrawOp declaration
└── (rest unchanged)                  # ✓ Layout/, Composition, Primitive, Style, etc.

src/Fugue.Cli/
├── RenderBridge.fsi                  # ＋ NEW — bridge module signature (relocated toDrawOp)
├── RenderBridge.fs                   # ＋ NEW — bridge module body (~20 LOC)
├── Surface.fs                        # ⨯ MIGRATE 13 Spectre call sites (PR P1)
├── Render.fs                         # ⨯ MIGRATE 5 Spectre call sites (PR P2)
├── Doctor.fs                         # ⨯ MIGRATE 1 Spectre call site (PR P2)
├── MarkdownRender.fs                 # ⨯ MIGRATE 3 Spectre call sites (PR P3)
├── StreamRender.fs                   # ⨯ MIGRATE 1 Spectre call site (PR P3)
├── DiffRender.fs                     # ⨯ MIGRATE 2 Spectre call sites (PR P4)
├── StackTraceRender.fs               # ⨯ MIGRATE 2 Spectre call sites (PR P4)
├── Picker.fs                         # ⨯ MIGRATE 1 Spectre call site (PR P4)
├── LayoutHost.fs                     # ✓ EXISTING — now becomes production canonical mount (PR P1 wires it)
├── Repl.fs                           # ⨯ REMOVE legacyRender env-var branch (PR P5, after soak)
└── Fugue.Cli.fsproj                  # ⨯ ADD Compile entries for RenderBridge

tests/Fugue.Tests/
├── RenderBridgeTests.fs              # ＋ NEW — happy + error tests for the bridge (PR P1)
├── RenderSnapshotTests.fs            # ＋ NEW — per-render-path snapshot assertions (added incrementally across PRs)
├── Snapshots/                        # ＋ NEW — frozen .txt ANSI fixtures
│   ├── surface_status_bar.txt
│   ├── surface_markdown_basic.txt
│   ├── surface_diff_basic.txt
│   ├── render_boot.txt
│   ├── doctor_output.txt
│   ├── markdown_streaming_complete.txt
│   ├── diff_render_edit.txt
│   ├── stacktrace_render_exception.txt
│   └── picker_turns_basic.txt
└── (existing tests unchanged)
```

**Structure Decision**: Existing F# monorepo layout. No new project (.fsproj) created. The migration extends existing projects with new files. Snapshot fixtures live under `tests/Fugue.Tests/Snapshots/` (new directory) — `.txt` files containing raw ANSI as captured from the pre-migration binary against a known-input Composition tree. Tests use `File.ReadAllText` to load fixtures and assert byte-for-byte equality against `Renderer.toRawAnsi` output.

## Complexity Tracking

> Constitution Check passed all 11 gates without violations. No entries needed.

The PR-sequencing complexity (5 PRs vs 1 big-bang) is justified by FR-013 (independent reviewability + bounded blast radius) and explicitly chosen, not a violation. Documented in `contracts/pr-sequencing.md`.
