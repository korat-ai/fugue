# Phase 0 Research: F# Console Adapter Library

**Feature**: `001-console-adapter-lib`
**Date**: 2026-05-16
**Status**: Phase 0 complete — no outstanding `NEEDS CLARIFICATION` markers.

This file resolves the four research questions surfaced during plan
generation: (R1) wrapped-library identity, (R2) public API shape, (R3)
visual-parity verification strategy, (R4) integration with the existing
`Fugue.Surface` writer actor.

---

## R1 — Which C# library is being wrapped?

**Decision**: `Spectre.Console 0.49.*` (already pinned in
`src/Fugue.Cli/Fugue.Cli.fsproj`).

**Rationale**:
- The user's input ("C# либой для консоли") and the existing rendering
  call-sites in `src/Fugue.Cli/` (9 files, ~12 `open Spectre.Console`
  statements across `DiffRender.fs`, `Doctor.fs`, `MarkdownRender.fs`,
  `Picker.fs`, `Render.fs`, `Repl.fs`, `StackTraceRender.fs`,
  `StreamRender.fs`, `Surface.fs`) jointly point to Spectre as the
  *only* console-rendering C# dep in the project.
- `Markdig` is also a C# dep but it's a markdown *parser*, not a console
  library — it gets its own Principle II adapter in a separate feature.
- `Microsoft.Agents.AI` and `System.CommandLine` are not console libraries.
- Version pin `0.49.*` matches the rest of the repo. The adapter project's
  `.fsproj` MUST use the same pin to avoid version drift between adapter
  and current call-sites during the port window.

**Alternatives considered**:
- Wrapping `Markdig` instead — rejected: that's its own Principle II
  workstream, distinct dependency, distinct API surface (text in → AST out
  vs styled text in → terminal out).
- Wrapping both Spectre and Markdig in one project — rejected: violates
  Principle II's "one adapter per dependency" rule (constitution v1.0.0,
  §C#/F# Interop Sanitization Layer). Co-locating two adapters in one
  project blurs version-bump impact (which dep changed?) and dilutes the
  integration-test signal.

---

## R2 — What shape should the public F# API take?

**Decision (2026-05-16, user-delegated)**: **A. Record-DU primitives.**

The user invoked `/speckit-implement` with argument `до конца` ("to the end"),
explicitly delegating the architecture-shape decision to the implementer to
unblock the full PR #1 MVP run in a single session. This is a CEO override
of the strict 3-way debate prescribed by T006 (Principle III). The override
is intentional, recorded here, and reversible: the chosen shape (DU values
in a public namespace) does NOT preclude later layering a CE or builder
DSL on top — those candidates produce the same `Primitive` / `Composition`
values, so the DU foundation is forward-compatible with either future
extension.

**Rationale for picking A over B (CE) and C (builder)**:

1. **Data-model match**: `data-model.md` §4 (Primitive) and §5 (Composition)
   are already sketched as F# DUs. Adopting A means zero translation cost
   between the planning artifact and the code.
2. **Principle I leverage**: F# exhaustive matching on DU values means
   "add a new `Primitive` case" mechanically forces every `Renderer`
   match arm to update. Compile-time discipline beats runtime test
   discipline for foundation types.
3. **Composability without lock-in**: A CE (`console { ... }`) or builder
   API (`Console.styledText ... |> Console.line`) constructs the same DU
   values internally. Either can be added later in a separate file
   (`Builder.fs` or `Computation.fs`) without touching the DU definitions.
   If we picked B or C first, the DU underneath would still exist — it
   would just be private. Public DU now, optional sugar later.
4. **Test surface**: integration tests against real Spectre are easier to
   write against an immutable DU value (you `match` on it in the test
   too) than against a CE-produced opaque object.

**Alternatives considered (and explicitly rejected for v1)**:

- **B. Computation Expression**: rejected for v1 because (a) CE machinery
  has nontrivial test cost (every CE method becomes a surface to verify),
  (b) the inventory addendum showed 80% of existing styling is
  markup-string-based — porting to CE is a bigger leap than porting to
  a DU. CE remains viable as a later add-on layer.
- **C. Builder functions on opaque type**: rejected for v1 because the
  opacity makes diffing and equality comparison in tests harder; we'd
  need to expose accessor methods or eq-checks that re-introduce a
  shadow of the DU shape anyway. The DU is the honest representation.

