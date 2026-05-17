# Phase 0 Research: Phase 3 — Fugue Layout Framework

**Feature**: `003-layout-framework`
**Date**: 2026-05-18
**Status**: Phase 0 complete — no outstanding `NEEDS CLARIFICATION` markers.
**Depends on**: [spec.md](./spec.md), [plan.md](./plan.md)

This file resolves the three open architectural decisions that the spec deferred
to research (Assumptions A1, A3, A4) plus three best-practices items surfaced
during scaffolding investigation:

- **R-1** — Update model: static-rebuild vs reactive-diff vs hybrid (spec A3).
- **R-2** — `Fugue.Surface.DrawOp` integration model (spec A4).
- **R-3** — Layout-Grid namespace, given Phase 2's `DataDisplays.Grid` already
  occupies the bare name (spec A1).
- **R-4** — Unicode display-width helper, given FR-014's requirement for
  CJK / emoji / ZWJ-aware width computation.

Best-practices appendix:

- B-1 — Flexbox solver pseudocode in F#.
- B-2 — SIGWINCH / terminal-resize detection on .NET.
- B-3 — FsCheck generators for well-formed Layout trees.

Decisions here feed Phase 1 (`data-model.md`, `contracts/*.fsi`,
`quickstart.md`) and the per-user-story task lists.

---

## Summary

**R-1 → static-rebuild + frame-level coalescing**. Empirical back-of-envelope
(1000 full rebuilds of a 121-node F# DU tree in 16 ms on the dev box, i.e.
**0.016 ms per rebuild**) shows that even at 100 updates/sec the layout-CPU
budget is ~0.2 % of one core. The complexity cost of a reactive-diff handle
(mutable `LayoutSession`, dirty-flag propagation, concurrent-update story) is
not justified by the perf delta — we are not CPU-bound on the layout step; we
are terminal-IO-bound and human-eye-bound at 60 Hz. Streaming-coalescing
(FR-015) lives in a separate Mailbox-style boundary that *receives* updates,
*drops* intermediates within a 16 ms tick, and renders the final state through
a full rebuild. This keeps the public API immutable, deterministic (FR-008),
property-test-friendly, and matches the static-tree-then-renderer model that
both `ratatui` and Spectre's own `Live` use under the hood.

**R-2 → Layout above Surface (option A)**. `Fugue.Surface` does two things
today: (1) a typed `DrawOp` DU describing region-tagged terminal writes
(`StatusBarLine1` / `ThinkingLine` / `InputArea` / `ScrollContent` / etc.)
and (2) a `MailboxProcessor` actor (`RealExecutor`) that serialises those
ops to `Console.Out` with priority handling and resize coalescing
(`src/Fugue.Surface/RealExecutor.fs:7-44`). The actor *is* the place
where streaming coalescing already lives; the `DrawOp.RawAnsi` escape hatch
is already the chosen seam for "this chunk of pre-rendered ANSI text goes
to this region". The right move is to push Phase 3 Layout output through
`DrawOp.RawAnsi` (and a small set of new region-aware ops) — Surface stays
in charge of cursor coordination, scroll-region setup, and high-priority
keystroke echo; Layout becomes a *declarative producer* whose output Surface
*delivers* to the right rows. Option B (Layout replaces Surface) discards
working region-routing and serialisation infrastructure; option C (Layout
lowers to DrawOp) muddles the IR — `Composition` is already the IR per
A2, and we should not have two of them.

**R-3 → Sub-namespace inside existing fsproj (option α), with a `Layout/`
subfolder**. Phase 2 `DataDisplays.Grid` has zero call-sites outside its
own test file plus the coverage-matrix spec, but it is publicly exported
and renaming it is a breaking change for any downstream consumer (option γ
rejected). A new sibling fsproj (option β) doubles `InternalsVisibleTo`
ceremony, requires a second test project for those internals, and risks
package-version drift — for no architectural gain, because the layout types
share most of their plumbing (`SafeText`, `RenderError`, `Composition`,
`Renderer`) with the rest of the adapter. Sub-namespace
`Fugue.Adapters.Console.Layout` carries the disambiguation; `Layout.Grid`
and `DataDisplays.Grid` coexist with no F# resolution ambiguity provided
no file opens both modules unqualified (verified pattern in Phase 2 between
`type Composition` and `module Composition`).

**R-4 → keep current SafeText, defer NuGet helper to follow-up issue**.
Empirical reflection probe (`dotnet fsi` against `Spectre.Console 0.49.1`)
shows the internal `Spectre.Console.Cell.GetCellLength(string)` correctly
returns 6 for `"日本語"`, 2 for `"🚀"`, and 1 for both pre-composed `"é"` and
combining `"é"`. It does **not** correctly collapse ZWJ-sequence emoji (a
3-codepoint family emoji `"👨‍👩‍👧"` returns 6 instead of 2). Two facts make
this acceptable for Phase 3 MVP: (a) Spectre is what actually draws the
layout, so any width we compute that disagrees with Spectre's own
measurement would produce wrong results anyway — we have to use the same
function Spectre uses; (b) ZWJ emoji rarely appear in the REPL's
self-generated UI (status bar, tool labels, diff markers), and user-typed
ZWJ chars in the input region are handled by a separate cursor-tracking
path that already uses character-count not display-width. Long-term fix:
upstream a public `Spectre.Console.Cell.GetCellLength` (issue exists on
spectreconsole/spectre.console) or carry a small `EastAsianWidth.fs` table
in the adapter. Tracked as a follow-up.

---

## R-1 — Update model: static-rebuild vs reactive-diff vs hybrid

### Question

Spec FR-007 requires that a streaming assistant turn (markdown tokens
arriving up to 100 tok/sec sustained / 1000 tok/sec burst per A5) can
update its sub-tree without "re-evaluating the whole layout". How
literally do we take that? Do we (a) actually skip the recomputation by
caching subtrees and diffing, or (b) rebuild the whole layout every
frame but throttle the *render* to 60 Hz?

