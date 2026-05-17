namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Composition lowering factories (Phase 3 → Phase 2 Composition IR).
// =============================================================================

module Composition =

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// A blank separator row for vertical gap (bottom-padding only).
    /// Uses Padded with bottom=N on a zero-content Markup leaf.
    let private blankRow (rows: int) : Composition =
        // Composition.Padded(left, right, top, bottom, inner)
        Composition.Padded (0, 0, 0, rows, Composition.Leaf (Primitive.Markup ""))

    /// A fixed-width horizontal spacer for horizontal gap.
    /// Uses a string of `cols` space characters, styled with empty Style.
    let private spacerCol (cols: int) : float * Composition =
        // Ratio: we allocate a narrow spacer relative to the full width.
        // For horizontal stacks we use the column count as a fixed-ratio spacer.
        // The ratio is expressed as a fraction of 1.0; for a spacer of `cols`
        // display columns inside an 80-column terminal, ratio = cols / 80.0.
        // However, since `Composition.Columns` requires ratios summing to ≤ 1.0,
        // we use a small epsilon ratio for spacers and rely on Spectre's layout.
        // Practical choice: spacer gets ratio proportional to its column count
        // out of a nominal 80-column grid (safe for typical terminals).
        // If the ratio sum would exceed 1.0, Spectre clips — acceptable for MVP.
        let ratio = float cols / 80.0
        (ratio, Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral (System.String (' ', cols)))))

    /// Apply CrossAxisAlignment wrapping to a single child Composition
    /// for a vertical Stack (cross axis = horizontal).
    let private applyVerticalCrossAlign (align: CrossAxisAlignment) (child: Composition) : Composition =
        match align with
        | Start   -> child
        | Stretch -> child  // Spectre fills to parent width by default for Rows
        | Center  -> Composition.Aligned (Alignment.CentreAlign, child)
        | End_    -> Composition.Aligned (Alignment.RightAlign, child)

    /// Apply CrossAxisAlignment wrapping to a single child Composition
    /// for a horizontal Stack (cross axis = vertical — we can't express
    /// vertical alignment inside Columns without intrinsic height, so
    /// we leave it as-is for MVP; Spectre aligns to top by default).
    let private applyHorizontalCrossAlign (align: CrossAxisAlignment) (child: Composition) : Composition =
        // Horizontal stack cross axis = vertical. Spectre.Console.Columns
        // aligns children to the top by default. We cannot control this
        // without measuring intrinsic heights, which requires render-time info.
        // For MVP, alignment is deferred; the child is returned unchanged.
        // Follow-up: full cross-axis alignment for Horizontal requires the
        // lazy IRenderable approach used by LayoutGrid/Flex (T082).
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
            // For horizontal layout we use Composition.Columns.
            // Columns requires (ratio, child) pairs summing to ≤ 1.0.
            // We assign equal ratios to content children and small ratios
            // to gap spacers, then normalise to sum ≤ 1.0.
            let contentCount = float children.Length
            let gapCount = float (children.Length - 1) // gaps between children
            let gapRatio = if gap > 0 then float gap / 80.0 else 0.0
            let totalGapRatio = gapCount * gapRatio
            // Remaining ratio for content children (capped at 1.0 − totalGap).
            let contentRatioTotal = max 0.01 (1.0 - totalGapRatio)
            let contentRatio = contentRatioTotal / contentCount

            let pairs : (float * Composition) list =
                children
                |> List.mapi (fun i child ->
                    let alignedChild = applyHorizontalCrossAlign (Stack.align stack) child
                    if i = 0 then
                        [(contentRatio, alignedChild)]
                    else
                        // Insert gap spacer before each non-first child.
                        let (_, spacerComp) = spacerCol gap
                        [(gapRatio, spacerComp); (contentRatio, alignedChild)])
                |> List.concat

            // Clamp total to 1.0 to stay within Composition.columns invariant.
            let total = pairs |> List.sumBy fst
            let scale = if total > 1.0 + 1e-9 then 1.0 / total else 1.0
            let normalised = pairs |> List.map (fun (r, c) -> (r * scale, c))

            // Use Composition.Columns (validated smart ctor) or fallback to vertical Stack.
            match Composition.columns normalised with
            | Ok comp -> comp
            | Error _ ->
                // Fallback: if ratio validation fails (can happen with very many
                // children or very large gap), degrade to a vertical stack.
                Composition.Stack (children |> List.map (applyVerticalCrossAlign (Stack.align stack)))
