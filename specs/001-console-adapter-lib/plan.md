# Implementation Plan: F# Console Adapter Library

**Branch**: `001-console-adapter-lib` | **Date**: 2026-05-16 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-console-adapter-lib/spec.md`

## Summary

Add a new standalone F# project, `Fugue.Adapters.Console`, that wraps
`Spectre.Console` 0.49 behind an idiomatic F# API (typed primitives,
`Result`/`Option` returns, immutable composition values — no exposed C# types).
The existing REPL code in `src/Fugue.Cli/` (9 files, ~12 `open Spectre.Console`
statements across DiffRender, Doctor, MarkdownRender, Picker, Render, Repl,
StackTraceRender, StreamRender, Surface) is the *future* port target — actual
ports land in follow-up PRs, one call-site cluster at a time, gated on visual
parity. This feature delivers (a) the library, (b) an integration test
project that exercises every public primitive against the real Spectre
library (no mocks at the boundary — Principle II, NON-NEGOTIABLE), and (c) a
"from old to new" mapping doc covering the rendering call patterns observed
in the existing codebase at v1 ship.

## Technical Context

**Language/Version**: F# 9 on .NET 10 (matches the rest of the repo;
`TreatWarningsAsErrors=true`, `WarningLevel=5`).

**Primary Dependencies**: `Spectre.Console 0.49.*` (the wrapped C# library —
single dependency by design; per Principle II "one adapter per dependency"
markdown/CLI-parser wrappers are out of scope for this project). No other
project references except `Fugue.Surface` for `DrawOp` / `SurfaceMessage`
interop (FR-009 — adapter renders *through* the existing serialising actor).

**Storage**: N/A. The adapter is a pure render layer; it owns no state and
performs no IO probing (FR-009, Assumption: "host process retains ownership
of `Console.Out`").

**Testing**: xUnit + FsUnit for unit tests of pure helpers. `Verify.Xunit`
(snapshot testing — see §5.4 of the F# autotester guide) for visual-parity
suites that assert byte-for-byte equivalence between adapter output and a
recorded Spectre baseline. Integration tests run against the *real*
`Spectre.Console` assembly — no mocks at the C#/F# boundary (Principle II).
Property-based tests (FsCheck.Xunit) for primitives with algebraic invariants
(round-trip styling, idempotent normalisation of widths).

**Target Platform**: macOS-arm64 and Linux-x64 (the two supported Fugue
platforms per CLAUDE.md). The adapter does not call platform-specific APIs.

**Project Type**: F# library (`.fsproj` producing an assembly), plus a
sibling test project. Two .fsproj files total: `src/Fugue.Adapters.Console/`
and `tests/Fugue.Adapters.Console.Tests/`.

**Performance Goals**: Integration test suite finishes in < 30 s on a
developer laptop (SC-003). Adapter call overhead vs direct Spectre is
*non-load-bearing* — the rendering hot path is bounded by terminal IO, not
by F# wrapper allocation; we accept whatever overhead idiomatic F# imposes
(typically one extra allocation per call) as the cost of the abstraction.

**Constraints**:
- JIT-only project (Principle V, FR-010). Does **not** appear in
  `Fugue.Cli.Aot`'s closure; trim warnings are not enforced here.
- No public type may expose a Spectre type (FR-003). Caller F# must be able
  to `open Fugue.Adapters.Console` without also opening `Spectre.Console`.
- All fallible operations return `Result<_,_>` / `Option<_>` — never throw
  across the API boundary (FR-004).
- Calls must land at the existing `Fugue.Surface` actor's serialisation
  point (FR-009) — the adapter produces `DrawOp` values or strings that
  the actor then writes; the adapter does **not** touch `Console.Out`
  directly.

**Scale/Scope**: Wrap ~12–20 distinct Spectre types/methods covering 80%+
of the existing call-sites at v1 (SC-002). Exact inventory pending the
background Explore-subagent report (research.md captures the final list);
working estimate from the current code:
- `Markup`, `Text` (styled text)
- `Panel`, `Padder`, `Align` (containers)
- `Rule` (horizontal divider — DiffRender)
- `Columns`, `Rows` (layout — Picker)
- `AnsiConsole.Markup`, `MarkupLine`, `Write`, `WriteLine` (entry points)
- `AnsiConsole.Create`/`AnsiConsoleSettings`/`AnsiConsoleOutput` (already
  the pattern in `Surface.fs` — Spectre console isolation)
- `Style`, `Color`, `Decoration` (typed style — currently bypassed by
  markup-string style strings in most places)
- `IRenderable` (composition output type — must be hidden behind an
  adapter type).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify this plan against `.specify/memory/constitution.md` (**v1.1.0**).
Each gate below maps to a numbered Principle. Tick every box, or document
the violation under "Complexity Tracking" with an explicit justification.

- [x] **Principle I — Test Coverage Discipline**: Every public adapter
      primitive gets at least one integration test against real Spectre
      (happy path) and one error-path test (invalid markup, width 0,
      colour disabled). FR-005 makes this explicit; tasks.md enumerates
      one test task per primitive. No "ship without tests" carve-out.
- [x] **Principle II — C#/F# Interop Sanitization**: The whole *point* of
      this feature is to be the sanitization layer for `Spectre.Console`.
      The plan includes an adapter project AND a paired integration test
      project that hits the real Spectre assembly (no mocks at the
      boundary). FR-003, FR-004, FR-005 enforce the sanitization shape.
- [x] **Principle III — Subagent-Driven Development**: The Spectre API
      inventory is dispatched to a background `Explore` subagent before
      writing this plan (the inventory feeds research.md). The API-shape
      decision (record-DU vs CE vs builder) is flagged as a 3-way
      *dual-subagent debate* gate (tasks.md T006) — it WILL go through
      the for/against pattern over all three candidates before Phase 3
      implementation, not be picked unilaterally now.
- [x] **Principle IV — F# Purity at Source**: All new files are `.fs` /
      `.fsi` / `.fsproj`. No `.cs` files. Spectre stays a NuGet dep.
- [x] **Principle V — AOT-Closure vs JIT Discipline**: `Fugue.Adapters.Console`
      is JIT-only (FR-010). It is referenced from `Fugue.Cli` but NOT from
      `Fugue.Cli.Aot` / `Fugue.Core` / `Fugue.Tools` / `Fugue.Agent`. AOT
      publish of the headless binary remains untouched.
- [x] **Principle VI — Prompt-First Slash Commands**: N/A — this feature
      adds no slash commands. The *inverse rule* (v1.1.0 addition —
      promote prompt to code when failure patterns repeat) is also N/A:
      no prompt templates are being promoted or demoted by this work.
- [x] **Principle VII — Decision Hygiene & Merge Strategy**: PR-strategy
      in tasks.md (Implementation Strategy section) explicitly uses
      *merge commits* across 4 incremental PRs, not squash. CEO
      escalation rules apply at release time (this feature does not
      release on its own; it ships as part of the port-wave once US1
      lands).
- [x] **Principle VIII — Tool Contract Discipline** *(v1.1.0 — N/A for v1)*:
      This feature delivers a library, not a tool. `Fugue.Adapters.Console`
      is callable from F# code, not from the LLM directly. When the port
      wave moves Fugue's rendering through the adapter, the *tools* that
      use it (e.g., file-edit result rendering) still satisfy VIII via
      their existing dispatcher contracts. Adapter itself adds no tool.
- [x] **Principle IX — Runtime Policy ≠ Prompt Policy** *(v1.1.0 — N/A)*:
      This feature introduces no new safety policy. The colour-enabled
      flag (FR-007) is a *capability* check, not a safety guard.
- [x] **Principle X — Untrusted Content Boundary** *(v1.1.0 — N/A)*:
      The adapter takes typed F# values (`Style`, `SafeText`, `Composition`)
      as input. It does not retrieve, fetch, or ingest external content.
      Upstream callers of the adapter that *do* read external content
      (e.g., `WebFetch` result rendering) are responsible for X at their
      own layer, with `SafeText.ofUser` providing the escape-tracking
      assist.
- [x] **Principle XI — Context as Engineered Artifact** *(v1.1.0 — N/A)*:
      The adapter does not touch system prompt, `CLAUDE.md`, tool-schema
      preamble, or conversation compaction. It is a render-time concern,
      not a context-assembly concern.
- [x] **Quality Gates**: New project adds compile time + test time but
      cannot regress `dotnet publish src/Fugue.Cli.Aot` (it isn't in that
      closure). `dotnet build` and `dotnet test` on `.slnx` continue to
      gate every PR. Headless `--version` / `--help` smoke unchanged.

**Result (pre-research)**: all 11 gates pass. No entries in Complexity Tracking.

**Re-check (post-Phase 1 + post-constitution-amendment, 2026-05-16)**:
all 11 gates STILL pass. Phase 1 design *strengthened* Principles I and II
rather than introducing violations; constitution amendment v1.0.0 → v1.1.0
added Principles VIII–XI which are uniformly N/A for this library-shaped
feature (rationales above). The amendment also expanded Principle VI with
the prompt-to-code inverse rule — confirmed not in play here.

- Principle I — data-model.md §3 explicitly specifies a property-based
  idempotence test for `SafeText.ofUser`; quickstart.md §12 lists
  "Verify snapshots green" as a per-file port gate; tasks.md
  enumerates one happy + one error test per primitive (T013a covers
  `Style.ofMarkupHint`; T022a covers `Renderer.render` actor-delivery —
  added 2026-05-16 per `/speckit-analyze` M2/M3 remediation).
- Principle II — strengthened by the `SafeText` type (escape-tracking at
  the type level) and a closed `RenderError` DU with no `Other of string`
  catch-all (no untyped drift).
- Principle III — preserved: the background `Explore` subagent's API
  inventory was the input to data-model.md sizing; R2 is now a 3-way
  debate (record-DU vs CE vs builder) per tasks.md T006 (widened
  2026-05-16 per `/speckit-analyze` M1 remediation).
- Principles IV / V / VI / VII / VIII / IX / X / XI / Quality Gates —
  no change from pre-check.

## Project Structure

### Documentation (this feature)

```text
specs/001-console-adapter-lib/
├── plan.md                          # This file
├── research.md                      # Phase 0 output — see below
├── data-model.md                    # Phase 1 output
├── quickstart.md                    # Phase 1 output — "from old to new"
│                                    # mapping doc (becomes SC-005 deliverable)
├── contracts/
│   └── Fugue.Adapters.Console.fsi   # Phase 1 output — public API
│                                    # signature sketch (not the eventual .fsi
│                                    # in src/, just the contract for review)
├── checklists/
│   └── requirements.md              # Already exists from /speckit-specify
└── tasks.md                         # Phase 2 — /speckit-tasks output
```

### Source Code (repository root)

```text
src/
├── Fugue.Core/                      # AOT-clean, unchanged by this feature
├── Fugue.Agent/                     # AOT-clean, unchanged
├── Fugue.Tools/                     # AOT-clean, unchanged
├── Fugue.Cli.Aot/                   # AOT-only, unchanged
├── Fugue.Surface/                   # JIT-only, unchanged (referenced by
│                                    # the new adapter for DrawOp interop)
├── Fugue.Cli/                       # JIT-only, future port target
│                                    # (ports happen in follow-up PRs)
└── Fugue.Adapters.Console/          # NEW — JIT-only F# library
    ├── Fugue.Adapters.Console.fsproj
    ├── Style.fs                     # Style/Color/Decoration F# records+DUs
    ├── Primitive.fs                 # styled text, line break, raw, rule
    ├── Composition.fs               # panel, padder, align, columns, rows
    ├── Context.fs                   # RenderContext record + capability probe
    ├── Renderer.fs                  # public eval functions → DrawOp / string
    └── Console.fsi                  # public signature (kept narrow)