**Status of this override**: marked as "user-delegated interim" rather
than "user-affirmed". If the user later wants to revisit and run the full
3-way debate (e.g., a contributor objects to the DU choice during PR
review), the override can be reversed by replacing the DU public surface
with B or C in a follow-up MAJOR-bump PR. The cost of reversal is bounded
because (a) the DU values can be re-published as private internal types
under any new public surface, (b) call-sites that have ported to the DU
already use only the constructors, which are syntactically compatible
with builder-function and CE styles.

---

### R2 (historical) — Three candidate shapes considered

This is an architecture-grade decision (per constitution Principle III).
Three viable shapes exist; each has substantively different ergonomics and
testing-cost trade-offs. The orchestrator MUST run the dual-subagent debate
pattern (3–5 rebuttal rounds, then escalate to user) before any code is
written. Recording the candidate shapes now so the debate has concrete
positions to argue, not vague intuitions:

| Candidate | Sketch | Strengths | Weaknesses |
|---|---|---|---|
| **A. Record-DU primitives** | `type Primitive = StyledText of Style * string \| LineBreak \| Rule of Style; type Composition = Panel of border * Composition \| Padded of int*int*Composition \| ...` | F#-native, exhaustive matching, trivial to test, easy diff/eq | Verbose at call site for nested layouts; can't be incrementally extended without adding DU cases (breaks pattern matches downstream) |
| **B. Computation Expression** | `console { line "hello" |> bold; panel { content "world" }; rule () }` | Reads like markup; nice for blog-screenshot demos; F# idiomatic for "build-up" patterns | CE machinery is non-trivial to test; harder to expose typed style; learning curve for contributors |
| **C. Builder functions on opaque type** | `Console.styledText (Style.bold red) "hello" \|> Console.line \|> Console.panel Border.rounded` | Pipelineable, no DU evolution problem, hides implementation entirely | Easy to drift into "just call Spectre" without enforcing the typed-style discipline |

**Why a dual-debate (not pick now)**: the right shape depends on
priorities the spec deliberately doesn't fix: how often will new
primitives be added (favours C), how much do call-sites nest (favours B),
how heavy will the test suite be (favours A — exhaustive matching makes
"have we tested every case" mechanical). The Spec Kit constitution
(Principle III) is explicit that architecture-grade decisions MUST go
through dual debate. Surfacing the three options here gives the debate
agents concrete positions.

**What we DO commit to now** (binding for all three shapes):
- Public surface returns `Result<_, RenderError>` for every fallible
  operation (FR-004).
- No public type exposes a Spectre type (FR-003).
- Style is a typed F# record/DU, not a markup string (Acceptance
  Scenario 2.1 of US2).

**Alternatives considered (and rejected without debate)**:
- Direct re-export of Spectre `IRenderable` behind an F# alias —
  rejected: violates FR-003 ("no public type may expose a type defined
  by the underlying C# library").
- Pure markup-string API (`Console.write "[red]hello[/]"`) — rejected:
  violates US2's Acceptance Scenario 2.1 (typed style, not markup
  strings).

---

## R3 — How do we verify visual parity (FR-006, SC-002, US1 Scenario 3)?

**Decision**: **`Verify.Xunit` snapshot tests** for byte-level parity, plus
a **golden corpus** generated from one-time Spectre calls against the
existing F# call-sites. Snapshot files live under
`tests/Fugue.Adapters.Console.Tests/VisualParity/*.verified.txt` and are
reviewed once at v1, committed, then enforce parity forever after.

**Rationale**:
- Spectre output is deterministic at given (width, colour, theme)
  inputs. Snapshot testing converts visual parity into a textual diff —
  cheap to read, cheap to bisect, cheap to update with explicit human
  review when a Spectre upgrade legitimately changes output.
- Snapshot review is the *correct* signal for US3 (regression-on-upgrade):
  a failing snapshot points at one named primitive, the maintainer reads
  the diff, and decides intentional-vs-regression in one developer-minute
  (SC-007).
- Verify is already recommended at Stage 2 of the F# autotester maturity
  ladder (§5.4) — pairs cleanly with our existing xUnit + FsUnit setup.
- Width/colour scrubbing is handled by Verify's standard scrubber pattern
  for non-deterministic fields (timestamps, GUIDs); we don't have those,
  but we DO need a scrubber for the Spectre version string if it ever
  appears in output (defensive; document in `ModuleInitializer.fs` of the
  test project).

