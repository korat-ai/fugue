module Fugue.Adapters.Console.Tests.Layout.FlexTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout
open System.Text.RegularExpressions

// ============================================================================
// Helpers (mirrors LayoutGridTests.fs pattern)
// ============================================================================

/// Standard 80×24 test context (colour on).
let private ctx80 () =
    RenderContext.create 80 24 true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

/// Standard 80×24 test context (colour off).
let private ctx80nc () =
    RenderContext.create 80 24 false "default"
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

/// Parse CUP-positioned regions from FlexRenderable output.
///
/// FlexRenderable emits segments in the form:
///   \x1b[row;colH<content>\x1b[row;colH<content>...
///
/// Returns a list of (1-based-row, 1-based-col, content-text) triples, where
/// content-text is the text between consecutive CUP sequences (ANSI-stripped).
let private parseCupRegions (output: string) : (int * int * string) list =
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
let private findMarker (marker: string) (regions: (int * int * string) list) : (int * int) option =
    regions
    |> List.tryPick (fun (row, col, content) ->
        if content.Contains marker then Some (row, col) else None)

// ============================================================================
// T047 — Fixed + Grow + Fixed: exact column positions at width=80
// ============================================================================
//
// Algorithm trace (Horizontal, width=80):
//   items: fixed_(10) label, grow(1.0) input, fixed_(8) submit
//   basis: [10; 0; 8], totalBasis=18, freeSpace=80-18=62
//   freeSpace > 0, totalGrow=1.0
//   label: 10 + round(62 × 0.0/1.0) = 10
//   input:  0 + round(62 × 1.0/1.0) = 62
//   submit: 8 + round(62 × 0.0/1.0) = 8
//   sizes=[10;62;8], sum=80, residue=0
//   colOffsets: label=1, input=1+10=11, submit=1+10+62=73
//
// EXACT positions: LBL at col=1, INP at col=11, SUB at col=73.
//
// Marker name rationale: markers must fit in a SINGLE line within their
// allocated column width (Spectre wraps, not clips). The narrowest column
// is 8 (submit). "LBL"=3<10, "INP"=3<62, "SUB"=3<8 — all fit on one line.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T047 — Fixed 10 + Grow 1 + Fixed 8 at width=80: exact column positions`` () =
    // Markers short enough to fit within their allocated widths (no line-wrap):
    //   label  = fixed 10 → "LBL" (3 chars) fits
    //   input  = grow  62 → "INP" (3 chars) fits
    //   submit = fixed  8 → "SUB" (3 chars) fits
    let labelComp  = namedLeaf "LBL"
    let inputComp  = namedLeaf "INP"
    let submitComp = namedLeaf "SUB"
    match Flex.fixed_ 10 labelComp,
          Flex.grow 1.0 inputComp,
          Flex.fixed_ 8 submitComp with
    | Ok labelItem, Ok inputItem, Ok submitItem ->
        match Flex.create Horizontal [labelItem; inputItem; submitItem] with
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            match Renderer.toRawAnsi (ctxWH 80 24 ()) comp with
            | Error e -> failwith $"render failed: {e}"
            | Ok output ->
                let regions = parseCupRegions output
                match findMarker "LBL" regions,
                      findMarker "INP" regions,
                      findMarker "SUB" regions with
                | Some (_, labelCol), Some (_, inputCol), Some (_, submitCol) ->
                    // Exact CUP positions — no tolerance (PR-P3 BLOCKER lesson).
                    // Trace: label=col 1, input=col 11, submit=col 73.
                    labelCol = 1 && inputCol = 11 && submitCol = 73
                | None, _, _ -> failwith "LBL not found in any CUP region"
                | _, None, _ -> failwith "INP not found in any CUP region"
                | _, _, None -> failwith "SUB not found in any CUP region"
    | Error e, _, _ -> failwith $"Flex.fixed_ failed: {e}"
    | _, Error e, _ -> failwith $"Flex.grow failed: {e}"
    | _, _, Error e -> failwith $"Flex.fixed_ failed: {e}"

