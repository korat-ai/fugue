namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// DockEdge DU — declared here (implementation); signature in Dock.fsi.
type DockEdge =
    | Top
    | Bottom
    | Left
    | Right
    | Fill

// Internal record holding validated Dock data.
// Must be `internal` (not `private`) so the Dock constructor arg is
// at least as accessible as the Dock type itself.
type internal DockData =
    { Children: (DockEdge * Composition) list }

/// Opaque sealed Dock type. Callers obtain values only via `Dock.create`.
[<Sealed>]
type Dock (data: DockData) =
    member internal _.Data = data

module Dock =

    let create
            (children: (DockEdge * Composition) list)
            : Result<Dock, RenderError> =
        if children.IsEmpty then
            Error (RenderError.EmptyComposition "Dock: at least one child required")
        else
            // Exactly one Fill child required (0 or 2+ are both rejected).
            let fillCount = children |> List.filter (fun (edge, _) -> edge = Fill) |> List.length
            if fillCount <> 1 then
                Error (RenderError.InvalidArgument ("Dock", "exactly one Fill child permitted"))
            else
                // Depth check: each child contributes its depth; the Dock itself adds 1.
                // Spec edge case: depth ≤ 100 permitted; depth > 100 rejected.
                // Dock itself adds 1 layer, so child's max depth must be ≤ 99 for total ≤ 100.
                let childComps = children |> List.map snd
                let childDepth = LayoutDepth.maxChildDepth childComps
                if childDepth >= 100 then
                    Error (RenderError.InvalidArgument ("Layout", "depth exceeds 100"))
                else
                    Ok (Dock { Children = children })

    // Assembly-internal accessor for LayoutComposition.fs — declared in .fsi as internal.
    let internal children (d: Dock) = d.Data.Children
