namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Flex — CSS flexbox-inspired axis distribution primitive.
//
// Key decisions (data-model.md §6):
//   * `FlexItem` is a separate opaque sealed type — callers use the
//     `Flex.fixed_` / `Flex.grow` / `Flex.shrink` / `Flex.item` smart
//     constructors. Item parameters are validated at item-construction time.
//   * `Overflow` (defined in `Layout.fsi` via `Common.fs`) controls
//     behaviour when children cannot be shrunk further:
//       - `Clip`     — truncate to available space (default).
//       - `Strict`   — return `Error (LayoutOverflow ("Flex", …))`.
//       - `WrapNext` — continue in a new row/column.
//   * Solver: CSS §9.7 three-phase (basis → grow → shrink).
//     See research.md §B-1 for full pseudocode.
//   * `fixed` is a reserved keyword in some F# positions; the function
//     is named `fixed_` here with a trailing underscore.
//
// No C# type appears in this .fsi (SC-004 invariant).
// =============================================================================

// -----------------------------------------------------------------------------
// FlexItem — per-child flex sizing descriptor
// -----------------------------------------------------------------------------

/// An opaque descriptor for a single child within a `Flex` layout. Carries
/// grow ratio, shrink ratio, initial basis (in terminal cells), and the
/// child `Composition`. Constructed only via `Flex.fixed_`, `Flex.grow`,
/// `Flex.shrink`, or `Flex.item`.
[<Sealed>]
type FlexItem

// -----------------------------------------------------------------------------
// Flex — flexbox-like axis distribution
// -----------------------------------------------------------------------------

/// Immutable flexbox-like layout. Distributes available space along a
/// main axis among a non-empty list of `FlexItem` children according to
/// each item's grow, shrink, and basis declarations. Constructed only via
/// `Flex.create` or `Flex.createWith`.
[<Sealed>]
type Flex

module Flex =

    // --- FlexItem smart constructors ----------------------------------------

    /// Fixed-size item: `basis = n` terminal cells, `grow = 0.0`, `shrink = 0.0`.
    /// The item neither grows into free space nor shrinks when space is scarce.
    ///
    /// Validates: `n ≥ 0` — `Error (InvalidArgument ("FlexItem", "…"))`.
    val fixed_ :
        n: int ->
            child: Composition ->
            Result<FlexItem, RenderError>

    /// Grow-only item: `basis = 0`, `grow = ratio`, `shrink = 0.0`.
    /// Starts at zero width/height and absorbs free space in proportion to `ratio`.
    ///
    /// Validates: `ratio ≥ 0.0` — `Error (InvalidArgument ("FlexItem", "ratio must be non-negative"))`.
    val grow :
        ratio: float ->
            child: Composition ->
            Result<FlexItem, RenderError>

    /// Shrink-capable item: `basis = n`, `grow = 0.0`, `shrink = ratio`.
    /// Starts at `n` cells and yields space proportionally when items overflow.
    ///
    /// Validates: `n ≥ 0`, `ratio ≥ 0.0` — `Error (InvalidArgument ("FlexItem", "…"))`.
    val shrink :
        basis: int ->
            ratio: float ->
            child: Composition ->
            Result<FlexItem, RenderError>

    /// Full-control item: explicit `basis`, `grow`, and `shrink`.
    ///
    /// Validates: `basis ≥ 0`, `grow ≥ 0.0`, `shrink ≥ 0.0`.
    val item :
        basis: int ->
            grow: float ->
            shrink: float ->
            child: Composition ->
            Result<FlexItem, RenderError>

    // --- Flex smart constructors ---------------------------------------------

    /// Build a `Flex` layout along `axis` from a non-empty list of `FlexItem`
    /// values. Uses `Overflow.Clip` as the default overflow policy.
    ///
    /// Validates:
    ///   * `items` is non-empty — `Error (EmptyComposition "Flex: …")`.
    ///   * Nesting depth of any item's child ≤ 98 —
    ///     `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofFlex` to convert to the Phase 2
    /// `Composition` tree. The CSS §9.7 three-phase solver runs at render
    /// time when the concrete `RenderContext` (width/height) is known.
    val create :
        axis: Orientation ->
            items: FlexItem list ->
            Result<Flex, RenderError>

    /// Same as `create` with an explicit `overflow` policy.
    val createWith :
        axis: Orientation ->
            items: FlexItem list ->
            overflow: Overflow ->
            Result<Flex, RenderError>

    // --- Assembly-internal accessors for LayoutComposition.fs lowering ------

    val internal axis     : Flex -> Orientation
    val internal items    : Flex -> FlexItem list
    val internal overflow : Flex -> Overflow
    val internal itemGrow   : FlexItem -> float
    val internal itemShrink : FlexItem -> float
    val internal itemBasis  : FlexItem -> int
    val internal itemChild  : FlexItem -> Composition

    // --- Internal solver for direct unit testing (mirrors LayoutDepth precedent) ---

    module internal Internal =

        /// Plain data record exposed for white-box testing of solveDistribution.
        /// Not part of the public API.
        [<NoComparison; NoEquality>]
        type SolverItem =
            { Grow:   float
              Shrink: float
              Basis:  int }

        /// Pure flexbox distribution solver (research.md §B-1).
        /// Returns per-item resolved sizes summing to axisLen (when not overflowing).
        val solveDistribution : items: SolverItem list -> axisLen: int -> int list
