namespace Fugue.Adapters.Console

// =============================================================================
// Data-display primitives — Phase 2 (US2)
//
// Eight typed wrappers over Spectre's static-data widgets. Every wrapper is
// an opaque type with a smart constructor returning `Result<_, RenderError>`.
//
// Each widget module exposes `toComposition : <Widget> -> Composition` which
// lowers the widget into the adapter's Composition tree via the
// `Composition.Foreign` path established by P1 (Renderable bridge). No new
// renderer arms are needed — the Foreign arm handles all eight.
//
// NOTE on naming: F# prohibits a type (`type Composition`) and a module
// (`module Composition`) with the same name from existing in *different* files
// of the same namespace (FS0250). Since the `Composition` DU + module are
// declared in `Composition.fsi/fs` (compiled before this file), we cannot add
// a `module Composition` augmentation here. The factories are therefore
// `<Widget>.toComposition` (e.g. `Tree.toComposition`, `Json.toComposition`).
// External callers use these instead of a hypothetical `Composition.ofTree`.
//
// Invariants:
//   * No Spectre type appears in this .fsi (SC-004).
//   * Every user-content string field accepts `SafeText` (FR-007).
//   * Smart constructors reject degenerate inputs before render (FR-005).
// =============================================================================

// ---------------------------------------------------------------------------
// Tree — hierarchical display with line-drawing guides
// ---------------------------------------------------------------------------

/// Builder node for tree construction. Constructed only via
/// `TreeNode.leaf` / `TreeNode.branch`; collapsed into an immutable
/// `Tree` via `Tree.create`.
[<Sealed>]
type TreeNode

/// Immutable tree, ready to render. Depth-bounded (validated at `Tree.create`
/// time — max depth 1000).
[<Sealed>]
type Tree

module TreeNode =

    /// Leaf node with a label. No children.
    val leaf : label: SafeText -> TreeNode

    /// Branch node with a label and ordered children.
    val branch : label: SafeText -> children: TreeNode list -> TreeNode

module Tree =

    /// Build an immutable `Tree` from a `TreeNode` root. Validates depth ≤ 1000.
    /// Returns `Error (InvalidArgument ("Tree", _))` on depth overflow.
    val create : root: TreeNode -> Result<Tree, RenderError>

    /// Alias for `create`.
    val build : root: TreeNode -> Result<Tree, RenderError>

    /// Lower a `Tree` into the Composition tree via the Renderable bridge.
    val toComposition : Tree -> Composition

// ---------------------------------------------------------------------------
// Json — syntax-coloured JSON payload (requires spectre.console.json dep)
// ---------------------------------------------------------------------------

/// JSON payload primitive. Constructed from a string via `Json.create`;
/// on parse failure or size-limit overflow returns `Error InvalidArgument`.
/// Requires the `spectre.console.json` PackageReference (data-model.md §0.2).
[<Sealed>]
type Json

module Json =

    /// Parse `payload` as JSON. Returns:
    ///   * `Error InvalidArgument` if parsing fails (`JsonException`);
    ///   * `Error InvalidArgument` if payload size > 10 MiB;
    ///   * `Ok Json` on successful parse.
    val create : payload: string -> Result<Json, RenderError>

    /// Lower a `Json` into the Composition tree via the Renderable bridge.
    val toComposition : Json -> Composition

// ---------------------------------------------------------------------------
// BarChart — single-series bar chart
// ---------------------------------------------------------------------------

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

    /// Lower a `BarChart` into the Composition tree via the Renderable bridge.
    val toComposition : BarChart -> Composition

// ---------------------------------------------------------------------------
// BreakdownChart — proportional stacked horizontal bar
// ---------------------------------------------------------------------------

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

    /// Lower a `BreakdownChart` into the Composition tree via the Renderable bridge.
    val toComposition : BreakdownChart -> Composition

// ---------------------------------------------------------------------------
// Calendar — month calendar widget with optional event markers
// ---------------------------------------------------------------------------

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

    /// Lower a `Calendar` into the Composition tree via the Renderable bridge.
    val toComposition : Calendar -> Composition

// ---------------------------------------------------------------------------
// FigletText — large-format ASCII-art text
// ---------------------------------------------------------------------------

[<Sealed>]
type FigletFont

[<Sealed>]
type FigletText

module FigletFont =

    /// The default ("standard") FIGlet font (built-in).
    val default_ : FigletFont

    /// Load a FIGlet font from a file path. Returns:
    ///   * `Error InvalidArgument` if the file does not exist;
    ///   * `Error RenderFailed` if parsing fails.
    val fromFile : path: string -> Result<FigletFont, RenderError>

    /// Load a FIGlet font from an open stream.
    val fromStream : stream: System.IO.Stream -> Result<FigletFont, RenderError>

module FigletText =

    /// Validates: text length ≤ 100 (data-model.md §3.2.6).
    val create :
        text: SafeText ->
            font: FigletFont ->
            Result<FigletText, RenderError>

    /// Lower a `FigletText` into the Composition tree via the Renderable bridge.
    val toComposition : FigletText -> Composition

// ---------------------------------------------------------------------------
// Grid — multi-column tabular layout (no borders, unlike Table)
// ---------------------------------------------------------------------------

[<Sealed>]
type GridColumn

[<Sealed>]
type Grid

module GridColumn =

    /// Construct a grid column with per-side padding (left, right, top, bottom)
    /// and horizontal alignment. Negative padding values are clamped to 0;
    /// returns `GridColumn` directly (not `Result`) because padding is cosmetic.
    val create :
        padding: (int * int * int * int) ->
            alignment: Alignment ->
            GridColumn

module Grid =

    /// Validates: columns non-empty, every row's length = columns.Length
    /// (ragged rows rejected at build time — data-model.md §3.2.7).
    val create :
        columns: GridColumn list ->
            rows: Composition list list ->
            Result<Grid, RenderError>

    /// Lower a `Grid` into the Composition tree via the Renderable bridge.
    val toComposition : Grid -> Composition

// ---------------------------------------------------------------------------
// TextPath — formatted filesystem path display
// ---------------------------------------------------------------------------

[<Sealed>]
type TextPath

module TextPath =

    /// Tolerant: any path string is acceptable (display widget, not a
    /// path-resolver). Returns `Error InvalidArgument` only if `path`
    /// was constructed from a null literal (data-model.md §3.2.8).
    val create :
        path: SafeText ->
            alignment: Alignment ->
            Result<TextPath, RenderError>

    /// Lower a `TextPath` into the Composition tree via the Renderable bridge.
    val toComposition : TextPath -> Composition
