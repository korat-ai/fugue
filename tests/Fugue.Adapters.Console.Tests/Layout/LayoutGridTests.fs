module Fugue.Adapters.Console.Tests.Layout.LayoutGridTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout
open System.Text.RegularExpressions

// ============================================================================
// Helpers (mirrors DockTests.fs pattern)
// ============================================================================

/// Standard 80×24 test context (colour on).
let private ctx80 () =
    RenderContext.create 80 24 true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

/// Standard 80×24 test context (colour off).
let private ctx80nc () =
    RenderContext.create 80 24 false "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

/// Standard 100×24 test context (colour on).
let private ctx100 () =
    RenderContext.create 100 24 true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

/// Context with given width×height (colour on).
let private ctxWH (w: int) (h: int) () =
    RenderContext.create w h true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

/// A named styled leaf composition — distinct marker string for placement assertions.
let private namedLeaf (marker: string) : Composition =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral marker))

/// Strip ANSI CSI escape sequences (\x1b[...m and other CSI sequences) from a string.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-?]*[ -/]*[@-~]", "")

/// Parse CUP-positioned regions from LayoutGridRenderable output.
///
/// LayoutGridRenderable emits segments in the form:
///   \x1b[row;colH<content>\x1b[row;colH<content>...
///
/// Returns a list of (1-based-row, 1-based-col, content-text) triples, where
/// content-text is the text between consecutive CUP sequences (ANSI-stripped).
///
/// Used by T034/T035/T036a to assert real row/column placement.
let private parseCupRegions (output: string) : (int * int * string) list =
    // Match all CUP sequences: ESC [ row ; col H
    let cupRe = Regex(@"\x1b\[(\d+);(\d+)H", RegexOptions.None)
    let matches = cupRe.Matches(output)
    if matches.Count = 0 then []
    else
        [
            for i in 0 .. matches.Count - 1 do
                let m            = matches.[i]
                let row          = int m.Groups.[1].Value
                let col          = int m.Groups.[2].Value
                let contentStart = m.Index + m.Length
                let contentEnd   =
                    if i + 1 < matches.Count then matches.[i + 1].Index
                    else output.Length
                let raw     = output.[contentStart .. contentEnd - 1]
                let content = stripAnsi raw
                yield (row, col, content)
        ]

/// Find the region (row, col) that contains the given marker substring.
/// Returns None if no region contains the marker.
let private findMarker (marker: string) (regions: (int * int * string) list) : (int * int) option =
    regions
    |> List.tryPick (fun (row, col, content) ->
        if content.Contains marker then Some (row, col) else None)

