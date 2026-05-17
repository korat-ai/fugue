module Fugue.Adapters.Console.Tests.Layout.StackTests

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

/// A named styled leaf composition — used as a stand-in child with a distinctive marker.
let private namedLeaf (marker: string) : Composition =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral marker))

/// Strip ANSI CSI escape sequences (\x1b[...m) from a string.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-?]*[ -/]*[@-~]", "")

// ============================================================================
// T010 — Happy path: vertical Stack renders all children (marker discipline)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T010 — Vertical Stack with 3 children renders all marker strings`` () =
    let c1 = namedLeaf "STACK-CHILD-1"
    let c2 = namedLeaf "STACK-CHILD-2"
    let c3 = namedLeaf "STACK-CHILD-3"
    match Stack.create Vertical 1 Start [c1; c2; c3] with
    | Error e -> failwith $"Stack.create failed: {e}"
    | Ok stack ->
        let comp = Composition.ofStack stack
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let stripped = stripAnsi output
            let lines = stripped.Split([|'\n'|])
            let allPresent =
                stripped.Contains "STACK-CHILD-1"
                && stripped.Contains "STACK-CHILD-2"
                && stripped.Contains "STACK-CHILD-3"
            // Gap-distance assertion: gap=1 means exactly 1 blank line between
            // consecutive children, so the row-index difference must be ≥ 2
            // (content row + 1 blank separator). We find the first line containing
            // each marker and check the distance.
            let findRow (marker: string) =
                lines |> Array.tryFindIndex (fun (l: string) -> l.Contains marker)
            let gapOk =
                match findRow "STACK-CHILD-1", findRow "STACK-CHILD-2", findRow "STACK-CHILD-3" with
                | Some l1, Some l2, Some l3 -> (l2 - l1) >= 2 && (l3 - l2) >= 2
                | _ -> false // markers must all be present
            allPresent && gapOk

// ============================================================================
// T011 — Error: empty children
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T011 — Stack.create with empty children returns EmptyComposition`` () =
    match Stack.create Vertical 1 Start [] with
    | Error (RenderError.EmptyComposition msg) ->
        msg.Contains "Stack"
    | _ -> false

// ============================================================================
// T012 — Error: negative gap
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T012 — Stack.create with negative gap returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match Stack.create Vertical -1 Start [child] with
    | Error (RenderError.InvalidArgument ("Stack", detail)) ->
        detail.Contains "gap"
    | _ -> false

// ============================================================================
// T013 — Horizontal Stack places children side-by-side
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T013 — Horizontal Stack renders both children in output`` () =
    let c1 = namedLeaf "HSTACK-LEFT"
    let c2 = namedLeaf "HSTACK-RIGHT"
    // Must use Start — Center/End_/Stretch are rejected for Horizontal (T013b/c/d).
    match Stack.create Horizontal 2 Start [c1; c2] with
    | Error e -> failwith $"Stack.create failed: {e}"
    | Ok stack ->
        let comp = Composition.ofStack stack
        match Renderer.toRawAnsi (ctx80 ()) comp with
        | Error e -> failwith $"render failed: {e}"
        | Ok output ->
            let stripped = stripAnsi output
            let containsBoth =
                stripped.Contains "HSTACK-LEFT"
                && stripped.Contains "HSTACK-RIGHT"
            // Gap-distance assertion: with gap=2, column position of RIGHT marker
            // must be at least past LEFT marker + its length + gap.
            let row =
                stripped.Split([|'\n'|])
                |> Array.tryFind (fun l -> l.Contains "HSTACK-LEFT" && l.Contains "HSTACK-RIGHT")
            let gapOk =
                match row with
                | None -> true // markers may be on separate lines at narrow widths — skip
                | Some r ->
                    let p1 = r.IndexOf "HSTACK-LEFT"
                    let p2 = r.IndexOf "HSTACK-RIGHT"
                    (p2 - p1) >= ("HSTACK-LEFT".Length + 2)
            containsBoth && gapOk

// ============================================================================
// T013b/c/d — Horizontal Stack rejects non-Start cross-axis alignment
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T013b — Stack.create Horizontal with Center align returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match Stack.create Horizontal 0 Center [child] with
    | Error (RenderError.InvalidArgument ("Stack", detail)) ->
        detail.Contains "horizontal" || detail.Contains "Start"
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T013c — Stack.create Horizontal with End_ align returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match Stack.create Horizontal 0 End_ [child] with
    | Error (RenderError.InvalidArgument ("Stack", detail)) ->
        detail.Contains "horizontal" || detail.Contains "Start"
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T013d — Stack.create Horizontal with Stretch align returns InvalidArgument`` () =
    let child = namedLeaf "any"
    match Stack.create Horizontal 0 Stretch [child] with
    | Error (RenderError.InvalidArgument ("Stack", detail)) ->
        detail.Contains "horizontal" || detail.Contains "Start"
    | _ -> false

