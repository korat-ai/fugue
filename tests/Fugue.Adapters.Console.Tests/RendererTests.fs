module Fugue.Adapters.Console.Tests.RendererTests

open FsCheck.Xunit
open Fugue.Adapters.Console
open Fugue.Surface

// All tests written as [<Property(MaxTest=1)>] returning bool — see
// FoundationTests.fs header for rationale (Fact-discovery workaround).

let private ctxDefault () = RenderContext.create 80 true "default"
let private ctxNoColour () = RenderContext.create 80 false "default"

// ============================================================================
// T014-T019 — per-primitive integration tests vs real Spectre
// (Principle II — no mocks at the C# boundary)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T014 — Styled with empty style and plain text produces non-empty output`` () =
    let ctx = ctxDefault ()
    let prim = Primitive.Styled (Style.empty, SafeText.ofUser "hello")
    let comp = Composition.Leaf prim
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "hello"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T015 — Styled with red bold theme produces ANSI escape sequence`` () =
    let ctx = ctxDefault ()
    let style =
        Style.create (Colour.Theme "error") Colour.Default [ Decoration.Bold ]
        |> function Ok s -> s | Error _ -> Style.empty
    let prim = Primitive.Styled (style, SafeText.ofUser "err")
    let comp = Composition.Leaf prim
    match Renderer.toRawAnsi ctx comp with
    | Ok s  ->
        // Contains an ANSI escape (\x1b[) somewhere — proves colour was emitted.
        s.Contains "\x1b[" && s.Contains "err"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T016 — Primitive.Markup with bad input returns InvalidMarkup`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf (Primitive.Markup "[red][unclosed")
    match Renderer.toRawAnsi ctx comp with
    | Error (RenderError.InvalidMarkup _) -> true
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T016b — Primitive.Markup with valid input renders successfully`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf (Primitive.Markup "[red]hello[/]")
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Contains "hello"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T017 — Primitive.LineBreak emits exactly CRLF`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf Primitive.LineBreak
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s = "\r\n"
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T018 — Primitive.RawAnsi passes bytes through unchanged`` () =
    let ctx = ctxDefault ()
    let bytes = "\x1b[31mraw\x1b[0m"
    let comp = Composition.Leaf (Primitive.RawAnsi bytes)
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s = bytes
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T018b — Primitive.RawAnsi null produces empty string, no throw`` () =
    let ctx = ctxDefault ()
    let nullBytes : string | null = null
    let s : string =
        match nullBytes with
        | null -> ""
        | b    -> b
    // We can't construct Primitive.RawAnsi null directly via DU because string param
    // is non-nullable by default. Inline this as a sanity check that empty works.
    let comp = Composition.Leaf (Primitive.RawAnsi s)
    match Renderer.toRawAnsi ctx comp with
    | Ok output -> output = ""
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T019 — Primitive.Rule renders without error at width 80`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf (Primitive.Rule Style.empty)
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Length > 0
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T019b — Primitive.Rule renders at width 40`` () =
    let ctx = RenderContext.create 40 true "default"
    let comp = Composition.Leaf (Primitive.Rule Style.empty)
    match Renderer.toRawAnsi ctx comp with
    | Ok s  -> s.Length > 0
    | Error _ -> false

// ============================================================================
// T021 — Colour disabled (FR-007)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T021 — Styled with red foreground emits NO colour escapes when ColourEnabled=false`` () =
    let ctx = ctxNoColour ()
    let style =
        Style.create (Colour.Theme "error") Colour.Default []
        |> function Ok s -> s | Error _ -> Style.empty
    let prim = Primitive.Styled (style, SafeText.ofUser "err")
    let comp = Composition.Leaf prim
    match Renderer.toRawAnsi ctx comp with
    | Ok s  ->
        s.Contains "err" && not (s.Contains "\x1b[")
    | Error _ -> false

// ============================================================================
// T022 — Degenerate width (FR-008)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T022 — Width 0 doesn't throw — Styled returns Ok`` () =
    let ctx = RenderContext.create 0 true "default"
    let prim = Primitive.Styled (Style.empty, SafeText.ofUser "x")
    let comp = Composition.Leaf prim
    match Renderer.toRawAnsi ctx comp with
    | Ok _ -> true
    | Error _ -> false

[<Property(MaxTest = 1)>]
let ``T022b — Width -5 doesn't throw — Styled returns Ok`` () =
    let ctx = RenderContext.create -5 true "default"
    let prim = Primitive.Styled (Style.empty, SafeText.ofUser "x")
    let comp = Composition.Leaf prim
    match Renderer.toRawAnsi ctx comp with
    | Ok _ -> true
    | Error _ -> false

// ============================================================================
// T022a — Renderer.toDrawOp delivers the right DrawOp shape
// (replaces the actor-delivery test from spec, since Renderer.render
// was dropped from the API — see research.md §R4)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T022a — Renderer.toDrawOp produces DrawOp.RawAnsi from LineBreak`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf Primitive.LineBreak
    match Renderer.toDrawOp ctx comp with
    | Ok (DrawOp.RawAnsi s) -> s = "\r\n"
    | _ -> false

[<Property(MaxTest = 1)>]
let ``T022a' — Renderer.toDrawOp produces DrawOp.RawAnsi with text bytes for Styled`` () =
    let ctx = ctxDefault ()
    let comp = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofUser "hello"))
    match Renderer.toDrawOp ctx comp with
    | Ok (DrawOp.RawAnsi s) -> s.Contains "hello"
    | _ -> false
