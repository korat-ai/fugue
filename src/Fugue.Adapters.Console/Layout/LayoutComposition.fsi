namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Composition lowering factories for Phase 3 layout primitives.
//
// This module EXTENDS the Phase 2 `Composition` module (same module name,
// different namespace) to add lowering factories for each layout primitive.
// After lowering, the resulting `Composition` is renderable via
// `Renderer.toRawAnsi` with no additional steps.
//
// No upstream-library type appears in this .fsi (SC-004 invariant).
// All functions return `Composition` directly (not `Result`) because
// validation has already occurred in the primitive smart constructors.
// =============================================================================

/// Lowering factories for Phase 3 layout primitives into the Phase 2
/// `Composition` intermediate representation.
///
/// Import both namespaces to access all Composition functions:
///   open Fugue.Adapters.Console
///   open Fugue.Adapters.Console.Layout
module Composition =

    /// Lower a `Stack` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// Vertical stacks emit `Composition.Stack` (a `Composition.Rows`-backed
    /// node) with optional gap separators (blank-row `Composition.Padded`
    /// nodes between children) and `Composition.Aligned` wrappers for
    /// `CrossAxisAlignment.Center` / `End_`.
    ///
    /// Horizontal stacks emit `Composition.Columns` with fixed-width spacer
    /// children representing the gap between children.
    ///
    /// The returned `Composition` is deterministic and immutable: calling
    /// `ofStack` on the same `Stack` value twice yields structurally
    /// equivalent (though not referentially equal) trees.
    val ofStack : Stack -> Composition

    /// Lower a `Dock` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// Uses a lazy-IRenderable pattern: wraps the entire Dock as a single
    /// `Composition.Foreign` node. Geometry is computed at render time when
    /// the IRenderable is measured by the Spectre renderer, so intrinsic
    /// child sizes are measured against the actual terminal width/height.
    ///
    /// Edge allocation order: Top → Bottom → Left → Right → Fill.
    /// The Fill child receives all space remaining after edge children are
    /// allocated. All edge children are stacked vertically or arranged as
    /// columns via nested `Composition.Stack` and `Composition.Columns` nodes.
    ///
    /// The returned `Composition` is deterministic and immutable: calling
    /// `ofDock` on the same `Dock` value twice yields byte-identical output
    /// at the same `RenderContext`.
    val ofDock : Dock -> Composition