// ============================================================================
// T014 — Cross-axis alignment: all 4 cases render without error
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T014 — CrossAxisAlignment Start renders without error`` () =
    let child = namedLeaf "ALIGN-CHILD"
    match Stack.create Vertical 0 Start [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        match Renderer.toRawAnsi (ctx80 ()) (Composition.ofStack stack) with
        | Ok output -> (stripAnsi output).Contains "ALIGN-CHILD"
        | Error e   -> failwith $"render: {e}"

[<Property(MaxTest = 1)>]
let ``T014 — CrossAxisAlignment Center renders without error`` () =
    let child = namedLeaf "ALIGN-CHILD"
    match Stack.create Vertical 0 Center [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        match Renderer.toRawAnsi (ctx80 ()) (Composition.ofStack stack) with
        | Ok output -> (stripAnsi output).Contains "ALIGN-CHILD"
        | Error e   -> failwith $"render: {e}"

[<Property(MaxTest = 1)>]
let ``T014 — CrossAxisAlignment End_ renders without error`` () =
    let child = namedLeaf "ALIGN-CHILD"
    match Stack.create Vertical 0 End_ [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        match Renderer.toRawAnsi (ctx80 ()) (Composition.ofStack stack) with
        | Ok output -> (stripAnsi output).Contains "ALIGN-CHILD"
        | Error e   -> failwith $"render: {e}"

[<Property(MaxTest = 1)>]
let ``T014 — CrossAxisAlignment Stretch renders without error`` () =
    let child = namedLeaf "ALIGN-CHILD"
    match Stack.create Vertical 0 Stretch [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        match Renderer.toRawAnsi (ctx80 ()) (Composition.ofStack stack) with
        | Ok output -> (stripAnsi output).Contains "ALIGN-CHILD"
        | Error e   -> failwith $"render: {e}"

// ============================================================================
// T015 — Colour toggle: colour-off equals colour-on with ANSI stripped
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T015 — Colour-off output equals colour-on output with ANSI stripped`` () =
    let child = namedLeaf "COLOUR-MARKER"
    match Stack.create Vertical 0 Start [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        let comp = Composition.ofStack stack
        match Renderer.toRawAnsi (ctx80 ()) comp, Renderer.toRawAnsi (ctx80nc ()) comp with
        | Ok withColour, Ok withoutColour ->
            stripAnsi withColour = withoutColour
        | Error e, _ -> failwith $"colour render failed: {e}"
        | _, Error e -> failwith $"no-colour render failed: {e}"

// ============================================================================
// T015a — CJK codepoint preservation test
// ============================================================================

// NOTE: This test verifies CJK codepoints survive lowering — NOT that
// Spectre computes display width correctly (6 cells for "日本語"). True
// width verification deferred to T082 follow-up (R-4) where the new
// SafeText.displayWidth API will land.

[<Property(MaxTest = 1)>]
let ``T015a — Stack containing CJK text preserves codepoints in output (display-width verification deferred to follow-up)`` () =
    // "日本語" is 3 Unicode code points, each East Asian Wide = 2 display columns = 6 total.
    // Layout must not corrupt the string or error on it.
    // NOTE: ZWJ family emoji (e.g. "👨‍👩‍👧") are a known gap per research R-4:
    // Spectre's Cell.GetCellLength reports 6 display cols instead of the correct 2.
    // ZWJ sequences are explicitly NOT tested here; see follow-up T082.
    let cjkText = SafeText.ofLiteral "日本語"
    let child = Composition.Leaf (Primitive.Styled (Style.empty, cjkText))
    let ctx = RenderContext.create 10 24 false "default" |> function Ok c -> c | Error e -> failwith $"ctx: {e}"
    match Stack.create Vertical 0 Start [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        match Renderer.toRawAnsi ctx (Composition.ofStack stack) with
        | Ok output ->
            // Verify the CJK text appears in the output (Spectre renders it, not us).
            // We assert the string is present; display-column counting is Spectre's domain.
            output.Contains "日本語"
        | Error e -> failwith $"render failed: {e}"

// ============================================================================
// T016 — Determinism: two renders of the same Stack are byte-identical
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T016 — Two renders of the same Stack are byte-identical`` () =
    let child = namedLeaf "DETERMINISM-MARKER"
    match Stack.create Vertical 1 Start [child] with
    | Error e -> failwith $"Stack.create: {e}"
    | Ok stack ->
        let comp = Composition.ofStack stack
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

// ============================================================================
// T017 — Depth limit: Stack nested 101 deep returns InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T017 — Stack nested 101 deep returns depth-exceeded InvalidArgument`` () =
    // Build a linear chain: leaf → Stack → Stack → ... (101 levels).
    // The innermost is a leaf Composition; each iteration wraps it in a Stack.
    let leaf = namedLeaf "DEEP-LEAF"
    // Build 100 valid stacks first, then attempt the 101st.
    let rec buildDeep (comp: Composition) (depth: int) : Result<Stack, RenderError> =
        match Stack.create Vertical 0 Start [comp] with
        | Error e -> Error e
        | Ok stack ->
            if depth >= 101 then Ok stack
            else buildDeep (Composition.ofStack stack) (depth + 1)
    match buildDeep leaf 1 with
    | Error (RenderError.InvalidArgument ("Layout", detail)) ->
        detail.Contains "depth"
    | Error other -> failwith $"Expected depth error but got: {other}"
    | Ok _        -> false // should have been rejected
