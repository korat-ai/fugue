namespace Fugue.Adapters.Console.Layout

open Fugue.Adapters.Console

// =============================================================================
// Composition lowering factories (Phase 3 → Phase 2 Composition IR).
// =============================================================================

// Local type aliases — keep Spectre types out of the .fsi (SC-004).
type private IRenderable    = Spectre.Console.Rendering.IRenderable
type private Segment        = Spectre.Console.Rendering.Segment
type private Measurement    = Spectre.Console.Rendering.Measurement
type private RenderOptions  = Spectre.Console.Rendering.RenderOptions

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
    // ofDock — lazy-IRenderable pattern (CEO option b.1)
    // -------------------------------------------------------------------------

    // Count display columns in an ANSI string by stripping escape sequences.
    // Strips CSI escape sequences (\x1b[...m and other CSI sequences) and
    // cursor-position sequences (\x1b[row;colH), then counts UTF-16 chars
    // as a proxy for display columns. This is a conservative approximation
    // sufficient for region allocation; CJK double-width is not handled in MVP.
    let private displayWidth (s: string) : int =
        // Strip all CSI escape sequences: ESC [ ... terminator (0x40–0x7e)
        let mutable i = 0
        let mutable cols = 0
        let len = s.Length
        while i < len do
            if i < len - 1 && s.[i] = '\x1b' && s.[i+1] = '[' then
                // Skip CSI: ESC [ ... until terminator in range 0x40–0x7e
                i <- i + 2
                while i < len && (int s.[i] < 0x40 || int s.[i] > 0x7e) do
                    i <- i + 1
                i <- i + 1  // skip terminator
            elif s.[i] = '\x1b' && i + 1 < len then
                // Other escape: ESC + one char (e.g. ESC M)
                i <- i + 2
            elif s.[i] = '\r' || s.[i] = '\n' then
                i <- i + 1  // don't count line endings
            else
                cols <- cols + 1
                i <- i + 1
        cols

    // Count lines in a rendered ANSI string (split on \n; drop trailing empty).
    let private lineCount (s: string) : int =
        let parts = s.Split '\n'
        // Trim trailing empty line produced by a trailing newline.
        let lastNonEmpty =
            parts
            |> Array.tryFindIndexBack (fun p -> p.Trim () <> "" && p.Trim () <> "\r")
        match lastNonEmpty with
        | None   -> 0
        | Some i -> i + 1

    // Compute the maximum display width of any line in a rendered ANSI string.
    let private maxLineWidth (s: string) : int =
        s.Split '\n'
        |> Array.map (fun line -> displayWidth line)
        |> Array.fold max 0

    // ANSI CUP (Cursor Position) — 1-based row and column.
    let private cup (row: int) (col: int) : string =
        $"\x1b[{row};{col}H"

    /// Private DockRenderable: implements IRenderable so geometry is computed
    /// at render time using the actual MaxWidth (and ConsoleSize.Height from
    /// Spectre's RenderOptions). This is the lazy-IRenderable pattern required
    /// by CEO option (b.1) and data-model.md §4 "Practical lowering".
    ///
    /// Edge allocation order: Top → Bottom → Left → Right → Fill.
    /// Each edge child is rendered at full width to measure its height; then
    /// ANSI cursor-positioning escapes (CUP \x1b[row;colH) stitch all regions
    /// into the final output returned as a single Segment.Control.
    ///
    /// FAIL-FAST: if Renderer.toRawAnsi returns Error on any child during Render,
    /// propagate via failwithf (PR-P1 lesson #1 — no silent fallbacks).
    type private DockRenderable(dock: Dock, ctx: RenderContext) =

        // Render a Composition child at the given width/height using ctx settings.
        // The stored ctx provides ThemeName and ColourEnabled; Width/Height come
        // from the actual render-time values supplied by Spectre.
        let renderChild (childWidth: int) (childHeight: int) (comp: Composition) : string =
            let childCtx =
                RenderContext.create childWidth childHeight ctx.ColourEnabled ctx.ThemeName
                |> function
                   | Ok c  -> c
                   | Error e -> failwithf "DockRenderable: could not build child RenderContext (width=%d, height=%d): %A" childWidth childHeight e
            match Renderer.toRawAnsi childCtx comp with
            | Ok s    -> s
            | Error e -> failwithf "Dock.Render: child render failed: %A" e

        interface IRenderable with

            member _.Measure(_options: RenderOptions, maxWidth: int) : Measurement =
                // DockRenderable always consumes the full available width.
                Measurement(0, maxWidth)

            member _.Render(options: RenderOptions, maxWidth: int) : System.Collections.Generic.IEnumerable<Segment> =
                let totalWidth  = max 1 maxWidth
                // Use ConsoleSize.Height from options; fall back to ctx.Height sentinel.
                let totalHeight =
                    let h = options.ConsoleSize.Height
                    if h > 0 then h else ctx.Height

                let children = Dock.children dock

                // Partition children by edge (declaration order preserved).
                let tops    = children |> List.choose (fun (e, c) -> if e = Top    then Some c else None)
                let bottoms = children |> List.choose (fun (e, c) -> if e = Bottom then Some c else None)
                let lefts   = children |> List.choose (fun (e, c) -> if e = Left   then Some c else None)
                let rights  = children |> List.choose (fun (e, c) -> if e = Right  then Some c else None)
                let fillComp =
                    children
                    |> List.choose (fun (e, c) -> if e = Fill then Some c else None)
                    |> function
                       | [f] -> f
                       | other ->
                           failwithf "DockRenderable.Render: expected exactly one Fill child; got %d — Dock.create validation bug" other.Length

                // Pass 1: measure Top children (render at full width, count lines).
                let topOutputs = tops |> List.map (renderChild totalWidth totalHeight)
                let topHeights = topOutputs |> List.map lineCount
                let totalTopHeight = topHeights |> List.sum

                // Pass 2: measure Bottom children.
                let bottomOutputs = bottoms |> List.map (renderChild totalWidth totalHeight)
                let bottomHeights = bottomOutputs |> List.map lineCount
                let totalBottomHeight = bottomHeights |> List.sum

                // Remaining height for the centre band (Left + Fill + Right).
                let centreHeight = max 1 (totalHeight - totalTopHeight - totalBottomHeight)

                // Pass 3: measure Left children (render at full width to get intrinsic width;
                // then clip to measured width for the final render).
                let leftMeasured =
                    lefts |> List.map (fun comp ->
                        let probe = renderChild totalWidth centreHeight comp
                        let w     = max 1 (maxLineWidth probe)
                        (comp, w))

                // Pass 4: measure Right children similarly.
                let rightMeasured =
                    rights |> List.map (fun comp ->
                        let probe = renderChild totalWidth centreHeight comp
                        let w     = max 1 (maxLineWidth probe)
                        (comp, w))

                let totalLeftWidth  = leftMeasured  |> List.sumBy snd
                let totalRightWidth = rightMeasured |> List.sumBy snd
                let fillWidth       = max 1 (totalWidth - totalLeftWidth - totalRightWidth)

                // Pass 5: final render of all children at their allocated widths.
                let topFinal =
                    tops
                    |> List.mapi (fun i comp ->
                        (comp, renderChild totalWidth (topHeights.[i]) comp, topHeights.[i]))

                let bottomFinal =
                    bottoms
                    |> List.mapi (fun i comp ->
                        (comp, renderChild totalWidth (bottomHeights.[i]) comp, bottomHeights.[i]))

                let leftFinal =
                    leftMeasured
                    |> List.map (fun (comp, w) ->
                        (comp, renderChild w centreHeight comp, w))

                let rightFinal =
                    rightMeasured
                    |> List.map (fun (comp, w) ->
                        (comp, renderChild w centreHeight comp, w))

                let fillOutput = renderChild fillWidth centreHeight fillComp

                // Assemble: write regions using ANSI CUP cursor-position escapes.
                // All positions are 1-based (VT100 CUP convention).
                let buf = System.Text.StringBuilder()

                // Emit Top children (stacked from row 1).
                let mutable currentRow = 1
                for (_, output, h) in topFinal do
                    buf.Append(cup currentRow 1) |> ignore
                    buf.Append(output) |> ignore
                    currentRow <- currentRow + h

                // Top of centre band.
                let centreStartRow = currentRow

                // Bottom children occupy the bottom rows; compute their start rows.
                // Bottom children are stacked in declaration order from the bottom up.
                // Build bottom start rows: last bottom child ends at totalHeight.
                let bottomStartRows =
                    let mutable row = totalHeight - totalBottomHeight + 1
                    bottomFinal |> List.map (fun (_, _, h) ->
                        let r = row
                        row <- row + h
                        r)

                for ((_, output, _), startRow) in List.zip bottomFinal bottomStartRows do
                    buf.Append(cup startRow 1) |> ignore
                    buf.Append(output) |> ignore

                // Emit Left children (stacked from centreStartRow, left-aligned).
                let mutable leftCol = 1
                for (_, output, w) in leftFinal do
                    buf.Append(cup centreStartRow leftCol) |> ignore
                    buf.Append(output) |> ignore
                    leftCol <- leftCol + w

                // Emit Fill in the centre column region.
                let fillCol = leftCol
                buf.Append(cup centreStartRow fillCol) |> ignore
                buf.Append(fillOutput) |> ignore

                // Emit Right children (stacked right of Fill).
                let mutable rightCol = fillCol + fillWidth
                for (_, output, w) in rightFinal do
                    buf.Append(cup centreStartRow rightCol) |> ignore
                    buf.Append(output) |> ignore
                    rightCol <- rightCol + w

                // Position cursor at end (after last bottom row, or after centre band).
                let finalRow = max (centreStartRow + centreHeight) (totalHeight + 1)
                buf.Append(cup finalRow 1) |> ignore

                seq { yield Segment.Control(buf.ToString()) }

    // -------------------------------------------------------------------------
    // ofDock — public entry point
    // -------------------------------------------------------------------------

    /// Lower a validated `Dock` into the Phase 2 `Composition` IR.
    ///
    /// Uses the lazy-IRenderable pattern (data-model.md §4 "Practical lowering"):
    ///   1. Compute the Dock's depth at call time via `LayoutDepth.maxChildDepth`
    ///      over the (DockEdge × Composition) children list, + 1 for the Dock.
    ///   2. Wrap a `DockRenderable` (private IRenderable impl) as a `Renderable`
    ///      via `Renderable.fromSpectre`, then via `Composition.ofRenderableWithDepth`
    ///      so that `LayoutDepth.layoutDepth` honors the actual pre-lowering depth.
    ///
    /// FAIL-FAST on unreachable paths: DockRenderable.Render uses `failwithf`
    /// on any Error from Renderer.toRawAnsi (PR-P1 lesson #1).
    let ofDock (dock: Dock) : Composition =
        let children   = Dock.children dock
        let childComps = children |> List.map snd

        // Depth of the Dock = 1 (for the Dock layer itself) + max child depth.
        let computedDepth = 1 + LayoutDepth.maxChildDepth childComps

        // Capture a RenderContext for ThemeName/ColourEnabled at lowering time.
        // Width and Height are overridden at render time inside DockRenderable.Render.
        // Use a safe sentinel width=80 height=Int32.MaxValue here — it's overwritten.
        let sentinelCtx =
            RenderContext.create 80 System.Int32.MaxValue true "default"
            |> function Ok c -> c | Error e -> failwithf "ofDock: sentinel RenderContext creation failed: %A" e

        let dockRenderable = DockRenderable(dock, sentinelCtx)
        let r = Renderable.fromSpectre (dockRenderable :> IRenderable)
        Composition.ofRenderableWithDepth r computedDepth

    // -------------------------------------------------------------------------
    // ofLayoutGrid — lazy-IRenderable pattern (mirrors DockRenderable)
    // -------------------------------------------------------------------------

    /// Private LayoutGridRenderable: implements IRenderable so geometry is
    /// computed at render time using the actual MaxWidth and ConsoleSize.Height.
    ///
    /// Two-pass sizing algorithm (data-model.md §5):
    ///   Pass 1 — column widths:
    ///     1. Fixed columns allocated first (exact widths).
    ///     2. Auto columns: measure each cell in the column via Spectre's
    ///        IRenderable.Measure path; take the maximum.
    ///     3. Star columns: distribute remaining width proportionally by ratio.
    ///        Rounding residue goes to the first Star column.
    ///   Pass 1b — row heights: same three phases (Fixed → Auto → Star).
    ///   Pass 2 — cell assembly: each cell rendered at its (colWidth × rowHeight)
    ///     rectangle, stitched via ANSI CUP cursor-position escapes.
    ///
    /// FAIL-FAST: if any child render returns Error, propagate via failwithf
    /// (PR-P1 lesson #1 — no silent fallbacks).
    type private LayoutGridRenderable(grid: LayoutGrid, ctx: RenderContext) =

        let renderChild (childWidth: int) (childHeight: int) (comp: Composition) : string =
            let childCtx =
                RenderContext.create childWidth childHeight ctx.ColourEnabled ctx.ThemeName
                |> function
                   | Ok c  -> c
                   | Error e ->
                       failwithf "LayoutGridRenderable: could not build child RenderContext (w=%d h=%d): %A"
                           childWidth childHeight e
            match Renderer.toRawAnsi childCtx comp with
            | Ok s    -> s
            | Error e -> failwithf "LayoutGridRenderable.Render: child render failed: %A" e

        // Measure the display width of a rendered ANSI string (widest line).
        let measuredDisplayWidth (s: string) : int = maxLineWidth s

        // Measure the line count of a rendered ANSI string.
        let measuredLineCount (s: string) : int = lineCount s

        // Distribute `total` among `ratios` proportionally; rounding residue
        // applied to the first element.
        let distributeProportional (total: int) (ratios: int list) : int list =
            let sumR = ratios |> List.sum |> float
            let raw  = ratios |> List.map (fun r -> int (float total * float r / sumR))
            let residue = total - (raw |> List.sum)
            // Add residue to the first element.
            match raw with
            | []       -> []
            | h :: t   -> (h + residue) :: t

        // ANSI CUP (Cursor Position) — 1-based row and column.
        let cupLocal (row: int) (col: int) : string = $"\x1b[{row};{col}H"

        interface IRenderable with

            member _.Measure(_options: RenderOptions, maxWidth: int) : Measurement =
                Measurement(0, maxWidth)

            member _.Render(options: RenderOptions, maxWidth: int) : System.Collections.Generic.IEnumerable<Segment> =
                let totalWidth  = max 1 maxWidth
                let totalHeight =
                    let h = options.ConsoleSize.Height
                    if h > 0 then h else ctx.Height

                let cols  = LayoutGrid.columns grid
                let rows  = LayoutGrid.rows    grid
                let cells = LayoutGrid.cells   grid

                let nCols = cols.Length
                let nRows = rows.Length

                // ─── Pass 1: column widths ──────────────────────────────────

                // Sentinel: probe each cell at full width to measure Auto columns.
                // For Auto column j: max over all rows of the rendered width at row i.
                let measureAutoColWidth (j: int) : int =
                    let mutable maxW = 1
                    for i in 0 .. nRows - 1 do
                        let probe = renderChild totalWidth totalHeight cells.[i].[j]
                        let w = max 1 (measuredDisplayWidth probe)
                        if w > maxW then maxW <- w
                    maxW

                // Phase 1a: Fixed
                let fixedColWidths =
                    cols |> List.map (fun c ->
                        match c with
                        | ColumnSize.Fixed w -> Some w
                        | _ -> None)

                // Phase 1b: Auto (measured)
                let autoColWidths =
                    cols |> List.mapi (fun j c ->
                        match c with
                        | ColumnSize.Auto -> Some (measureAutoColWidth j)
                        | _ -> None)

                let totalFixed =
                    fixedColWidths |> List.sumBy (Option.defaultValue 0)
                let totalAuto  =
                    autoColWidths  |> List.sumBy (Option.defaultValue 0)
                let remainingForStar = max 0 (totalWidth - totalFixed - totalAuto)

                // Phase 1c: Star distribution
                let starRatios =
                    cols |> List.choose (fun c ->
                        match c with
                        | ColumnSize.Star r -> Some r
                        | _ -> None)
                let starColWidths =
                    if starRatios.IsEmpty then []
                    else distributeProportional remainingForStar starRatios

                // Build final column width array by merging in order.
                let mutable starIdx = 0
                let colWidths : int array = Array.create nCols 0
                for j in 0 .. nCols - 1 do
                    match cols.[j] with
                    | ColumnSize.Fixed w ->
                        colWidths.[j] <- max 1 w
                    | ColumnSize.Auto ->
                        colWidths.[j] <- max 1 (autoColWidths.[j] |> Option.defaultValue 1)
                    | ColumnSize.Star _ ->
                        let w = if starIdx < starColWidths.Length then starColWidths.[starIdx] else 1
                        colWidths.[j] <- max 1 w
                        starIdx <- starIdx + 1

                // ─── Pass 1b: row heights ────────────────────────────────────

                let measureAutoRowHeight (i: int) : int =
                    let mutable maxH = 1
                    for j in 0 .. nCols - 1 do
                        let probe = renderChild colWidths.[j] totalHeight cells.[i].[j]
                        let h = max 1 (measuredLineCount probe)
                        if h > maxH then maxH <- h
                    maxH

                let fixedRowHeights =
                    rows |> List.map (fun r ->
                        match r with
                        | RowSize.Fixed h -> Some h
                        | _ -> None)

                let autoRowHeights =
                    rows |> List.mapi (fun i r ->
                        match r with
                        | RowSize.Auto -> Some (measureAutoRowHeight i)
                        | _ -> None)

                let totalFixedRows  = fixedRowHeights |> List.sumBy (Option.defaultValue 0)
                let totalAutoRows   = autoRowHeights  |> List.sumBy (Option.defaultValue 0)
                let remainingForStarRows = max 0 (totalHeight - totalFixedRows - totalAutoRows)

                let starRowRatios =
                    rows |> List.choose (fun r ->
                        match r with
                        | RowSize.Star ratio -> Some ratio
                        | _ -> None)
                let starRowHeights =
                    if starRowRatios.IsEmpty then []
                    else distributeProportional remainingForStarRows starRowRatios

                let mutable starRowIdx = 0
                let rowHeights : int array = Array.create nRows 0
                for i in 0 .. nRows - 1 do
                    match rows.[i] with
                    | RowSize.Fixed h ->
                        rowHeights.[i] <- max 1 h
                    | RowSize.Auto ->
                        rowHeights.[i] <- max 1 (autoRowHeights.[i] |> Option.defaultValue 1)
                    | RowSize.Star _ ->
                        let h = if starRowIdx < starRowHeights.Length then starRowHeights.[starRowIdx] else 1
                        rowHeights.[i] <- max 1 h
                        starRowIdx <- starRowIdx + 1

                // ─── Pass 2: assemble cells ──────────────────────────────────

                // Compute cumulative column offsets (1-based CUP positions).
                let colOffsets : int array = Array.create nCols 1
                for j in 1 .. nCols - 1 do
                    colOffsets.[j] <- colOffsets.[j - 1] + colWidths.[j - 1]

                // Compute cumulative row offsets (1-based CUP positions).
                let rowOffsets : int array = Array.create nRows 1
                for i in 1 .. nRows - 1 do
                    rowOffsets.[i] <- rowOffsets.[i - 1] + rowHeights.[i - 1]

                let buf = System.Text.StringBuilder()

                for i in 0 .. nRows - 1 do
                    for j in 0 .. nCols - 1 do
                        let startRow = rowOffsets.[i]
                        let startCol = colOffsets.[j]
                        let w      = colWidths.[j]
                        let h      = rowHeights.[i]
                        let output = renderChild w h cells.[i].[j]
                        buf.Append(cupLocal startRow startCol) |> ignore
                        buf.Append(output) |> ignore

                // Position cursor past the last row.
                let totalRenderedHeight = rowOffsets.[nRows - 1] + rowHeights.[nRows - 1]
                buf.Append(cupLocal totalRenderedHeight 1) |> ignore

                seq { yield Segment.Control(buf.ToString()) }

    // -------------------------------------------------------------------------
    // ofFlex — lazy-IRenderable pattern (mirrors DockRenderable + LayoutGridRenderable)
    // -------------------------------------------------------------------------

    /// Private FlexRenderable: implements IRenderable so geometry is computed
    /// at render time using the actual MaxWidth (Horizontal) or ConsoleSize.Height
    /// (Vertical). Mirrors DockRenderable + LayoutGridRenderable patterns.
    ///
    /// Solver: calls `Flex.Internal.solveDistribution` to compute per-item sizes,
    /// then stitches children via ANSI CUP cursor-position escapes.
    ///
    /// Overflow.Strict: when the solver returns sizes summing > axisLen (no shrink
    /// available), raises `LayoutRenderErrorException (LayoutOverflow (...))` so
    /// the renderer can surface it as `Error (LayoutOverflow ...)` rather than
    /// wrapping it in `RenderFailed`.
    ///
    /// FAIL-FAST: other child-render errors propagate via `failwithf`
    /// (PR-P1 lesson #1 — no silent fallbacks).
    type private FlexRenderable(flex: Flex, ctx: RenderContext) =

        let renderChild (childWidth: int) (childHeight: int) (comp: Composition) : string =
            let childCtx =
                RenderContext.create childWidth childHeight ctx.ColourEnabled ctx.ThemeName
                |> function
                   | Ok c  -> c
                   | Error e ->
                       failwithf "FlexRenderable: could not build child RenderContext (w=%d h=%d): %A"
                           childWidth childHeight e
            match Renderer.toRawAnsi childCtx comp with
            | Ok s    -> s
            | Error e -> failwithf "FlexRenderable.Render: child render failed: %A" e

        interface IRenderable with

            member _.Measure(_options: RenderOptions, maxWidth: int) : Measurement =
                Measurement(0, maxWidth)

            member _.Render(options: RenderOptions, maxWidth: int) : System.Collections.Generic.IEnumerable<Segment> =
                let axis      = Flex.axis flex
                let itemList  = Flex.items flex
                let policy    = Flex.overflow flex

                let totalWidth  = max 1 maxWidth
                let totalHeight =
                    let h = options.ConsoleSize.Height
                    if h > 0 then h else ctx.Height

                let axisLen =
                    match axis with
                    | Horizontal -> totalWidth
                    | Vertical   -> totalHeight

                // Convert FlexItem list to SolverItem list for the pure solver.
                let solverItems : Flex.Internal.SolverItem list =
                    itemList |> List.map (fun fi ->
                        { Flex.Internal.Grow   = Flex.itemGrow fi
                          Flex.Internal.Shrink = Flex.itemShrink fi
                          Flex.Internal.Basis  = Flex.itemBasis fi })

                let sizes = Flex.Internal.solveDistribution solverItems axisLen

                // Check overflow: sum may exceed axisLen when no shrink path exists.
                let totalSize = sizes |> List.sum
                if totalSize > axisLen then
                    match policy with
                    | Strict ->
                        // Raise typed LayoutRenderErrorException so Renderer.toRawAnsi
                        // can surface it as Error (LayoutOverflow ...) rather than RenderFailed.
                        raise (LayoutRenderErrorException (
                            RenderError.LayoutOverflow (
                                "Flex",
                                $"overflow on {axis}: requested {totalSize}, available {axisLen}")))
                    | Clip | WrapNext ->
                        // Clip: accept the sizes as-is (Spectre clips visually).
                        // WrapNext: treated as Clip in Phase 3 MVP.
                        ()

                let buf = System.Text.StringBuilder()

                match axis with
                | Horizontal ->
                    // Place each child at the computed column position (1-based CUP).
                    let mutable currentCol = 1
                    for (fi, sz) in List.zip itemList sizes do
                        let w = max 1 sz
                        let child = Flex.itemChild fi
                        let output = renderChild w totalHeight child
                        buf.Append(cup 1 currentCol) |> ignore
                        buf.Append(output) |> ignore
                        currentCol <- currentCol + w

                | Vertical ->
                    // Place each child at the computed row position (1-based CUP).
                    let mutable currentRow = 1
                    for (fi, sz) in List.zip itemList sizes do
                        let h = max 1 sz
                        let child = Flex.itemChild fi
                        let output = renderChild totalWidth h child
                        buf.Append(cup currentRow 1) |> ignore
                        buf.Append(output) |> ignore
                        currentRow <- currentRow + h

                // Position cursor past the last region.
                match axis with
                | Horizontal ->
                    buf.Append(cup 2 1) |> ignore
                | Vertical ->
                    let finalRow = max 2 (1 + (sizes |> List.sumBy (max 1)) + 1)
                    buf.Append(cup finalRow 1) |> ignore

                seq { yield Segment.Control(buf.ToString()) }

    // -------------------------------------------------------------------------
    // ofLayoutGrid — public entry point
    // -------------------------------------------------------------------------

    /// Lower a validated `LayoutGrid` into the Phase 2 `Composition` IR.
    ///
    /// Uses the lazy-IRenderable pattern (data-model.md §5):
    ///   1. Compute the LayoutGrid's depth at call time via
    ///      `LayoutDepth.maxChildDepth` over all flattened cells, +1 for the
    ///      LayoutGrid itself.
    ///   2. Wrap a `LayoutGridRenderable` (private IRenderable impl) as a
    ///      `Renderable` via `Renderable.fromSpectre`, then via
    ///      `Composition.ofRenderableWithDepth` so that `LayoutDepth.layoutDepth`
    ///      honours the actual pre-lowering depth.
    ///
    /// FAIL-FAST: LayoutGridRenderable.Render uses `failwithf` on any Error
    /// from Renderer.toRawAnsi (PR-P1 lesson #1 — no silent fallbacks).
    let ofLayoutGrid (grid: LayoutGrid) : Composition =
        let flatCells     = LayoutGrid.cells grid |> List.concat
        let computedDepth = 1 + LayoutDepth.maxChildDepth flatCells

        let sentinelCtx =
            RenderContext.create 80 System.Int32.MaxValue true "default"
            |> function
               | Ok c  -> c
               | Error e -> failwithf "ofLayoutGrid: sentinel RenderContext creation failed: %A" e

        let gridRenderable = LayoutGridRenderable(grid, sentinelCtx)
        let r = Renderable.fromSpectre (gridRenderable :> IRenderable)
        Composition.ofRenderableWithDepth r computedDepth

    // -------------------------------------------------------------------------
    // ofFlex — public entry point
    // -------------------------------------------------------------------------

    /// Lower a validated `Flex` into the Phase 2 `Composition` IR.
    ///
    /// Uses the lazy-IRenderable pattern (data-model.md §6 "Lowering"):
    ///   1. Compute the Flex's depth at call time via `LayoutDepth.maxChildDepth`
    ///      over the children (item.Child), + 1 for the Flex layer itself.
    ///   2. Wrap a `FlexRenderable` (private IRenderable impl) as a `Renderable`
    ///      via `Renderable.fromSpectre`, then via `Composition.ofRenderableWithDepth`
    ///      so that `LayoutDepth.layoutDepth` honours the actual pre-lowering depth.
    ///
    /// FAIL-FAST: FlexRenderable.Render uses `failwithf` on child render errors
    /// (PR-P1 lesson #1 — no silent fallbacks).
    /// Overflow.Strict raises LayoutRenderErrorException which Renderer.toRawAnsi
    /// surfaces as `Error (LayoutOverflow ...)`.
    let ofFlex (flex: Flex) : Composition =
        let childComps    = Flex.items flex |> List.map Flex.itemChild
        let computedDepth = 1 + LayoutDepth.maxChildDepth childComps

        let sentinelCtx =
            RenderContext.create 80 System.Int32.MaxValue true "default"
            |> function
               | Ok c  -> c
               | Error e -> failwithf "ofFlex: sentinel RenderContext creation failed: %A" e

        let flexRenderable = FlexRenderable(flex, sentinelCtx)
        let r = Renderable.fromSpectre (flexRenderable :> IRenderable)
        Composition.ofRenderableWithDepth r computedDepth
