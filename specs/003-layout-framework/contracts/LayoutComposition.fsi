// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  003-layout-framework
// User Story: P1–P4 (Composition lowering factories for all four layout
//             primitives)
//
// This file sketches the EXTENSION to the existing `Composition` module
// (Phase 1, `src/Fugue.Adapters.Console/Composition.fs/.fsi`) that lowers
// each Phase 3 layout type into the Phase 2 `Composition` intermediate
// representation.
//
// Implementation note:
//   The source file is `Layout/Composition.fs` / `Layout/Composition.fsi`
//   (inside the `Fugue.Adapters.Console.Layout` sub-namespace). The
//   functions are declared in `module Composition` — F# allows multiple
//   partial definitions of the same module across files within the same
//   namespace, so `Composition.ofStack` etc. are accessible to callers
//   that open `Fugue.Adapters.Console` (Phase 1 Composition) AND
//   `Fugue.Adapters.Console.Layout` (Phase 3 extensions) together.
//
//   This avoids FS0250 "This type implements or inherits the same interface
//   at different generic instantiations" — the concern is not applicable to
//   module extension, but the sub-namespace siting prevents ambiguous-open
//   issues documented in Phase 2 (`DataDisplays.fsi:14-20`).
//
// Key decisions (data-model.md §3–§6):
//   * `ofStack`      — Vertical: wraps Composition.Stack with gap separators
//                      and CrossAxisAlignment padding. Horizontal: uses
//                      Composition.Columns with spacer children.
//   * `ofDock`       — Two-pass geometry: measures edge child intrinsic
//                      sizes, allocates rectangles, wraps in Composition.Padded.
//   * `ofLayoutGrid` — Lazy IRenderable wrapper: geometry deferred to
//                      Renderer.toRawAnsi invocation time.
//   * `ofFlex`       — Lazy IRenderable wrapper: CSS §9.7 solver deferred
//                      to render time. Overflow policy applied after solve.
//
// Invariants:
//   * No C# type appears in this .fsi (SC-004).
//   * All four functions return `Composition` directly (not `Result`):
//     validation has already happened in the smart constructors of Stack,
//     Dock, LayoutGrid, and Flex. Lowering from a valid layout value is
//     total. If the underlying IRenderable encounters an error at actual
//     render time, `Renderer.toRawAnsi` propagates `Result<…, RenderError>`.
// =============================================================================

namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// Composition lowering factories for Phase 3 layout primitives
// -----------------------------------------------------------------------------

/// Extensions to the Phase 2 `Composition` module providing lowering
/// factories for the four Phase 3 layout primitives. After lowering,
/// the resulting `Composition` is renderable via `Renderer.toRawAnsi`
/// with no additional steps.
///
/// Import both namespaces to access all Composition functions:
///   open Fugue.Adapters.Console
///   open Fugue.Adapters.Console.Layout
module Composition =

    /// Lower a `Stack` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// Vertical stacks emit `Composition.Stack` with optional gap separators
    /// (blank-row `Composition.Padded` nodes) and `Composition.Aligned`
    /// wrappers for `CrossAxisAlignment.Center` / `End_`.
    ///
    /// Horizontal stacks emit `Composition.Columns` with fixed-width spacer
    /// children representing the gap.
    ///
    /// The returned `Composition` is deterministic and immutable: calling
    /// `ofStack` on the same `Stack` value twice yields structurally
    /// equivalent (though not referentially equal) trees.
    val ofStack : Stack -> Composition

    /// Lower a `Dock` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// Edge child intrinsic sizes are measured via the `IRenderable.Measure`
    /// bridge (transitively through `Composition.Foreign`). The measurement
    /// uses the active `RenderContext` width at render time. The resulting
    /// geometry is wrapped in `Composition.Padded` nodes that position each
    /// child in its allocated sub-rectangle.
    ///
    /// If edge-child measurement fails, the fallback is a plain vertical
    /// `Composition.Stack` of all children (degenerate but always renders).
    val ofDock : Dock -> Composition

    /// Lower a `LayoutGrid` layout primitive into a Phase 2 `Composition`
    /// tree.
    ///
    /// Column and row geometry (Fixed / Auto / Star sizing) is computed
    /// lazily at `Renderer.toRawAnsi` invocation time via an internal
    /// `IRenderable` wrapper. Auto columns are sized to the widest child
    /// in that column; Star columns share remaining space by ratio.
    ///
    /// Distinct from `Composition.ofGrid` (Phase 2, lowers the
    /// data-display `Grid` widget). This factory is named `ofLayoutGrid`
    /// to prevent accidental confusion at call sites.
    val ofLayoutGrid : LayoutGrid -> Composition

    /// Lower a `Flex` layout primitive into a Phase 2 `Composition` tree.
    ///
    /// The CSS §9.7 three-phase solver (basis → grow → shrink) runs at
    /// render time via an internal `IRenderable` wrapper. The `Overflow`
    /// policy stored in the `Flex` value controls behaviour when children
    /// cannot fit: `Clip` truncates, `Strict` surfaces `LayoutOverflow`,
    /// `WrapNext` continues in a new row or column.
    val ofFlex : Flex -> Composition
