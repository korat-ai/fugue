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
///
/// SC-004 note: `Renderable.fromSpectre` is the SINGLE intentional Spectre
/// type in this adapter's public `.fsi` surface (FR-008 carve-out).
type Renderable

/// Factory for the opaque Renderable bridge.
module Renderable =

    /// Wrap a Spectre `IRenderable` into the adapter's opaque type.
    /// This is the single intentional Spectre type leak in the adapter's
    /// public API (FR-008). `null` is accepted at construction time; the
    /// failure surfaces at render time as `RenderFailed ("null", ...)`.
    val fromSpectre : Spectre.Console.Rendering.IRenderable | null -> Renderable

    /// Internal: retrieve the underlying Spectre IRenderable as obj|null for use by Renderer.fs.
    /// Caller (Renderer.fs) null-checks then downcasts to IRenderable. Not public API.
    val internal backendObj : Renderable -> obj | null

    /// Internal: retrieve the captured type name for use in RenderFailed error messages.
    /// Not part of the public adapter API.
    val internal typeName : Renderable -> string
