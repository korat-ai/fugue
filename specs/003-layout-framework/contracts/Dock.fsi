// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  003-layout-framework
// User Story: P2 (Dock layout primitive)
//
// `Dock` splits a region into edge-pinned sub-regions plus one fill
// region. It is the natural model for TUI chrome: status bars pinned to
// the bottom, sidebars pinned to the left/right, and a content area
// that fills whatever remains.
//
// Key decisions (data-model.md §4):
//   * Exactly one `DockEdge.Fill` child is required — enforced at
//     construction time (spec US2 acceptance scenario 3).
//   * Edge children are allocated in declaration order (Top before Bottom
//     before Left before Right before Fill).
//   * Intrinsic child height/width is measured at render time via the
//     existing `IRenderable.Measure` bridge; callers do NOT pass
//     explicit dimensions per edge.
//   * Dock does NOT expose Spectre types or `IRenderable` in this `.fsi`.
//
// Invariants:
//   * No C# type appears in this .fsi (SC-004).
//   * Smart constructor rejects: empty children, ≠1 Fill child, depth > 100.
//   * User content travels through `Composition` children — no raw strings.
// =============================================================================

namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// DockEdge — which edge a Dock child is pinned to
// -----------------------------------------------------------------------------

/// Specifies which terminal edge a child composition is pinned to within
/// a `Dock` layout. Exactly one child per `Dock` must use `Fill`.
type DockEdge =
    /// Pin this child to the top of the region. Its height = intrinsic
    /// height measured at render time.
    | Top
    /// Pin this child to the bottom of the region. Its height = intrinsic
    /// height measured at render time.
    | Bottom
    /// Pin this child to the left edge. Its width = intrinsic width
    /// measured at render time.
    | Left
    /// Pin this child to the right edge. Its width = intrinsic width
    /// measured at render time.
    | Right
    /// This child fills all space remaining after edge children are
    /// allocated. Exactly one `Fill` child is required per `Dock`.
    | Fill

// -----------------------------------------------------------------------------
// Dock — edge-pinned region split
// -----------------------------------------------------------------------------

/// Immutable edge-pinned layout. Splits a terminal region into at most
/// four edge-pinned sub-regions plus one fill region. Constructed only
/// via `Dock.create`.
///
/// Edge allocation order: `Top` → `Bottom` → `Left` → `Right` → `Fill`.
/// Children declared earlier get priority if edges would otherwise conflict.
[<Sealed>]
type Dock

module Dock =

    /// Build a `Dock` layout from a list of (edge, child) pairs.
    ///
    /// Validates:
    ///   * `children` is non-empty — `Error (EmptyComposition "Dock: …")`.
    ///   * Exactly one entry has `DockEdge.Fill` —
    ///     `Error (InvalidArgument ("Dock", "exactly one Fill child permitted"))`.
    ///   * Nesting depth of any child ≤ 99 —
    ///     `Error (InvalidArgument ("Layout", "depth exceeds 100"))`.
    ///
    /// Lowering: call `Composition.ofDock` to convert to the Phase 2
    /// `Composition` tree, which is then renderable via `Renderer.toRawAnsi`.
    val create :
        children: (DockEdge * Composition) list ->
            Result<Dock, RenderError>
