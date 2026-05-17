// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  003-layout-framework
// User Story: P1 (Stack layout primitive)
//
// `Stack` is the simplest Phase 3 layout primitive: a vertical or
// horizontal flow of child `Composition` values with a configurable gap
// and cross-axis alignment policy. It lowers into the existing
// `Composition` tree via `Composition.ofStack`.
//
// Key decisions (data-model.md §3):
//   * Opaque sealed type — no DU constructor exposed to callers.
//   * Smart constructor validates: non-empty children, non-negative gap,
//     total nesting depth ≤ 100.
//   * `Overflow` is NOT a Stack field; Stack does not clip — it lays
//     children end-to-end. Overflow handling (if the total extent exceeds
//     the terminal) is a Surface / scroll-region concern.
//   * `CrossAxisAlignment.Stretch` maps to Spectre's default row-fill;
//     `Start` / `Center` / `End_` add `Composition.Aligned` wrapping.
//
// Invariants:
//   * No C# type appears in this .fsi (SC-004 carries over from Phase 2).
//   * User content travels through Phase 1/2 `SafeText` — Stack itself
//     takes `Composition` children (not raw strings).
//   * Smart constructor rejects empty children BEFORE any render call.
// =============================================================================

namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// Orientation — axis direction (shared with Flex)
// -----------------------------------------------------------------------------

/// Axis along which a layout primitive distributes its children.
type Orientation =
    /// Children flow top-to-bottom.
    | Vertical
    /// Children flow left-to-right.
    | Horizontal

// -----------------------------------------------------------------------------
// CrossAxisAlignment — alignment perpendicular to the stack axis
// -----------------------------------------------------------------------------

/// Controls how children are positioned along the axis perpendicular to
/// the Stack's main orientation.
///
/// For a `Vertical` Stack the cross axis is horizontal (left / centre /
/// right). For a `Horizontal` Stack the cross axis is vertical (top /
/// middle / bottom).
type CrossAxisAlignment =
    /// Align children to the leading edge of the cross axis.
    | Start
    /// Centre children on the cross axis.
    | Center
    /// Align children to the trailing edge of the cross axis.
    /// Named `End_` to avoid clash with the F# keyword `end`.
    | End_
    /// Expand children to fill the full cross-axis extent.
    | Stretch

// -----------------------------------------------------------------------------
// Stack — vertical or horizontal flow of child Compositions
// -----------------------------------------------------------------------------

/// Immutable vertical-or-horizontal flow layout. Children are arranged in
/// declaration order along the main axis with a configurable gap and
/// cross-axis alignment. Constructed only via `Stack.create`.
[<Sealed>]
type Stack

module Stack =

    /// Build a `Stack` layout from an orientation, a gap, a cross-axis
    /// alignment policy, and a non-empty list of child `Composition` values.
    ///
    /// Validates:
    ///   * `children` is non-empty — `Error (EmptyComposition "Stack: …")`.
    ///   * `gap ≥ 0` — `Error (InvalidArgument ("Stack", "gap must be non-negative"))`.
    ///   * Nesting depth of any child ≤ 99 — `Error (InvalidArgument ("Layout",
    ///     "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofStack` to convert to the Phase 2
    /// `Composition` tree, which is then renderable via `Renderer.toRawAnsi`.
    val create :
        orientation: Orientation ->
            gap: int ->
            align: CrossAxisAlignment ->
            children: Composition list ->
            Result<Stack, RenderError>
