namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Composition lowering factories (Phase 3 → Phase 2 Composition IR).
// =============================================================================

module Composition =

    // -------------------------------------------------------------------------
    // Private helpers (shared with ofStack and ofDock)
    // -------------------------------------------------------------------------

    // MVP: ratio is computed against a nominal 80-column terminal width.
    // Real-width-aware ratio computation deferred to follow-up (tasks.md T082).
    let private NominalTerminalWidth = 80.0

    /// A blank separator row for vertical gap (bottom-padding only).
    /// Uses Padded with bottom=N on a zero-content Markup leaf.
    let private blankRow (rows: int) : Composition =
        Composition.Padded (0, 0, 0, rows, Composition.Leaf (Primitive.Markup ""))

    /// A fixed-width horizontal spacer for horizontal gap.
    let private spacerCol (cols: int) : float * Composition =
        let ratio = float cols / NominalTerminalWidth
        (ratio, Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral (System.String (' ', cols)))))

    /// Apply CrossAxisAlignment wrapping to a single child Composition
    /// for a vertical Stack (cross axis = horizontal).
    let private applyVerticalCrossAlign (align: CrossAxisAlignment) (child: Composition) : Composition =
        match align with
        | Start   -> child
        | Stretch -> child  // Spectre fills to parent width by default for Rows
        | Center  -> Composition.Aligned (Alignment.CentreAlign, child)
        | End_    -> Composition.Aligned (Alignment.RightAlign, child)

    /// Apply CrossAxisAlignment wrapping for a horizontal Stack (cross axis = vertical).
    let private applyHorizontalCrossAlign (align: CrossAxisAlignment) (child: Composition) : Composition =
        // Horizontal cross-axis alignment deferred per T082 (MVP).
        let _ = align
        child

    // -------------------------------------------------------------------------
    // ofStack
    // -------------------------------------------------------------------------

    /// Lower a validated `Stack` into the Phase 2 `Composition` IR.
    let ofStack (stack: Stack) : Composition =
        let children = Stack.children stack
        let gap = Stack.gap stack

        match Stack.orientation stack with
        | Vertical ->
            // Apply cross-axis alignment to each child.
            let aligned = children |> List.map (applyVerticalCrossAlign (Stack.align stack))
            // Interleave with gap separators if gap > 0.
            let interleaved =
                if gap <= 0 then
                    aligned
                else
                    let sep = blankRow gap
                    aligned
                    |> List.mapi (fun i c -> if i = 0 then [c] else [sep; c])
                    |> List.concat
            Composition.Stack interleaved

        | Horizontal ->
            let contentCount = float children.Length
            let gapCount = float (children.Length - 1)
            let gapRatio = if gap > 0 then float gap / NominalTerminalWidth else 0.0
            let totalGapRatio = gapCount * gapRatio
            let contentRatioTotal = max 0.01 (1.0 - totalGapRatio)
            let contentRatio = contentRatioTotal / contentCount

            let pairs : (float * Composition) list =
                children
                |> List.mapi (fun i child ->
                    let alignedChild = applyHorizontalCrossAlign (Stack.align stack) child
                    if i = 0 then
                        [(contentRatio, alignedChild)]
                    else
                        let (_, spacerComp) = spacerCol gap
                        [(gapRatio, spacerComp); (contentRatio, alignedChild)])
                |> List.concat

            let total = pairs |> List.sumBy fst
            let scale = if total > 1.0 + 1e-9 then 1.0 / total else 1.0
            let normalised = pairs |> List.map (fun (r, c) -> (r * scale, c))

            match Composition.columns normalised with
            | Ok comp -> comp
            | Error e ->
                // Normalisation guarantees valid ratios — reaching here is a bug.
                failwithf "Stack.Horizontal lowering produced invalid Composition.columns: %A — please file a bug" e

    // -------------------------------------------------------------------------
    // ofDock
    // -------------------------------------------------------------------------

    /// Lower a validated `Dock` into the Phase 2 `Composition` IR.
    ///
    /// Two-pass geometry (direct lowering, not lazy-IRenderable):
    ///
    /// Pass 1 — vertical separation:
    ///   Top children → stacked at the top of a vertical `Composition.Stack`.
    ///   Bottom children → stacked at the bottom.
    ///   Centre band (Left | Fill | Right) → placed between Top and Bottom rows.
    ///
    /// Pass 2 — horizontal separation (centre band):
    ///   No Left/Right → Fill child at full width (no Columns wrapper needed).
    ///   Left/Right present → `Composition.Columns` with heuristic equal ratios.
    ///   Heuristic: each Left/Right child gets an equal share of 1.0/(N+1) where
    ///   N = number of side children; Fill gets the remaining share.
    ///   MVP: exact intrinsic widths deferred to follow-up (research.md §R-4).
    ///
    /// FAIL-FAST on unreachable paths (PR-P1 review #1 lesson):
    ///   If `Composition.columns` returns Error after normalisation, it is a bug
    ///   in this function — use `failwithf`, NOT a silent fallback.
    ///
    /// Note: this approach builds a real `Composition` tree (not `Composition.Foreign`)
    /// so that depth measurement in nested Dock constructions works correctly — the
    /// depth-100 guard in `Dock.create` traverses the Composition tree; `Foreign`
    /// nodes are depth=1 and would hide deep nesting. Using a real tree ensures
    /// `Composition.Stack` wrapping increases measurable depth on each nesting level.
    let ofDock (dock: Dock) : Composition =
        let children = Dock.children dock

        // Partition children by edge type (declaration order preserved by List.filter).
        let tops    = children |> List.choose (fun (e, c) -> if e = Top    then Some c else None)
        let bottoms = children |> List.choose (fun (e, c) -> if e = Bottom then Some c else None)
        let lefts   = children |> List.choose (fun (e, c) -> if e = Left   then Some c else None)
        let rights  = children |> List.choose (fun (e, c) -> if e = Right  then Some c else None)
        let fills   = children |> List.choose (fun (e, c) -> if e = Fill   then Some c else None)

        // Safety: Dock.create validated exactly one Fill child.
        let fillComp =
            match fills with
            | [f] -> f
            | _   ->
                failwithf "ofDock: expected exactly one Fill child; got %d — Dock.create validation bug" fills.Length

        // Build the centre horizontal band.
        let centreBand : Composition =
            match lefts, rights with
            | [], [] ->
                // No side panels: Fill takes the full width.
                fillComp

            | _ ->
                // Side panels present. Assign heuristic column ratios.
                // Each side child (left or right) gets an equal share; fill gets the remainder.
                // We interleave: lefts → fill → rights, in declaration order.
                let sideCount = lefts.Length + rights.Length
                // Distribute 60% to fill, rest to sides equally.
                // Clamp sideRatio to avoid overflow if many side children.
                let sideRatio = min 0.20 (0.40 / float (max 1 sideCount))
                let fillRatio = max 0.20 (1.0 - float sideCount * sideRatio)

                let leftPairs  = lefts  |> List.map (fun c -> (sideRatio, c))
                let rightPairs = rights |> List.map (fun c -> (sideRatio, c))
                let pairs = leftPairs @ [(fillRatio, fillComp)] @ rightPairs

                // Normalise ratios to sum ≤ 1.0.
                let total = pairs |> List.sumBy fst
                let scale = if total > 1.0 + 1e-9 then 1.0 / total else 1.0
                let normalised = pairs |> List.map (fun (r, c) -> (r * scale, c))

                match Composition.columns normalised with
                | Ok comp -> comp
                | Error e ->
                    // Normalisation guarantees valid ratios — reaching here is a bug.
                    failwithf "ofDock: Composition.columns returned Error after normalisation: %A — please file a bug" e

        // Assemble vertical layout: top rows → centre band → bottom rows.
        // Empty top/bottom lists contribute nothing; the centre band is always present.
        let allRows =
            (tops |> List.map id)
            @ [centreBand]
            @ (bottoms |> List.map id)

        Composition.Stack allRows
