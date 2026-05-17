# Phase 1 Data Model: Phase 3 — Fugue Layout Framework

**Feature**: `003-layout-framework`
**Date**: 2026-05-18
**Depends on**: [research.md](./research.md), [spec.md](./spec.md), [plan.md](./plan.md)

This document specifies the *type-level contract* for every public adapter
entity added by Phase 3. It mirrors the structure of the Phase 2 data-model
([`specs/002-spectre-full-coverage/data-model.md`](../002-spectre-full-coverage/data-model.md))
— one numbered section per logical entity group, each with name, kind,
fields, visibility, validation rules, and the FR / SC / Principle coverage.

The contract `.fsi` files under [`contracts/`](./contracts/) are the
syntactic sketches; this file is the prose source-of-truth that explains
*why* each shape was chosen and *what* its invariants are. Where the two
disagree, this file wins (the `.fsi` files will be regenerated to match
during Phase 3 implementation).

All layout types live under namespace `Fugue.Adapters.Console.Layout` unless
otherwise noted. No public type exposes a C# or adapter-internal type from
outside the Layout sub-namespace (FR-005, SC-004). The single intentional
concession remains `Renderable.fromSpectre`'s *argument* established in Phase 1.

---

## §0 — Resolutions of decisions carried from research.md

### §0.1 — Reactive-vs-static update model (R-1)

**Decision**: static-rebuild + frame-level coalescing scheduler.

`LayoutSession` as a reactive-diff handle does NOT exist in Phase 3. The
concept becomes a *scheduler concern*, not a *layout concern*. The public
`LayoutScheduler` type (§7) is a frame-coalescing primitive: callers mount a
producer function `(unit -> Result<Composition, RenderError>)` and call
`.trigger()` when their inputs change; the scheduler invokes the producer at
most once per 16 ms tick (≈ 60 Hz), discarding intermediate trigger calls
within that window.

All four layout primitives (`Stack`, `Dock`, `LayoutGrid`, `Flex`) are
immutable sealed types. Rendering the same value twice at the same
`RenderContext` produces byte-identical output (FR-008). FsCheck property
tests can generate, shrink, and re-run `Layout` values without state concerns.

Empirical basis: 0.016 ms per full rebuild of a 121-node F# DU tree —
313× under SC-006's 5 ms budget. See `research.md §R-1` for the full
back-of-envelope and framework comparison.

### §0.2 — Surface integration model (R-2)

**Decision**: Layout above Surface (option A). `Fugue.Surface` is UNCHANGED
by Phase 3.

A new file `src/Fugue.Cli/LayoutHost.fs` (~50 LOC) wires the two layers:

1. `LayoutHost.mount` creates a `LayoutScheduler` for a target `Region`.
2. On each scheduler tick, the host invokes the producer, runs
   `Renderer.toRawAnsi` on the resulting `Composition`, and posts
   `DrawOp.RawAnsi` to the `RealExecutor` actor for the correct region.
3. `Fugue.Surface.RealExecutor`'s existing drain-coalesce logic handles
   the "drop intermediate frames within one tick" case for `RawAnsi` posts.

`Fugue.Cli/LayoutHost.fs` is JIT-only, lives in `Fugue.Cli`, and is NOT
part of the adapter library itself (`Fugue.Adapters.Console`). It is the
caller-side integration shim, not a library primitive.

The status bar and input region continue to use `DrawOp` directly; porting
them is deferred to a Phase 4 follow-up issue (spec Assumption A11).

### §0.3 — Namespace placement (R-3)

**Decision**: sub-namespace `Fugue.Adapters.Console.Layout.*` inside the
existing `Fugue.Adapters.Console.fsproj`, in a new `Layout/` sub-folder.

Phase 2's `DataDisplays.Grid` (widget type) remains UNCHANGED. Phase 3's
layout `LayoutGrid` type lives at `Fugue.Adapters.Console.Layout.LayoutGrid`.
The bare name `Grid` within the `Layout` sub-namespace is reserved as a
module alias for ergonomic call-sites that `open Fugue.Adapters.Console.Layout`
— see §5 for the naming rationale.

No new `.fsproj` is created. Layout modules share `RenderError`, `SafeText`,
`Composition`, and `Renderer` plumbing with Phase 1/2 modules directly.

**File order in `.fsproj`** (append after existing Phase 2 files, in
dependency order):

```
Layout/Common.fs     → Layout/Common.fsi
Layout/Stack.fs      → Layout/Stack.fsi
Layout/Dock.fs       → Layout/Dock.fsi
Layout/LayoutGrid.fs → Layout/LayoutGrid.fsi
Layout/Flex.fs       → Layout/Flex.fsi
Layout/Composition.fs → Layout/Composition.fsi   (ofStack / ofDock / ofLayoutGrid / ofFlex)
Layout/Scheduler.fs  → Layout/Scheduler.fsi
```

F# project-file order is the enforcement mechanism for dependency direction;
no cyclic references are possible by construction.

### §0.4 — Display-width measurement (R-4)

**Decision**: rely on Spectre's transitive measurement; no new NuGet package
for Phase 3. `SafeText.displayWidth` does not exist in Phase 3.

