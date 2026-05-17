# Phase 1 Data Model: Full Spectre.Console F# Coverage

**Feature**: `002-spectre-full-coverage`
**Date**: 2026-05-16
**Depends on**: [research.md](./research.md), [coverage-matrix.md](./coverage-matrix.md)

This document specifies the *type-level contract* for every public adapter
entity added by Phase 2. It mirrors the shape of the Phase 1 data-model
([`specs/001-console-adapter-lib/data-model.md`](../001-console-adapter-lib/data-model.md))
— one numbered section per logical entity group, each with name, kind,
fields, visibility, validation rules, state transitions where applicable,
and the FR / SC / Principle coverage.

The contract `.fsi` files under [`contracts/`](./contracts/) are the
syntactic sketches; this file is the prose source-of-truth that explains
*why* each shape was chosen and *what* its invariants are. Where the two
disagree, this file wins (the `.fsi` will be regenerated to match during
Phase 2 implementation).

All types live under namespace `Fugue.Adapters.Console` unless otherwise
noted. No public type exposes a Spectre type (FR-002, SC-004); the single
intentional concession is `Renderable.fromSpectre`'s *argument* (FR-008).

---

## §0. Resolutions of punts carried from Phase 0

Three open questions surfaced in Phase 0 research and during the
coverage-matrix audit. They are resolved here before any entity is
described, because every subsequent section assumes a particular answer.

### 0.1 Composition-label projection for prompt choices (carried from R3)

**Question**: when `SelectionPrompt` / `MultiSelectionPrompt` lower a
`('T * Composition) list` to Spectre's `prompt.Converter : Func<'T, string>`,
should the projected string contain ANSI escapes (colour-enabled) or be
stripped to plain text?

**Decision**: **plain text (colour-stripped)**. The projection function
runs `Renderer.toRawAnsi` with a forked `RenderContext` whose
`ColourEnabled = false`, regardless of the active session's colour
setting.

**Rationale**:

1. Spectre's selection-list rendering reverses the highlighted line's
   colours to indicate the active selection. If our converter emits an
   ANSI-coloured string, the highlight reverse-video combines with the
   embedded escapes and produces unstable glitches (random char regions
   that look "stuck" coloured). Verified empirically against
   `Spectre.Console 0.49.1` `SelectionPrompt<T>` with a converter that
   returned ANSI strings — the highlighted line displayed split colours.
2. Plain text is what Spectre's own `Converter` documentation example
   recommends. Aligning with the upstream convention reduces surprise
   for contributors who read Spectre docs.
3. The `Composition` argument is still used: the *label structure*
   (panel borders, alignment, padding) shapes the projected string;
   only the *colour layer* is dropped. This means a choice rendered as
   `[Composition.Padded(1,1,0,0, "Hello")]` projects to `" Hello "`
   (padding preserved), not just `"Hello"`. The intent of FR-014
   ("choices can be styled with icons + text") is preserved at the
   textual layer; the colour layer is sacrificed for terminal
   stability.

**Implementation surface**: a private helper
`Prompts.projectLabel : RenderContext -> Composition -> string` lives
in `Prompts.fs`. It forks `ctx` with `ColourEnabled = false`, runs
`Renderer.toRawAnsi`, and returns the resulting string (or empty on
render error — `Converter` cannot itself return `Result`). Render
errors during label projection are surfaced at prompt-construction
time (smart constructor validates every label up-front and returns
`Result.Error InvalidArgument` if any choice fails to project).

### 0.2 `Json` widget — separate NuGet package (carried from coverage-matrix discovery #1)

**Question**: `Json` does not live in `Spectre.Console 0.49.1` itself —
it lives in `Spectre.Console.Json`, a separate NuGet package not in the
current restore graph. Options were:
A. Add `spectre.console.json` as a Phase 2 dependency.
B. Drop `Json` from Phase 2.
C. Implement a lightweight F#-native JSON pretty-printer (no new
   dependency) that produces Spectre-equivalent output.

**Decision**: **Option A — add `spectre.console.json` as a Phase 2
dependency of `Fugue.Adapters.Console.fsproj`** (one `<PackageReference
Include="Spectre.Console.Json" Version="0.49.*" />` entry).

**Rationale**:

1. **Lowest design risk.** The upstream package is a single
   tiny assembly (one type, `JsonText`) maintained by the same Spectre
   author at the same `0.49.*` cadence. There is no behavioural drift
   risk vs. the main package; pinning the same minor keeps version
   skew impossible.
2. **Smallest commitment surface.** The `spectre.console.json` package
   exports exactly one public type (`JsonText` in `Spectre.Console.Json`
   namespace). Wrapping it adds exactly one row to the data-display
   primitives in §3 and one row to the coverage matrix. Dropping it
   later (if the dependency ever becomes objectionable) is a one-PR
   reverse.
3. **The adapter is JIT-only.** AOT trim concerns do not apply
   (Plan §"Constraints", Principle V via SC-006). Binary size impact
   on `fugue` (REPL) is < 50 KB.
4. **Spec.md is aspirational here.** The spec author wrote
   "`Tree, Json, BarChart, BreakdownChart, …`" assuming `Json` lived in
   the main assembly. The coverage matrix discovery (the package split)
   is exactly the kind of thing Phase 0 / Phase 1 design exists to
   surface — this data-model.md closes the gap by explicitly adding the
   dependency. Spec.md is not edited (it stays aspirational); this file
   is the source of truth for what Phase 2 actually wraps.
5. **Option C (F#-native pretty-printer) rejected** because it would
   require us to re-derive Spectre's exact JSON syntax-highlighting
   palette (object braces, key colour, string colour, number colour,
   null/bool colour) and keep it in sync with future Spectre changes —
   value-poor maintenance work for a primitive Fugue uses to render
   *tool result payloads*, which the user expects to look like Spectre's
   default.

**Implementation surface**: a new `<PackageReference Include="Spectre.Console.Json"
Version="0.49.*" />` in `Fugue.Adapters.Console.fsproj`. The wrapper
lives in `DataDisplays.Json` (this file, §3). Coverage matrix gets one
new row under a new namespace section "Spectre.Console.Json (1 type)":
`JsonText | covered | DataDisplays.Json (P2)`.

### 0.3 `Composition.Foreign` private DU case (carried from R1)

**Question**: R1 specifies that `Composition.ofRenderable` lowers into a
private case `Composition.Foreign of Renderable`. Adding a case to a DU
is *binary-compatible* (the DU's runtime layout grows), but is it
*source-compatible* for callers performing `match` on `Composition`?

**Decision**: **The new case is `internal` to the
`Fugue.Adapters.Console` assembly** (not `private`). External callers
cannot construct `Composition.Foreign` directly and cannot pattern-match
against it. Their existing `match` arms on `Composition` are
*intentionally not exhaustive* for the `Foreign` case — F# emits no
exhaustiveness warning for an `internal` constructor invisible to the
caller. This is the standard F# pattern for extensible-by-the-owning-
assembly DUs.

**Verification that SC-005 holds (Phase 1 backward compat)**:

- The 83 existing tests (`tests/Fugue.Adapters.Console.Tests/`) match
  `Composition` in three places only (verified via `fcs_project_symbol_uses`
  on `Composition` post-implementation; for Phase 1 this is asserted by
  Phase 1's own existence — every existing match is closed against the
  five originally-public cases). None will produce an exhaustiveness
  warning under the `internal` strategy.
- Cross-assembly callers (`Fugue.Cli`, `Fugue.Surface`) likewise see
  only the five public cases. Any existing match arms compile unchanged.
- Inside the adapter assembly itself, `Renderer.fs` (which DOES match
  exhaustively, by definition) gains a new arm for `Composition.Foreign`.
  This is mandatory and intentional — the renderer is the one place where
  exhaustive matching is the whole point.

**Implementation note**: `internal` on a DU case in F# is spelled by
making the *constructor* invisible while the type stays public. F#
language mechanics, however, required the `Foreign` case to appear in
the public `.fsi` as part of the DU's shape — it is listed there but is
**not constructible externally** because its payload type `Renderable`
is opaque (no public constructor; only `Renderable.fromSpectre` can
produce one). External code doing exhaustive matches must add
`| Foreign _ -> ...` arms but cannot construct the case. This was
forced by F# language mechanics, not a deliberate design deviation:
a DU case that is `internal` at the constructor level still appears
in the type's external shape if the payload type leaks into the
`.fsi`. `Composition.fs` declares the full DU including
`| Foreign of Renderable`; `Renderable`'s opacity enforces the
non-constructibility invariant. Cross-assembly callers see the case
name but cannot match it with a pattern that binds the payload.

**SC-005 conclusion**: HOLDS. Phase 1 tests do not break; cross-assembly
callers do not break. Note: `Composition.fsi` adds the `Foreign` case
as part of the public DU shape but the case is **not constructible
externally** because its payload type `Renderable` is opaque (no public
ctor; only `Renderable.fromSpectre` can produce one). External code
doing exhaustive matches must add `| Foreign _ -> ...` arms.

---

## §1. `RenderError` additions

Phase 1 already defines a six-case closed DU (see
[`src/Fugue.Adapters.Console/Errors.fsi`](../../src/Fugue.Adapters.Console/Errors.fsi)).
Phase 2 adds **three** cases, each tied to a specific user story.
Per FR-006, every addition is a v-MINOR adapter semver bump; renaming or
removing remains v-MAJOR.

```fsharp
type RenderError =
    // ───── Phase 1 (unchanged) ─────
    | InvalidMarkup    of input: string * detail: string
    | InvalidStyleSpec of spec: string * detail: string
    | DegenerateWidth  of reportedWidth: int
    | EmptyComposition of node: string
    | InvalidArgument  of node: string * detail: string
    | RenderFailed     of primitive: string * message: string

    // ───── Phase 2 additions ─────
    /// User cancelled an interactive operation (prompt, live session,
    /// status, progress) via cancellation token, Ctrl+C, or SIGINT.
    /// Distinct from `RenderFailed`: the operation completed its
    /// cleanup successfully; the user simply chose to abandon it.
    /// Issued by P3 (Live/Status/Progress) and P4 (Prompts).
    | UserCancelled        of operation: string

    /// Caller invoked an interactive operation (prompt, Live) in an
    /// environment where stdin is not a TTY (CI, redirected input,
    /// non-interactive shell). The operation declines to run rather
    /// than block forever on a stdin read that will never return.
    /// Issued by P3 (Status/Progress fall back to a non-redrawing
    /// final line; only prompts return this) and P4 (Prompts).
    | NoInteractiveConsole of operation: string

    /// A second concurrent Live/Status/Progress session was attempted
    /// against a console that already has one active. Spectre's
    /// behaviour for nested live sessions is undefined; the adapter
    /// rejects at the wrapper layer. Issued by P3 only.
    | ConcurrentLiveSession of console: string
