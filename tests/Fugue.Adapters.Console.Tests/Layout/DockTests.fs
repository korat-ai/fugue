module Fugue.Adapters.Console.Tests.Layout.DockTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout
open System.Text.RegularExpressions

// ============================================================================
// Helpers
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

/// A named styled leaf composition — used as a stand-in child with a distinctive marker.
let private namedLeaf (marker: string) : Composition =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral marker))

/// Strip ANSI CSI escape sequences (\x1b[...m and other CSI sequences) from a string.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-?]*[ -/]*[@-~]", "")

/// Split stripped output into lines.
let private lines (s: string) : string array =
    s.Split([| '\n' |])

/// Parse CUP-positioned regions from DockRenderable output.
///
/// DockRenderable emits segments in the form:
///   \x1b[row;colH<content>\x1b[row;colH<content>...
///
/// Returns a list of (1-based-row, 1-based-col, content-text) triples, where
/// content-text is the text between consecutive CUP sequences (ANSI-stripped).
/// The last entry's content extends to end-of-string.
///
/// Used by T022/T023/T024 to assert real row/column placement.
let private parseCupRegions (output: string) : (int * int * string) list =
    // Match all CUP sequences: ESC [ row ; col H
    let cupRe = Regex(@"\x1b\[(\d+);(\d+)H", RegexOptions.None)
    let matches = cupRe.Matches(output)
    if matches.Count = 0 then []
    else
        [
            for i in 0 .. matches.Count - 1 do
                let m       = matches.[i]
                let row     = int m.Groups.[1].Value
                let col     = int m.Groups.[2].Value
                // Content: from end of this CUP sequence to start of next (or end of string).
                let contentStart = m.Index + m.Length
                let contentEnd =
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
// T022 — Happy path: Bottom + Fill renders status near last row, main fills rest
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T022 — Dock Bottom+Fill: STATUS-MARKER near last row, MAIN-MARKER at lower row`` () =
    let statusComp = namedLeaf "STATUS-MARKER"
    let mainComp   = namedLeaf "MAIN-MARKER"
    match Dock.create [ DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "STATUS-MARKER" regions, findMarker "MAIN-MARKER" regions with
            | Some (statusRow, _), Some (mainRow, _) ->
                // STATUS-MARKER must be placed at or near the last row (row 24, 1-based).
                // Tolerance ±1 to account for minor Spectre padding differences.
                let statusNearBottom = statusRow >= 23  // row 23 or 24 (of 24)
                // MAIN-MARKER must appear at a lower row number than STATUS-MARKER.
                let mainAboveStatus = mainRow < statusRow
                statusNearBottom && mainAboveStatus
            | None, _ -> failwith "STATUS-MARKER not found in any CUP region"
            | _, None -> failwith "MAIN-MARKER not found in any CUP region"

// ============================================================================
// T023 — Multi-edge: Left + Right + Fill assigns correct column regions
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T023 — Dock Left+Right+Fill: SIDEBAR left of MAIN left of METADATA`` () =
    let sidebarComp  = namedLeaf "SIDEBAR-MARKER"
    let metaComp     = namedLeaf "METADATA-MARKER"
    let mainComp     = namedLeaf "MAIN-MARKER"
    match Dock.create [ DockEdge.Left, sidebarComp; DockEdge.Right, metaComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx100 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "SIDEBAR-MARKER" regions,
                  findMarker "MAIN-MARKER"    regions,
                  findMarker "METADATA-MARKER" regions with
            | Some (_, sidebarCol), Some (_, mainCol), Some (_, metaCol) ->
                // Column ordering: SIDEBAR < MAIN < METADATA (left-to-right).
                // SIDEBAR starts near col 1 (within ~5 cols).
                // METADATA starts after MAIN (strict ordering).
                let sidebarNearLeft  = sidebarCol <= 5
                let columnOrderOk    = sidebarCol < mainCol && mainCol < metaCol
                sidebarNearLeft && columnOrderOk
            | None, _, _ -> failwith "SIDEBAR-MARKER not found in any CUP region"
            | _, None, _ -> failwith "MAIN-MARKER not found in any CUP region"
            | _, _, None -> failwith "METADATA-MARKER not found in any CUP region"

// ============================================================================
// T024 — Top-pin: Top + Fill renders HEADER-MARKER at row 1, BODY-MARKER below
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T024 — Dock Top+Fill: HEADER-MARKER at row 1, BODY-MARKER at row ≥ 2`` () =
    let headerComp = namedLeaf "HEADER-MARKER"
    let bodyComp   = namedLeaf "BODY-MARKER"
    match Dock.create [ DockEdge.Top, headerComp; DockEdge.Fill, bodyComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let regions = parseCupRegions output
            match findMarker "HEADER-MARKER" regions, findMarker "BODY-MARKER" regions with
            | Some (headerRow, _), Some (bodyRow, _) ->
                // HEADER-MARKER must appear in row 1 (top of terminal, 1-based).
                // Tolerance: row ≤ 2 in case of any leading empty region.
                let headerAtTop = headerRow <= 2
                // BODY-MARKER must appear at a row after the header.
                let bodyBelowHeader = bodyRow > headerRow
                headerAtTop && bodyBelowHeader
            | None, _ -> failwith "HEADER-MARKER not found in any CUP region"
            | _, None -> failwith "BODY-MARKER not found in any CUP region"

// ============================================================================
// T025 — Error: two Fill children → InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T025 — Dock.create with two Fill children returns InvalidArgument`` () =
    let c1 = namedLeaf "C1"
    let c2 = namedLeaf "C2"
    match Dock.create [ DockEdge.Fill, c1; DockEdge.Fill, c2 ] with
    | Error (RenderError.InvalidArgument ("Dock", detail)) ->
        detail.Contains "Fill"
    | _ -> false

// ============================================================================
// T026 — Error: empty children → EmptyComposition
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T026 — Dock.create with empty list returns EmptyComposition`` () =
    match Dock.create [] with
    | Error (RenderError.EmptyComposition msg) ->
        msg.Contains "Dock"
    | _ -> false

// ============================================================================
// T027 — Error: zero Fill children → InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T027 — Dock.create with no Fill child returns InvalidArgument`` () =
    let c1 = namedLeaf "C1"
    let c2 = namedLeaf "C2"
    match Dock.create [ DockEdge.Top, c1; DockEdge.Bottom, c2 ] with
    | Error (RenderError.InvalidArgument ("Dock", detail)) ->
        detail.Contains "Fill"
    | _ -> false

// ============================================================================
// T028 — Colour toggle: both colour-on and colour-off outputs contain the same
// markers at the same rows (CUP-based output; not a simple string equality test)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T028 — Colour-off output places markers at the same rows as colour-on`` () =
    let statusComp = namedLeaf "COLOUR-STATUS"
    let mainComp   = namedLeaf "COLOUR-MAIN"
    match Dock.create [ DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp, Renderer.toRawAnsi (ctx80nc ()) comp with
        | Ok withColour, Ok withoutColour ->
            let regionsColour   = parseCupRegions withColour
            let regionsNoColour = parseCupRegions withoutColour
            // Both renders must place COLOUR-STATUS at the same row.
            let statusRowColour   = findMarker "COLOUR-STATUS" regionsColour   |> Option.map fst
            let statusRowNoColour = findMarker "COLOUR-STATUS" regionsNoColour |> Option.map fst
            let mainRowColour     = findMarker "COLOUR-MAIN"   regionsColour   |> Option.map fst
            let mainRowNoColour   = findMarker "COLOUR-MAIN"   regionsNoColour |> Option.map fst
            statusRowColour = statusRowNoColour && mainRowColour = mainRowNoColour
        | Error e, _ -> failwith $"colour render failed: {e}"
        | _, Error e -> failwith $"no-colour render failed: {e}"

// ============================================================================
// T028a — Depth limit: Dock nested 101 levels deep returns InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T028a — Dock nested 101 deep returns depth-exceeded InvalidArgument`` () =
    // Build a linear chain: leaf → Dock (Fill) → Dock (Fill) → ... 101 levels.
    // The innermost child is a leaf Composition; each iteration wraps it in a Dock.
    let leaf = namedLeaf "DEEP-LEAF"
    let rec buildDeep (comp: Composition) (depth: int) : Result<Dock, RenderError> =
        match Dock.create [ DockEdge.Fill, comp ] with
        | Error e -> Error e
        | Ok dock ->
            if depth >= 101 then Ok dock
            else buildDeep (Composition.ofDock dock) (depth + 1)
    match buildDeep leaf 1 with
    | Error (RenderError.InvalidArgument ("Layout", detail)) ->
        detail.Contains "depth"
    | Error other -> failwith $"Expected depth error but got: {other}"
    | Ok _        -> false // should have been rejected

// ============================================================================
// T029 — Determinism: two renders of the same Dock are byte-identical
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T029 — Two renders of the same Dock are byte-identical`` () =
    let statusComp = namedLeaf "DETERM-STATUS"
    let mainComp   = namedLeaf "DETERM-MAIN"
    match Dock.create [ DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        let ctx1 =
            RenderContext.create 80 24 true "default"
            |> function Ok c -> c | Error e -> failwith $"ctx1: {e}"
        let ctx2 =
            RenderContext.create 80 24 true "default"
            |> function Ok c -> c | Error e -> failwith $"ctx2: {e}"
        match Renderer.toRawAnsi ctx1 comp, Renderer.toRawAnsi ctx2 comp with
        | Ok s1, Ok s2 -> s1 = s2
        | Error e, _   -> failwith $"render 1 failed: {e}"
        | _, Error e   -> failwith $"render 2 failed: {e}"
