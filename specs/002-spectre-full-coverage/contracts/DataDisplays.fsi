// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  002-spectre-full-coverage
// User Story: P2 (Data-display primitives)
//
// Eight typed wrappers over Spectre's static-data widgets. Every
// wrapper is an opaque sealed type with a smart constructor returning
// `Result<_, RenderError>`. Integration with the existing Phase 1
// `Composition` tree is via per-widget `Composition.of<Widget>`
// factories that lower through the `Composition.Foreign` internal
// case introduced by P1 (Renderable.fsi).
//
// Dependency note (data-model.md §0.2):
//   `Json` requires the `spectre.console.json 0.49.*` PackageReference
//   in `Fugue.Adapters.Console.fsproj`. This is a Phase 2 new
//   dependency. If the dependency cannot be added, drop `Json` and
//   re-evaluate.
//
// Invariants:
//   * No Spectre type appears in this .fsi (SC-004).
//   * Every user-content string field accepts `SafeText`, not raw
//     `string` (FR-007).
//   * Smart constructors reject degenerate inputs BEFORE render
//     (FR-005).
// =============================================================================

namespace Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// Tree — hierarchical display with line-drawing guides
// -----------------------------------------------------------------------------

/// Builder node for tree construction. Constructed only via
/// `TreeNode.leaf` / `TreeNode.branch`; collapsed into an immutable
/// `Tree` via `Tree.create`.
[<Sealed>]
type TreeNode

/// Immutable tree, ready to render. Cycle-free and depth-bounded
/// (validated at `Tree.create` time).
[<Sealed>]
type Tree

module TreeNode =

    /// Leaf node with a label. No children.
    val leaf : label: SafeText -> TreeNode

    /// Branch node with a label and ordered children.
    val branch : label: SafeText -> children: TreeNode list -> TreeNode

module Tree =

    /// Build an immutable `Tree` from a `TreeNode` root. Validates:
    ///   * no cycles (currently impossible to construct via leaf/branch
    ///     but defensively checked for forward compatibility);
    ///   * depth ≤ 1000 (defensive cap below Spectre's stack-overflow
    ///     threshold).
    val create : root: TreeNode -> Result<Tree, RenderError>

    /// Alias for `create`. Some call-sites read more naturally as
    /// `Tree.build builderNode`.
    val build : TreeNode -> Result<Tree, RenderError>

module Composition =

    val ofTree : Tree -> Composition

// -----------------------------------------------------------------------------
// Json — syntax-coloured JSON payload (requires spectre.console.json dep)
// -----------------------------------------------------------------------------

/// JSON payload primitive. Constructed from a string via
/// `Json.create`; on parse failure or size-limit overflow returns
/// `Error InvalidArgument`. Requires the `spectre.console.json`
/// PackageReference; see data-model.md §0.2.
[<Sealed>]
type Json

module Json =

    /// Parse `payload` as JSON. Returns:
    ///   * Error InvalidArgument if parsing fails (`JsonException`);
    ///   * Error InvalidArgument if payload size > 10 MiB;
    ///   * Ok Json on successful parse.
    val create : payload: string -> Result<Json, RenderError>

module Composition =

    val ofJson : Json -> Composition

// -----------------------------------------------------------------------------
// BarChart — single-series bar chart
// -----------------------------------------------------------------------------

[<Sealed>]
type BarChartItem

[<Sealed>]
type BarChart

module BarChartItem =

    /// Validates: value finite and non-negative.
    val create :
        label: SafeText ->
            value: float ->
            colour: Colour ->
            Result<BarChartItem, RenderError>

module BarChart =

    /// Validates: items non-empty, width in (0, 1000].
    val create :
        title: SafeText option ->
            items: BarChartItem list ->
            width: int ->
            Result<BarChart, RenderError>

module Composition =

    val ofBarChart : BarChart -> Composition

// -----------------------------------------------------------------------------
// BreakdownChart — proportional stacked horizontal bar
// -----------------------------------------------------------------------------

[<Sealed>]
type BreakdownChartItem

[<Sealed>]
type BreakdownChart

module BreakdownChartItem =

    /// Validates: value finite and non-negative.
    val create :
        label: SafeText ->
            value: float ->
            colour: Colour ->
            Result<BreakdownChartItem, RenderError>

module BreakdownChart =

    /// Validates: items non-empty, width in (0, 1000].
    val create :
        items: BreakdownChartItem list ->
            width: int ->
            Result<BreakdownChart, RenderError>

module Composition =

    val ofBreakdownChart : BreakdownChart -> Composition

// -----------------------------------------------------------------------------
// Calendar — month calendar widget with optional event markers
// -----------------------------------------------------------------------------

[<Sealed>]
type CalendarEvent

[<Sealed>]
type Calendar

module CalendarEvent =

    val create :
        date: System.DateOnly ->
            description: SafeText ->
            Result<CalendarEvent, RenderError>

module Calendar =

    /// Validates: year ∈ [1, 9999], month ∈ [1, 12].
    val create :
        year: int ->
            month: int ->
            events: CalendarEvent list ->
            Result<Calendar, RenderError>

module Composition =

    val ofCalendar : Calendar -> Composition

// -----------------------------------------------------------------------------
// FigletText — large-format ASCII-art text
// -----------------------------------------------------------------------------

[<Sealed>]
type FigletFont

[<Sealed>]
type FigletText

module FigletFont =

    /// The default ("standard") FIGlet font shipped with Spectre.
    val default_ : FigletFont

    /// Load a FIGlet font from a file path. Returns:
    ///   * Error InvalidArgument if the file does not exist;
    ///   * Error RenderFailed if parsing fails.
    val fromFile : path: string -> Result<FigletFont, RenderError>

    /// Load a FIGlet font from an open stream.
    val fromStream : stream: System.IO.Stream -> Result<FigletFont, RenderError>

module FigletText =

    /// Validates: text length ≤ 100 (per spec.md "very large input"
    /// edge case for FigletText).
    val create :
        text: SafeText ->
            font: FigletFont ->
            Result<FigletText, RenderError>

module Composition =

    val ofFigletText : FigletText -> Composition

// -----------------------------------------------------------------------------
// Grid — multi-column tabular layout (no borders, unlike Table)
// -----------------------------------------------------------------------------

[<Sealed>]
type GridColumn

[<Sealed>]
type Grid

module GridColumn =

    /// Construct a grid column with per-side padding and alignment.
    /// Negative padding values are clamped to 0 (debug-logged); this
    /// returns a `GridColumn` directly (not `Result`) because grid
    /// padding is purely cosmetic.
    val create :
        padding: (int * int * int * int) ->
            alignment: Alignment ->
            GridColumn

module Grid =

    /// Validates: columns non-empty, every row's length = columns.Length
    /// (spec.md edge case 1.3 — ragged rows rejected at build time).
    val create :
        columns: GridColumn list ->
            rows: Composition list list ->
            Result<Grid, RenderError>

module Composition =

    val ofGrid : Grid -> Composition

// -----------------------------------------------------------------------------
// TextPath — formatted filesystem path display
// -----------------------------------------------------------------------------

[<Sealed>]
type TextPath

module TextPath =

    /// Tolerant: any path string is acceptable (TextPath is a display
    /// widget, not a path-resolver). Returns Ok unless `path` is null.
    val create :
        path: SafeText ->
            alignment: Alignment ->
            Result<TextPath, RenderError>

module Composition =

    val ofTextPath : TextPath -> Composition
