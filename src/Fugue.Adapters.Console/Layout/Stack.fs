namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// Internal record holding validated Stack data.
// Must be `internal` (not `private`) so the Stack constructor arg is
// at least as accessible as the Stack type itself.
type internal StackData =
    { Orientation: Orientation
      Gap:         int
      Align:       CrossAxisAlignment
      Children:    Composition list }


/// Opaque sealed Stack type. Callers obtain values only via `Stack.create`.
[<Sealed>]
type Stack (data: StackData) =
    member internal _.Data = data

module Stack =

    let create
            (orientation: Orientation)
            (gap:         int)
            (align:       CrossAxisAlignment)
            (children:    Composition list)
            : Result<Stack, RenderError> =
        if children.IsEmpty then
            Error (RenderError.EmptyComposition "Stack: at least one child required")
        elif gap < 0 then
            Error (RenderError.InvalidArgument ("Stack", "gap must be non-negative"))
        else
            // Horizontal layout: cross-axis alignment beyond Start is not yet
            // implemented in MVP (tracked as Phase 3 follow-up — see tasks.md T082
            // "horizontal cross-axis alignment").
            match orientation, align with
            | Horizontal, CrossAxisAlignment.Center
            | Horizontal, CrossAxisAlignment.End_
            | Horizontal, CrossAxisAlignment.Stretch ->
                Error (RenderError.InvalidArgument
                    ("Stack", "horizontal layout currently only supports Start cross-axis alignment; Center/End_/Stretch tracked as Phase 3 follow-up"))
            | _ ->
            // Depth check: each child contributes its depth; the Stack itself adds 1.
            // Spec edge case: depth ≤ 100 permitted; depth > 100 rejected.
            // Stack itself adds 1 layer, so child's max depth must be ≤ 99 for total ≤ 100.
            // Reject when childDepth >= 100 (total would be ≥ 101).
            let childDepth = LayoutDepth.maxChildDepth children
            if childDepth >= 100 then
                Error (RenderError.InvalidArgument ("Layout", "depth exceeds 100"))
            else
                let stackData =
                    { Orientation = orientation
                      Gap         = gap
                      Align       = align
                      Children    = children }
                Ok (Stack stackData)

    // Assembly-internal accessors for LayoutComposition.fs — declared in .fsi as internal.
    let internal orientation (s: Stack) = s.Data.Orientation
    let internal gap         (s: Stack) = s.Data.Gap
    let internal align       (s: Stack) = s.Data.Align
    let internal children    (s: Stack) = s.Data.Children
