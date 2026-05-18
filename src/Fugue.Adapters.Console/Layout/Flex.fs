namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// Internal record holding validated per-item flex data.
// Must be `internal` (not `private`) so the FlexItem constructor argument is
// at least as accessible as the FlexItem type itself (same pattern as LayoutGrid.fs).
type internal FlexItemData =
    { Grow:   float
      Shrink: float
      Basis:  int
      Child:  Composition }

/// Opaque sealed FlexItem type. Callers obtain values only via
/// `Flex.fixed_`, `Flex.grow`, `Flex.shrink`, or `Flex.item`.
[<Sealed>]
type FlexItem (data: FlexItemData) =
    member internal _.Data = data

// Internal record holding validated Flex data.
// Must be `internal` (not `private`) so the Flex constructor argument is
// at least as accessible as the Flex type itself.
type internal FlexData =
    { Axis:     Orientation
      Items:    FlexItem list
      Overflow: Overflow }

/// Opaque sealed Flex type. Callers obtain values only via
/// `Flex.create` or `Flex.createWith`.
[<Sealed>]
type Flex (data: FlexData) =
    member internal _.Data = data

module Flex =

    // -------------------------------------------------------------------------
    // FlexItem validation helpers
    // -------------------------------------------------------------------------

    let private validateBasis (n: int) : Result<unit, RenderError> =
        if n < 0 then Error (RenderError.InvalidArgument ("FlexItem", "basis must be non-negative"))
        else Ok ()

    let private validateRatio (r: float) : Result<unit, RenderError> =
        if r < 0.0 then Error (RenderError.InvalidArgument ("FlexItem", "ratio must be non-negative"))
        else Ok ()

    // -------------------------------------------------------------------------
    // FlexItem smart constructors
    // -------------------------------------------------------------------------

    let fixed_ (n: int) (child: Composition) : Result<FlexItem, RenderError> =
        match validateBasis n with
        | Error e -> Error e
        | Ok () ->
            Ok (FlexItem { Grow = 0.0; Shrink = 0.0; Basis = n; Child = child })

    let grow (ratio: float) (child: Composition) : Result<FlexItem, RenderError> =
        match validateRatio ratio with
        | Error e -> Error e
        | Ok () ->
            Ok (FlexItem { Grow = ratio; Shrink = 0.0; Basis = 0; Child = child })

    let shrink (basis: int) (ratio: float) (child: Composition) : Result<FlexItem, RenderError> =
        match validateBasis basis with
        | Error e -> Error e
        | Ok () ->
            match validateRatio ratio with
            | Error e -> Error e
            | Ok () ->
                Ok (FlexItem { Grow = 0.0; Shrink = ratio; Basis = basis; Child = child })

    let item (basis: int) (grow: float) (shrink: float) (child: Composition) : Result<FlexItem, RenderError> =
        match validateBasis basis with
        | Error e -> Error e
        | Ok () ->
            match validateRatio grow with
            | Error e -> Error e
            | Ok () ->
                match validateRatio shrink with
                | Error e -> Error e
                | Ok () ->
                    Ok (FlexItem { Grow = grow; Shrink = shrink; Basis = basis; Child = child })

    // -------------------------------------------------------------------------
    // Flex smart constructors
    // -------------------------------------------------------------------------

    let private validateItems (items: FlexItem list) : Result<unit, RenderError> =
        if items.IsEmpty then
            Error (RenderError.EmptyComposition "Flex: at least one item required")
        else
            // Depth check: any item's child at depth > 98 → total > 99 → reject.
            let childComps = items |> List.map (fun i -> i.Data.Child)
            let maxDepth = LayoutDepth.maxChildDepth childComps
            if maxDepth >= 100 then
                Error (RenderError.InvalidArgument ("Layout", "depth exceeds 100"))
            else Ok ()

    let create (axis: Orientation) (items: FlexItem list) : Result<Flex, RenderError> =
        match validateItems items with
        | Error e -> Error e
        | Ok () -> Ok (Flex { Axis = axis; Items = items; Overflow = Clip })

    let createWith (axis: Orientation) (items: FlexItem list) (overflow: Overflow) : Result<Flex, RenderError> =
        match validateItems items with
        | Error e -> Error e
        | Ok () -> Ok (Flex { Axis = axis; Items = items; Overflow = overflow })

    // -------------------------------------------------------------------------
    // Assembly-internal accessors for LayoutComposition.fs lowering
    // -------------------------------------------------------------------------

    let internal axis     (f: Flex)     = f.Data.Axis
    let internal items    (f: Flex)     = f.Data.Items
    let internal overflow (f: Flex)     = f.Data.Overflow
    let internal itemGrow   (i: FlexItem) = i.Data.Grow
    let internal itemShrink (i: FlexItem) = i.Data.Shrink
    let internal itemBasis  (i: FlexItem) = i.Data.Basis
    let internal itemChild  (i: FlexItem) = i.Data.Child

    // -------------------------------------------------------------------------
    // Internal solver — exposed via module Internal for white-box tests
    // (mirrors Phase 2 P4 preflightLabels pattern + LayoutDepth precedent).
    // -------------------------------------------------------------------------

    module internal Internal =

        [<NoComparison; NoEquality>]
        type SolverItem =
            { Grow:   float
              Shrink: float
              Basis:  int }

        /// Distribute `total` cells among items whose rounded allocation would sum
        /// to `rawSum`. The residue (total - rawSum) is added to the first element.
        let private distributeResidue (total: int) (sizes: int list) : int list =
            let rawSum = sizes |> List.sum
            let residue = total - rawSum
            match sizes with
            | []     -> []
            | h :: t -> (h + residue) :: t

        /// Pure CSS §9.7 three-phase flexbox distribution solver.
        /// Returns per-item resolved sizes. The sum equals axisLen when:
        ///   - freeSpace = 0 (exact fit)
        ///   - freeSpace > 0 and at least one item has Grow > 0.0
        ///   - freeSpace < 0 and at least one item has Shrink > 0.0
        /// Otherwise (no grow / no shrink) sizes may sum ≠ axisLen; the
        /// FlexRenderable caller consults the Overflow policy.
        let solveDistribution (items: SolverItem list) (axisLen: int) : int list =
            let totalBasis = items |> List.sumBy (fun it -> it.Basis)
            let freeSpace  = axisLen - totalBasis

            if freeSpace = 0 then
                // Exact fit — basis is final.
                items |> List.map (fun it -> it.Basis)

            elif freeSpace > 0 then
                // Phase 2a: distribute free space by grow ratio.
                let totalGrow = items |> List.sumBy (fun it -> it.Grow)
                if totalGrow = 0.0 then
                    // No grow — basis stands, free space is wasted.
                    items |> List.map (fun it -> it.Basis)
                else
                    items
                    |> List.map (fun it ->
                        let share = float freeSpace * (it.Grow / totalGrow)
                        it.Basis + int (round share))
                    |> distributeResidue axisLen

            else
                // Phase 2b: shrink. freeSpace < 0 — items overflow by |freeSpace|.
                // CSS spec §9.7.4: basis-scaled shrink (larger items shrink more).
                let totalScaledShrink =
                    items |> List.sumBy (fun it -> it.Shrink * float it.Basis)
                if totalScaledShrink = 0.0 then
                    // No shrink — items keep basis; caller handles overflow policy.
                    items |> List.map (fun it -> it.Basis)
                else
                    items
                    |> List.map (fun it ->
                        let scaledShrink = it.Shrink * float it.Basis
                        let reduce =
                            float (-freeSpace) * (scaledShrink / totalScaledShrink)
                        max 0 (it.Basis - int (round reduce)))
                    |> distributeResidue axisLen