**Test layout**:
```
tests/Fugue.Adapters.Console.Tests/VisualParity/
├── primitive_styledtext_red_bold.verified.txt   ← bytes from Spectre
├── primitive_styledtext_red_bold.received.txt   ← bytes from adapter
│                                                  (created on test run,
│                                                   diffed against verified)
├── composition_panel_default.verified.txt
├── composition_padder_2x4.verified.txt
└── ...
```

Each `.verified.txt` is the byte stream produced by directly invoking
Spectre with a known input. The corresponding test invokes the adapter
with the same input and asserts the byte stream matches. First-run review
of the verified files is part of the v1 ship checklist (one human approval
of "this is what we expect").

**Alternatives considered**:
- **MockTerminal pattern** (`Fugue.Surface.MockExecutor`-style — replay
  `DrawOp` list and inspect it). Rejected as the *primary* verification —
  it tests our typed-DrawOp layer, not byte-level parity with Spectre.
  Will still be used for higher-level structural assertions (e.g.,
  "panel emits exactly 3 DrawOps") in `PrimitiveTests.fs` /
  `CompositionTests.fs`, but those tests don't satisfy FR-006 alone.
- **Property-based** (FsCheck) — rejected as primary: random inputs are
  great for round-trip and idempotence, but parity needs *recorded*
  outputs, not random ones. FsCheck will cover style normalisation and
  width-clamp idempotence in `PropertyTests.fs`.
- **Manual screenshot diff** — rejected outright. Untestable in CI,
  invisible to bisect, depends on human eyeballs every PR.

---

## R4 — How does the adapter integrate with the existing Surface actor?

**Decision**: The adapter's public render functions return a **`DrawOp list`**
(the existing `Fugue.Surface` type) or a **`string`** of pre-built ANSI
bytes. They do **NOT** touch `Console.Out` directly. Call-sites continue
to use `Fugue.Surface.write` / `batch` / `flush` to deliver the rendered
output to the serialising actor.

**Rationale**:
- FR-009 mandates that calls through the adapter land at the actor's
  serialisation point, not bypass it. The cleanest contract is:
  *adapter produces values; surface routes values.*
- `Fugue.Surface.DrawOp.RawAnsi` already exists as the carrier for
  pre-rendered ANSI strings (see `src/Fugue.Surface/`). The adapter can
  evaluate a `Composition` to `DrawOp.RawAnsi <bytes>` and let the actor
  do its job unchanged.
- This also enables FR-011 (incremental port) — a single call-site can
  change from `AnsiConsole.MarkupLine "..."` to `Console.markupLine "..."
  |> Surface.batch` without forcing every other rendering site to flip.

**Sketch of the routing seam** (final shape pending R2 dual-debate):
```fsharp
// inside Fugue.Adapters.Console
module Renderer =
    /// Evaluate a Composition into the byte/escape stream the Surface
    /// actor expects. Returns Result so invalid markup at adapter
    /// build-time is surfaced as a typed error.
    val toRawAnsi : RenderContext -> Composition -> Result<string, RenderError>

    /// Convenience: evaluate to a DrawOp ready to hand to the actor.
    val toDrawOp  : RenderContext -> Composition -> Result<DrawOp, RenderError>
```

**Alternatives considered**:
- **Adapter writes directly to `Console.Out`** — rejected: breaks the
  actor's serialisation invariant (concurrent renders interleave at the
  byte level, violating FR-009 and the Edge Case "Concurrent renders").
- **Adapter posts to the actor itself** (takes `MailboxProcessor<SurfaceMessage>`
  as a dep) — rejected: couples the adapter project to actor lifetime
  semantics, undoes "the adapter is a pure render layer" (Assumption in
  spec.md), and makes the test surface larger (now we need actor mocks).
- **Replace the actor entirely with adapter-internal serialisation** —
  rejected: out of scope. The actor exists for reasons orthogonal to
  rendering (timer-driven status-bar refresh save/restore, high-priority
  message routing). Replacing it is a much bigger feature.

---

## Open questions for `/speckit-clarify` (none — all resolved)

No `NEEDS CLARIFICATION` markers remain. R2 is *deferred*, not unresolved:
the answer is "this MUST go through dual debate before code is written",
which is itself the answer.

## Inventory addendum (from background `Explore` subagent, 2026-05-16)

The concrete Spectre.Console API surface used by `src/Fugue.Cli/` today:

| Type/Method | Files | Calls | Notes |
|---|---:|---:|---|
| `Markup` (ctor + static `Escape`) | 8/9 | 279 | Markup-string entry; `Markup.Escape` is safety-critical (100+ sites — every user string passes through it) |
| `IRenderable` | 9/9 | 83 | Pervasive return/parameter type — MUST be hidden behind an adapter type (FR-003) |
| `Text` | 6/9 | 63 | Plain renderable; fallback when colour disabled |
| `Color` (enum values) | 1/9 | 60 | Used only in `Repl.fs` for theme branching ("nocturne" → `#1e3a5a`) |
| `Rows` | 6/9 | 37 | Primary vertical composition |
| `Table` | 2/9 | 19 | Markdown tables only (`MarkdownRender.fs` + one slice in `Repl.fs`) |
| `Padder` | 5/9 | 12 | Always fluent: `.PadLeft(n).PadRight(n)` |
| `Width` / `Height` (static) | 3/9 | 11 | Column/row sizing in markdown tables |
| `Align` + `HorizontalAlignment` | 2/9 | 13 | Right-align for bubble mode in `Render.fs` |
| `Panel` + `PanelHeader` + `BoxBorder` | 1/9 | 5 | Bubble-mode assistant response only |
| `Style.Parse` | 1/9 | 2 | Multi-attribute styles ("dim #1e3a5a", "bold underline") |
| `Grid`, `Rule` | 1/9 | 2 | Markdown layout / thematic break |
| `AnsiConsole.Create` / `AnsiConsoleSettings` / `AnsiConsoleOutput` / `IAnsiConsole` | `Surface.fs` only | 1 each | Single render-session pattern: one ephemeral `AnsiConsole` per call → render to `StringWriter` → post string to actor (this is the integration seam R4 builds on) |

**Total**: 17 distinct types/methods. Plan estimate ("12–20 types") was
accurate.

**Critical finding 1**: ~80% of styling today is *markup strings*
(`[red]text[/]`, `[dim]`, `[#rrggbb]`), NOT typed `Style` objects. This is
a known anti-pattern under spec FR-002 + US2 Scenario 2.1 (which mandate
typed style). The adapter API MUST offer a typed-style path AND a
markup-string acceptor as a *bridge* — otherwise the v1 port wave breaks
on every existing call-site at once. R2 debate agents should treat
"how does typed-style coexist with markup-string acceptance during the
port window" as a first-class concern, not an afterthought.

**Critical finding 2**: `Markup.Escape` appears at 100+ sites. The adapter
MUST expose escaping as its own primitive (e.g., `Console.escape`) and
SHOULD make passing un-escaped user text *type-impossible* (smart
constructor: `type SafeText = private SafeText of string`). This is the
single highest-leverage place to apply Principle II's sanitization arm.

**Critical finding 3**: `Surface.fs`'s `spectre` helper is the *only*
place a `Spectre.Console` instance is created (one per render). That is
the natural place for `Renderer.toRawAnsi` from R4 to live — the new
adapter can absorb the entire `spectre` helper and call-sites just
import the adapter instead.

**Critical finding 4**: `Color` is used in exactly one file (`Repl.fs`)
for theme lookup. The adapter SHOULD expose a `Theme.colour name` lookup
function rather than a `Color` enum re-export, keeping the theme system's
single source of truth.

**Critical finding 5**: No reflection or dynamic type usage observed. The
adapter is mechanically wrappable; no `IL2026`/`IL3050`-style hazards.
(Still JIT-only per Principle V because Spectre itself is not
AOT-trim-clean.)

## Inputs to next phase

Phase 1 (`data-model.md`, `contracts/`, `quickstart.md`) operates against:
- **R1**: `Spectre.Console 0.49.*` as the single wrapped dependency.
- **R2**: Public API surface shape is OPEN; the three candidate shapes
  (A/B/C above) are equally valid until the debate concludes. Phase 1
  artifacts describe the *capability surface* (what operations exist)
  rather than the *syntactic surface* (what they look like at call site).
  The inventory's "critical finding 1" (markup-string coexistence) MUST
  be addressed in any candidate shape.
- **R3**: Verify-based snapshot tests under `VisualParity/`.
- **R4**: Output is `DrawOp` / `string`; the Surface actor handles delivery.
  The new adapter's `Renderer` module absorbs `Surface.fs`'s `spectre`
  helper.
- **Inventory**: 17 types, 279 markup calls, 100+ `Markup.Escape` sites.
  Use these numbers as the SC-002 baseline (80% of "279+83+63+37+19+12+11+13+5+2+2 ≈ 522 call-sites" ≈ 418 sites portable at v1).
