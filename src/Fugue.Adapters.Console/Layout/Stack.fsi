namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Stack — vertical or horizontal flow of child Compositions.
//
// Key decisions (data-model.md §3):
//   * Opaque sealed type — no DU constructor exposed to callers.
//   * Smart constructor validates: non-empty children, non-negative gap,
//     total nesting depth ≤ 100 (guards against stack overflow in Renderer).
//   * Overflow handling (total extent vs terminal) is a Surface / scroll-region
//     concern — Stack does not clip, it lays children end-to-end.
//   * No upstream-library type appears in this .fsi (SC-004 invariant).
//   * No SafeText parameter — Stack composes existing Composition values;
//     user content travels through Phase 1/2 primitives.
// =============================================================================

/// Immutable vertical-or-horizontal flow layout. Children are arranged in
/// declaration order along the main axis with a configurable gap and
/// cross-axis alignment. Constructed only via `Stack.create`.
[<Sealed>]
type Stack

module Stack =

    /// Build a `Stack` layout from an orientation, a gap (in cells or rows),
    /// a cross-axis alignment policy, and a non-empty list of child
    /// `Composition` values.
    ///
    /// Validates in order:
    ///   1. `children` is non-empty →
    ///      `Error (EmptyComposition "Stack: at least one child required")`.
    ///   2. `gap >= 0` →
    ///      `Error (InvalidArgument ("Stack", "gap must be non-negative"))`.
    ///   3. Max nesting depth of any child ≤ 99 →
    ///      `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofStack` to convert to the Phase 2
    /// `Composition` tree, renderable via `Renderer.toRawAnsi`.
    val create :
        orientation: Orientation ->
            gap: int ->
            align: CrossAxisAlignment ->
            children: Composition list ->
            Result<Stack, RenderError>

    // -------------------------------------------------------------------------
    // Assembly-internal accessors for LayoutComposition.fs lowering.
    // Not part of the public API — omit from external documentation.
    // -------------------------------------------------------------------------
    val internal orientation : Stack -> Orientation
    val internal gap         : Stack -> int
    val internal align       : Stack -> CrossAxisAlignment
    val internal children    : Stack -> Composition list