// ============================================================================
// T048 — Grow ratio 1:2:1 vertical at height=20: exact row positions
// ============================================================================
//
// Algorithm trace (Vertical, height=20):
//   items: grow(1.0) a, grow(2.0) b, grow(1.0) c
//   basis: [0; 0; 0], totalBasis=0, freeSpace=20
//   freeSpace > 0, totalGrow=4.0
//   a: 0 + round(20 × 1.0/4.0) = 5
//   b: 0 + round(20 × 2.0/4.0) = 10
//   c: 0 + round(20 × 1.0/4.0) = 5
//   sizes=[5;10;5], sum=20, residue=0
//   rowOffsets: a=1, b=1+5=6, c=1+5+10=16
//
// EXACT positions: A at row=1, B at row=6, C at row=16.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T048 — Grow 1:2:1 vertical at height=20: exact row positions`` () =
    let aComp = namedLeaf "GROW-A"
    let bComp = namedLeaf "GROW-B"
    let cComp = namedLeaf "GROW-C"
    match Flex.grow 1.0 aComp,
          Flex.grow 2.0 bComp,
          Flex.grow 1.0 cComp with
    | Ok aItem, Ok bItem, Ok cItem ->
        match Flex.create Vertical [aItem; bItem; cItem] with
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            match Renderer.toRawAnsi (ctxWH 80 20 ()) comp with
            | Error e -> failwith $"render failed: {e}"
            | Ok output ->
                let regions = parseCupRegions output
                match findMarker "GROW-A" regions,
                      findMarker "GROW-B" regions,
                      findMarker "GROW-C" regions with
                | Some (aRow, _), Some (bRow, _), Some (cRow, _) ->
                    // Exact CUP rows — no tolerance.
                    // Trace: a=row 1, b=row 6, c=row 16.
                    aRow = 1 && bRow = 6 && cRow = 16
                | None, _, _ -> failwith "GROW-A not found in any CUP region"
                | _, None, _ -> failwith "GROW-B not found in any CUP region"
                | _, _, None -> failwith "GROW-C not found in any CUP region"
    | Error e, _, _ -> failwith $"Flex.grow failed: {e}"
    | _, Error e, _ -> failwith $"Flex.grow failed: {e}"
    | _, _, Error e -> failwith $"Flex.grow failed: {e}"

// ============================================================================
// T049 — Overflow Clip (default): fixed 100 at width=80 clips, no error
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T049 — Overflow Clip: fixed 100 at width=80 clips without error`` () =
    let aComp = namedLeaf "CLIP-MARKER"
    match Flex.fixed_ 100 aComp with
    | Error e -> failwith $"Flex.fixed_ failed: {e}"
    | Ok item ->
        match Flex.create Horizontal [item] with   // Clip is the default
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            // Should render successfully (no error) even though child basis exceeds width.
            match Renderer.toRawAnsi (ctxWH 80 24 ()) comp with
            | Error e -> failwith $"Clip mode must not return error, got: {e}"
            | Ok output ->
                // Child content must appear in the output (clipped, but present).
                (stripAnsi output).Contains "CLIP-MARKER"

// ============================================================================
// T050 — Overflow Strict: fixed 100 at width=80 returns LayoutOverflow
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T050 — Overflow Strict: fixed 100 at width=80 returns LayoutOverflow`` () =
    let aComp = namedLeaf "STRICT-MARKER"
    match Flex.fixed_ 100 aComp with
    | Error e -> failwith $"Flex.fixed_ failed unexpectedly: {e}"
    | Ok item ->
        match Flex.createWith Horizontal [item] Strict with
        | Error e -> failwith $"Flex.createWith failed unexpectedly: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            match Renderer.toRawAnsi (ctxWH 80 24 ()) comp with
            | Ok _ -> false   // Must return error under Strict
            | Error (RenderError.LayoutOverflow ("Flex", detail)) ->
                // detail must mention the numeric overflow context
                detail.Contains "100" && detail.Contains "80"
            | Error other -> failwith $"Expected LayoutOverflow but got: {other}"

// ============================================================================
// T051 — Error: empty items list
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T051 — Flex.create with empty items returns EmptyComposition`` () =
    match Flex.create Horizontal [] with
    | Error (RenderError.EmptyComposition msg) ->
        msg.Contains "Flex" && msg.Contains "item"
    | _ -> false

// ============================================================================
// T052 — Error: negative grow ratio
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T052 — Flex.grow with negative ratio returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match Flex.grow -1.0 child with
    | Error (RenderError.InvalidArgument ("FlexItem", detail)) ->
        detail.Contains "non-negative"
    | _ -> false