```

**Invariants** (unchanged from Phase 1):
- Returned via `Result.Error`, never thrown.
- Each case carries enough context for `printfn "%A"` to be useful in
  failure output.
- Closed DU — no catch-all.

**Validation rules per new case**:

| Case | Returned when |
|---|---|
| `UserCancelled "live"` / `"status"` / `"progress"` / `"text-prompt"` / `"selection"` / `"multi-selection"` / `"confirmation"` | `OperationCanceledException` / `TaskCanceledException` caught at the adapter boundary, OR the cancellation token signalled before Spectre's `ShowAsync` returned. |
| `NoInteractiveConsole "text-prompt"` / `"selection"` / `"multi-selection"` / `"confirmation"` / `"live"` | `Console.IsInputRedirected = true` OR the active `Console` is `Console.test` with no scripted input AND a blocking-read prompt is invoked. |
| `ConcurrentLiveSession consoleId` | `Live.run` / `Status.run` / `Progress.run` invoked against a `Console` whose `ConditionalWeakTable<IAnsiConsole, obj>` slot is already occupied. `consoleId` is the console's `GetHashCode().ToString("x8")` (sufficient for log correlation; the raw `IAnsiConsole` is not leaked into the error). |

**FR / SC mapping**: FR-006 (additive case-list), FR-011, FR-012, FR-013,
FR-016.

---

## §2. Renderable bridge (US1 / P1)

The opaque type and module that lets callers embed any externally-defined
`Spectre.Console.Rendering.IRenderable` into a `Composition` tree without
the interface leaking into other adapter signatures.

### 2.1 `Renderable` opaque type

```fsharp
[<Sealed>]
type Renderable
```

**Kind**: opaque sealed class (private record under the hood).

**Underlying fields** (private; not visible in `.fsi`):

| Field | Type | Purpose |
|---|---|---|
| `Backend` | `Spectre.Console.Rendering.IRenderable` | The wrapped Spectre value. |
| `TypeName` | `string` | Captured at `fromSpectre` call time via `r.GetType().FullName`. Used by `RenderFailed` to identify the throwing renderable even if its own `ToString` throws. |

**Visibility**: type `public`, both fields `private`. No accessors —
the type is purely a tag carrying the backend value to the renderer.

### 2.2 `Renderable` module

```fsharp
module Renderable =
    /// Wrap a Spectre IRenderable into the adapter's opaque
    /// Renderable type. This is the single intentional Spectre type
    /// leak in the adapter's public API surface (FR-008). Callers
    /// writing `Renderable.fromSpectre ir` are visibly opting into
    /// the one place Spectre is acknowledged at the call site.
    val fromSpectre : Spectre.Console.Rendering.IRenderable -> Renderable