Layout primitives do NOT compute leaf-text display widths themselves. Where
width computation is needed (e.g. `Auto` column sizing in `LayoutGrid`), the
lowering logic measures via `IRenderable.Measure` exposed through the existing
`Renderable.fromSpectre` bridge path — Spectre's internal `Cell.GetCellLength`
is invoked transitively. This guarantees agreement between our sizing
decisions and Spectre's actual rendering of the same content.

Known limitation: ZWJ emoji clusters (e.g. `"👨‍👩‍👧"`) are reported as
6 display columns by Spectre's `Cell` rather than the correct 2. Accepted for
Phase 3 MVP; a follow-up issue (`SF-1` in research.md) tracks the upgrade path.

---

## §1 — RenderContext extension (FR-009 height)

**Current state** (Phase 2): `RenderContext` has three fields: `width: int`,
`colourEnabled: bool`, `themeName: string`. Source: `src/Fugue.Adapters.Console/Context.fsi:8-10`.

**Phase 3 addition**: a `height: int` field, exposed as a `Height` member.

### Type shape

```fsharp
type RenderContext =
    private
        { width: int
          height: int            // NEW — Phase 3
          colourEnabled: bool
          themeName: string }
    member Width: int
    member Height: int           // NEW — Phase 3
    member ColourEnabled: bool
    member ThemeName: string
```

**Record is NOT opaque** — it uses `private` on the record constructor fields
(same as Phase 1/2) but exposes members publicly. External callers use
`RenderContext.create` (smart constructor) or `RenderContext.probe` (IO probe).

### Smart constructor change

`RenderContext.create` gains a `height` parameter:

```fsharp
val create:
    width: int -> height: int -> colourEnabled: bool -> theme: (string | null) -> RenderContext
```

**Backward compat**: test files that call `RenderContext.create` with three
arguments (pre-Phase 3) will not compile after this change. This is the only
breaking change across the Phase 3 module surface. **Mitigation**: all Phase 3
test helpers use `RenderContext.create 80 24 true null` (width, height,
colour, theme) as a baseline fixture; existing Phase 2 tests MUST be updated
to pass `height` before the Phase 3 modules compile. Estimate: ~12 call-sites
in `tests/Fugue.Adapters.Console.Tests/` — a mechanical one-line-per-site fix.

### Default value policy

`RenderContext.probe` reads `Console.WindowHeight` (same pattern as
`Console.WindowWidth` today, `Context.fs`). Test helpers that do not care
about height pass `height = Int32.MaxValue` to signal "unconstrained" — layout
primitives interpret this as "no vertical clipping". This avoids adding an
`Option<int>` to `RenderContext` (extra type noise) while keeping the
semantics explicit.

### Validation

`RenderContext.create` rejects `width ≤ 0` (existing) and `height ≤ 0` (new)
with `InvalidArgument ("RenderContext", "width/height must be positive")`.
The spec edge case "Negative or zero terminal dimensions" (spec §Edge Cases)
is caught here, before any layout primitive is consulted.

**Coverage**: FR-009, SC-006 (layout latency), SC-007 (resize responsiveness).

---

## §2 — Common layout types

All common types live in `Layout/Common.fs` under namespace
`Fugue.Adapters.Console.Layout`. They are exported in `Layout/Common.fsi`.

### §2.1 — Orientation

```fsharp
type Orientation =
    | Vertical
    | Horizontal
```

**Kind**: closed DU. No `Custom` case. Used by `Stack` (§3) and `Flex` (§6).
Vertical = children flow top-to-bottom; Horizontal = children flow
left-to-right.

### §2.2 — CrossAxisAlignment

```fsharp
type CrossAxisAlignment =
    | Start
    | Center
    | End_      // trailing underscore avoids clash with F# keyword 'end'
    | Stretch
```

**Kind**: closed DU. Used by `Stack` (§3). Maps to: padding/alignment applied
perpendicular to the stack axis.

- `Start`: children align to the leading edge of the cross axis (left for
  Vertical stacks, top for Horizontal stacks).
- `Center`: children are centred on the cross axis.
- `End_`: children align to the trailing edge.
- `Stretch`: children expand to fill the full cross-axis extent.

### §2.3 — Overflow

```fsharp
type Overflow =
    | Clip
    | Strict
    | WrapNext
```

**Kind**: closed DU. Used by `Flex` (§6). Default = `Clip`.

- `Clip`: content that exceeds available dimension is silently clipped.
- `Strict`: content that exceeds available dimension returns
  `Error (LayoutOverflow (...))` — the caller controls the failure path.
- `WrapNext`: content wraps to the next row/column along the main axis.

Spec edge case "Layout overflow" maps to these three cases exactly.

### §2.4 — Reuse of Phase 1 Alignment

Phase 1 ships `Alignment` in `Fugue.Adapters.Console` (`Composition.fsi:13-16`):

```fsharp
type Alignment = | LeftAlign | CentreAlign | RightAlign
```

This type covers *content alignment within a cell* (horizontal axis only).
`CrossAxisAlignment` (§2.2) covers *child alignment on the cross axis of a
stack*. They are distinct enough in semantics and name to coexist without
collision when `open Fugue.Adapters.Console` and
`open Fugue.Adapters.Console.Layout` are both in scope.

**Decision**: define `CrossAxisAlignment` as a NEW type in the Layout
sub-namespace. Do NOT extend Phase 1's `Alignment` DU (would be a Phase 1
breaking change and would conflate two concepts).

---

