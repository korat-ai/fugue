namespace Fugue.Adapters.Console

/// Border style for Panel compositions. Maps to Spectre's `BoxBorder`.
type Border =
    | None_
    | Square
    | Rounded
    | Heavy

/// Horizontal alignment for Aligned compositions. Maps to Spectre's
/// `HorizontalAlignment`. Named with a suffix to avoid clashing with
/// Spectre's type in files that open both namespaces.
type Alignment =
    | LeftAlign
    | CentreAlign
    | RightAlign

/// Recursive layout tree. Leaf wraps a single Primitive; all other cases
/// are composition nodes. Immutable — a Composition value can be cached or
/// memoised. See data-model.md §5.
///
/// Adding a new case forces every Renderer match arm to update at compile
/// time (Principle I leverage via exhaustive matching).
type Composition =
    /// Leaf — wraps a single Primitive.
    | Leaf of Primitive
    /// Vertically stacked children. Maps to Spectre's `Rows`.
    | Stack of Composition list
    /// Inner content padded by N cells left/right and M rows top/bottom.
    /// Smart constructor `Composition.padded` rejects negative values.
    | Padded of left: int * right: int * top: int * bottom: int * inner: Composition
    /// Border-decorated panel, optional header text. Maps to Spectre's
    /// `Panel` with optional `PanelHeader`.
    | Panel of border: Border * header: SafeText option * inner: Composition
    /// Horizontal columns. Width hints as ratios summing to ≤ 1.0.
    /// Smart constructor `Composition.columns` validates ratios.
    | Columns of children: (float * Composition) list
    /// Alignment within the available width. Maps to Spectre's `Align`.
    | Aligned of Alignment * inner: Composition
    /// Markdown-style table. Headers determine the column count; every body
    /// row must have the same count. Smart constructor validates.
    | Table of headers: Composition list * rows: Composition list list

/// Smart constructors for Composition nodes that can fail structurally.
/// Non-validating constructors (`stack`, `panel`, `aligned`) build the DU
/// directly; validating ones (`padded`, `columns`, `table`) return Result.
module Composition =

    /// Vertically stack children. Empty list is allowed (renders as nothing).
    val stack: children: Composition seq -> Composition

    /// Pad inner content. Returns Error if any padding value is negative
    /// (negative padding is meaningless and indicates a caller bug).
    val padded:
        left: int ->
            right: int ->
            top: int ->
            bottom: int ->
            inner: Composition -> Result<Composition, RenderError>

    /// Wrap inner content in a border-styled panel with an optional header.
    /// No structural validation — header is optional and all border styles
    /// are valid at construction time.
    val panel:
        border: Border -> header: SafeText option -> inner: Composition -> Composition

    /// Lay out children in horizontal columns. Each child is given a ratio
    /// (0.0–1.0) of the available width; ratios MUST sum to ≤ 1.0.
    /// Returns Error if any ratio is negative, > 1.0 + 1e-9, or the sum exceeds 1.0 + 1e-9.
    val columns:
        children: (float * Composition) seq -> Result<Composition, RenderError>

    /// Align inner content within the available width.
    val aligned: alignment: Alignment -> inner: Composition -> Composition

    /// Build a table from a header row and body rows. Every body row must
    /// have the same column count as the header row; ragged rows are
    /// rejected at build time. Empty header is also rejected.
    val table:
        headers: Composition seq ->
            rows: Composition seq seq -> Result<Composition, RenderError>