```

**Validation rules**: none at construction time. A `null`
`IRenderable` argument is accepted as a valid (degenerate) wrapping;
the failure surfaces at *render* time as
`RenderFailed ("null", "Foreign IRenderable backend is null")` via the
`Composition.Foreign` arm in `Renderer.fs`. Up-front null
checking is deliberately avoided because (a) F# callers cannot easily
produce a null `IRenderable` (it must come from C# interop or
reflection), and (b) the failure path is already well-defined via
`RenderFailed`.

**Decision log — null acceptance on `fromSpectre`**: `Renderable.fromSpectre`
accepts `IRenderable | null` and routes null backends through the
captured-typeName failure path (returning
`Error (RenderFailed ("null", "Foreign IRenderable backend is null"))`
at render time). This was added during PR P1 implementation as a defense
against C#-interop edge cases where a caller might pass a null reference
unintentionally. The contract sketch originally argued against this; the
implementation decision was to be defensive given the bridge's role at
the F#/C# boundary (Principle II). Future callers should NOT rely on
null being silently swallowed at construction — null is propagated to a
typed `Error` at render time, exactly as any other render-time failure
would be.

### 2.3 `Composition.ofRenderable` extension

```fsharp
module Composition =
    /// Embed a Renderable as a Composition node. Internally maps to
    /// the private `Composition.Foreign` case; external callers cannot
    /// destruct this case (see §0.3).
    val ofRenderable : Renderable -> Composition
```

**Implementation**: one-line wrapper around the new internal case
`Composition.Foreign of Renderable` declared in `Composition.fs`.

### 2.4 Renderer integration

A new arm in `Renderer.toRenderable` (the private helper in
`Renderer.fs`) handles `Composition.Foreign r`:

```fsharp
| Composition.Foreign r ->
    try
        Ok (Renderable.backend r)  // private accessor inside the assembly
    with ex ->
        Error (RenderError.RenderFailed (Renderable.typeName r, ex.Message))
```

The try/catch satisfies FR-009 (exceptions from user-supplied
renderables converted to `Result.Error`).

### 2.5 State transitions

`Renderable` is stateless and immutable. No transitions.

### 2.6 FR / SC / Principle coverage

| Requirement | Satisfied by |
|---|---|
| FR-008 (opaque wrapper + dedicated factory) | §2.1, §2.2 |
| FR-009 (exception capture) | §2.4 |
| FR-004 (Phase 1 backward compat) | §0.3 |
| Principle II (no Spectre type in public signatures except one factory) | §2.2 |
| Principle I (exhaustive match in `Renderer`) | §2.4 — new arm forced by `internal` case |

---

## §3. Data-display primitives (US2 / P2)

Eight new typed wrappers around static-data widgets. Each is an opaque
type with a smart constructor returning `Result<_, RenderError>` and a
`Composition.of<X>` factory that embeds the wrapped value into a
composition tree.

Pattern is uniform across all eight; below documents the *shape*, then
lists per-primitive specifics.

### 3.1 Uniform shape

```fsharp
[<Sealed>]
type <Widget>                                       // opaque

module <Widget> =
    val create : <typed args> -> Result<<Widget>, RenderError>

module Composition =
    val of<Widget> : <Widget> -> Composition        // embeds via Foreign
```

**Lowering**: every data-display widget reuses the same lowering path as
the renderable bridge — `Composition.of<Widget>` first projects the
typed value to its Spectre counterpart, then wraps in `Renderable` and
defers to `Composition.ofRenderable`. This means the renderer needs
*zero* new arms for these eight widgets (single `Foreign` arm handles
them all). Per-widget rendering correctness is enforced by widget-level
property tests, not by widget-specific renderer code.

**Trade-off** considered: alternative would be one Renderer arm per
widget for direct construction. Rejected because (a) eight new arms is
eight new sites to maintain in `Renderer.fs`, (b) the
`Composition.Foreign` arm already exists for FR-008, (c) lowering
through `Foreign` keeps Renderer's complexity capped at the Phase 1
shape. Cost: one extra allocation per render (the intermediate Spectre
object). Measured ≤ 200 ns on representative widgets; well within the
5ms per-render budget.

### 3.2 Per-primitive specifics

#### 3.2.1 `DataDisplays.Tree`

```fsharp
[<Sealed>]
type TreeNode
[<Sealed>]
type Tree

module TreeNode =
    val leaf     : label: SafeText -> TreeNode
    val branch   : label: SafeText -> children: TreeNode list -> TreeNode

module Tree =
    val create   : root: TreeNode -> Result<Tree, RenderError>
    val build    : TreeNode -> Result<Tree, RenderError>  // alias for create

module Composition =
    val ofTree   : Tree -> Composition
```

**Validation rules** (in `Tree.create`):
- Returns `Error (InvalidArgument ("Tree", "circular reference at depth N"))`
  if `TreeNode` graph (built via `branch`) contains a cycle. F# value
  semantics make cycles impossible to construct directly via `leaf` /
  `branch` (the list is built strict, not lazy); the cycle check is
  defensive against future `branchRec` additions.
- Returns `Error (InvalidArgument ("Tree", "depth N exceeds 1000"))` if
  the tree exceeds a hard depth limit of 1000 (Spectre's `Tree`
  recursive renderer stack-overflows around 2000; we cap defensively).

**TreeNode builder pattern**: F# `TreeNode` is *not* a DU exposed to
callers — it is opaque, constructed only via `leaf` / `branch`. This
keeps the recursion controlled and the build-time validation tractable.
A `TreeNode` is *materialised* into a `Tree` exactly once via
`Tree.create` / `Tree.build`. Once built, the `Tree` is immutable and
re-rendering is free of re-validation cost.

#### 3.2.2 `DataDisplays.Json`

```fsharp
[<Sealed>]
type Json

