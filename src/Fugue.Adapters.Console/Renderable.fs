namespace Fugue.Adapters.Console

// Local type alias — keeps the Spectre namespace out of the rest of this file.
type private IRenderable = Spectre.Console.Rendering.IRenderable

/// Opaque wrapper around any externally-defined Spectre renderable.
/// The private record fields are only accessible within this assembly
/// via the internal accessors below (used by Renderer.fs after
/// Composition.Foreign is matched in toRenderable).
type Renderable =
    private { Backend: IRenderable | null; TypeName: string }

/// Factory and internal accessors for the Renderable bridge.
module Renderable =

    /// Wrap a Spectre `IRenderable` into the adapter's opaque type.
    /// Captures `r.GetType().FullName` at construction time into `TypeName`
    /// so that error messages remain informative even if the backend
    /// IRenderable's own `ToString` throws later.
    /// `null` is accepted — the null backend is stored as-is and the failure
    /// surfaces at render time as `RenderFailed ("null", ...)`.
    let fromSpectre (r: IRenderable | null) : Renderable =
        let typeName =
            match r with
            | null -> "null"
            | nonNull ->
                match nonNull.GetType().FullName with
                | null -> "unknown"
                | name -> name
        { Backend = r; TypeName = typeName }

    /// Internal accessor: retrieve the underlying Spectre IRenderable as obj.
    /// Used exclusively by `Renderer.fs` — it downcasts to IRenderable.
    /// Returned as obj to avoid exposing the Spectre type in the .fsi (SC-004).
    let internal backendObj (r: Renderable) : obj | null = box r.Backend

    /// Internal accessor: retrieve the captured type name for error messages.
    /// Used exclusively by `Renderer.fs` when the backend throws.
    let internal typeName (r: Renderable) : string = r.TypeName
