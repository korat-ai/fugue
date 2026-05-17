// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  003-layout-framework
// User Story: P3 (Grid layout primitive)
//
// `LayoutGrid` is a WPF-style 2D layout with explicit column and row
// sizing (Fixed / Auto / Star). It is named `LayoutGrid` — not `Grid` —
// to prevent F# unqualified-resolution ambiguity with the Phase 2
// data-display `Grid` widget (`Fugue.Adapters.Console.DataDisplays.Grid`,
// `src/Fugue.Adapters.Console/DataDisplays.fsi:216,228`).
//
// Disambiguation rule (data-model.md §5):
//   * `LayoutGrid` is the canonical public type name in this contract.
//   * The companion module is also `LayoutGrid`.
//   * Within a file that opens `Fugue.Adapters.Console.Layout`, the
//     resolver sees `LayoutGrid` unambiguously.
//   * Files that open BOTH `Fugue.Adapters.Console` (DataDisplays) AND
//     `Fugue.Adapters.Console.Layout` must qualify the widget Grid as
//     `DataDisplays.Grid` to avoid confusion — a code review concern,
//     not a compiler concern (they are different identifiers).
//
// Key decisions (data-model.md §5):
//   * Two-pass sizing: Fixed first, then Auto (content-measured), then
//     Star (proportional remainder). Same three-phase model as WPF Grid
//     and CSS Grid `fr` units.
//   * Ragged rows (cells.Length ≠ columns.Length for any row) are
//     rejected at construction time.
//   * Lowering defers geometry to render time via an internal
//     `IRenderable` wrapper — the `ofLayoutGrid` factory returns a
//     `Composition` that computes column/row sizes from `RenderContext`
//     on demand.
//
// Invariants:
//   * No C# type appears in this .fsi (SC-004).
//   * Smart constructor validates: non-empty columns, non-empty rows,
//     conforming cell count per row, positive Fixed sizes, positive Star ratios.
//   * User content arrives as `Composition` cell children — no raw strings.
// =============================================================================

namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

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
    /// columns are allocated. `ratio` must be ≥ 1. Multiple Star columns
    /// share remaining space proportionally by ratio.
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
// LayoutGrid — 2D tabular layout with explicit column and row sizing
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
    /// Validates:
    ///   * `columns` is non-empty —
    ///     `Error (EmptyComposition "LayoutGrid: at least one column required")`.
    ///   * `rows` is non-empty —
    ///     `Error (EmptyComposition "LayoutGrid: at least one row required")`.
    ///   * `cells.Length = rows.Length` —
    ///     `Error (InvalidArgument ("LayoutGrid", "expected N rows of cells, got M"))`.
    ///   * Each `cells[i].Length = columns.Length` —
    ///     `Error (InvalidArgument ("LayoutGrid", "row i has M cells, expected K"))`.
    ///   * Any `Fixed w` with `w < 1` or `Fixed h` with `h < 1` —
    ///     `Error (InvalidArgument ("LayoutGrid", "Fixed size must be ≥ 1"))`.
    ///   * Any `Star r` with `r < 1` —
    ///     `Error (InvalidArgument ("LayoutGrid", "Star ratio must be ≥ 1"))`.
    ///   * Nesting depth of any cell child ≤ 98 —
    ///     `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofLayoutGrid` to convert to the Phase 2
    /// `Composition` tree. Column and row geometry is computed lazily when
    /// `Renderer.toRawAnsi` is invoked with a concrete `RenderContext`.
    val create :
        columns: ColumnSize list ->
            rows: RowSize list ->
            cells: Composition list list ->
            Result<LayoutGrid, RenderError>