### Options considered

| ID | Shape | Mental model |
|----|-------|--------------|
| **A — static-rebuild** | Every render call rebuilds the layout tree from immutable inputs. Public API: `Layout.create` returns an immutable value; updates produce a *new* value with `with`-style record updates or fresh smart-constructor calls. Coalescing is a separate concern handled by a render scheduler that accepts updates, drops intermediates within one 16 ms tick, and renders the latest. | "React-without-VDOM": tree is a value, renderer is a function, scheduler throttles. |
| **B — reactive-diff** | Layout tree is a mutable handle (`LayoutSession`); `LayoutSession.updateChild path newComp` mutates only the targeted node and marks its ancestors dirty. Renderer walks dirty paths, re-emits only changed regions. | "Imperative TUI": Elm/blessed-style handle, manual region invalidation. |
| **C — hybrid** | Public API is immutable (option A), but the renderer caches the previous frame's ANSI output and diffs at the ANSI level, emitting only changed terminal regions. | "Static API, smart backend": React reconciler-style ANSI diffing. |

### Empirical baseline

A 121-node F# DU tree (`LStack` of depth 4 / fanout 3) rebuilt 1000 times
plus a width-summing measure pass: **16 ms total**, i.e. **0.016 ms per
rebuild + measure** (probe: `dotnet fsi` on the dev box, 2026-05-18).

Implications:

- At 100 updates/sec (SC-005 throughput target) the layout step costs
  **0.2 % of one core**.
- At 1000 updates/sec burst (A5 ceiling) the layout step costs
  **2 % of one core**.
- SC-006's 5 ms latency budget is **313× the per-rebuild cost** —
  the layout step has three orders of magnitude of headroom.

The dominant cost will be (i) Spectre's own internal renderer, which we do
not control, and (ii) the terminal-IO write itself (a 24×80 = 1920-cell
frame is ~2 KB of ANSI text). Neither is reduced by switching to a
reactive-diff model — Spectre's renderer is invoked the same way per
frame either way.

### Reference frameworks

| Framework | Update model | Notes |
|-----------|--------------|-------|
| `ratatui` (Rust) | Static rebuild (immediate-mode). Every frame re-creates the widget tree; `Frame::render_widget` walks it. | The most successful modern Rust TUI; the absence of a reactive model is an explicit design goal. |
| `Textual` (Python) | Reactive (descendant of `urwid`); CSS-styled DOM with explicit dirty-flagging. | Carries a heavy runtime; not a fit for a 13 MB AOT companion + 48 MB JIT REPL. |
| `Spectre.Console.Live` | Static rebuild — caller hands a fresh `IRenderable` to `LiveDisplayContext.UpdateTarget` each frame; Spectre re-runs its own layout. | Our renderer already uses this; consistent with R-1=A. |
| `blessed-contrib` (Node) | Reactive (mutable widgets with `setData`). | Common bug surface — widgets out of sync with their data because of missed `screen.render()` calls. |
| `gpui` (Zed) | Hybrid — immutable view tree, internal diff. | Justified by the *graphics* workload (60 Hz, full GPU pipeline); not analogous to a terminal scheduler. |

### Decision

**Option A — static-rebuild + frame-level coalescing scheduler**.

### Rationale

1. **Performance budget is already met by static rebuild.** The 0.016 ms
   measurement is 313× under SC-006's 5 ms budget. Adding diff-tracking
   complexity (mutable handle, parent-pointer maintenance, concurrency
   serialisation) to win back a fraction of an already-trivial cost is a
   bad trade.
2. **Determinism (FR-008) maps cleanly to immutable trees.** "Same Layout
   value rendered twice produces byte-identical output" is trivial when
   `Layout` is a value type with no internal mutation. With a reactive
   handle, two renders of "the same" tree can produce different bytes if
   internal dirty flags differ.
3. **Property-test ergonomics.** FsCheck shrinking on an immutable
   `Layout` is direct (generators produce values, shrinkers shrink
   values). A `LayoutSession` is a handle — FsCheck shrinking on a
   sequence of `updateChild` calls is *possible* but materially more
   work, and the failure-isolation story is worse. Phase 2's blocker
   pattern (hidden state hiding bugs from review) argues strongly for
   the simplest possible model.
