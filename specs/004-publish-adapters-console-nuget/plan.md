# Implementation Plan: Publish Fugue.Adapters.Console as NuGet Package

**Status**: **Drafted but NOT pursued** — feature deferred during Phase 0 research. See spec.md "Deferral Note" for conditions of revisit. This document is preserved as the analysis baseline for the future return to the feature; only Constitution Check (passed all 11 gates trivially) and Phase 0 research (`research.md`) were completed. Phase 1 artefacts (`data-model.md`, `contracts/`, `quickstart.md`) were never written.

**Branch**: `004-publish-adapters-console-nuget` (merged to main as documentation only; branch then deleted) | **Date**: 2026-05-19 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/004-publish-adapters-console-nuget/spec.md`

## Summary

Ship `src/Fugue.Adapters.Console/` (F# wrapper over Spectre.Console — Phase 1+2 primitives + Phase 3 Layout framework: Stack/Dock/LayoutGrid/Flex + LayoutScheduler) as a standalone NuGet package published directly from the existing `korat-ai/fugue` monorepo. No code extraction into a separate repository — monorepo publish is the chosen distribution model.

Technical approach:

1. **Decouple** the single point of coupling to `Fugue.Surface` by relocating `Renderer.toDrawOp` out of the package and into a new `Fugue.Cli.RenderBridge` module (Fugue-internal consumer code). The package becomes self-contained in its dependency graph (only Spectre.Console + siblings).
2. **Annotate** `Fugue.Adapters.Console.fsproj` with NuGet packaging metadata (`PackageId`, `Version=0.1.0-prerelease`, `PackageReadmeFile`, license, repository URL, tags, `IsPackable=true`).
3. **Author** `src/Fugue.Adapters.Console/README.md` as the embedded `PackageReadmeFile`: one-paragraph purpose, ≤ 15-line Quick Start, one minimal example per layout primitive, "When to use this" comparison with raw Spectre, link to the in-repo spec/data-model docs.
4. **Add** a tag-driven GitHub Actions workflow `.github/workflows/publish-adapters-console.yml` that runs only on tags matching `adapters-console-v*`, executes `dotnet pack` → `dotnet nuget push`, reads `NUGET_API_KEY` from repository secrets.
5. **Document** the SemVer-bump convention in `CLAUDE.md` under "Conventions & red lines" so contributors know when to bump the package version field on PRs that touch the public API surface.
6. **Smoke-test** the end-to-end consumption path: build the `.nupkg` locally, install into a throwaway F# project via a local NuGet feed, render a non-trivial layout, assert non-empty well-formed output. Wire as a CI job.

Out of scope (per spec): namespace rebranding, splitting into multiple packages, AOT validation, publishing to public nuget.org at feature completion (first release is dry-run on local/staging feed).

## Technical Context

**Language/Version**: F# 9 (.NET 10)

**Primary Dependencies**: `Spectre.Console 0.49.*`, `Spectre.Console.Json 0.49.*`, `Spectre.Console.Testing 0.49.*` (existing — unchanged by this feature)

**Storage**: N/A (library package, no persistent state)

**Testing**: xUnit + FsUnit + FsCheck.Xunit (existing test suite, 264+ adapter tests + 573 core); one new smoke test job in CI that builds the `.nupkg` locally and installs it into a throwaway F# project

**Target Platform**: Cross-platform .NET 10 (`net10.0` target framework); the package itself is platform-agnostic. CI continues to run on `ubuntu-latest`, `macos-14`, `windows-latest`.

**Project Type**: Library (NuGet package) packaged from a monorepo; CI workflow + metadata configuration

**Performance Goals**: N/A for the packaging change itself. Performance characteristics of the rendered output are unchanged from Phase 3 (SC-006 — median ≤ 10 ms on typical Fugue UI tree).

**Constraints**:

- Package size MUST be reasonable (no debug symbols in `Release` pack; no embedded large fonts or fixtures beyond what's needed for Spectre Figlet).
- Tag-pattern workflow MUST be deterministic — same tag = same artefact bytes; concurrent tag pushes MUST NOT race on the same artefact path.
- Publish credentials MUST come from `${{ secrets.NUGET_API_KEY }}`, never committed.
- Existing 837+ test count on all 3 OSes MUST hold after decoupling (Quality Gate).

**Scale/Scope**:

- Source change footprint: ~10 lines of fsproj metadata + ~100 LOC `RenderBridge.fs` (extracted/relocated) + ~150 lines README.md + ~80 lines workflow YAML + ~30 lines CLAUDE.md addendum.
- Net new files: ~4 (README, RenderBridge.fs+fsi, workflow YAML, smoke-test fs). Modified files: ~3 (fsproj, Renderer.fs+fsi, CLAUDE.md). Removed lines: ~10 from `Renderer.fs`/`Renderer.fsi` (the relocated `toDrawOp`).
- Expected PR count: 4 (P1 decouple → P2 metadata+README → P3 workflow → P4 polish+SemVer doc), with Polish optional if smoke test gets covered inline.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verified against `.specify/memory/constitution.md` (v1.1.0). Each gate maps to a numbered Principle.

- [X] **Principle I — Test Coverage Discipline**: Decoupling adds new `Fugue.Cli.RenderBridge` module — happy-path + error-path tests will be added in `tests/Fugue.Tests/RenderBridgeTests.fs`. Smoke-test (FR-013) is a regression-guard for end-to-end package consumption. No new public API surface in the package itself (this PR only relocates an existing function).
- [X] **Principle II — C#/F# Interop Sanitization**: No new C#/F# boundary introduced. The existing Spectre.Console boundary is unchanged; we're packaging it for redistribution. Existing 264+ adapter integration tests (which hit the real Spectre library, not mocks) continue to apply.
- [X] **Principle III — Subagent-Driven Development**: 4 PRs, each delegatable to a specialist subagent: P1 decouple (`engineering-fsharp-developer`), P2 metadata+README (`engineering-senior-developer` or `engineering-fsharp-developer`), P3 workflow YAML (`engineering-senior-developer`), P4 polish (`engineering-fsharp-developer`). No dual-debate needed — all R-questions have informed defaults documented in spec; if research surfaces a real architectural fork, dual-debate fires then.
- [X] **Principle IV — F# Purity at Source**: No `.cs` files added. The only NuGet dependency change is *metadata* on an existing project, not a new package reference.
- [X] **Principle V — AOT-Closure vs JIT Discipline**: `Fugue.Adapters.Console` lives in the JIT closure today. The package is consumed by either JIT or AOT downstream applications, but the package itself does not change closure. The README will explicitly state "AOT consumption is unvalidated at v0.1" per FR-011 — that's a *consumer* warning, not a constitution violation. The new `Fugue.Cli.RenderBridge` lands in `Fugue.Cli` (JIT closure) where the relocated `Renderer.toDrawOp` logically belongs anyway.
- [X] **Principle VI — Prompt-First Slash Commands**: N/A. No slash commands added.
- [X] **Principle VIII — Tool Contract Discipline**: N/A. No tools added or modified.
- [X] **Principle IX — Runtime Policy ≠ Prompt Policy**: N/A. No safety policies added. The CI workflow's credential handling reads from `${{ secrets.NUGET_API_KEY }}` — standard GitHub-native secrets, no policy code involved.
- [X] **Principle X — Untrusted Content Boundary**: N/A. No retrieval channels added or modified. The publish workflow consumes a git tag (trusted — pushed by maintainer) and a secret (trusted — owned by maintainer); no external content enters the agent's action loop.
- [X] **Principle XI — Context as Engineered Artifact**: This feature does NOT touch the system prompt, conversation compaction, or tool-schema preamble. The CLAUDE.md addendum (SemVer rule, ~30 lines) is a one-time write to a stable section — does not bust prompt cache differently from any other CLAUDE.md edit.
- [X] **Quality Gates**: The decoupling work MUST preserve clean `dotnet build`, `dotnet test`, and `dotnet publish src/Fugue.Cli.Aot`. Smoke-test job adds a new CI step but does not alter existing gate semantics. Tag-driven publish workflow is gated on tag pattern (does not run on PRs/branches) so it does not add cost to the merge-time gate set.

**Result**: 11/11 gates pass. No Complexity Tracking entries required.

## Project Structure

### Documentation (this feature)

```text
specs/004-publish-adapters-console-nuget/
├── plan.md              # This file
├── research.md          # Phase 0: resolves R-1 (Renderer.toDrawOp), R-2 (mono vs split), R-3 (SemVer), R-4 (AOT-friendliness)
├── data-model.md        # Phase 1: package-metadata entity, release-tag entity, workflow-job entity, README sections
├── quickstart.md        # Phase 1: local pack + install + render smoke procedure (FR-013 manual variant)
├── contracts/
│   ├── package-metadata.md   # required .fsproj PropertyGroup fields + their semantics
│   ├── publish-workflow.md   # tag pattern, workflow inputs, step-by-step contract
│   └── readme-structure.md   # mandated README sections in order
└── tasks.md             # Phase 2 — created by /speckit-tasks
```

### Source Code (repository root)

```text
src/Fugue.Adapters.Console/                  # the package source — minor edits only
├── Fugue.Adapters.Console.fsproj            # ⨯ add PackageId, Version, ReadmeFile, etc.
├── README.md                                # ＋ NEW — embedded PackageReadmeFile
├── Renderer.fs                              # ⨯ remove `toDrawOp` (relocate to Fugue.Cli)
├── Renderer.fsi                             # ⨯ remove `toDrawOp` declaration
├── Layout/                                  # ✓ unchanged (Stack, Dock, LayoutGrid, Flex, Scheduler, etc.)
└── (rest unchanged)                         # ✓ Style, SafeText, Composition, Primitive, Console, etc.

