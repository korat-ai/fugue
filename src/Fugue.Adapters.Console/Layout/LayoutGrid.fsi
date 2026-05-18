namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// LayoutGrid — 2D tabular layout with explicit column and row sizing.
//
// Key decisions (data-model.md §5):
//   * Two-pass sizing: Fixed first, then Auto (content-measured), then
//     Star (proportional remainder). Same three-phase model as WPF Grid
//     and CSS Grid `fr` units.
//   * Ragged rows (cells.Length ≠ columns.Length for any row) are
//     rejected at construction time.
//   * Lowering defers geometry to render time via an internal
//     IRenderable wrapper — the `ofLayoutGrid` factory returns a
//     Composition that computes column/row sizes from RenderContext
//     on demand.
//
// Disambiguation: the type is named `LayoutGrid` — not `Grid` — to
// prevent F# unqualified-resolution ambiguity with the Phase 2 widget
// `Fugue.Adapters.Console.DataDisplays.Grid`. See data-model.md §5 for
// the full rationale.
//
// No C# type appears in this .fsi (SC-004 invariant).
// =============================================================================

// -----------------------------------------------------------------------------
// ColumnSize — per-column sizing policy
// -----------------------------------------------------------------------------

/// Sizing policy for a single column in a `LayoutGrid`.
type ColumnSize =
    /// Exactly `width` terminal columns wide. `width` must be ≥ 1.
    | Fixed of width: int
    /// Size to the intrinsic width of the widest cell in this column.
    | Auto
    /// Proportional share of remaining space after `Fixed` and `Auto`
    /// columns are allocated. `ratio` must be ≥ 1.
    | Star of ratio: int

// -----------------------------------------------------------------------------
// RowSize — per-row sizing policy
// -----------------------------------------------------------------------------

/// Sizing policy for a single row in a `LayoutGrid`.
type RowSize =
    /// Exactly `height` terminal rows tall. `height` must be ≥ 1.
    | Fixed of height: int
    /// Size to the intrinsic height of the tallest cell in this row.
    | Auto
    /// Proportional share of remaining height after `Fixed` and `Auto`
    /// rows are allocated. `ratio` must be ≥ 1.
    | Star of ratio: int

// -----------------------------------------------------------------------------
// LayoutGrid — opaque sealed type
// -----------------------------------------------------------------------------

/// Immutable 2D grid layout. Lays child `Composition` values into a
/// rows × columns grid whose column widths and row heights are determined
/// by a combination of Fixed, Auto, and Star sizing policies. Constructed
/// only via `LayoutGrid.create`.
///
/// Distinct from `Fugue.Adapters.Console.DataDisplays.Grid` (a
/// table-without-borders display widget). See data-model.md §5 for the
/// disambiguation rationale.
[<Sealed>]
type LayoutGrid

module LayoutGrid =

    /// Build a `LayoutGrid` from column sizes, row sizes, and a 2D cell
    /// matrix. `cells` is a list of rows; each row is a list of
    /// `Composition` values, one per column.
    ///
    /// Validates in order:
    ///   * `columns` is non-empty —
    ///     `Error (EmptyComposition "LayoutGrid: at least one column required")`.
    ///   * `rows` is non-empty —
    ///     `Error (EmptyComposition "LayoutGrid: at least one row required")`.
    ///   * Each `cells[i].Length = columns.Length` —
    ///     `Error (InvalidArgument ("LayoutGrid", "row i has M cells, expected K"))`.
    ///   * Any `Fixed w` or `Fixed h` with value `< 1` —
    ///     `Error (InvalidArgument ("LayoutGrid", "Fixed size must be ≥ 1"))`.
    ///   * Any `Star r` with `r < 1` —
    ///     `Error (InvalidArgument ("LayoutGrid", "Star ratio must be ≥ 1"))`.
    ///   * Any cell child at nesting depth > 98 —
    ///     `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofLayoutGrid` to convert to the Phase 2
    /// `Composition` tree, renderable via `Renderer.toRawAnsi`.
    val create :
        columns: ColumnSize list ->
            rows: RowSize list ->
            cells: Composition list list ->
            Result<LayoutGrid, RenderError>

    // -------------------------------------------------------------------------
    // Assembly-internal accessors for LayoutComposition.fs lowering.
    // Not part of the public API.
    // -------------------------------------------------------------------------
    val internal columns : LayoutGrid -> ColumnSize list
    val internal rows    : LayoutGrid -> RowSize list
    val internal cells   : LayoutGrid -> Composition list list