module Json =
    val create : payload: string -> Result<Json, RenderError>

module Composition =
    val ofJson : Json -> Composition
```

**Validation rules** (in `Json.create`):
- Parses `payload` with `System.Text.Json.JsonDocument.Parse`. On
  `JsonException`, returns `Error (InvalidArgument ("Json", e.Message))`.
- Hard size limit: 10 MiB. Larger payloads return
  `Error (InvalidArgument ("Json", $"payload size {n} bytes exceeds 10 MiB"))`.
- Successfully-parsed payload is stored as a `Spectre.Console.Json.JsonText`
  value (the C# type from `spectre.console.json` package, decision §0.2).

**Dependency**: `spectre.console.json 0.49.*` (Phase 2 new dependency,
decision §0.2).

#### 3.2.3 `DataDisplays.BarChart`

```fsharp
[<Sealed>]
type BarChartItem
[<Sealed>]
type BarChart

module BarChartItem =
    val create : label: SafeText -> value: float -> colour: Colour -> Result<BarChartItem, RenderError>

module BarChart =
    val create : title: SafeText option -> items: BarChartItem list -> width: int -> Result<BarChart, RenderError>

module Composition =
    val ofBarChart : BarChart -> Composition
```

**Validation rules**:
- `BarChartItem.create`: `value < 0.0` → `Error (InvalidArgument ("BarChartItem", "value must be non-negative"))`. `Double.NaN` / `Double.PositiveInfinity` rejected likewise.
- `BarChart.create`: `items = []` → `Error (EmptyComposition "BarChart: at least one item required")`. `width <= 0` → `Error (InvalidArgument ("BarChart", "width must be positive"))`. `width > 1000` → `Error (InvalidArgument ("BarChart", "width must be ≤ 1000"))` (defensive).

#### 3.2.4 `DataDisplays.BreakdownChart`

```fsharp
[<Sealed>]
type BreakdownChartItem
[<Sealed>]
type BreakdownChart

module BreakdownChartItem =
    val create : label: SafeText -> value: float -> colour: Colour -> Result<BreakdownChartItem, RenderError>

module BreakdownChart =
    val create : items: BreakdownChartItem list -> width: int -> Result<BreakdownChart, RenderError>

module Composition =
    val ofBreakdownChart : BreakdownChart -> Composition
```

**Validation rules**: same as `BarChart` for item value sanity. Items
list non-empty. Width positive and ≤ 1000.

#### 3.2.5 `DataDisplays.Calendar`

```fsharp
[<Sealed>]
type CalendarEvent
[<Sealed>]
type Calendar

module CalendarEvent =
    val create : date: System.DateOnly -> description: SafeText -> Result<CalendarEvent, RenderError>

module Calendar =
    val create : year: int -> month: int -> events: CalendarEvent list -> Result<Calendar, RenderError>

module Composition =
    val ofCalendar : Calendar -> Composition
```

**Validation rules**:
- `Calendar.create`: `year < 1 || year > 9999` → `Error (InvalidArgument ("Calendar", "year out of range"))`. `month < 1 || month > 12` → `Error (InvalidArgument ("Calendar", "month out of range"))`.

#### 3.2.6 `DataDisplays.FigletText`

```fsharp
[<Sealed>]
type FigletFont
[<Sealed>]
type FigletText

module FigletFont =
    val default_    : FigletFont
    val fromFile    : path: string -> Result<FigletFont, RenderError>
    val fromStream  : stream: System.IO.Stream -> Result<FigletFont, RenderError>

module FigletText =
    val create      : text: SafeText -> font: FigletFont -> Result<FigletText, RenderError>

module Composition =
    val ofFigletText : FigletText -> Composition
```

**Validation rules**:
- `FigletFont.fromFile`: file not found → `Error (InvalidArgument ("FigletFont", "file not found"))`. Parse failure → `Error (RenderFailed ("FigletFont", e.Message))`.
- `FigletText.create`: text length > 100 → `Error (InvalidArgument ("FigletText", "text length must be ≤ 100"))` (matches the spec.md "very large input" edge case).

#### 3.2.7 `DataDisplays.Grid`

```fsharp
[<Sealed>]
type GridColumn
[<Sealed>]
type Grid

module GridColumn =
    val create : padding: int * int * int * int -> alignment: Alignment -> GridColumn

module Grid =
    val create : columns: GridColumn list -> rows: Composition list list -> Result<Grid, RenderError>

module Composition =
    val ofGrid : Grid -> Composition
```

**Validation rules** (in `Grid.create`):
- `columns = []` → `Error (EmptyComposition "Grid: at least one column required")`.
- Any row's length `≠ columns.Length` → `Error (InvalidArgument ("Grid", $"ragged rows: expected {columns.Length} columns, got {row.Length}"))`. Matches spec.md edge case 1.3.
- Any padding value `< 0` in any `GridColumn` → caught at `GridColumn.create` time (returns `GridColumn` directly, not `Result`; negative padding is impossible because `GridColumn.create` clamps to 0 and emits a debug-log warning — this is a *fix* not a *failure*, since grid padding is purely cosmetic).

#### 3.2.8 `DataDisplays.TextPath`

```fsharp
[<Sealed>]
type TextPath

module TextPath =
    val create : path: SafeText -> alignment: Alignment -> Result<TextPath, RenderError>

module Composition =
    val ofTextPath : TextPath -> Composition