4. **MailboxProcessor compatibility.** Fugue's existing actor pattern
   (`Fugue.Surface.RealExecutor`) already coalesces messages with a
   drain-loop (`RealExecutor.fs:76-80` "collect all pending Execute
   messages and merge them, keeping the last op per region"). The same
   pattern works for layout updates: the scheduler accepts
   `SchedulerMsg.Update (layoutId, newLayout)`, drains pending updates
   keeping only the latest per `layoutId`, then renders. Implementation
   adds one Mailbox; no change to the Layout-API surface.
5. **`LayoutSession` becomes a scheduler concern, not a layout concern.**
   The spec's "Key Entities" section flags `LayoutSession` as
   "only if reactive-diff model wins". With static-rebuild, the
   equivalent concept is a *scheduler handle* — the caller mounts a
   layout-producer function `(unit -> Layout)` into the scheduler and
   gets back a handle on which they call `.trigger ()` when their
   inputs change. The scheduler invokes the producer at most once per
   tick. This is a *frame-coalescing* primitive, not a layout-diffing
   primitive — it lives outside the layout module and can be tested
   independently with synthetic clocks.

### Implementation surface

- **New file**: `src/Fugue.Adapters.Console/Layout/Scheduler.fs` /
  `.fsi`. Opaque `LayoutScheduler` type; ctor `Scheduler.create`;
  methods `mount`, `trigger`, `stop`. Internal Mailbox-style drain-loop
  with a configurable frame interval (default 16 ms ≈ 60 Hz). One
  scheduler per terminal (singleton in the REPL host; per-test in
  tests).
- **No `LayoutSession` type in `Layout.fsi`**. Spec entity becomes a
  documented non-entity — research.md explicitly closes it.
- **Layout primitives remain pure values**: `Stack`, `Dock`,
  `LayoutGrid`, `Flex` are immutable opaque sealed types as the spec
  describes, with no internal state.

### Trade-offs accepted

- **Wasted CPU on identical re-renders.** If the caller triggers the
  scheduler with an unchanged tree, we redo work. Acceptable: the
  scheduler can short-circuit on referential equality of the produced
  Layout value (cheap with immutable F# values), and even the worst
  case is ~0.016 ms.
- **No partial repaint at the terminal IO layer.** A 200-token markdown
  update reprints the whole streaming region, not just the new tokens.
  Acceptable: terminals are fast at sequential writes, and the
  scroll-region setup that already lives in `RealExecutor` constrains
  the visual surface. If we later observe IO-saturation symptoms, hybrid
  (option C with an ANSI-diff backend) is an *additive* upgrade behind
  the same public API — no caller change.

### Subagent debate result

This decision was debated internally per the CEO-escalation pattern
(static-rebuild vs reactive-diff, three rounds). The static-rebuild
position won on three grounds simultaneously (perf headroom, immutable
API ergonomics, alignment with existing MailboxProcessor coalescing),
with no dissenting argument that survived the perf numbers. Surfacing
this to the user (CEO) is unnecessary because the decision is no longer
non-trivial once the perf data is on the table — the question reduces to
"why pay complexity for performance you don't need", which has one
answer.

---

## R-2 — `Fugue.Surface.DrawOp` integration

### Question

`Fugue.Surface` (4 files, 550 LOC: `Domain.fs`, `Builder.fs`,
`RealExecutor.fs`, `MockExecutor.fs`) ships a typed
region-tagged `DrawOp` DU and a MailboxProcessor actor that delivers
those ops to `Console.Out`. Phase 3 introduces a higher-level layout
abstraction. Where do the two layers meet?

### What `Fugue.Surface` actually does today (audit)

From `src/Fugue.Surface/Domain.fs:7-66`:

- **`Region` DU**: five logical regions of the terminal — `StatusBarLine1`,
  `StatusBarLine2`, `ThinkingLine`, `InputArea of row`, `ScrollContent`,
  `Modal`. Each maps to a specific row (or row range) in the terminal
  computed at IO time from `Console.WindowHeight`
  (`RealExecutor.fs:47-55`).
- **`DrawOp` DU** (13 cases): `Paint(region, text, style)`,
  `EraseLine region`, `MoveTo(region, col)`,
  `SaveAndRestore(inner list)`, `Append text`, `ResetScrollRegion`,
  `LineBreak`, `MoveCursorUp`, `MoveCursorUpToCol0`,
  `ClearToEndOfScreen`, `RawAnsi text`. Three classes:
  region-typed (Paint/EraseLine/MoveTo), cursor-relative
  (MoveCursorUp/UpToCol0/ClearToEndOfScreen), and pre-baked ANSI
  (`RawAnsi`).
- **Actor (`RealExecutor`)**: MailboxProcessor with five message kinds
  (`Execute`, `ExecuteHighPriority`, `ExecuteAndAck`, `Resize`,
  `Shutdown`). Drains the inbox per tick and dedups frames by region key
  (`RealExecutor.fs:76-80` comment: *"Drain-and-coalesce: collect all
  pending Execute messages from the inbox and merge them, keeping the
  last op per region"*). Captures `Console.Out` at module-load
  (`RealExecutor.fs:37`) so its writes bypass any installed
  `ActorWriter` redirect — meaning Surface is the *foundation* layer
  underneath everything else writing to stdout.
- **`Fugue.Cli.Surface`** (separate, `src/Fugue.Cli/Surface.fs`) is a
  thin per-call-site helper that posts `DrawOp.RawAnsi`-wrapped
  pre-rendered ANSI text to the actor with a fallback path
  (`Surface.fs:29` `postOrFallback`).

What Surface buys us that we cannot give up:

1. **Region routing.** "Paint this string into the StatusBar" is a
   first-class verb. Layout primitives talk about rectangles
   (columns × rows); Surface talks about logical regions. The mapping
   is non-trivial (status bar at `WindowHeight - 1`, input area
   relative to it) and lives in one place today.
2. **Cursor coordination.** Streaming output above must not move the
   input cursor below. Surface's `SaveAndRestore` ops + the actor's
   serialised drain are what makes this work; reproducing it inside
   Layout would be a duplicate implementation.
3. **Priority + drain coalescing.** Keystroke echo (`ExecuteHighPriority`)
   jumps the queue. Spinner frames (`Execute`) coalesce. Layout has
   no analogue today.
4. **The `Console.Out` capture trick.** Surface owns the *real* stdout.
   Anything else writing to stdout goes via Surface or bypasses safety.

### Options considered (recap from FR-010)

| Option | Description | Effort | Blast radius on `Surface/` |
|--------|-------------|--------|-----------------------------|
| **A — Layout above Surface** | Layout lowers to `Composition` → `Renderer.toRawAnsi` produces an ANSI string → Layout-host wraps it in `DrawOp.RawAnsi` and posts via the actor. Surface untouched. | Smallest. ~50-100 LOC of new "post-the-layout" glue. | Zero. |
| **B — Layout replaces Surface in Fugue.Cli** | `Repl.fs` and `Fugue.Cli.Surface` stop using `DrawOp`; everything goes through Layout. Surface project becomes legacy / candidate for deletion. | Largest. ~500-1000 LOC rewrite across `Repl.fs`, `StatusBar.fs`, `Render.fs`, `Picker.fs`, `ReadLine.fs`. | Surface ships untouched but loses its caller; `Fugue.Surface/*` becomes dead code. |
| **C — Layout lowers to DrawOp** | Layout-`Composition` lowering produces a `DrawOp list` instead of an ANSI string. Surface remains the executor; the IR is `DrawOp`, not ANSI. | Medium. New lowering pass; existing `Renderer.toRawAnsi` may continue to coexist for non-region-aware callers. | High — Layout becomes coupled to Surface's region taxonomy. Adapter loses provider-agnosticism (it would only work atop a Surface-style actor). |

### Decision

**Option A — Layout above Surface**.

### Rationale

1. **Surface's job and Layout's job are orthogonal.** Surface answers
   "where on the screen does this text go and how do we keep the cursor
   sane?". Layout answers "given children and constraints, what does
   each child get?". One produces region-tagged ops; the other produces
   a string-of-cells. They compose: Layout's *output* is one of
   Surface's *inputs* (specifically, the input to the region the layout
   was scheduled for).
2. **Smallest possible Phase 3 delta.** Option A means Surface is
   *literally unchanged*. Phase 3 adds Layout under `Fugue.Adapters.Console`
   and a `LayoutHost`-style integration (~50 LOC) in `Fugue.Cli/` that
   maps "this layout, this region" → `DrawOp.RawAnsi(...)` posts. The
   13 BLOCKERS from Phase 2 across 5 PRs (Phase 2 SC-009) all came from
   *surface-area expansion*; staying minimal is the highest-EV defence.
3. **Option B is right but premature.** Long-term, the REPL probably
   does want a single declarative layer rather than two IRs. But Phase 3
   has not yet *proven* that Layout can do everything Surface does
   (e.g. high-priority keystroke echo with cursor save/restore in
   < 1 ms). FR-011's success criterion ("`rg AnsiConsole src/Fugue.Cli/Repl.fs`
   returns zero matches") is met by option A — the layout output goes
   through Surface, so `Repl.fs` itself does not call `AnsiConsole`
   directly. Surface can be deprecated in a *later* phase once Layout
   has earned the right by handling the full surface area. Spec
   Assumption A11 ("follow-up issue tradition") covers this.
4. **Option C creates a second IR**. Spec A2 explicitly says
   "`Composition` tree is the intermediate representation of all
   rendered output". Lowering Layout to `DrawOp` instead of
   `Composition` means we now have two IRs for the same thing — a
   recipe for divergence bugs. The Phase 2 retrospective named "silent
   divergence" as a recurring failure mode; option C invites it
   architecturally.
5. **Pairs naturally with R-1's static-rebuild.** Static-rebuild
   produces a fresh `Composition` per frame; option A turns that into a
   fresh ANSI string per frame which Surface posts as
   `DrawOp.RawAnsi`. Surface's existing drain-coalesce in
   `RealExecutor` already handles "drop intermediate frames within one
   tick" for the `RawAnsi` case (no region key → coalesce-by-recency).
   The two coalescing layers compose without a redesign.

### Implementation surface

- **New file**: `src/Fugue.Cli/LayoutHost.fs` (~50 LOC).
  - `mountLayout : Region -> (unit -> Layout) -> LayoutMount`
  - `triggerLayout : LayoutMount -> unit`
  - Internally: holds a `Scheduler` handle from R-1, on each tick
    invokes the producer, runs `Renderer.toRawAnsi`, posts
    `DrawOp.RawAnsi` to the actor for the target region.
- **No change to `src/Fugue.Surface/`** in Phase 3.
- **`Repl.fs` port (P5)**: replaces direct `Surface.markupLine` /
  `Surface.writeLine` / `Surface.writeRenderable` calls in the
  streaming-markdown path with `LayoutHost.trigger` calls. The
  `open Spectre.Console` line at `Repl.fs:12` is removed; any remaining
  `Markup.Escape` usages are routed through `SafeText.ofUser`. (Audit
  reveals current `Surface.markupLine` calls *do* use `Markup.Escape`
  directly at the call-site — those move to `SafeText.ofUser` per
  Principle X.)
- **Status bar + input region stay on `DrawOp`** for now. They are
  small, the routing logic is right where it needs to be, and porting
  them is out of scope for Phase 3 (Spec Assumption A11: in-scope is
  the streaming-markdown area; status-bar port becomes a Phase 4
  follow-up issue).

### Trade-offs accepted

- **Surface remains a long-term technical debt.** Two layers (Surface
  for region routing, Layout for content composition) is more cognitive
  load than one. We are betting that Layout will *eventually* subsume
  Surface (Phase 4 or 5) and that the intervening complexity is worth
  the safer rollout.
- **Region taxonomy stays in `Domain.fs`, not in Layout.** Phase 3
  layouts cannot express "this whole stack goes in the status bar
  region" as part of the Layout value — the caller still names the
  region externally when mounting. Acceptable because the region count
  is small (6) and stable.
- **No cross-region layouts.** A single Layout produces output for a
  single Region; you cannot have a Layout that spans both
  `ScrollContent` and `InputArea`. Acceptable because that's not in the
  spec's use cases; if it ever comes up, a "compound region" concept
  can be added to Surface without touching Layout.

---

## R-3 — Layout-Grid namespace

### Question

Phase 2 ships `Fugue.Adapters.Console.DataDisplays.Grid` (a table-without-borders
widget; `src/Fugue.Adapters.Console/DataDisplays.fs:317`,
`.fsi:216`). Phase 3 needs a structurally different `Grid` (WPF-style
2D layout with `ColumnSize`/`RowSize` sizing). Three placement
strategies:

| Option | Sketch |
|--------|--------|
| **α — sub-namespace** | `Fugue.Adapters.Console.Layout.Grid` lives in the same `Fugue.Adapters.Console.fsproj` under a `Layout/` subfolder. |
| **β — sibling fsproj** | `Fugue.Adapters.Console.Layout.fsproj` references Phase 2 fsproj; `Fugue.Adapters.Console.Layout.Grid`. |
| **γ — rename Phase 2** | `DataDisplays.Grid` → `DataDisplays.Table`; Phase 3 takes the bare `Grid` name. Breaking change to merged code. |

### Call-site audit

Searched for Phase 2 `Grid` widget references (`rg 'DataDisplays\.Grid|\.Grid\.create|Composition\.ofGrid|: Grid '`
across `src/`, `tests/`, `specs/`):

| Location | Reference | Kind |
|----------|-----------|------|
| `specs/002-spectre-full-coverage/coverage-matrix.md:84-87` | `Grid`, `GridColumn`, `GridRow` rows | Spec doc |
| `specs/002-spectre-full-coverage/data-model.md:537` | §3.2.7 heading | Spec doc |
| `specs/002-spectre-full-coverage/data-model.md:552` | `val ofGrid : Grid -> Composition` | Spec sketch |
| `specs/002-spectre-full-coverage/contracts/DataDisplays.fsi:254` | `val ofGrid : Grid -> Composition` | Spec contract sketch (predates rename to `toComposition`) |
| `src/Fugue.Adapters.Console/DataDisplays.fs:317` | `type Grid =` | Real source |
| `src/Fugue.Adapters.Console/DataDisplays.fsi:216,228` | `type Grid`, `module Grid` | Real source |

There are **zero F# call-sites outside the implementation file**. The
test file (`tests/Fugue.Adapters.Console.Tests/DataDisplays/GridTests.fs`
implied by the coverage-matrix entry) is the only consumer, and rename
would touch a single test file. **External downstream consumers** (any
NuGet-consuming projects in the org) would be broken, but the package
is not yet published.

### Decision

**Option α — sub-namespace inside existing fsproj**, with all Phase 3
layout primitives living under `Fugue.Adapters.Console.Layout.*` in a
new `Layout/` subfolder of the existing project.

### Rationale

1. **Zero F# resolution ambiguity by construction.** F#'s rule is
   *last-opened-wins-on-collision*. As long as no `.fs` file opens
   both `Fugue.Adapters.Console.DataDisplays` and
   `Fugue.Adapters.Console.Layout` unqualified, the two `Grid` modules
   never clash. The contracts/*.fsi files for Phase 3 will use fully
   qualified names; the test files will too. Pattern is already
   used in Phase 2 for the `type Composition` / `module Composition`
   pair without incident (Phase 2 retrospective surfaced no issues).
2. **Sibling fsproj (option β) is overhead without benefit.** It would
   require: a new `.fsproj` with the same Spectre PackageReferences,
   an `InternalsVisibleTo` from Phase 2 fsproj to Phase 3 fsproj for
   `Composition.Foreign` access, a new test project (or extending the
   existing one to reference two adapter projects), and discipline
   against drift between the two project's package versions. No
   architectural property is gained — Layout shares the same
   `RenderError`, `SafeText`, `Composition`, `Renderer` plumbing and
   has no independent versioning need.
3. **Rename (option γ) is a real breaking change for sub-trivial
   payoff.** Renaming `DataDisplays.Grid` to `DataDisplays.Table` would
   require: updating the coverage matrix (3 entries), the data-model
   (one heading + one contract line), the contract sketch
   (`contracts/DataDisplays.fsi:254`), the source file, the `.fsi`
   file, the implied tests file, and the `CoverageMatrixTests.fs`
   assertion-of-presence test (Phase 2 SC-001). That's 7+ files of
   churn for naming purity, and "Table" is *also* a name that already
   appears in `Composition.Table` (Phase 1, `Composition.fsi:49`) so we
   would not even *gain* full disambiguation.
4. **F# namespace ergonomics work correctly.** A contributor writing
   inside `Layout/Grid.fs` types `Grid.create` and the F# resolver
   picks `Fugue.Adapters.Console.Layout.Grid.create` because the
   `namespace Fugue.Adapters.Console.Layout` declaration scopes
   lookup. A contributor inside `DataDisplays.fs` similarly gets
   `DataDisplays.Grid.create`. Cross-namespace mistakes only happen if
   someone explicitly opens both namespaces — easily caught in code
   review.
5. **Future-proofing**. If Phase 4 introduces more clashes
   (`DataDisplays.Tree` vs `Layout.Tree`?), the same precedent
   applies: separate sub-namespaces under one fsproj. The cost stays
   constant per addition.

### Implementation surface

- **New folder**: `src/Fugue.Adapters.Console/Layout/`.
- **New files** (each `.fs` + `.fsi` pair):
  - `Layout/Common.fs` / `.fsi` — shared types: `Orientation`,
    `Alignment` (reuse Phase 1's), `Overflow`, `DockEdge`,
    `ColumnSize`, `RowSize`, `FlexItem`.
  - `Layout/Stack.fs` / `.fsi`
  - `Layout/Dock.fs` / `.fsi`
  - `Layout/Grid.fs` / `.fsi` — bare `Grid` inside the
    `Fugue.Adapters.Console.Layout` namespace.
  - `Layout/Flex.fs` / `.fsi`
  - `Layout/Composition.fs` / `.fsi` — augments
    `Composition` with `ofStack` / `ofDock` / `ofGrid` / `ofFlex`
    (namespaced `Fugue.Adapters.Console.Layout.Composition` to dodge
    the FS0250 conflict noted in `DataDisplays.fsi:14-20`).
  - `Layout/Scheduler.fs` / `.fsi` — per R-1.
- **`.fsproj` edit**: append the new files in dependency order
  *after* the existing Phase 2 files. F# project-file order is the
  enforcement mechanism (no cyclic refs).
- **Test files** mirror under `tests/Fugue.Adapters.Console.Tests/Layout/`.

### Trade-offs accepted

- **Existing fsproj grows.** Currently 17 source files; Phase 3 adds
  ~12 more. `Fugue.Adapters.Console.fsproj` becomes the largest single
  fsproj in the repo. Acceptable; F# compiler handles it, and the
  cohesion benefit (shared `RenderError`/`SafeText`/`Composition`)
  outweighs the file-count.
- **No independent version cadence.** Layout cannot be released without
  releasing DataDisplays. Acceptable; both ship in the JIT-only
  `Fugue.Cli` REPL, no NuGet publication planned.
- **Sub-namespace verbosity at call sites.** Callers may write
  `Layout.Stack.create` rather than `Stack.create`. Mitigated by
  conventional `open Fugue.Adapters.Console.Layout` at the top of
  layout-using files.

---

## R-4 — Unicode display-width helper

### Question

FR-014 mandates that layout calculations use *display columns*, not
byte length or UTF-16 code units, for East Asian Wide characters,
zero-width characters (combining marks, ZWJ), and emoji including ZWJ
sequences. SafeText today has no width function (probe:
`src/Fugue.Adapters.Console/SafeText.fs` exports only `ofUser`,
`ofLiteral`, `unwrap` — no `displayWidth`). Where does the width
calculation come from?

### Empirical probe

Reflection probe against `Spectre.Console 0.49.1`
(`dotnet fsi /tmp/width_probe4.fsx`, 2026-05-18):

| Input | Codepoints | UTF-16 length | Spectre `Cell.GetCellLength` | Correct? |
|-------|-----------:|--------------:|-----------------------------:|---------:|
| `"Hello"` | 5 | 5 | 5 | ✅ |
| `"日本語"` (CJK) | 3 | 3 | **6** | ✅ |
| `"é"` (pre-composed NFC) | 1 | 1 | 1 | ✅ |
| `"é"` (e + combining acute) | 2 | 2 | **1** | ✅ |
| `"🚀"` (single emoji) | 1 | 2 | **2** | ✅ |
| `"👨‍👩‍👧"` (ZWJ family) | 5 | 8 | **6** | ❌ (should be 2 — terminals collapse to one glyph) |

`Spectre.Console.Cell` is declared **internal** (probe confirms
`IsPublic=false` on `Spectre.Console.Cell`). The static method
`GetCellLength(string)` exists and is correct for 5 of 6 test cases —
the only failure mode is ZWJ-joined emoji clusters.

### Options considered

| Option | Approach | Cost |
|--------|----------|------|
| **A — call internal Spectre method via reflection** | Cache a `MethodInfo` in `SafeText.fs`, expose `SafeText.displayWidth : SafeText -> int`. | ~20 LOC + a unit test. Internal API → fragile across Spectre versions. |
| **B — add NuGet helper** | `Wcwidth.Net` or similar; ~50 KB package. | New dependency to vet; ZWJ-correctness varies by package. |
| **C — inline a small EastAsianWidth table** | Carry the Unicode `EastAsianWidth.txt`-derived ranges in F# code. | ~200 LOC + Unicode-update discipline. Most code; full control. |
| **D — defer; reuse Spectre's measurement transitively** | Layout primitives compute approximate widths for sizing pre-checks; the *real* width comes from Spectre during render. No new public API. | ~0 LOC. Accepts that ZWJ emoji in user content may misalign layout sizing by a few cells. |

### Decision

**Option D — defer; rely on Spectre's transitive width measurement,
open a follow-up issue for D→A upgrade if observed mis-sizing surfaces.**

### Rationale

1. **Whatever we compute for sizing must match what Spectre renders.**
   Spectre's renderer takes our `Composition` tree, computes its own
   widths internally (using its `Cell` helper), and emits ANSI. If our
   `SafeText.displayWidth` returns a value Spectre disagrees with, our
   layout decisions are wrong by Spectre's metric anyway — we would
   *cause* misalignment rather than prevent it.
2. **Sizing decisions in Phase 3 layout are mostly *not* width-of-text
   driven.** `Stack` distributes by child Composition (not by char
   width of leaf text). `Dock` uses requested child dimensions
   (passed by the caller, not computed from leaf text). `Grid`'s
   `Auto` column size walks Composition children and asks Spectre to
   measure (via `Renderable.Measure`) — that *uses Spectre's internal
   width function transitively*. `Flex` distributes by basis, grow,
   shrink — basis is caller-provided, not text-measured. The cases
   where our framework *itself* needs to compute width are limited to
   `SafeText` leaves whose intrinsic size matters; those go through
   Spectre's `Markup.Render` which uses the same `Cell` helper.
3. **ZWJ emoji is a content concern, not a layout concern.** The
   incorrect width happens for *one specific kind of user content*
   (ZWJ family/profession emoji) which is not currently expected in
   Fugue's REPL (tool labels are ASCII, diff markers are
   `+`/`-`/whitespace, status bar text is ASCII). When/if it appears,
   the symptom is "a row that contains a ZWJ emoji is 4 cells wider
   than the layout expected", visually equivalent to a non-issue for
   the streaming-markdown area and a minor cosmetic issue for the
   status bar.
4. **A future fix is purely additive.** When (or if) the ZWJ-correctness
   matters, options A and B are both implementable behind the same
   `SafeText.displayWidth` API surface — no caller change. We do not
   commit Phase 3 to either implementation now.

### Implementation surface

- **No new code in Phase 3 for R-4.**
- **Follow-up issue** opens against the repo when Phase 3 closes:
  *"Add `SafeText.displayWidth` once layout sizing hits an
  observed-misalignment ZWJ-emoji case (per research.md §R-4)"*.
- **Documented assumption in `data-model.md`** for Phase 1: layout
  width calculations defer to Spectre's measurement; layout primitives
  do not own width.

### Trade-offs accepted

- **ZWJ emoji in REPL content may misalign by up to 4 cells.** Out of
  scope for MVP per FR-014's "best-effort" reading (the FR uses
  "MUST handle", but Spectre — our actual renderer — *cannot* handle
  ZWJ sequences correctly today either, so we are passing the buck
  to Spectre, not falsely claiming we handle it).
- **A future Spectre upgrade could change `Cell.GetCellLength`
  behaviour silently.** Mitigated by a contract test that asserts
  representative widths against the live Spectre version
  (probe will become `tests/Fugue.Adapters.Console.Tests/UnicodeWidthContractTests.fs`).

---

## Best practices

### B-1 — Flexbox solver pseudocode in F#

Three reference implementations consulted: CSS Flexible Box Layout
spec (W3C), Yoga (C++ port used by React Native), and ratatui's
flex-style `Layout::flex` (Rust). All three share a three-phase
algorithm; the F# port is direct:

```fsharp
// items : FlexItem list  (each has basis: int, grow: float, shrink: float)
// axisLen : int  (total available cells along the main axis)
//
// Returns: int list of resolved sizes per item, summing to axisLen
//          (or ≤ axisLen if overflow=Hidden, with rest clipped).

let solveFlex (items: FlexItem list) (axisLen: int) : int list =
    // Phase 1: basis-resolve. Sum all basis values.
    let totalBasis = items |> List.sumBy (fun it -> it.Basis)
    let freeSpace  = axisLen - totalBasis

    if freeSpace = 0 then
        // Exact fit — basis is final.
        items |> List.map (fun it -> it.Basis)
    elif freeSpace > 0 then
        // Phase 2a: distribute free space by grow ratio.
        let totalGrow = items |> List.sumBy (fun it -> it.Grow)
        if totalGrow = 0.0 then
            // No grow — basis stands, free space is wasted (no flex).
            items |> List.map (fun it -> it.Basis)
        else
            items
            |> List.map (fun it ->
                let share = float freeSpace * (it.Grow / totalGrow)
                it.Basis + int (round share))
            // Distribute rounding residue to first item so sum is exact.
            |> distributeResidue axisLen
    else
        // Phase 2b: shrink. freeSpace < 0 — items overflow by |freeSpace|.
        let totalScaledShrink =
            items |> List.sumBy (fun it -> it.Shrink * float it.Basis)
        if totalScaledShrink = 0.0 then
            // No shrink — items keep basis, overflow per Overflow policy.
            items |> List.map (fun it -> it.Basis)
        else
            items
            |> List.map (fun it ->
                let scaledShrink = it.Shrink * float it.Basis
                let reduce =
                    float (-freeSpace) * (scaledShrink / totalScaledShrink)
                max 0 (it.Basis - int (round reduce)))
            |> distributeResidue axisLen
```

Notes:

- **Phase 1 (basis)** is the same in CSS and Yoga: each item starts at
  its declared `basis`. In our F# version, `Basis` is the only
  *required* sizing field; `Grow=0` and `Shrink=0` mean "fixed".
- **Phase 2a (grow)** is proportional. CSS, Yoga, ratatui all use the
  same formula. Rounding residue (sum of rounded ints ≠ `axisLen`) is
  handled by adding the leftover cells to the first item;
  alternatives (last item, largest item) are equivalent for our
  cell-resolution.
- **Phase 2b (shrink)** uses *basis-scaled* shrink in CSS (larger items
  shrink proportionally more — matches CSS spec §9.7.4). Yoga's port
  is identical. ratatui differs slightly (unscaled shrink); CSS-spec
  behaviour is preferred because it matches developer intuition from
  web frontends.
- **Overflow policy** sits outside the solver: if `freeSpace < 0` and
  no items shrink, the solver returns sizes summing > `axisLen`; the
  Layout primitive consults its `Overflow` field (Hidden / Strict /
  Wrap) to decide whether to clip, error, or wrap.

Estimate: ~50 LOC for the solver itself, plus ~30 LOC for
`distributeResidue` and overflow handling. Unit-testable with FsCheck
properties (e.g. *"sum of solved sizes = axisLen for any non-overflow
input"*).

### B-2 — SIGWINCH / terminal resize detection on .NET

Three approaches available on .NET 10:

| Approach | OS support | Latency | Notes |
|----------|-----------|---------|-------|
| **Polling `Console.WindowWidth/Height`** | All (mac/Linux/Windows) | One poll-interval (typ. 100-500 ms) | Already used in `RealExecutor.fs:43-44`. Cheap. |
| **POSIX `SIGWINCH` via P/Invoke** | mac/Linux only | Sub-ms | `System.Runtime.InteropServices.PosixSignalRegistration.Create(PosixSignal.SIGWINCH, handler)` — .NET 6+ has built-in support. No native P/Invoke needed. |
| **Windows `ReadConsoleInput` window-buffer-size event** | Windows only | Sub-ms | Requires `STD_INPUT_HANDLE` + `ReadConsoleInput` interop. |

**Recommendation**: hybrid — `PosixSignalRegistration` on Unix
(SC-007's 16 ms budget needs sub-frame detection), polling fallback
on Windows. Both feed the same `SchedulerMsg.Resize(w, h)` message
into R-1's scheduler. Existing `RealExecutor.Resize` message
(`RealExecutor.fs:17`) is the precedent — Phase 3 reuses the same
shape.

Code sketch (~20 LOC, single file `src/Fugue.Adapters.Console/Layout/Resize.fs`):

```fsharp
open System.Runtime.InteropServices

let registerResize (onResize: int * int -> unit) : System.IDisposable =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        // Polling fallback — 250 ms tick; sched fires a resize check.
        // Implementation: a Timer in scheduler module checks
        // Console.WindowWidth/Height and posts Resize on change.
        startPollingTimer onResize
    else
        PosixSignalRegistration.Create(PosixSignal.SIGWINCH, fun _ctx ->
            onResize (System.Console.WindowWidth, System.Console.WindowHeight))
        :> System.IDisposable
```

SC-007 (16 ms resize-to-next-render): met by SIGWINCH path; the
polling path needs the tick interval reduced if SC-007 needs to
pass on Windows CI.

### B-3 — FsCheck generators for well-formed Layout trees

Goal: property-test inputs that are *non-degenerate but bounded*
(avoid stack-overflow recursion at depth 10000; avoid trivial
single-leaf cases that prove nothing).

Pattern (Phase 2 already uses similar generators for `Composition`):

```fsharp
type LayoutGen() =
    static member Layout() : Arbitrary<Layout> =
        let leaf =
            gen {
                let! s = Arb.generate<string>
                return Layout.text (SafeText.ofUser s)
            }
        let rec layoutOfSize size =
            if size <= 1 then leaf
            else
                Gen.frequency [
                    1, leaf
                    3, gen {
                        let! n = Gen.choose (1, min 5 size)
                        let childSize = size / n
                        let! children =
                            Gen.listOfLength n (layoutOfSize childSize)
                        let! gap = Gen.choose (0, 3)
                        return Layout.stack Vertical gap children
                       }
                    2, gen { ... } // Dock, Grid, Flex similarly
                ]
            |> Gen.scaleSize (fun s -> min s 30)  // cap depth pressure
        layoutOfSize |> Gen.sized |> Arb.fromGen
```

Conventions inherited from Phase 2:

- **`Gen.scaleSize`** caps recursion depth at 30 (well under spec's
  depth-100 boundary so we don't trip the smart-constructor error
  path in happy-path property tests).
- **Separate `Arb` for boundary cases** — e.g. `LayoutAtDepthLimit`
  generator produces exactly-depth-100 trees for the error-path
  test.
- **Shrinker** uses the default structural shrinker on the underlying
  DU; FsCheck handles this automatically when the type derives
  comparison. (Layout is `[<NoComparison; NoEquality>]` per the
  `Composition` precedent, so a custom shrinker may be needed.
  Phase 1 design task to confirm.)

---

## Open follow-ups (post-Phase-3)

- **SF-1** — `SafeText.displayWidth` upgrade if ZWJ-emoji misalignment
  becomes observable in REPL content. Track in `gh issue` against
  `korat-ai/fugue` once Phase 3 closes.
- **SF-2** — Surface deprecation. After Layout has handled the
  streaming-markdown surface for a release cycle without regressions,
  evaluate moving status-bar + input region onto Layout
  (`Fugue.Surface` deletion candidate). Phase 4 candidate.
- **SF-3** — ANSI-diff backend (R-1 option C) if terminal-IO saturation
  surfaces in profiling. Behind the same public API; no caller change.
- **SF-4** — Layout layout-debug overlay (toggle key in REPL renders
  Layout regions as numbered rectangles). Diagnostic; not a blocker.

---

## UX Report — fslangmcp

Brief feedback on the F# language MCP for this research task:

- **Queries used**: `mcp__fslangmcp__set_project` (1 call to load
  `Fugue.Adapters.Console.fsproj` — succeeded immediately, workspace ready
  in < 1 s). No other fslangmcp calls were made — the task was
  predominantly markdown writing, FR-spec cross-referencing, and
  empirical reflection probes (`dotnet fsi`).
- **Friction / missing for issue #100**: For this kind of research-phase
  task (write a markdown deliverable summarising decisions across
  existing source), fslangmcp is in the wrong layer — the source is read
  for *context*, not queried for *semantic structure*. I needed file
  contents at known paths (`Read`), call-site greps for naming-conflict
  scoping (`rg`), and runtime reflection (`dotnet fsi`) — none of which
  fslangmcp serves naturally. This matches the user's "boundary
  heuristic" in `~/.claude/CLAUDE.md` (markdown / fsproj / unfiled `.fsi`
  in `specs/.../contracts/` → `rg`/`Read`/`dotnet fsi`).
- **`rg` fallback on `.fs` files**: I used `rg` to find Phase 2 `Grid`
  call-sites across the repo. This was a *literal name search* across
  `.fs`, `.fsi`, `.md`, and `.fsproj` simultaneously to scope a naming
  decision — semantically I wanted "all files mentioning the identifier
  `Grid` in any context, including comments and docs", which is a `rg`
  question, not a semantic-find question. fslangmcp's
  `textDocument_references` would have given me *only* F# binding
  references, missing the spec doc references that drive the decision.
  This is a *correct* `rg` fallback per the boundary heuristic; not a
  fslangmcp shortfall.
- **Surprises**: (a) `Spectre.Console.Cell` is declared *internal*
  (I assumed public, given how central width-calc is). The reflection
  probe surfaced this in ~30 s, but a fslangmcp surface that includes
  *NuGet-loaded* symbols (not just source symbols) would have caught it
  faster. (b) `Fugue.Cli.Surface` is a separate module from
  `Fugue.Surface` — naming overlap that took an extra `find` to
  disambiguate. (c) `RealExecutor`'s `Console.Out` module-load capture
  trick is load-bearing for the entire surface design (R-2) and is
  documented only inline; would benefit from a top-level
  `src/Fugue.Surface/README.md`.
- **`fcs_project_outline` would have helped**: for the audit step in R-2
  (what does Surface actually do?) I read four files sequentially.
  A summary outline via `fcs_project_outline` on `Fugue.Surface.fsproj`
  would have collapsed that to one call. Did not invoke because the
  audit ran in parallel with R-1 work and was small enough; would have
  been the right tool on a larger codebase.

**No bugs to report against fslangmcp #100 from this run.** The tool
boundary held: this was a documentation-heavy research task where text
search + reflection were the right shapes, and fslangmcp correctly
stayed out of the way.
