// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  002-spectre-full-coverage
// User Story: P1 (Custom-renderable bridge)
//
// This file sketches the public API surface for the opaque IRenderable
// bridge described in research.md §R1 and data-model.md §2. It is
// COMPILATION-PLAUSIBLE against the existing Phase 1 sources (assumes
// Composition.fs declares the new `internal Foreign` case — see
// "Companion change" comment near the bottom).
//
// Invariants:
//   * The ONLY Spectre type appearing in this contract is the
//     `IRenderable` argument to `Renderable.fromSpectre`. This is the
//     single intentional Spectre type leak per FR-008.
//   * `Renderable` is opaque (sealed, private record).
//   * `Composition.ofRenderable` lowers to an `internal` DU case that
//     external pattern matches cannot reference (data-model.md §0.3).
// =============================================================================

namespace Fugue.Adapters.Console

/// Opaque wrapper around any externally-defined Spectre renderable.
/// Constructed via `Renderable.fromSpectre`; embedded into a
/// `Composition` tree via `Composition.ofRenderable`.
///
/// Rendering a `Renderable` whose backend throws is captured at the
/// adapter boundary and surfaced as
/// `Result.Error (RenderError.RenderFailed (typeName, message))`
/// (FR-009). The `typeName` is captured at `fromSpectre` time so it is
/// still informative even if the throwing renderable's own `ToString`
/// throws too.
[<Sealed>]
type Renderable

module Renderable =

    /// Wrap a Spectre `IRenderable` into the adapter's opaque type.
    /// This is the single intentional Spectre type leak in the
    /// adapter's public API (FR-008). Callers writing
    /// `Renderable.fromSpectre ir` are visibly opting into the one
    /// place Spectre is acknowledged at the call site.
    ///
    /// `null` is accepted at construction time; the failure surfaces
    /// at render time as `RenderFailed ("System.NullReferenceException",
    /// _)`. Up-front null checks are deliberately avoided because F#
    /// callers cannot easily produce a null `IRenderable` without
    /// reflection or C# interop.
    val fromSpectre : Spectre.Console.Rendering.IRenderable -> Renderable

/// Extension to the existing Phase 1 `Composition` module.
///
/// COMPANION CHANGE required in `src/Fugue.Adapters.Console/Composition.fs`:
///
///     // (existing public cases unchanged)
///     | Leaf    of Primitive
///     | Stack   of Composition list
///     | Padded  of left: int * right: int * top: int * bottom: int * inner: Composition
///     | Panel   of border: Border * header: SafeText option * inner: Composition
///     | Columns of children: (float * Composition) list
///     | Aligned of Alignment * inner: Composition
///     | Table   of headers: Composition list * rows: Composition list list
///
///     // NEW: internal-only case, not visible in Composition.fsi to
///     //      external callers. Their existing match arms remain
///     //      exhaustive (F# does not warn against missing arms for
///     //      internal-only constructors viewed from another assembly).
///     | internal Foreign of Renderable
///
/// Composition.fsi DOES NOT add this case to its public DU declaration
/// (Phase 1 backward compatibility — SC-005). The internal case is
/// only visible inside the `Fugue.Adapters.Console` assembly, which is
/// exactly where the renderer needs it.
module Composition =

    /// Embed a `Renderable` as a `Composition` node. Internally maps
    /// to the private `Composition.Foreign` case described in the
    /// companion-change comment above.
    val ofRenderable : Renderable -> Composition