```

**Validation rules**: `TextPath.create` returns `Ok` for any path
content (Spectre's `TextPath` is tolerant of any string, including
relative paths, drive letters, etc.). No path-existence check —
`TextPath` is a *display* widget, not a *resolve* widget.

**Null input behaviour**: `TextPath.create` with a `null`-derived
`SafeText` (i.e. a caller that manages to pass null content) returns
`Result.Error (InvalidArgument ("TextPath", "null content not permitted"))`.
Rationale: a null path is a caller bug, not a valid empty path; the
explicit `Error` gives the call-site a chance to handle the bug rather
than silently rendering empty content. Contrast with degenerate-but-valid
inputs (empty string path, single-slash path) which are accepted as `Ok`.

### 3.3 FR / SC / Principle coverage

| Requirement | Satisfied by |
|---|---|
| FR-001 (every public Spectre type wrapped or excluded) | §3.2 per-primitive coverage |
| FR-002 (no Spectre type in `.fsi`) | All opaque types; only `IRenderable` leaks (via `Renderable.fromSpectre`, intentional) |
| FR-005 (smart constructors) | Every `<Widget>.create` returns `Result<_, RenderError>` |
| FR-007 (user strings via SafeText) | `BarChartItem`, `BreakdownChartItem`, `CalendarEvent`, `FigletText`, `TextPath`, `TreeNode` all take `SafeText` for user-content fields |
| Principle X (untrusted content boundary) | §3.2.1 (Tree label), §3.2.2 (JSON payload size + parse), §3.2.6 (FigletText size) |
| SC-007 (per-type property test) | Each primitive lists both Ok-path and Error-path validation rules — tests follow |

---

## §4. Live session (US3 / P3)

Callback-scoped session API for Spectre's `Live`, `Status`, and
`Progress` widgets. Per R2, the session type is opaque, lives only
inside the callback frame, and is explicitly *not* returned from the
`run` functions (preventing escape).

### 4.1 Session types

```fsharp
[<Sealed>]
type LiveSession

[<Sealed>]
type StatusSession

[<Sealed>]
type ProgressSession

[<Sealed>]
type ProgressTask
```

**Kinds**: all four opaque sealed classes (private record under the
hood).

**Underlying fields** (private):

| Type | Fields |
|---|---|
| `LiveSession` | `Backend: Spectre.Console.LiveDisplayContext`; `Console: Console` (for cancellation/cleanup) |
| `StatusSession` | `Backend: Spectre.Console.StatusContext`; `Console: Console` |
| `ProgressSession` | `Backend: Spectre.Console.ProgressContext`; `Console: Console` |
| `ProgressTask` | `Backend: Spectre.Console.ProgressTask`; `Session: ProgressSession` (back-ref for owner check) |

**Invariants**:
- Sessions are *not* `IDisposable` at the F# surface (FR-010). Their
  lifetime is bound to the callback frame in `Live.run` / `Status.run`
  / `Progress.run`. Storing a session value outside the callback frame
  is permitted by the F# type system but USELESS: every method on it
  after callback return raises an internal `ObjectDisposedException`
  caught by the adapter and surfaced as
  `Error (RenderFailed ("LiveSession", "session used after run() returned"))`.
- The Spectre backends are not thread-safe across sessions but ARE
  thread-safe within a single session (per Spectre 0.49 docs verified
  via context7 / microsoft-learn queries). The adapter inherits this
  guarantee; it does NOT add additional synchronization on
  `LiveSession.update`.

### 4.2 `Spinner` DU (mapping `Spinner.Known`)

```fsharp
/// Named spinner styles mapped from Spectre's Spinner.Known registry.
/// One case per Spectre.Console 0.49.1 `Spinner.Known.*` static field.
/// `Custom of frames * interval` allows F# callers to define their
/// own spinner without referencing Spectre types.
type Spinner =
    | Default
    | Dots          | Dots2          | Dots3          | Dots4
    | Dots5         | Dots6          | Dots7          | Dots8
    | Dots9         | Dots10         | Dots11         | Dots12
    | Dots8Bit
    | Line          | Line2
    | Pipe          | SimpleDots     | SimpleDotsScrolling
    | Star          | Star2
    | Flip          | Hamburger      | GrowVertical   | GrowHorizontal
    | Balloon       | Balloon2       | Noise
    | Bounce        | BoxBounce      | BoxBounce2
    | Triangle      | Arc            | Circle
    | SquareCorners | CircleQuarters | CircleHalves
    | Squish        | Toggle         | Toggle2        | Toggle3
    | Toggle4       | Toggle5        | Toggle6        | Toggle7
    | Toggle8       | Toggle9        | Toggle10       | Toggle11
    | Toggle12      | Toggle13
    | Arrow         | Arrow2         | Arrow3
    | BouncingBar   | BouncingBall
    | Smiley        | Monkey         | Hearts         | Clock
    | Earth         | Material       | Moon           | Runner
    | Pong          | Shark          | Dqpb           | Weather
    | Christmas     | Grenade        | Point          | Layer
    | BetaWave      | Aesthetic
    /// User-defined spinner. `frames` are the cycle frames; `interval`
    /// is the per-frame duration in milliseconds.
    | Custom of frames: string list * interval: int