// ============================================================================
// T053 — Shrink behaviour: two fixed 30 with shrink at width=20
// ============================================================================
//
// Algorithm trace (Horizontal, width=20):
//   items: shrink(30, 1.0) a, shrink(30, 1.0) b
//   basis: [30; 30], totalBasis=60, freeSpace=20-60=-40
//   freeSpace < 0, totalScaledShrink = 1.0×30 + 1.0×30 = 60.0
//   reduce_a = 40 × (1.0×30 / 60.0) = 20 → a = 30-20 = 10
//   reduce_b = 40 × (1.0×30 / 60.0) = 20 → b = 30-20 = 10
//   sizes=[10;10], sum=20, residue=0
//   colOffsets: A at col=1, B at col=1+10=11
//
// EXACT positions: A at col=1, B at col=11.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T053 — Shrink proportional: two fixed-30 items at width=20 shrink to 10+10`` () =
    let aComp = namedLeaf "SHRINK-A"
    let bComp = namedLeaf "SHRINK-B"
    match Flex.shrink 30 1.0 aComp,
          Flex.shrink 30 1.0 bComp with
    | Ok aItem, Ok bItem ->
        match Flex.create Horizontal [aItem; bItem] with
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            match Renderer.toRawAnsi (ctxWH 20 24 ()) comp with
            | Error e -> failwith $"render failed: {e}"
            | Ok output ->
                let regions = parseCupRegions output
                match findMarker "SHRINK-A" regions, findMarker "SHRINK-B" regions with
                | Some (_, aCol), Some (_, bCol) ->
                    // Exact CUP positions — no tolerance.
                    // Trace: A at col=1, B at col=11.
                    aCol = 1 && bCol = 11
                | None, _ -> failwith "SHRINK-A not found in any CUP region"
                | _, None -> failwith "SHRINK-B not found in any CUP region"
    | Error e, _ -> failwith $"Flex.shrink failed: {e}"
    | _, Error e -> failwith $"Flex.shrink failed: {e}"

// ============================================================================
// T054 — Colour-toggle + determinism
// ============================================================================
//
// Two renders: ctx with colour=true vs colour=false.
// CUP positions must be IDENTICAL regardless of colour mode.
// This is the REAL colour-toggle test (PR-P3 BLOCKER #4 lesson):
//   - uses ctx80() for colour=true and ctx80nc() for colour=false
//   - asserts EXACT CUP positions match between the two renders
//   - does NOT just compare byte equality of identical renders
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T054 — Colour-off renders to the same CUP positions as colour-on`` () =
    let labelComp = namedLeaf "COLOUR-LABEL"
    let inputComp = namedLeaf "COLOUR-INPUT"
    match Flex.fixed_ 20 labelComp, Flex.grow 1.0 inputComp with
    | Ok labelItem, Ok inputItem ->
        match Flex.create Horizontal [labelItem; inputItem] with
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp   = Composition.ofFlex flex
            let ctxOn  = ctx80 ()   // colour=true
            let ctxOff = ctx80nc () // colour=false
            match Renderer.toRawAnsi ctxOn comp, Renderer.toRawAnsi ctxOff comp with
            | Ok onOutput, Ok offOutput ->
                let onRegions  = parseCupRegions onOutput
                let offRegions = parseCupRegions offOutput
                match findMarker "COLOUR-LABEL" onRegions,  findMarker "COLOUR-LABEL" offRegions,
                      findMarker "COLOUR-INPUT" onRegions,  findMarker "COLOUR-INPUT" offRegions with
                | Some (onLR, onLC), Some (offLR, offLC),
                  Some (onIR, onIC), Some (offIR, offIC) ->
                    // CUP positions must match between colour-on and colour-off.
                    onLR = offLR && onLC = offLC &&
                    onIR = offIR && onIC = offIC
                | None, _, _, _    -> failwith "COLOUR-LABEL not found in colour-on output"
                | _, None, _, _    -> failwith "COLOUR-LABEL not found in colour-off output"
                | _, _, None, _    -> failwith "COLOUR-INPUT not found in colour-on output"
                | _, _, _, None    -> failwith "COLOUR-INPUT not found in colour-off output"
            | Error e, _ -> failwith $"colour-on render failed: {e}"
            | _, Error e -> failwith $"colour-off render failed: {e}"
    | Error e, _ -> failwith $"Flex.fixed_ failed: {e}"
    | _, Error e -> failwith $"Flex.grow failed: {e}"

[<Property(MaxTest = 1)>]
let ``T054 — Two renders of the same Flex are byte-identical`` () =
    let labelComp = namedLeaf "DETERM-LABEL"
    let inputComp = namedLeaf "DETERM-INPUT"
    match Flex.fixed_ 20 labelComp, Flex.grow 1.0 inputComp with
    | Ok labelItem, Ok inputItem ->
        match Flex.create Horizontal [labelItem; inputItem] with
        | Error e -> failwith $"Flex.create failed: {e}"
        | Ok flex ->
            let comp = Composition.ofFlex flex
            let ctx1 = RenderContext.create 80 24 true "default" |> function Ok c -> c | Error e -> failwith $"ctx1: {e}"
            let ctx2 = RenderContext.create 80 24 true "default" |> function Ok c -> c | Error e -> failwith $"ctx2: {e}"
            match Renderer.toRawAnsi ctx1 comp, Renderer.toRawAnsi ctx2 comp with
            | Ok s1, Ok s2 -> s1 = s2
            | Error e, _   -> failwith $"render 1 failed: {e}"
            | _, Error e   -> failwith $"render 2 failed: {e}"
    | Error e, _ -> failwith $"Flex.fixed_ failed: {e}"
    | _, Error e -> failwith $"Flex.grow failed: {e}"