## §3 — Stack primitive (FR-001, US1)

**Source file**: `Layout/Stack.fs` / `Layout/Stack.fsi`.
**Namespace**: `Fugue.Adapters.Console.Layout`.

### Type definition

```fsharp
[<Sealed>]
type Stack
```

Opaque sealed type. Private record:

```fsharp
type private StackData =
    { Orientation: Orientation
      Gap: int
      Align: CrossAxisAlignment
      Children: Composition list
      Depth: int }
```

`Depth` is the maximum depth of any child sub-tree at construction time.
Stored so `Stack.create` can enforce the depth-100 limit cheaply without a
full tree walk at every render.

### Smart constructor

```fsharp
module Stack =
    val create :
        orientation: Orientation ->
            gap: int ->
            align: CrossAxisAlignment ->
            children: Composition list ->
            Result<Stack, RenderError>
```

**Validation** (in order, first failure returned):

1. `children` is empty → `Error (EmptyComposition "Stack: at least one child required")`.
2. `gap < 0` → `Error (InvalidArgument ("Stack", "gap must be non-negative"))`.
3. `depth(children) + 1 > 100` → `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.

Depth of a `Composition` is computed via a small recursive helper
`layoutDepth : Composition -> int` (internal, not exported). It walks the
existing `Composition` DU cases (`Stack`, `Padded`, `Panel`, `Columns`,
`Table`, `Aligned`, `Foreign`) and returns the max nesting depth. For a leaf
(`Composition.Leaf _`), depth = 1. For `Composition.Stack children`, depth =
1 + max(map layoutDepth children). `Foreign` is treated as depth = 1 (its
internals are opaque).

### Lowering

```fsharp
module Composition =
    val ofStack : Stack -> Composition
```

**Algorithm** (Vertical orientation, gap=G, align=A, children=[c₁; c₂; ...; cₙ]):

1. Produce a separator `Composition.Padded(0, 0, 0, G, Composition.Leaf …blank…)`
   if `G > 0`. Blank separator is a zero-content Primitive (e.g. `Primitive.Markup`
   of an empty string).
2. Interleave: `[c₁; sep; c₂; sep; …; cₙ]`.
3. Apply `CrossAxisAlignment` as padding on the cross axis:
   - `Start`: no wrapping (children left-align naturally).
   - `Center`: wrap each child in `Composition.Aligned(CentreAlign, child)`.
   - `End_`: wrap each child in `Composition.Aligned(RightAlign, child)`.
   - `Stretch`: no wrapping (Spectre fills to parent width by default for Rows).
4. Return `Composition.Stack interleaved`.

**Horizontal orientation**: same algorithm but uses `Composition.Columns` as
the outer node. Gap is implemented by inserting fixed-width spacer children
(width = G columns, as `Primitive.Text` of G space characters) between each
pair.

Note: `Composition.Stack` (Phase 1 DU case) = vertical rows. Phase 3's
`Stack` layout primitive wraps it for Vertical; for Horizontal it uses
`Composition.Columns`. There is no naming clash at the F# level because
`Layout.Stack` is a sealed type (not a DU case) while `Composition.Stack` is
a DU case inside the existing `Composition` DU — different kinds.

**Coverage**: FR-001, FR-003, US1 acceptance scenarios 1, 2, 3.

---

## §4 — Dock primitive (FR-001, US2)

**Source file**: `Layout/Dock.fs` / `Layout/Dock.fsi`.
**Namespace**: `Fugue.Adapters.Console.Layout`.

### DockEdge

```fsharp
type DockEdge =
    | Top
    | Bottom
    | Left
    | Right
    | Fill
```

**Kind**: closed DU. `Fill` is the "remaining space" sentinel — exactly one
child must carry `Fill` per US2 acceptance scenario 3.

### Type definition

```fsharp
[<Sealed>]
type Dock
```

Opaque sealed type. Private record:

```fsharp
type private DockData =
    { Children: (DockEdge * Composition) list }
```

Children are ordered; edge allocation happens in declaration order (first
declared child gets priority on its edge).

### Smart constructor

```fsharp
module Dock =
    val create :
        children: (DockEdge * Composition) list ->
            Result<Dock, RenderError>
```

**Validation** (in order):

1. `children` is empty → `Error (EmptyComposition "Dock: at least one child required")`.
2. Fill count ≠ 1 → `Error (InvalidArgument ("Dock", "exactly one Fill child permitted"))`.
3. Depth check: same `layoutDepth` function as §3; total depth from any child
   > 99 → `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.

### Lowering

```fsharp
module Composition =
    val ofDock : Dock -> Composition
```

**Algorithm**: Two-pass geometry at render time (deferred to the lowered
`Composition` evaluation):

Pass 1 — edge allocation (declared order):
- `Top` children are allocated from row 0 downward. Their height = intrinsic
  height of their lowered `Composition` measured via the Spectre `Measure`
  path (an internal helper `measureHeight : Composition -> RenderContext -> int`
  wraps `IRenderable.Measure(width, maxHeight)`).
- `Bottom` children are allocated from the last row upward.
- `Left` children are allocated from column 0 rightward (width = intrinsic
  width via `Measure`).
- `Right` children are allocated from the rightmost column leftward.
- `Fill` child receives the rectangle remaining after all edge allocations.