// ============================================================================
// T034 — Fixed+Star column: LABEL in cols 0..19, VALUE in cols 20..99
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T034 — Fixed 20 + Star 1: LABEL-MARKER at col 1, VALUE-MARKER at col 21`` () =
    // LayoutGrid.create [Fixed 20; Star 1] [Auto] [[labelComp; valueComp]] at width=100.
    // Algorithm trace: Fixed col0=20, Star remainder=100-20=80.
    // distributeProportional 80 [1] → [80].
    // colOffsets: col0=1, col1=1+20=21.
    // → LABEL at col=1 (exact), VALUE at col=21 (exact).
    let labelComp = namedLeaf "LABEL-MARKER"
    let valueComp = namedLeaf "VALUE-MARKER"
    match LayoutGrid.create [ColumnSize.Fixed 20; ColumnSize.Star 1] [RowSize.Auto] [[labelComp; valueComp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctx100 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "LABEL-MARKER" regions, findMarker "VALUE-MARKER" regions with
            | Some (_, labelCol), Some (_, valueCol) ->
                // Exact CUP positions — no tolerance (PR-P2 BLOCKER lesson).
                labelCol = 1 && valueCol = 21
            | None, _ -> failwith "LABEL-MARKER not found in any CUP region"
            | _, None -> failwith "VALUE-MARKER not found in any CUP region"

// ============================================================================
// T035 — Star ratio: 2×2 grid at 90×24 → 30/60 cols + 12/12 rows
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T035 — Star 1 + Star 2 cols, Star 1 + Star 1 rows: exact CUP positions at 90×24`` () =
    // LayoutGrid.create [Star 1; Star 2] [Star 1; Star 1] [[a;b];[c;d]] at 90×24.
    // Column trace: distributeProportional 90 [1;2] → sumR=3, raw=[30;60], residue=0 → [30;60].
    //   colOffsets: col0=1, col1=1+30=31.
    // Row trace: distributeProportional 24 [1;1] → sumR=2, raw=[12;12], residue=0 → [12;12].
    //   rowOffsets: row0=1, row1=1+12=13.
    // → A at (row=1,col=1), B at (row=1,col=31), C at (row=13,col=1), D at (row=13,col=31).
    let aComp = namedLeaf "CELL-A"
    let bComp = namedLeaf "CELL-B"
    let cComp = namedLeaf "CELL-C"
    let dComp = namedLeaf "CELL-D"
    match LayoutGrid.create
            [ColumnSize.Star 1; ColumnSize.Star 2]
            [RowSize.Star 1; RowSize.Star 1]
            [[aComp; bComp]; [cComp; dComp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctxWH 90 24 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "CELL-A" regions,
                  findMarker "CELL-B" regions,
                  findMarker "CELL-C" regions,
                  findMarker "CELL-D" regions with
            | Some (aRow, aCol), Some (bRow, bCol), Some (cRow, cCol), Some (dRow, dCol) ->
                // Exact CUP positions — no tolerance (PR-P2 BLOCKER lesson).
                // Star ratios must be honored; ordering alone does not verify ratio split.
                aRow = 1 && aCol = 1 &&
                bRow = 1 && bCol = 31 &&
                cRow = 13 && cCol = 1 &&
                dRow = 13 && dCol = 31
            | None, _, _, _ -> failwith "CELL-A not found in any CUP region"
            | _, None, _, _ -> failwith "CELL-B not found in any CUP region"
            | _, _, None, _ -> failwith "CELL-C not found in any CUP region"
            | _, _, _, None -> failwith "CELL-D not found in any CUP region"

// ============================================================================
// T036 — Auto sizing: single cell, output contains the child content
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T036 — Auto single cell: output contains child content`` () =
    let childComp = namedLeaf "AUTO-CONTENT"
    match LayoutGrid.create [ColumnSize.Auto] [RowSize.Auto] [[childComp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            (stripAnsi output).Contains "AUTO-CONTENT"

// ============================================================================
// T036a — Mixed sizing: Fixed + Auto + Star 1 + Star 2 at width=100
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T036a — Fixed 20 + Auto + Star 1 + Star 2: column ordering correct at width=100`` () =
    // Fixed 20: cols 1..20
    // Auto: cols 21..{20+autoWidth}
    // Star 1: part of remainder
    // Star 2: larger part of remainder
    let fixedComp = namedLeaf "FIXED-MARKER"
    let autoComp  = namedLeaf "AUTO-MARKER"
    let star1Comp = namedLeaf "STAR1-MARKER"
    let star2Comp = namedLeaf "STAR2-MARKER"
    match LayoutGrid.create
            [ColumnSize.Fixed 20; ColumnSize.Auto; ColumnSize.Star 1; ColumnSize.Star 2]
            [RowSize.Auto]
            [[fixedComp; autoComp; star1Comp; star2Comp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctx100 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "FIXED-MARKER" regions,
                  findMarker "AUTO-MARKER"  regions,
                  findMarker "STAR1-MARKER" regions,
                  findMarker "STAR2-MARKER" regions with
            | Some (_, fixedCol), Some (_, autoCol), Some (_, star1Col), Some (_, star2Col) ->
                // Column ordering must be strictly left-to-right:
                // FIXED → AUTO → STAR1 → STAR2
                fixedCol < autoCol && autoCol < star1Col && star1Col < star2Col
                // FIXED must start near col 1.
                && fixedCol <= 5
            | None, _, _, _ -> failwith "FIXED-MARKER not found in any CUP region"
            | _, None, _, _ -> failwith "AUTO-MARKER not found in any CUP region"
            | _, _, None, _ -> failwith "STAR1-MARKER not found in any CUP region"
            | _, _, _, None -> failwith "STAR2-MARKER not found in any CUP region"

// T036a sub-case (b): Star sums to fractional pixels — width=83 with two Star 1.
// Residue allocation: distributeProportional 83 [1;1]:
//   sumR=2, raw=[int(83/2); int(83/2)]=[41;41], residue=83-82=1 → [42;41].
//   colOffsets: col0=1, col1=1+42=43.
[<Property(MaxTest = 1)>]
let ``T036a-b — Star 1 + Star 1 at width=83: STAR-FRAC-1 at col 1, STAR-FRAC-2 at col 43`` () =
    let s1 = namedLeaf "STAR-FRAC-1"
    let s2 = namedLeaf "STAR-FRAC-2"
    match LayoutGrid.create [ColumnSize.Star 1; ColumnSize.Star 1] [RowSize.Auto] [[s1; s2]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctxWH 83 24 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "STAR-FRAC-1" regions, findMarker "STAR-FRAC-2" regions with
            | Some (_, col1), Some (_, col2) ->
                // Exact positions — residue goes to first Star column (col0 width=42).
                col1 = 1 && col2 = 43
            | None, _ -> failwith "STAR-FRAC-1 not found in any CUP region"
            | _, None -> failwith "STAR-FRAC-2 not found in any CUP region"

// T036a sub-case (c): all-Fixed with total < available — extra space blank to right.
// Algorithm trace: Fixed 20 + Fixed 20 at width=100.
//   colWidths=[20;20], colOffsets: col0=1, col1=1+20=21.
//   → FIXED-LEFT at col=1 (exact), FIXED-RIGHT at col=21 (exact).
[<Property(MaxTest = 1)>]
let ``T036a-c — Two Fixed columns at width=100: FIXED-LEFT at col 1, FIXED-RIGHT at col 21`` () =
    let f1 = namedLeaf "FIXED-LEFT"
    let f2 = namedLeaf "FIXED-RIGHT"
    // Fixed 20 + Fixed 20 = 40 total, width=100 → 60 cols blank to right.
    match LayoutGrid.create [ColumnSize.Fixed 20; ColumnSize.Fixed 20] [RowSize.Auto] [[f1; f2]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        match Renderer.toRawAnsi (ctx100 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "FIXED-LEFT" regions, findMarker "FIXED-RIGHT" regions with
            | Some (_, leftCol), Some (_, rightCol) ->
                // Exact CUP positions — no tolerance (PR-P2 BLOCKER lesson).
                leftCol = 1 && rightCol = 21
            | None, _ -> failwith "FIXED-LEFT not found in any CUP region"
            | _, None -> failwith "FIXED-RIGHT not found in any CUP region"

// ============================================================================
// T037 — Error: empty columns
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T037 — LayoutGrid.create with empty columns returns EmptyComposition`` () =
    let child = namedLeaf "any"
    match LayoutGrid.create [] [RowSize.Auto] [[child]] with
    | Error (RenderError.EmptyComposition msg) ->
        msg.Contains "LayoutGrid" && msg.Contains "column"
    | _ -> false

// ============================================================================
// T038 — Error: empty rows
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T038 — LayoutGrid.create with empty rows returns EmptyComposition`` () =
    let child = namedLeaf "any"
    match LayoutGrid.create [ColumnSize.Auto] [] [[child]] with
    | Error (RenderError.EmptyComposition msg) ->
        msg.Contains "LayoutGrid" && msg.Contains "row"
    | _ -> false

// ============================================================================
// T039 — Error: ragged row
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T039 — LayoutGrid.create with ragged row returns InvalidArgument`` () =
    let c1 = namedLeaf "C1"
    let c2 = namedLeaf "C2"
    // 2 columns declared but row 0 has only 1 cell.
    match LayoutGrid.create
            [ColumnSize.Auto; ColumnSize.Auto]
            [RowSize.Auto]
            [[c1]] with   // row 0 has 1 cell, expected 2
    | Error (RenderError.InvalidArgument ("LayoutGrid", detail)) ->
        detail.Contains "row 0" && detail.Contains "1" && detail.Contains "2"
    | _ -> false

// ============================================================================
// T039a — Error: cells.Length ≠ rows.Length
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T039a — LayoutGrid.create rejects cells.Length ≠ rows.Length (too few rows of cells)`` () =
    let c1 = namedLeaf "C1"
    let c2 = namedLeaf "C2"
    // rows=[Auto;Auto;Auto] (3) but cells has only 1 row.
    match LayoutGrid.create
            [ColumnSize.Auto; ColumnSize.Auto]
            [RowSize.Auto; RowSize.Auto; RowSize.Auto]
            [[c1; c2]] with  // 1 row of cells, 3 declared
    | Error (RenderError.InvalidArgument ("LayoutGrid", detail)) ->
        detail.Contains "3" && detail.Contains "1"
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T039a — LayoutGrid.create rejects cells.Length ≠ rows.Length (too many rows of cells)`` () =
    let c1 = namedLeaf "C1"
    let c2 = namedLeaf "C2"
    // rows=[Auto] (1) but cells has 2 rows.
    match LayoutGrid.create
            [ColumnSize.Auto; ColumnSize.Auto]
            [RowSize.Auto]
            [[c1; c2]; [c1; c2]] with  // 2 rows of cells, 1 declared
    | Error (RenderError.InvalidArgument ("LayoutGrid", detail)) ->
        detail.Contains "1" && detail.Contains "2"
    | _ -> false

// ============================================================================
// T040 — Error: Fixed 0 column
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T040 — LayoutGrid.create with Fixed 0 column returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match LayoutGrid.create [ColumnSize.Fixed 0] [RowSize.Auto] [[child]] with
    | Error (RenderError.InvalidArgument ("LayoutGrid", detail)) ->
        detail.Contains "Fixed" && detail.Contains "1"
    | _ -> false

// ============================================================================
// T041 — Error: Star 0 ratio
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T041 — LayoutGrid.create with Star 0 returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match LayoutGrid.create [ColumnSize.Star 0] [RowSize.Auto] [[child]] with
    | Error (RenderError.InvalidArgument ("LayoutGrid", detail)) ->
        detail.Contains "Star" && detail.Contains "1"
    | _ -> false

// ============================================================================
// T042 — Colour toggle parametric + determinism
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T042 — Colour-off output places markers at the same CUP positions as colour-on`` () =
    // Uses ctx80nc() (colour=false) vs ctx80 (colour=true) — both 80×24.
    // SGR sequences differ between the two renders so byte equality is wrong.
    // The invariant is: CUP positions of each marker must match regardless of colour mode.
    let labelComp = namedLeaf "COLOUR-LABEL"
    let valueComp = namedLeaf "COLOUR-VALUE"
    match LayoutGrid.create
            [ColumnSize.Fixed 20; ColumnSize.Star 1]
            [RowSize.Auto]
            [[labelComp; valueComp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp   = Composition.ofLayoutGrid grid
        let ctxOn  = ctx80 ()    // colour=true  (uses ctx80, defined line 14)
        let ctxOff = ctx80nc ()  // colour=false (uses ctx80nc, defined line 19)
        match Renderer.toRawAnsi ctxOn comp, Renderer.toRawAnsi ctxOff comp with
        | Ok onOutput, Ok offOutput ->
            let onRegions  = parseCupRegions onOutput
            let offRegions = parseCupRegions offOutput
            match findMarker "COLOUR-LABEL" onRegions,  findMarker "COLOUR-LABEL" offRegions,
                  findMarker "COLOUR-VALUE" onRegions,  findMarker "COLOUR-VALUE" offRegions with
            | Some (onLR, onLC), Some (offLR, offLC),
              Some (onVR, onVC), Some (offVR, offVC) ->
                // CUP positions must be identical regardless of colour mode.
                onLR = offLR && onLC = offLC &&
                onVR = offVR && onVC = offVC
            | None, _, _, _ -> failwith "COLOUR-LABEL not found in colour-on output"
            | _, None, _, _ -> failwith "COLOUR-LABEL not found in colour-off output"
            | _, _, None, _ -> failwith "COLOUR-VALUE not found in colour-on output"
            | _, _, _, None -> failwith "COLOUR-VALUE not found in colour-off output"
        | Error e, _ -> failwith $"colour-on render failed: {e}"
        | _, Error e -> failwith $"colour-off render failed: {e}"

[<Property(MaxTest = 1)>]
let ``T042 — Two renders of the same LayoutGrid are byte-identical`` () =
    let labelComp = namedLeaf "DETERM-LABEL"
    let valueComp = namedLeaf "DETERM-VALUE"
    match LayoutGrid.create
            [ColumnSize.Fixed 20; ColumnSize.Star 1]
            [RowSize.Auto]
            [[labelComp; valueComp]] with
    | Error e -> failwith $"LayoutGrid.create failed: {e}"
    | Ok grid ->
        let comp = Composition.ofLayoutGrid grid
        let ctx1 = RenderContext.create 100 24 true "default" |> function Ok c -> c | Error e -> failwith $"ctx1: {e}"
        let ctx2 = RenderContext.create 100 24 true "default" |> function Ok c -> c | Error e -> failwith $"ctx2: {e}"
        match Renderer.toRawAnsi ctx1 comp, Renderer.toRawAnsi ctx2 comp with
        | Ok s1, Ok s2 -> s1 = s2
        | Error e, _   -> failwith $"render 1 failed: {e}"
        | _, Error e   -> failwith $"render 2 failed: {e}"

// ============================================================================
// T042a — Depth limit: LayoutGrid nested 101 levels deep returns InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T042a — LayoutGrid nested 101 deep returns depth-exceeded InvalidArgument`` () =
    // Build a linear chain: leaf → LayoutGrid (1 cell) → LayoutGrid (1 cell) → ... 101 levels.
    // The innermost child is a leaf Composition; each iteration wraps it in a LayoutGrid.
    let leaf = namedLeaf "DEEP-LEAF"
    let rec buildDeep (comp: Composition) (depth: int) : Result<LayoutGrid, RenderError> =
        match LayoutGrid.create [ColumnSize.Auto] [RowSize.Auto] [[comp]] with
        | Error e -> Error e
        | Ok grid ->
            if depth >= 101 then Ok grid
            else buildDeep (Composition.ofLayoutGrid grid) (depth + 1)
    match buildDeep leaf 1 with
    | Error (RenderError.InvalidArgument ("Layout", detail)) ->
        detail.Contains "depth"
    | Error other -> failwith $"Expected depth error but got: {other}"
    | Ok _        -> false // should have been rejected