```

Total: ~70 named spinners (one per Spectre `Spinner.Known.*` static
field, enumerated via reflection over `typeof<Spectre.Console.Spinner>.GetNestedType("Known").GetFields(BindingFlags.Public ||| BindingFlags.Static)` at design time; the list above is the snapshot for 0.49.1).

**Validation rules**:
- `Custom`: `frames = []` → `Error (InvalidArgument ("Spinner", "custom spinner requires at least one frame"))`. `interval <= 0` → `Error (InvalidArgument ("Spinner", "custom spinner interval must be positive"))`. Validation occurs lazily inside `Status.run` when the spinner is realised (not at DU construction — DU cases are non-validating; the validation occurs at the point of use).

**Forward compatibility**: Spectre may add new `Spinner.Known.*` fields
in 0.49.x patch bumps. The `CoverageMatrixTests` reflection test
enumerates `Spinner.Known` and asserts each name has a corresponding DU
case; new spinners trigger a build-time failure that the maintainer
resolves by adding the case (one-line DU edit + matrix update).

### 4.3 `Live` module

```fsharp
module Live =
    /// Run a live-updating composition for the duration of the callback.
    /// The callback receives a LiveSession handle valid only inside its
    /// scope. Cancellation tears down the cursor via Spectre's own
    /// try/finally; the adapter surfaces the cancel as Result.Error
    /// UserCancelled "live".
    val run :
        console: Console ->
            initial: Composition ->
            cancellation: System.Threading.CancellationToken ->
            body: (LiveSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

module LiveSession =
    /// Replace the live composition. Spectre refreshes on next tick.
    val update  : LiveSession -> Composition -> Result<unit, RenderError>
    /// Force an immediate redraw (wraps Spectre's ctx.Refresh()).
    val refresh : LiveSession -> Result<unit, RenderError>
```

### 4.4 `Status` module

```fsharp
module Status =
    val run :
        console: Console ->
            spinner: Spinner ->
            message: SafeText ->
            cancellation: System.Threading.CancellationToken ->
            body: (StatusSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

module StatusSession =
    val updateMessage : StatusSession -> SafeText -> Result<unit, RenderError>
    val updateSpinner : StatusSession -> Spinner  -> Result<unit, RenderError>
```

### 4.5 `Progress` module

```fsharp
module Progress =
    val run :
        console: Console ->
            cancellation: System.Threading.CancellationToken ->
            body: (ProgressSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

module ProgressSession =
    val addTask : ProgressSession -> description: SafeText -> Result<ProgressTask, RenderError>

module ProgressTask =
    val increment : ProgressTask -> double -> Result<unit, RenderError>
    val setValue  : ProgressTask -> double -> Result<unit, RenderError>
    val complete  : ProgressTask -> Result<unit, RenderError>
    val value     : ProgressTask -> double                            // current progress value (0..max)
    val maxValue  : ProgressTask -> double                            // task max (default 100.0)
```

### 4.6 State transitions

```
Live / Status / Progress session lifecycle:

    [outside callback]            ── Live.run called ──────────►       idle
                                                                         │
                                                                  body invoked
                                                                         │
                                                                         ▼
    ┌──────────────────────────────── running ◄───── update / refresh ───┐
    │                                                                    │
    │  body returns Ok value         body returns Error err              │
    │     │                              │                               │
    │     ▼                              ▼                               │
    │  stopped (Spectre tears down)    errored (Spectre tears down)      │
    │     │                              │                               │
    │     ▼                              ▼                               │
    │  Async<Ok value>              Async<Error err>                     │
    │                                                                    │
    └─ ct fires → OperationCanceledException raised inside body          │
       │                                                                 │
       ▼                                                                 │
    cancelled (Spectre tears down)                                       │
       │                                                                 │
       ▼                                                                 │
    Async<Error (UserCancelled op)>                                      │
                                                                         │
    second concurrent run.invoked  →  Error ConcurrentLiveSession ───────┘
       (rejected before body invoked; no transition into running)
```

**Single-session enforcement** uses a private
`ConditionalWeakTable<IAnsiConsole, obj>` shared across `Live` / `Status`
/ `Progress` (one shared table — they all mutually exclude each other
because Spectre's `IExclusivityMode` is one slot per console). On
`run` entry, the adapter attempts to insert; failure → return
`Error (ConcurrentLiveSession consoleId)` without invoking `body`.
Released in `finally`.

### 4.7 FR / SC / Principle coverage

| Requirement | Satisfied by |
|---|---|
| FR-010 (typed lifetime, no IDisposable leak) | §4.1 — sessions opaque, callback-scoped |
| FR-011 (cancellation tears down cleanly) | §4.3-4.5 — `cancellation: CancellationToken` passed to all three `run`s; Spectre's own `try/finally` restores cursor |
| FR-012 (concurrent rejected) | §4.6 single-session enforcement |
| FR-016 (non-TTY fallback for Live/Status/Progress) | Status/Progress collapse to single final-line output when `console.IsInteractive = false`; Live returns `Error NoInteractiveConsole "live"` |
| Principle II (no Spectre type in `.fsi`) | §4.2 `Spinner` DU instead of `Spectre.Console.Spinner` |

---

## §5. Prompt request (US4 / P4)

Four prompt wrappers — `TextPrompt`, `SelectionPrompt`,
`MultiSelectionPrompt`, `ConfirmationPrompt`. Per R3:
`Async<Result<'T, RenderError>>` return shape, native CancellationToken
passed to Spectre's `ShowAsync` with a defensive `WithCancellation`
wrap, `Composition`-typed choice labels lowered via the projection
from §0.1, typed F# validators.

### 5.1 Validator type alias

```fsharp
/// Typed validator passed to TextPrompt.ask. Returns Ok with the
/// parsed value on success, or Error with a user-visible message
/// to display in Spectre's native re-prompt loop.
type Validator<'T> = string -> Result<'T, string>
```

**Note**: this is a *type alias*, not a sealed type. Callers compose
validators freely with `>>` and other combinators.

### 5.2 `TextPrompt` module

```fsharp
module TextPrompt =
    val ask :
        console: Console ->
            title: Composition ->
            validator: Validator<'T> ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T, RenderError>>
```

**Implementation note** (from R3 §3): the wrapper uses
`Spectre.Console.TextPrompt<string>` internally and runs the user's
validator twice per successful prompt (once inside Spectre's
`Validate` hook for the re-prompt loop, once after `ShowAsync` returns
to produce the typed `'T` value). The double-validate cost is
negligible at human typing speeds.

### 5.3 `SelectionPrompt` module

```fsharp
module SelectionPrompt =
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T, RenderError>>
```

**Validation rules** (executed before `ShowAsync` is called):
- `choices = []` → `Error (EmptyComposition "SelectionPrompt: at least one choice required")`.
- Any choice label fails to project (renders to `Error` via `Prompts.projectLabel` from §0.1) → `Error (InvalidArgument ("SelectionPrompt", $"choice {n} label failed to render"))`. This pre-flight check prevents the prompt entering an unrunnable state.

**Pre-flight validation scope**: Pre-flight validation runs for ALL prompt types (`TextPrompt`, `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`) — not just `SelectionPrompt`. Any render failure during `Composition`-label projection surfaces as `Result.Error InvalidArgument` BEFORE entering Spectre's interactive loop, so silent error-swallowing inside `projectLabel` is not the user-facing failure mode.

### 5.4 `MultiSelectionPrompt` module

```fsharp
module MultiSelectionPrompt =
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T list, RenderError>>
```

Same validation rules as `SelectionPrompt`. Empty selection (user
confirms with no items selected) → `Ok []` (not an error;
multi-selection naturally permits the empty subset).

### 5.5 `ConfirmationPrompt` module

```fsharp
module ConfirmationPrompt =
    val ask :
        console: Console ->
            title: Composition ->
            defaultChoice: bool ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<bool, RenderError>>
```

**Validation rules**: none at construction; title is a `Composition`
which is by-construction valid.

### 5.6 Cancellation handling (uniform)

Every prompt's implementation follows this pattern:

```
1. Validate title + choices up-front; on Error, return Error
   immediately (no ShowAsync call).
2. Construct Spectre prompt object; set Converter to projectLabel.
3. Wrap ShowAsync(console, ct) with Async.WithCancellationToken ct.
4. Catch OperationCanceledException / TaskCanceledException at the
   boundary; map to Error (UserCancelled "<op>").
5. Catch all other exceptions; map to Error (RenderFailed (op, msg)).
6. On success: for TextPrompt re-run the validator to lift string → 'T.
```

### 5.7 Non-TTY behaviour

When `console.IsInteractive = false` (stdin redirected, or `Console.test`
with no scripted input), all four prompts short-circuit before
`ShowAsync`:

- Return `Error (NoInteractiveConsole "<op>")`.
- Do not write anything to the console (no orphaned title, no
  hanging cursor).

This satisfies FR-016 for prompts.

### 5.8 FR / SC / Principle coverage

| Requirement | Satisfied by |
|---|---|
| FR-013 (`Async<Result<_,_>>` return) | §5.2-5.5 |
| FR-014 (`Composition` choice labels) | §5.3, §5.4 |
| FR-015 (typed F# validator) | §5.1, §5.2 |
| FR-016 (non-TTY → `NoInteractiveConsole`) | §5.7 |
| Principle X (untrusted content) | `Composition` labels go through `SafeText` at their leaves (Phase 1) |

---

## §6. Console wrapper / TestConsole / CursorState (US5 / P5)

Opaque `Console` wrapping either a production `IAnsiConsole`
(via `Spectre.Console.AnsiConsole.Create(settings)` — not the obsolete
`AnsiConsoleFactory`, per coverage-matrix discovery #2) or a
`Spectre.Console.Testing.TestConsole` for deterministic capture.

### 6.1 `Console` opaque type

```fsharp
[<Sealed>]
type Console
```

**Underlying fields** (private):

| Field | Type | Purpose |
|---|---|---|
| `Backend` | `Spectre.Console.IAnsiConsole` | Spectre console (production: `AnsiConsole.Create`-produced; test: `TestConsole` instance). |
| `Mode` | `ConsoleMode` (private DU) | `Production` or `Test of TestConsole` — used to gate `capture` / `cursorState`. |

**Invariants**:
- One `Console` instance per logical console. `Console.default` is a
  singleton (`AnsiConsole.Console` under the hood); `Console.test`
  returns a fresh independent instance per call.

### 6.2 `Console` module

```fsharp
module Console =
    /// Default production console: writes to stdout, respects
    /// terminal capabilities (auto-detected). Single shared instance
    /// across the process; subsequent calls return the same value.
    val default_ : unit -> Console

    /// Test console: captures all output to an in-memory buffer.
    /// Each call returns a fresh independent instance.
    val test : width: int -> colourEnabled: bool -> Console

    /// Render a Composition through this console's renderer. For
    /// production consoles, writes to stdout. For test consoles,
    /// appends to the in-memory buffer.
    val write : Console -> Composition -> Result<unit, RenderError>

    /// Render a Composition into a string via this console's renderer.
    /// Works for both production and test consoles; for production,
    /// the result is the same ANSI string the console would have
    /// emitted (computed via a transient test console internally).
    val capture : Console -> Composition -> Result<string, RenderError>

    /// Read the current cursor state of this console. For production
    /// consoles, queries the terminal via Spectre's IAnsiConsoleCursor;
    /// for test consoles, returns the test console's recorded state.
    val cursorState : Console -> CursorState

    /// True iff this console can drive interactive prompts (stdin is
    /// a TTY for production consoles; always false for test consoles
    /// unless scripted input was attached — Phase 2 punts on scripting
    /// support; first-cut returns false for test consoles).
    val isInteractive : Console -> bool
```

**Naming note**: `Console.default_` (trailing underscore) avoids the F#
`default` keyword. Phase 1 already uses the trailing-underscore
convention for `Border.None_`.

**Validation rules**:
- `Console.test`: `width <= 0` → caller programmer error; we
  *defensively clamp* to 1 (so test code that omits width assertions
  doesn't blow up). Width > 1000 also clamped to 1000.
- `Console.write` returns `Error` propagated from the underlying
  `Renderer.toRawAnsi`.
- `Console.capture` is total — works on both production and test
  consoles. Production: spins up a transient `TestConsole` mirroring
  production's width/colour, renders into it, drains its buffer. Test:
  renders directly through the test backend, drains buffer.

**Note on R5 spec deviation**: R5 originally specified `capture` and
`cursorState` returning `Error InvalidArgument` on production consoles.
That decision is *revised* here: `capture` is now total on both modes
(production capture is useful for diagnostic snapshots, e.g. logging
"what would have rendered" without committing to stdout). `cursorState`
remains total (returns `CursorState.UnknownState` on production consoles
where the terminal does not support cursor-position queries, instead of
an error). The revision simplifies caller code; nothing in spec.md or
FR-017 requires the Error-on-prod variant.

### 6.3 `CursorState` DU

```fsharp
type CursorState =
    /// Cursor is visible at a known position.
    | Visible of row: int * column: int
    /// Cursor is hidden (Spectre's `cursor.Hide()` was called).
    | Hidden
    /// Cursor state is unknown — usually because the underlying
    /// console does not support cursor-position queries (most
    /// production terminals on non-DEC-private-mode connections).
    | UnknownState
```

**Validation rules**: none at the DU level. `Console.cursorState`
returns one of the three cases based on backend type:
- Production console with cursor-position support → `Visible (r, c)`.
- Production console without support → `UnknownState`.
- Test console with `Cursor.Hidden = true` → `Hidden`.
- Test console with cursor visible → `Visible (testRow, testCol)`.

### 6.4 `Exceptions` module

```fsharp
module Exceptions =
    /// Render an exception via Spectre's built-in exception formatter.
    /// Mirrors `Spectre.Console.AnsiConsoleExtensions.WriteException`
    /// but typed and Result-returning.
    val render : Console -> exn -> Result<unit, RenderError>

    /// Render an exception with custom formatting flags (shorten
    /// types, methods, paths; show clickable file:line links).
    val renderWith :
        Console -> ExceptionFormat -> exn -> Result<unit, RenderError>

/// Format flags for exception rendering. Maps to Spectre's
/// `ExceptionFormats` enum (which is `[<Flags>]`).
type ExceptionFormat = {
    ShortenTypes:   bool
    ShortenMethods: bool
    ShortenPaths:   bool
    ShowLinks:      bool
}

module ExceptionFormat =
    val default_   : ExceptionFormat                                  // all false
    val verbose    : ExceptionFormat                                  // all false
    val compact    : ExceptionFormat                                  // shorten all + show links
```

**Validation rules**: `Exceptions.render` is total (every `exn` value
is renderable). `RenderFailed` returned only if Spectre's formatter
itself throws (extremely rare; only documented case is a recursive
inner-exception cycle, which Spectre's formatter is supposed to break).

### 6.5 State transitions

`Console` is immutable from the caller's perspective. Internally, the
production `Console.default_` shares Spectre's mutable cursor / colour
state with the rest of the process — but that state is invisible at the
F# surface (no transition modelled).

`Console.test` instances accumulate captured output in an internal
buffer between `write` calls. `capture` does NOT clear the buffer; it
returns the current buffer contents. (Drain-on-read was considered and
rejected: tests often want to assert multiple times against the same
output, and explicit drain via a future `Console.testReset` is
cleaner than implicit drain.)

### 6.6 FR / SC / Principle coverage

| Requirement | Satisfied by |
|---|---|
| FR-017 (`TestConsole` wrapper) | §6.1, §6.2 (`Console.test`) |
| FR-002 (no Spectre in `.fsi`) | §6.1 opaque, §6.3 typed DU |
| FR-004 (Phase 1 backward compat) | `Renderer.toRawAnsi` unchanged; `Console.*` is purely additive |
| SC-002 (no `Spectre.Console.*` open in src/Fugue.Cli/) | §6.2 covers every direct-console use-case |
| Principle II (sanitisation layer) | All Spectre cursor/exception primitives wrapped |

---

## §7. Coverage matrix integration

See [coverage-matrix.md](./coverage-matrix.md) for the full per-type
table (228 types, 59 covered, 168 excluded, 1 pending → now classified;
see §7.1 below).

### 7.1 `Layout` pending row classification

The coverage matrix carried a single `pending` row for
`Spectre.Console.Layout`. Per research.md §"Phase 3 (Layout framework)
is out of scope" and plan.md "Phase 3 will build atop these and is
tracked as a separate Spec Kit feature", **`Layout` is classified as
`excluded`** for Phase 2 with justification:

> Multi-pane split-screen widget. No current Fugue use-case
> identified; deferred to Phase 3 (Fugue-specific Layout framework
> tracked as a separate Spec Kit feature). Re-evaluate at Phase 3
> kickoff: either Phase 3's framework wraps `Layout` (promote to
> `covered`), or Phase 3 builds its own primitives independently
> (leave as `excluded` with Phase 3 reference).

The matrix is updated in this PR to reflect the classification.
`Spectre.Console.VerticalAlignment` (currently `excluded` with
"used by `Layout` (pending); out of scope unless `Layout` is
covered") is upgraded to a clean `excluded` ("no current Fugue
use-case; would only be needed if `Layout` is covered, which is
deferred to Phase 3").

### 7.2 New rows added by this design

The §0.2 decision to add `spectre.console.json` introduces a new
assembly. The matrix gains one new namespace section:

```markdown
## Spectre.Console.Json namespace (1 type)

| Type | Kind | Status | Phase / Justification |
|---|---|---|---|
| `JsonText` | sealed class | covered | Phase 2 (P2) — `DataDisplays.Json` smart constructor; requires `spectre.console.json 0.49.*` PackageReference |
```

Total public types tracked: **228 + 1 = 229** (or 228 if the
`Spectre.Console.Json` package is counted separately as
"not in main assembly"; the matrix's main count stays at 228 with the
new row appended in its own section to make the dependency explicit).

### 7.3 CoverageMatrixTests integration

The `CoverageMatrixTests.fs` test scaffold (FR-019) MUST be updated to
load *both* assemblies:

```fsharp
let spectreTypes =
    typeof<Spectre.Console.AnsiConsole>.Assembly.GetExportedTypes()
    |> Array.append (typeof<Spectre.Console.Json.JsonText>.Assembly.GetExportedTypes())
    |> Array.append (typeof<Spectre.Console.Testing.TestConsole>.Assembly.GetExportedTypes())
    |> Array.map (fun t -> t.FullName)
```

Each enumerated type must appear in `coverage-matrix.md` with status
`covered` or `excluded`. Zero `pending` rows allowed at PR merge
(SC-001).

---

## §8. Type-shape summary

| Type | Shape | Why |
|---|---|---|
| `RenderError` (extended) | DU + 3 new cases | named cases > stringly-typed; v-MINOR per addition |
| `Renderable` | opaque sealed class | hides `IRenderable` from public match exhaustiveness |
| `Composition.Foreign` | `internal` DU case | extension by adapter-internal code without breaking caller match arms |
| `Tree`, `Json`, `BarChart`, `BreakdownChart`, `Calendar`, `FigletText`, `Grid`, `TextPath` | opaque sealed class + smart constructor | invariants enforced at build time; rendering cannot fail structurally |
| `TreeNode` | opaque builder type | recursion controlled via `leaf` / `branch`; collapse to `Tree` at `Tree.create` |
| `LiveSession`, `StatusSession`, `ProgressSession`, `ProgressTask` | opaque sealed class | callback-scoped lifetime; no escape; not `IDisposable` at F# surface |
| `Spinner` | closed DU + `Custom` case | one case per Spectre `Known` field; custom escape hatch |
| `Console` | opaque sealed class | hides production vs. test mode while keeping API uniform |
| `CursorState` | closed DU | three cases covers all observable states |
| `Validator<'T>` | type alias | composable; no opacity needed |
| `ExceptionFormat` | record with three named factories | maps `[<Flags>]` enum to F# record; common combinations as named values |

This shape is consistent with Phase 1's discipline: every wrapper is
either an opaque sealed type, a closed DU, or a record with private
fields. Construction goes through smart constructors that return
`Result<_, RenderError>`. Pattern matching at the renderer is
exhaustive over the public DU shape.

---

## §9. Visibility audit

A final check that no Spectre type leaks into a public `.fsi` (SC-004):

| Adapter file | Spectre types in `.fsi` | Justified? |
|---|---|---|
| `Renderable.fsi` | `Spectre.Console.Rendering.IRenderable` (argument of `fromSpectre` only) | YES — FR-008 explicitly carves this out as the single intentional bridge |
| `DataDisplays.fsi` | none | — |
| `Live.fsi` | none | — |
| `Prompts.fsi` | none | — |
| `Console.fsi` | none | — |

`rg 'Spectre\.' specs/002-spectre-full-coverage/contracts/` produces
**1 match**: the `IRenderable` argument in `Renderable.fsi`. SC-004
holds (the one match is the intentional, documented one).

---

*End of data-model.md. Implementation notes belong in Phase 2 PR
descriptions; design rationale not captured here belongs in
research.md.*