src/Fugue.Cli/
├── RenderBridge.fs                          # ＋ NEW — houses the relocated `Composition → DrawOp` mapping
├── RenderBridge.fsi                         # ＋ NEW — public signature for RenderBridge
├── Fugue.Cli.fsproj                         # ⨯ add Compile entries for RenderBridge

tests/Fugue.Tests/
├── RenderBridgeTests.fs                     # ＋ NEW — happy + error path tests for the relocated function

tests/Fugue.Adapters.Console.Tests/
├── PackageSmokeTests.fs                     # ＋ NEW — FR-013 smoke (local pack → install → render)
                                             #         OR runs entirely in CI YAML if cleaner

.github/workflows/
├── publish-adapters-console.yml             # ＋ NEW — tag-driven publish

CLAUDE.md                                    # ⨯ append SemVer-bump rule under "Conventions & red lines"
```

**Structure Decision**: This feature operates on an existing monorepo whose structure is already established. Project layout is unchanged; the feature adds 4 new files, modifies 3 existing files, and removes ~10 lines of relocated code. Per spec scope, no new `.slnx` and no new `.fsproj` are created — the existing `Fugue.Adapters.Console.fsproj` becomes the packable project simply by adding metadata.

## Complexity Tracking

> Constitution Check passed all 11 gates without violations. No entries needed.
