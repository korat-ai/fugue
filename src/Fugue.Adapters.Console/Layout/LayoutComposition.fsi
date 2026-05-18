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
    /// Uses the lazy-IRenderable pattern (data-model.md §4 "Practical lowering"):
    /// wraps the entire Dock as a single `Composition.Foreign` node whose
    /// `embeddedDepth` carries the actual pre-lowering depth so that
    /// `LayoutDepth.layoutDepth` sees the true nesting depth (not depth=1).
    ///
    /// Geometry is computed at render time inside a private `DockRenderable`
    /// (implements the upstream IRenderable interface) using the actual
    /// `maxWidth` and `ConsoleSize.Height` from the upstream render options.
    /// Each edge child is rendered via `Renderer.toRawAnsi` at its allocated
    /// size; the Fill child receives all space remaining after edges are
    /// measured. Regions are stitched with ANSI CUP cursor-position escapes
    /// (`\x1b[row;colH`).
    ///
    /// Edge allocation order: Top → Bottom → Left → Right → Fill.
    ///
    /// FAIL-FAST: if any child render fails during `DockRenderable.Render`,
    /// the error propagates as `failwithf` (no silent fallbacks per PR-P1 lesson).
    ///
    /// The returned `Composition` is deterministic and immutable: calling
    /// `ofDock` on the same `Dock` value twice yields byte-identical output
    /// at the same `RenderContext` dimensions.
    val ofDock : Dock -> Composition

    /// Lower a `LayoutGrid` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// Uses the lazy-IRenderable pattern (data-model.md §5 "Practical lowering"):
    /// wraps the entire LayoutGrid as a single `Composition.Foreign` node whose
    /// `embeddedDepth` carries the actual pre-lowering depth so that
    /// `LayoutDepth.layoutDepth` sees the true nesting depth (not depth=1).
    ///
    /// Two-pass geometry at render time inside a private `LayoutGridRenderable`:
    ///   Pass 1 — column widths: Fixed allocated → Auto measured via Spectre's
    ///     transitive measurement → Star distributed proportionally by ratio.
    ///   Pass 1b — row heights: same three phases.
    ///   Pass 2 — cells stitched via ANSI CUP cursor-position escapes
    ///     (`\x1b[row;colH`) at each cell's allocated (col_x, row_y) position.
    ///
    /// FAIL-FAST: if any child render fails during `LayoutGridRenderable.Render`,
    /// the error propagates as `failwithf` (no silent fallbacks per PR-P1 lesson).
    ///
    /// The returned `Composition` is deterministic and immutable: calling
    /// `ofLayoutGrid` on the same `LayoutGrid` value twice yields byte-identical
    /// output at the same `RenderContext` dimensions.
    val ofLayoutGrid : LayoutGrid -> Composition