// ============================================================================
// T054a — Depth-limit: nesting Flex 101 levels deep returns InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T054a — Flex nested 101 deep returns depth-exceeded InvalidArgument`` () =
    // Build a linear chain: leaf → Flex (1 item) → Flex (1 item) → ... 101 levels.
    let leaf = namedLeaf "DEEP-LEAF"
    let rec buildDeep (comp: Composition) (depth: int) : Result<Flex, RenderError> =
        match Flex.fixed_ 10 comp with
        | Error e -> Error e
        | Ok item ->
            match Flex.create Horizontal [item] with
            | Error e -> Error e
            | Ok flex ->
                if depth >= 101 then Ok flex
                else buildDeep (Composition.ofFlex flex) (depth + 1)
    match buildDeep leaf 1 with
    | Error (RenderError.InvalidArgument ("Layout", detail)) ->
        detail.Contains "depth"
    | Error other -> failwith $"Expected depth error but got: {other}"
    | Ok _        -> false // should have been rejected at depth 101

// ============================================================================
// T060 — Direct unit tests for solveDistribution via Internal accessor
// ============================================================================
//
// These tests exercise the pure solver function directly, verifying correctness
// of each algorithmic branch (grow, shrink, exact-fit, zero-grow).
// ============================================================================

/// Access the internal solver for direct unit testing
/// (mirrors Phase 2 P4 preflightLabels pattern + LayoutDepth precedent).
let private solveDistribution = Flex.Internal.solveDistribution

// Sub-case (a): Basic 1:2:1 grow distribution
// Trace (axisLen=40):
//   items: grow(1.0,basis=0), grow(2.0,basis=0), grow(1.0,basis=0)
//   totalBasis=0, freeSpace=40
//   totalGrow=4.0
//   sizes: round(40×1/4)=10, round(40×2/4)=20, round(40×1/4)=10
//   sum=40, residue=0 → [10;20;10]
[<Property(MaxTest = 1)>]
let ``T060a — solveDistribution: 1:2:1 grow at axisLen=40 → [10;20;10]`` () =
    let items =
        [ { Flex.Internal.SolverItem.Grow=1.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=0 }
          { Flex.Internal.SolverItem.Grow=2.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=0 }
          { Flex.Internal.SolverItem.Grow=1.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=0 } ]
    solveDistribution items 40 = [10; 20; 10]

// Sub-case (b): All-fixed, no free space
// Trace (axisLen=30):
//   items: fixed(10), fixed(10), fixed(10)
//   totalBasis=30, freeSpace=0 → basis is final → [10;10;10]
[<Property(MaxTest = 1)>]
let ``T060b — solveDistribution: all-fixed no free space → basis unchanged`` () =
    let items =
        [ { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=10 }
          { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=10 }
          { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=10 } ]
    solveDistribution items 30 = [10; 10; 10]

// Sub-case (c): Negative free space with shrink
// Trace (axisLen=20, basis=[30;30], shrink=[1.0;1.0]):
//   totalBasis=60, freeSpace=20-60=-40
//   totalScaledShrink = 1.0×30 + 1.0×30 = 60.0
//   reduce_a = 40 × (30/60) = 20 → a = 30-20 = 10
//   reduce_b = 40 × (30/60) = 20 → b = 30-20 = 10
//   sum=20, residue=0 → [10;10]
[<Property(MaxTest = 1)>]
let ``T060c — solveDistribution: negative free space with shrink → [10;10]`` () =
    let items =
        [ { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=1.0; Flex.Internal.SolverItem.Basis=30 }
          { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=1.0; Flex.Internal.SolverItem.Basis=30 } ]
    solveDistribution items 20 = [10; 10]

// Sub-case (d): Zero grow + zero shrink + sum of basis < available
// Trailing free space is wasted (basis stands, no grow).
// Trace (axisLen=50, basis=[10;10]):
//   totalBasis=20, freeSpace=30
//   totalGrow=0.0 → no grow → basis stands → [10;10]
[<Property(MaxTest = 1)>]
let ``T060d — solveDistribution: zero grow+shrink with basis < available → trailing space wasted`` () =
    let items =
        [ { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=10 }
          { Flex.Internal.SolverItem.Grow=0.0; Flex.Internal.SolverItem.Shrink=0.0; Flex.Internal.SolverItem.Basis=10 } ]
    solveDistribution items 50 = [10; 10]