tests/
├── Fugue.Tests/                     # Existing 540+ tests, unchanged
└── Fugue.Adapters.Console.Tests/    # NEW — integration + property + snapshot
    ├── Fugue.Adapters.Console.Tests.fsproj
    ├── PrimitiveTests.fs            # one happy + one error path per primitive
    ├── CompositionTests.fs          # one happy + one error path per composition
    ├── ColourDisabledTests.fs       # FR-007 — NO_COLOR suite
    ├── DegenerateWidthTests.fs      # FR-008 — width 0/negative
    ├── VisualParity/                # Verify snapshots — golden Spectre output
    │   └── *.verified.txt
    └── PropertyTests.fs             # FsCheck — round-trip, idempotence
```

**Structure Decision**: Two new projects, both JIT-only, sibling to existing
`src/` and `tests/` layout. Project name **`Fugue.Adapters.Console`** chosen
over `Fugue.Console` / `Fugue.Terminal` / `Fugue.Spectre` to (a) make the
adapter intent explicit in the name (signals "this is the Principle II
boundary for Spectre"), (b) leave namespace room for sibling adapters
(`Fugue.Adapters.Markdown`, `Fugue.Adapters.Cli`) as they get spun up later
under the same principle, (c) avoid naming the wrapped library in the F#
project name, so a future swap to a different rendering backend doesn't
force a rename. Internal file split (`Style.fs`, `Primitive.fs`, etc.)
follows F# top-down dependency order (no forward references).

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| (none — all 7 Constitution gates pass) | — | — |

---

## Post-Implementation Constitution Re-Check (2026-05-16)

*All 11 Principles re-evaluated against the as-shipped implementation on
branch `001-console-adapter-lib`. Phases 1–6 complete.*

- **Principle I — Test Coverage Discipline**: PASS. 65 tests in
  `Fugue.Adapters.Console.Tests` covering all public primitives
  (T013–T031), all composition nodes (T032–T045), colour-disabled mode
  (T026–T031), degenerate-width behaviour (T020–T025), property tests
  (T039–T045 property suites, T046–T047 totality), corpus snapshot
  (T048a–c), suite-duration smoke (T049). Every public primitive has at
  least one happy-path and one error-path test. `[<Fact>]` discovery issue
  documented and worked around with `[<Property(MaxTest=1)>]`; follow-up
  tracked in tasks.md.

- **Principle II — C#/F# Interop Sanitization**: PASS. Zero mocks at the
  Spectre boundary — all integration tests instantiate real Spectre types
  via `AnsiConsole.Create` / `Renderer.toRawAnsi`. `SafeText` makes
  `Markup.Escape` skips a type error. `RenderError` DU is closed (no
  `Other of string`). No Spectre type appears in the public API.

- **Principle III — Subagent-Driven Development**: PASS. Background
  Explore subagent produced research.md inventory; R2 API-shape decision
  went through a 3-way debate (record-DU vs CE vs builder), resolved as
  Candidate A (record-DU). Implementation follows the settled design
  without unilateral pivots.

- **Principle IV — F# Purity at Source**: PASS. All new source files are
  `.fs` / `.fsproj`. No `.cs` files introduced.

- **Principle V — AOT-Closure vs JIT Discipline**: PASS. `Fugue.Adapters.Console`
  is not referenced from `Fugue.Cli.Aot`, `Fugue.Core`, `Fugue.Tools`, or
  `Fugue.Agent`. `dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64`
  produces 0 trim warnings (T056 verified 2026-05-16). AOT binary size and
  cold-start unchanged.

- **Principle VI — Prompt-First Slash Commands**: N/A. No slash commands
  added or modified by this feature.

- **Principle VII — Decision Hygiene & Merge Strategy**: PASS. Three
  incremental commits on `001-console-adapter-lib` (Phase 2+US1, Phase 4,
  Phase 5). PR will use `--merge` (merge commit), not squash.

- **Principle VIII — Tool Contract Discipline**: N/A. Adapter is a
  library, not a tool registered with the LLM.

- **Principle IX — Runtime Policy ≠ Prompt Policy**: N/A. No safety
  policy changes.

- **Principle X — Untrusted Content Boundary**: PASS. `SafeText.ofUser`
  is the escape-tracking entry point for any external string entering the
  render layer. Callers that read external content must use `ofUser`;
  internal literal text uses `ofLiteral`. Type system enforces the
  distinction — `Primitive.Styled` takes `SafeText`, not `string`.

- **Principle XI — Context as Engineered Artifact**: N/A. Adapter does
  not touch system prompt, `CLAUDE.md`, or conversation assembly.

**Result (post-implementation)**: all 11 gates pass. One known doc defect
corrected — data-model.md §3 falsely claimed `ofUser` is idempotent; fixed
in this Polish phase with the accurate non-idempotence invariant. No
Complexity Tracking entries required.

<!-- TODO(TOOL_AUDIT_VIII): When the port wave moves Fugue's rendering
through this adapter, re-evaluate Principle VIII at that time — the tools
that display file-edit results (Edit, Write, Bash) will then route through
the adapter; ensure their tool-contract schema annotations remain accurate.
Track as a follow-up to the first port PR. See constitution v1.1.0 §VIII
Sync Impact Report. -->