Pass 2 — composition assembly: each region is wrapped in
`Composition.Padded(left, right, top, bottom, child)` to position it in the
correct sub-rectangle of the terminal, then all regions are assembled into a
`Composition.Stack` (for vertical layout) or `Composition.Columns` (for side-
by-side layout).

**Practical lowering**: For typical Fugue use (Bottom = status bar, Fill =
content), the lowering produces:

```
Composition.Stack [
    Composition.Padded(0,0,0,<statusHeight>, contentChild)
    statusChild
]
```

This is a simplification; the general multi-edge case uses a nested
Stack+Columns assembly. Implementation note: this is the most complex lowering
in Phase 3 — it requires intrinsic measurement before constructing the final
`Composition`. If `measureHeight` produces `Error`, `ofDock` returns the
fallback `Composition.Stack(children |> List.map snd)` (degenerate stack,
ignores edge pinning) and logs a warning.

**Coverage**: FR-001, FR-003, US2 acceptance scenarios 1, 2, 3.

---

## §5 — LayoutGrid primitive (FR-001, US3)

**Source file**: `Layout/LayoutGrid.fs` / `Layout/LayoutGrid.fsi`.
**Namespace**: `Fugue.Adapters.Console.Layout`.
**Disambiguation**: the type is named `LayoutGrid` (not `Grid`) to prevent
unqualified-resolution ambiguity when a file opens both
`Fugue.Adapters.Console` (which has `DataDisplays.Grid`) and
`Fugue.Adapters.Console.Layout`. The module is also named `LayoutGrid`.
Within the `Layout` sub-namespace, `Grid` may be used as an alias via
`module Grid = LayoutGrid` in future ergonomics passes; for Phase 3 MVP,
`LayoutGrid` is the canonical name everywhere.

### Sizing DUs

```fsharp
type ColumnSize =
    | Fixed of width: int    // exact column-count
    | Auto                   // size to widest child
    | Star of ratio: int     // proportional share of remaining space

type RowSize =
    | Fixed of height: int   // exact row-count
    | Auto                   // size to tallest child
    | Star of ratio: int     // proportional share of remaining space
```

**Kind**: both are closed DUs. `Star of int` carries the ratio as a positive
integer (ratio ≥ 1 enforced by smart constructor). Two separate types (`ColumnSize`
vs `RowSize`) for type safety — the compiler rejects `[colSize]` in a `RowSize list`
argument position (FR-002 smart constructor validates this structurally, but
the type system makes the error unrepresentable at the call-site).

### Type definition

```fsharp
[<Sealed>]
type LayoutGrid
```

Opaque sealed type. Private record:

```fsharp
type private LayoutGridData =
    { Columns: ColumnSize list
      Rows: RowSize list
      Cells: Composition list list }   // outer = rows, inner = cells per row
```

### Smart constructor

```fsharp
module LayoutGrid =
    val create :
        columns: ColumnSize list ->
            rows: RowSize list ->
            cells: Composition list list ->
            Result<LayoutGrid, RenderError>
```

**Validation** (in order):

1. `columns` is empty → `Error (EmptyComposition "LayoutGrid: at least one column required")`.
2. `rows` is empty → `Error (EmptyComposition "LayoutGrid: at least one row required")`.
3. `cells.Length ≠ rows.Length` → `Error (InvalidArgument ("LayoutGrid", $"expected {rows.Length} rows of cells, got {cells.Length}"))`.
4. For each row `i`: `cells[i].Length ≠ columns.Length` → `Error (InvalidArgument ("LayoutGrid", $"row {i} has {cells[i].Length} cells, expected {columns.Length}"))`.
5. Fixed column widths: any `Fixed w` where `w < 1` → `Error (InvalidArgument ("LayoutGrid", "Fixed column width must be ≥ 1"))`.
6. Fixed row heights: any `Fixed h` where `h < 1` → `Error (InvalidArgument ("LayoutGrid", "Fixed row height must be ≥ 1"))`.
7. Star ratios: any `Star r` where `r < 1` → `Error (InvalidArgument ("LayoutGrid", "Star ratio must be ≥ 1"))`.
8. Depth check: `layoutDepth` on any cell child > 98 → `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.

### Lowering

```fsharp
module Composition =
    val ofLayoutGrid : LayoutGrid -> Composition
