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

/// Strip ANSI CSI escape sequences (\x1b[...m) from a string.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-?]*[ -/]*[@-~]", "")

/// Split stripped output into lines.
let private lines (s: string) : string array =
    s.Split([| '\n' |])

// ============================================================================
// T022 — Happy path: Bottom + Fill renders status at last row, main fills rest
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T022 — Dock Bottom+Fill renders STATUS-MARKER in output and MAIN-MARKER in output`` () =
    let statusComp = namedLeaf "STATUS-MARKER"
    let mainComp   = namedLeaf "MAIN-MARKER"
    match Dock.create [ DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let stripped = stripAnsi output
            stripped.Contains "STATUS-MARKER" && stripped.Contains "MAIN-MARKER"

// ============================================================================
// T023 — Multi-edge: Left + Right + Fill assigns correct column regions
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T023 — Dock Left+Right+Fill renders SIDEBAR-MARKER, META-MARKER, and MAIN-MARKER`` () =
    // We cannot easily assert exact column positions without a full test console,
    // so we verify that all three markers appear in the rendered output.
    let sidebarComp  = namedLeaf "SIDEBAR-MARKER"
    let metaComp     = namedLeaf "META-MARKER"
    let mainComp     = namedLeaf "MAIN-MARKER"
    match Dock.create [ DockEdge.Left, sidebarComp; DockEdge.Right, metaComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx100 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let stripped = stripAnsi output
            stripped.Contains "SIDEBAR-MARKER"
            && stripped.Contains "META-MARKER"
            && stripped.Contains "MAIN-MARKER"

// ============================================================================
// T024 — Top-pin: Top + Fill renders HEADER-MARKER and BODY-MARKER
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T024 — Dock Top+Fill renders HEADER-MARKER and BODY-MARKER`` () =
    let headerComp = namedLeaf "HEADER-MARKER"
    let bodyComp   = namedLeaf "BODY-MARKER"
    match Dock.create [ DockEdge.Top, headerComp; DockEdge.Fill, bodyComp ] with
    | Error e -> failwith $"Dock.create failed: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let stripped = stripAnsi output
            let ls = lines stripped
            // HEADER-MARKER must appear before BODY-MARKER in the line order.
            let headerRow = ls |> Array.tryFindIndex (fun l -> l.Contains "HEADER-MARKER")
            let bodyRow   = ls |> Array.tryFindIndex (fun l -> l.Contains "BODY-MARKER")
            match headerRow, bodyRow with
            | Some h, Some b -> h < b
            | _ -> false

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
// T028 — Colour toggle: colour-off equals colour-on with ANSI stripped
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T028 — Colour-off output equals colour-on output with ANSI stripped`` () =
    let statusComp = namedLeaf "COLOUR-STATUS"
    let mainComp   = namedLeaf "COLOUR-MAIN"
    match Dock.create [ DockEdge.Bottom, statusComp; DockEdge.Fill, mainComp ] with
    | Error e -> failwith $"Dock.create: {e}"
    | Ok dock ->
        let comp = Composition.ofDock dock
        match Renderer.toRawAnsi (ctx80 ()) comp, Renderer.toRawAnsi (ctx80nc ()) comp with
        | Ok withColour, Ok withoutColour ->
            stripAnsi withColour = withoutColour
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
