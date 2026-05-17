namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// ─────────────────────────────────────────────────────────────────────────────
// Internal layout depth measurement helper.
// Shared by all layout primitives (Stack, Dock, LayoutGrid, Flex) for the
// depth-100 guard (spec edge case "Recursive nesting").
// Not exported in Layout.fsi (internal to the assembly only).
// ─────────────────────────────────────────────────────────────────────────────
module internal LayoutDepth =

    /// Compute the maximum nesting depth of a Composition tree.
    /// Leaf / Aligned / Padded / Panel / Foreign = 1 structural layer.
    /// Stack / Columns / Table recurse to find the max child depth.
    let rec layoutDepth (comp: Composition) : int =
        match comp with
        | Composition.Leaf _                        -> 1
        | Composition.Aligned (_, inner)             -> 1 + layoutDepth inner
        | Composition.Padded (_, _, _, _, inner)     -> 1 + layoutDepth inner
        | Composition.Panel (_, _, inner)            -> 1 + layoutDepth inner
        | Composition.Foreign _                      -> 1
        | Composition.Stack children                 ->
            1 + (if children.IsEmpty then 0 else children |> List.map layoutDepth |> List.max)
        | Composition.Columns children               ->
            1 + (if children.IsEmpty then 0 else children |> List.map (snd >> layoutDepth) |> List.max)
        | Composition.Table (headers, rows) ->
            let hDepth = if headers.IsEmpty then 0 else headers |> List.map layoutDepth |> List.max
            let flatRows = rows |> List.concat
            let rDepth = if flatRows.IsEmpty then 0 else flatRows |> List.map layoutDepth |> List.max
            1 + max hDepth rDepth

    /// Maximum depth across a list of Compositions. Returns 0 for empty list.
    let maxChildDepth (children: Composition list) : int =
        if children.IsEmpty then 0
        else children |> List.map layoutDepth |> List.max

/// Axis along which a layout primitive distributes its children.
type Orientation =
    | Vertical
    | Horizontal

/// Alignment on the cross axis (perpendicular to the main layout axis).
/// Named `End_` with a trailing underscore to avoid clash with the F# keyword `end`.
type CrossAxisAlignment =
    | Start
    | Center
    | End_
    | Stretch

/// Policy for content that exceeds the available dimension.
type Overflow =
    | Clip
    | Strict
    | WrapNext