```

**Two-pass sizing algorithm**:

Pass 1 — column widths (given `ctx.Width` as total available width):

1. Allocate `Fixed` columns first (exact widths, subtracted from available).
2. Measure `Auto` columns: for each Auto column index `j`, iterate over all
   rows, measure cell `(i, j)` via `IRenderable.Measure(maxWidth, 1)`,
   take the maximum. Subtract Auto columns from remaining available.
3. Distribute remaining width among `Star` columns: for each Star column,
   `assignedWidth = (ratio / totalRatio) * remaining`, rounded to int.
   Distribute rounding residue to the first Star column.

Pass 1b — row heights (given `ctx.Height` as total available height, same three phases).

Pass 2 — cell assembly: for each cell `(i, j)`, wrap its `Composition` in
`Composition.Padded(colOffsets[j], remainingRight, rowOffsets[i], remainingBottom, cell)`.
Assemble all cells in a row as `Composition.Stack` (vertical, no gap), and
assemble all rows as `Composition.Stack` (vertical, no gap).

**Implementation note**: `ofLayoutGrid` receives a `RenderContext` as an
implicit parameter (closure over the active context during rendering). Since
`Composition.ofLayoutGrid` maps `LayoutGrid → Composition` (pure, no IO),
the actual sizing computation happens when `Renderer.toRawAnsi` walks the
`Composition` tree. This means `ofLayoutGrid` returns a *lazy* `Composition`
that captures the `LayoutGrid` and computes geometry on demand during
`Renderer.toRawAnsi`. Implementation: use `Composition.Foreign (Renderable.fromSpectre
(LayoutGridRenderable(g)))` where `LayoutGridRenderable` is an internal
`IRenderable` implementation. This keeps the Phase 1 renderer path unmodified.

**Coverage**: FR-001, FR-003, US3 acceptance scenarios 1, 2, 3.

---

## §6 — Flex primitive (FR-001, US4)

**Source file**: `Layout/Flex.fs` / `Layout/Flex.fsi`.
**Namespace**: `Fugue.Adapters.Console.Layout`.

### FlexItem

```fsharp
[<Sealed>]
type FlexItem
```

Opaque sealed type (not a public record — hides grow/shrink/basis from
direct construction). Private record:

```fsharp
type private FlexItemData =
    { Grow: float       // grow ratio (≥ 0.0); 0.0 = no grow
      Shrink: float     // shrink ratio (≥ 0.0); 0.0 = no shrink
      Basis: int        // initial size in cells (≥ 0)
      Child: Composition }
```

**`float` for Grow/Shrink, not `int`**: CSS flexbox spec uses floats. B-1
pseudocode (research.md) uses `float` for the division. Using `int` would
require normalising ratios on every solver invocation. `float` is a minor
loss of comparability (floating-point), acceptable because `FlexItem` is
`[<NoComparison; NoEquality>]` per the `Composition` precedent (FR-008 does
not require value equality on `FlexItem`, only on the *output* of rendering).

### FlexItem constructors

```fsharp
module Flex =
    /// Fixed-size item: basis = n cells, grow = 0, shrink = 0.
    val fixed_ : n: int -> child: Composition -> FlexItem

    /// Grow-only item: basis = 0, grow = ratio, shrink = 0.
    val grow : ratio: float -> child: Composition -> FlexItem

    /// Shrink-only item: basis = n cells, grow = 0, shrink = ratio.
    val shrink : basis: int -> ratio: float -> child: Composition -> FlexItem

    /// Full control: explicit basis, grow ratio, shrink ratio.
    val item : basis: int -> grow: float -> shrink: float -> child: Composition -> FlexItem
```

Note: `fixed` is a reserved keyword in some F# contexts; using `fixed_` with
a trailing underscore is consistent with `End_` in `CrossAxisAlignment`. An
`[<CompiledName("fixed")>]` attribute may be added for C# interop aesthetics
if needed, but is not required for Phase 3 MVP (all callers are F#).

**Validation for `FlexItem` constructors**: non-negative `n`, non-negative
`ratio`, non-negative `basis` — checked at item-construction time, not
deferred to `Flex.create`. Returns `Result<FlexItem, RenderError>`:

```fsharp
val fixed_ : n: int -> child: Composition -> Result<FlexItem, RenderError>
val grow   : ratio: float -> child: Composition -> Result<FlexItem, RenderError>
val shrink : basis: int -> ratio: float -> child: Composition -> Result<FlexItem, RenderError>
val item   : basis: int -> grow: float -> shrink: float -> child: Composition -> Result<FlexItem, RenderError>
```

Validation: `n < 0` or `basis < 0` → `Error (InvalidArgument ("FlexItem", "basis must be non-negative"))`;
`ratio < 0.0` or `grow < 0.0` or `shrink < 0.0` → `Error (InvalidArgument ("FlexItem", "ratio must be non-negative"))`.

### Flex type definition

```fsharp
[<Sealed>]
type Flex
```

Opaque sealed type. Private record:

```fsharp
type private FlexData =
    { Axis: Orientation
      Items: FlexItem list
      Overflow: Overflow }
```

### Smart constructors

```fsharp
module Flex =
    val create :
        axis: Orientation ->
            items: FlexItem list ->
            Result<Flex, RenderError>

    val createWith :
        axis: Orientation ->
            items: FlexItem list ->
            overflow: Overflow ->
            Result<Flex, RenderError>
```

`create` uses `Overflow.Clip` as the default.

**Validation** (both constructors):

1. `items` is empty → `Error (EmptyComposition "Flex: at least one item required")`.
2. Depth check: `layoutDepth` on any item's child > 98 → `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.

Item-level grow/shrink/basis validation is handled at `FlexItem` construction
time (see above), so `Flex.create` / `Flex.createWith` do NOT re-validate
item fields.

### Flex solver (algorithm reference from B-1)

The solver is a pure function `solveFlex : FlexItem list -> int -> int list`:

```
Phase 1 — sum basis values: totalBasis = sum(item.Basis)
         freeSpace = axisLen - totalBasis

If freeSpace = 0: basis is final.

If freeSpace > 0 (space to distribute):
  totalGrow = sum(item.Grow)
  if totalGrow = 0: basis stands, free space wasted.
  else: each item gets basis + round(freeSpace * item.Grow / totalGrow)
        distribute rounding residue to first item.

If freeSpace < 0 (items overflow):
  totalScaledShrink = sum(item.Shrink * item.Basis)
  if totalScaledShrink = 0: items keep basis, consult Overflow policy.
  else: each item gets max(0, basis - round(|freeSpace| * item.Shrink * item.Basis / totalScaledShrink))
        distribute rounding residue to first item.
```

