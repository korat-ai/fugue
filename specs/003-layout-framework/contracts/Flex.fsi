// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  003-layout-framework
// User Story: P4 (Flex layout primitive)
//
// `Flex` is a CSS flexbox-inspired axis distribution primitive: items
// declare grow/shrink/basis sizing and the solver distributes available
// space proportionally. It is the most expressive single-axis layout
// primitive in Phase 3.
//
// Key decisions (data-model.md §6):
//   * `FlexItem` is a separate opaque sealed type (not a plain record)
//     so callers use the `Flex.fixed_` / `Flex.grow` / `Flex.shrink` /
//     `Flex.item` smart constructors — item parameters are validated at
//     item-construction time, before `Flex.create` is called.
//   * `Overflow` (see `Stack.fsi`) controls behaviour when children
//     cannot be shrunk further:
//       - `Clip`     — truncate to available space (default).
//       - `Strict`   — return `Error (LayoutOverflow ("Flex", …))`.
//       - `WrapNext` — continue in a new row/column.
//   * Solver algorithm: CSS §9.7 three-phase (basis → grow → shrink).
//     See research.md §B-1 for full pseudocode.
//   * `fixed` is a reserved keyword in some F# positions; the function
//     is named `fixed_` here. Callers who prefer the name `fixed` may
//     use `let fixed = Flex.fixed_` locally.
//
// Invariants:
//   * No C# type appears in this .fsi (SC-004).
//   * `FlexItem` constructors validate non-negative basis, grow, shrink
//     BEFORE `Flex.create` is called.
//   * `Flex.create` / `Flex.createWith` validate: non-empty items list,
//     depth ≤ 100.
//   * `Overflow` DU is defined here (shared with Stack; Stack does not
//     use it, but it lives in `Common.fsi` — repeated here for
//     readability of this standalone sketch).
// =============================================================================

namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// Overflow — policy for content that exceeds available dimension
// -----------------------------------------------------------------------------

/// Controls behaviour when `Flex` items cannot fit within the available
/// terminal dimension along the main axis.
type Overflow =
    /// Silently clip content to the available dimension. Default.
    | Clip
    /// Return `Error (LayoutOverflow (layoutKind, detail))` — the caller
    /// handles the failure explicitly.
    | Strict
    /// Wrap overflowing items to the next row (Horizontal axis) or
    /// next column (Vertical axis).
    | WrapNext

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

    /// Fixed-size item: `basis = n` terminal cells, `grow = 0`, `shrink = 0`.
    /// The item neither grows into free space nor shrinks when space is scarce.
    ///
    /// Validates: `n ≥ 0` — `Error (InvalidArgument ("FlexItem", "…"))`.
    val fixed_ :
        n: int ->
            child: Composition ->
            Result<FlexItem, RenderError>

    /// Grow-only item: `basis = 0`, `grow = ratio`, `shrink = 0`.
    /// Starts at zero width/height and absorbs free space in proportion to `ratio`.
    ///
    /// Validates: `ratio ≥ 0.0` — `Error (InvalidArgument ("FlexItem", "…"))`.
    val grow :
        ratio: float ->
            child: Composition ->
            Result<FlexItem, RenderError>

    /// Shrink-capable item: `basis = n`, `grow = 0`, `shrink = ratio`.
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
