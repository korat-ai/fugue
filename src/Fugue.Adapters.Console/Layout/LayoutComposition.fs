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