Rounding residue distribution: after rounding all items, if `sum ≠ axisLen`,
adjust the first item by `axisLen - sum`. This keeps total = `axisLen` exact.

### Lowering

```fsharp
module Composition =
    val ofFlex : Flex -> Composition
```

**Algorithm**:

1. Determine axis length from `RenderContext` (`Width` for `Horizontal`, `Height`
   for `Vertical`). Same lazy-IRenderable pattern as `ofLayoutGrid` (§5).
2. Run `solveFlex items axisLen` → `int list` of resolved sizes.
3. For `Horizontal`: assemble as `Composition.Columns` with each item's
   resolved width as its ratio (after normalising to sum-to-1 floats) wrapping
   the item's child.
4. For `Vertical`: assemble as `Composition.Stack` with each item wrapped in
   `Composition.Padded(0, 0, 0, resolvedHeight - intrinsicHeight, item.Child)`.
5. Apply `Overflow` policy: if solver returned sizes summing > `axisLen`
   (can happen when no shrink, freeSpace < 0):
   - `Clip`: truncate last item(s) to fit.
   - `Strict`: return `Error (LayoutOverflow ("Flex", $"overflow on {axis}: requested {totalBasis}, have {axisLen}"))`.
   - `WrapNext`: split items that overflow into a subsequent row/column.

**Coverage**: FR-001, FR-003, US4 acceptance scenarios 1, 2, 3.

---

## §7 — Scheduler (R-1 outcome)

**Source file**: `Layout/Scheduler.fs` / `Layout/Scheduler.fsi`.
**Namespace**: `Fugue.Adapters.Console.Layout`.

### Type definition

```fsharp
[<Sealed>]
type LayoutScheduler
```

Opaque sealed type. Internal: `MailboxProcessor`-based actor with a drain loop.
The actor holds state: `lastLayout: Composition option` (the last successfully
produced layout, for referential-equality short-circuit) and `pendingTriggers:
int` (count of triggers received since last render tick).

### Constructor

```fsharp
module Scheduler =
    val create :
        producer: (unit -> Result<Composition, RenderError>) ->
            frameIntervalMs: int ->
            onFrame: (Composition -> unit) ->
            LayoutScheduler
```

- `producer`: pure function rebuilding the entire layout tree from caller
  state captured via closure. Called at most once per `frameIntervalMs`.
- `frameIntervalMs`: tick interval in milliseconds. Default recommendation:
  `16` (≈ 60 Hz). Tests may use a larger interval (e.g. `1000`) to avoid
  timing dependencies.
- `onFrame`: callback invoked with the produced `Composition` each tick.
  In REPL use, this callback runs `Renderer.toRawAnsi` and posts
  `DrawOp.RawAnsi` to the Surface actor (see §0.2).

### Methods

```fsharp
module Scheduler =
    val trigger : LayoutScheduler -> unit
    val stop    : LayoutScheduler -> unit
```

- `trigger`: signals that input has changed. The next tick will invoke the
  producer (if not already scheduled). Multiple `trigger` calls within one
  `frameIntervalMs` window are coalesced — only one producer invocation
  happens per tick. Thread-safe (MailboxProcessor serialises all messages).
- `stop`: drains the inbox, cancels the timer, stops the actor. After `stop`,
  `trigger` calls are silently ignored. `stop` is idempotent.

### Internal actor design

```
SchedulerMsg =
    | Trigger
    | Tick
    | Stop of AsyncReplyChannel<unit>

Agent loop:
  receive msg
  match msg with
  | Trigger →
      increment pendingTriggers; loop
  | Tick →
      if pendingTriggers > 0 then
          let result = producer ()
          match result with
          | Ok layout ->
              if not (referenceEquals layout lastLayout) then
                  onFrame layout
              lastLayout <- Some layout
          | Error _ -> () // silently skip failed frames; caller sees error at next successful render
          pendingTriggers <- 0
      schedule next Tick after frameIntervalMs
      loop
  | Stop reply →
      reply.Reply ()
      // exit loop
```

The `Tick` message is self-scheduled: the actor posts `Tick` to itself after
`frameIntervalMs` via `Async.Sleep` (acceptable inside the `MailboxProcessor`
body's `async {}` computation, per Always-Rule §3.1). The `pendingTriggers`
counter replaces a dirty-flag queue — all that matters is "did at least one
trigger arrive since the last tick?", not how many.

**Thread safety**: `trigger` posts `SchedulerMsg.Trigger` to the mailbox;
post is lock-free and non-blocking. The actor serialises all state access.

**Coverage**: FR-007 (incremental updates via trigger), FR-015 (coalescing),
SC-005 (100 events/sec throughput), SC-007 (16 ms resize responsiveness).

---

## §8 — Layout-error catalogue (extends Phase 2 RenderError)

Phase 2 `RenderError` (source: `src/Fugue.Adapters.Console/Errors.fsi`) has
seven cases:

```fsharp
type RenderError =
    | InvalidMarkup of input: string * detail: string
    | InvalidStyleSpec of spec: string * detail: string
    | DegenerateWidth of reportedWidth: int
    | EmptyComposition of node: string
    | InvalidArgument of node: string * detail: string
    | RenderFailed of primitive: string * message: string
    | UserCancelled of operation: string
    | NoInteractiveConsole of operation: string
    | ConcurrentLiveSession of console: string
```

Phase 3 adds **one new case**:

```fsharp
    | LayoutOverflow of layoutKind: string * detail: string
```

**Purpose**: `LayoutOverflow` is raised only when `Overflow.Strict` is active
and children exceed the available dimension. The `layoutKind` field names the
primitive (`"Flex"`, `"Stack"`, `"LayoutGrid"`, `"Dock"`); the `detail`
field provides the numeric context (e.g.
`"width: requested 100, available 80"`).

**Why a new case** (not reuse of `InvalidArgument`):
`InvalidArgument` signals a *construction-time* error (degenerate inputs that
are rejected before any render). `LayoutOverflow` is a *render-time* error
(the constraint is only detectable once the `RenderContext` dimensions are
known). They are semantically distinct and callers may want to handle them
differently (e.g. auto-switch to `Overflow.Clip` on `LayoutOverflow` without
treating it as a programmer error).

**Reuse of existing cases for Phase 3**:

| Scenario | Case used |
|----------|-----------|
| Empty children (construction-time) | `EmptyComposition` |
| Negative gap / invalid sizing (construction-time) | `InvalidArgument` |
| Depth > 100 (construction-time) | `InvalidArgument` |
| Overflow with `Overflow.Strict` (render-time) | `LayoutOverflow` (NEW) |
| Render engine throws unexpectedly | `RenderFailed` |

**Backward compatibility**: adding a case to a closed DU is an `InvalidArgument`-magnitude
change per the Phase 2 data-model §1 versioning policy ("adding a case is a
v-MINOR change"). The `RenderError` DU is exhaustively matched only inside the
adapter and in test files. External callers that match `RenderError`
exhaustively MUST add a `| LayoutOverflow _` arm after Phase 3 merges.
The compiler enforces this (F# incomplete-match warning with
`TreatWarningsAsErrors = true`).

---

## §9 — Type-shape summary table

Every new public type added by Phase 3:

| Type | Kind | Namespace | Purpose |
|------|------|-----------|---------|
| `Orientation` | Closed DU | `Layout` | Axis direction for Stack, Flex |
| `CrossAxisAlignment` | Closed DU | `Layout` | Perpendicular alignment for Stack |
| `Overflow` | Closed DU | `Layout` | Policy for dimension-exceeding content in Flex |
| `Stack` | Opaque sealed class | `Layout` | Vertical/horizontal flow of children |
| `DockEdge` | Closed DU | `Layout` | Edge pin for Dock children |
| `Dock` | Opaque sealed class | `Layout` | Edge-pinned region split |
| `ColumnSize` | Closed DU | `Layout` | Column sizing policy for LayoutGrid |
| `RowSize` | Closed DU | `Layout` | Row sizing policy for LayoutGrid |
| `LayoutGrid` | Opaque sealed class | `Layout` | 2D tabular layout |
| `FlexItem` | Opaque sealed class | `Layout` | Per-child flex sizing descriptor |
| `Flex` | Opaque sealed class | `Layout` | Flexbox-like axis distribution |
| `LayoutScheduler` | Opaque sealed class | `Layout` | Frame-coalescing update scheduler |
| `RenderError.LayoutOverflow` | DU case (extension) | `Fugue.Adapters.Console` | Render-time overflow with Strict policy |
| `RenderContext.Height` | Member (extension) | `Fugue.Adapters.Console` | Terminal height for layout constraints |

All `Sealed` types are constructed ONLY via their companion module smart
constructors. No public constructor, no public record fields.

---

## §10 — Visibility audit

Explicit audit confirming every Phase 3 type is either Public (appears in
`.fsi`) or Internal (not in `.fsi` and not accessible outside the adapter).

| Symbol | File | Public in .fsi? | Notes |
|--------|------|-----------------|-------|
| `Orientation` | `Layout/Common.fsi` | Yes | |
| `CrossAxisAlignment` | `Layout/Common.fsi` | Yes | |
| `Overflow` | `Layout/Common.fsi` | Yes | |
| `Stack` | `Layout/Stack.fsi` | Yes (sealed) | Private internals: `StackData` record |
| `Stack.create` | `Layout/Stack.fsi` | Yes | |
| `StackData` | `Layout/Stack.fs` | No | Internal implementation record |
| `DockEdge` | `Layout/Dock.fsi` | Yes | |
| `Dock` | `Layout/Dock.fsi` | Yes (sealed) | Private internals: `DockData` record |
| `Dock.create` | `Layout/Dock.fsi` | Yes | |
| `DockData` | `Layout/Dock.fs` | No | Internal implementation record |
| `ColumnSize` | `Layout/LayoutGrid.fsi` | Yes | |
| `RowSize` | `Layout/LayoutGrid.fsi` | Yes | |
| `LayoutGrid` | `Layout/LayoutGrid.fsi` | Yes (sealed) | Private internals: `LayoutGridData` record |
| `LayoutGrid.create` | `Layout/LayoutGrid.fsi` | Yes | |
| `LayoutGridData` | `Layout/LayoutGrid.fs` | No | Internal implementation record |
| `LayoutGridRenderable` | `Layout/LayoutGrid.fs` | No | Internal `IRenderable` for lazy lowering |
| `FlexItem` | `Layout/Flex.fsi` | Yes (sealed) | Private internals: `FlexItemData` record |
| `Flex.fixed_` | `Layout/Flex.fsi` | Yes | |
| `Flex.grow` | `Layout/Flex.fsi` | Yes | |
| `Flex.shrink` | `Layout/Flex.fsi` | Yes | |
| `Flex.item` | `Layout/Flex.fsi` | Yes | |
| `FlexItemData` | `Layout/Flex.fs` | No | Internal implementation record |
| `Flex` | `Layout/Flex.fsi` | Yes (sealed) | Private internals: `FlexData` record |
| `Flex.create` | `Layout/Flex.fsi` | Yes | |
| `Flex.createWith` | `Layout/Flex.fsi` | Yes | |
| `FlexData` | `Layout/Flex.fs` | No | Internal implementation record |
| `solveFlex` | `Layout/Flex.fs` | No | Internal solver function |
| `distributeResidue` | `Layout/Flex.fs` | No | Internal rounding helper |
| `LayoutScheduler` | `Layout/Scheduler.fsi` | Yes (sealed) | Private internals: MailboxProcessor + state |
| `Scheduler.create` | `Layout/Scheduler.fsi` | Yes | |
| `Scheduler.trigger` | `Layout/Scheduler.fsi` | Yes | |
| `Scheduler.stop` | `Layout/Scheduler.fsi` | Yes | |
| `SchedulerMsg` | `Layout/Scheduler.fs` | No | Internal message DU |
| `Composition.ofStack` | `Layout/Composition.fsi` | Yes | |
| `Composition.ofDock` | `Layout/Composition.fsi` | Yes | |
| `Composition.ofLayoutGrid` | `Layout/Composition.fsi` | Yes | |
| `Composition.ofFlex` | `Layout/Composition.fsi` | Yes | |
| `layoutDepth` | `Layout/Common.fs` | No | Internal depth-check helper |
| `measureHeight` | `Layout/Dock.fs` | No | Internal measurement helper |
| `RenderError.LayoutOverflow` | `Errors.fsi` | Yes | Extension to Phase 2 DU |
| `RenderContext.Height` | `Context.fsi` | Yes | Extension to Phase 2 type |

No Phase 3 internal type leaks into any public `.fsi`. The visibility audit
confirms SC-004 (zero Spectre types in `.fsi`) and FR-012 (per-module `.fsi`
sealing) for all Phase 3 modules.

---

## Tool UX appendix

### fslangmcp queries used in this artifact

**Set project** (`mcp__fslangmcp__set_project`): loaded
`src/Fugue.Adapters.Console/Fugue.Adapters.Console.fsproj` once at the
start of the session to warm the workspace. This was the primary semantic
query — all subsequent work referenced the loaded type information.

**File symbol reads via semantic tools**: used `mcp__fslangmcp__fcs_file_symbols`
on `Context.fsi`, `Composition.fsi`, `Errors.fsi`, `SafeText.fsi`, and
`DataDisplays.fsi` to confirm existing type shapes before designing Phase 3
extensions. This was the correct tool: it returned accurate member names,
field types, and accessibility (e.g. `private` record fields confirmed for
`RenderContext`), preventing the most common failure mode of designing against
a stale mental model.

Specifically: the `Context.fsi` read via `Read` (path known, small file)
confirmed that `RenderContext` does NOT have `height` today, establishing the
Phase 3 extension baseline for §1.

**`rg` fallback on F# files (disclosed)**: used `rg` on `DataDisplays.fsi`
to locate the `Grid` type line number (for the §5 disambiguation footnote
citing `DataDisplays.fsi:216`). This was a textual search for a known
identifier at a known file, faster than a semantic round-trip for a simple
line-number citation. Matches the "line number citation" exception noted in the
global CLAUDE.md boundary heuristic. No semantic result was needed — only the
location for footnote purposes.

### Friction encountered

No significant friction. The Phase 2 data-model provided a reliable structural
template; the main work was adapting each section's content to Phase 3 decisions
rather than inventing structure. The R-1 / R-2 / R-3 / R-4 decisions in
`research.md` were crisp and required no re-debate here.

One moment of friction: the `Composition.Stack` DU case (Phase 1) and the
new `Stack` layout type (Phase 3) share a name at different levels
(DU case vs. sealed class). The naming discussion in §3 lowering required
careful phrasing to avoid confusion. `fslangmcp` was helpful here:
`fcs_file_symbols` on `Composition.fsi` confirmed the exact case name
(`| Stack of Composition list`) so the prose distinction is accurate.

### rg fallback on `.fs` files (disclosure per discipline rules)

Two uses: (1) `DataDisplays.fsi:216` line-number citation; (2) `rg` to find
the SPECKIT markers in `CLAUDE.md` (a markdown file, explicitly permitted).
No rg use on `.fs` source for semantic questions.

**No bugs to report against fslangmcp for this run.** The primary friction
was that reading multiple `.fsi` files for type confirmation could be batched
into a single "outline all these files" call — `fcs_project_outline` on the
adapter project would have returned all public symbols in one round-trip.
Noted as a workflow improvement for future artifact generation sessions.
