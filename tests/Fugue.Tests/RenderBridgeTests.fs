module Fugue.Tests.RenderBridgeTests

open Xunit
open Fugue.Adapters.Console
open Fugue.Surface
open Fugue.Cli

[<Fact>]
let ``toDrawOp returns Ok DrawOp.RawAnsi for valid context and composition`` () =
    let ctx =
        match RenderContext.create 80 24 true "default" with
        | Ok c -> c
        | Error e -> failwithf "test context construction failed: %A" e
    let leaf = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "OK"))
    match RenderBridge.toDrawOp ctx leaf with
    | Ok (DrawOp.RawAnsi ansi) ->
        Assert.NotEmpty ansi
        Assert.Contains("OK", ansi)
    | Ok other ->
        failwithf "Expected DrawOp.RawAnsi, got %A" other
    | Error e ->
        failwithf "Expected Ok, got Error %A" e

[<Fact>]
let ``toDrawOp propagates RenderError for a Composition that fails rendering`` () =
    // Primitive.Markup with an unclosed tag causes Renderer.toRawAnsi to return
    // Error (RenderError.InvalidMarkup _) on an otherwise-valid context.
    // RenderBridge.toDrawOp applies Result.map DrawOp.RawAnsi, which is a no-op on
    // Error, so the error must reach the caller unchanged.
    // Natural failing input: confirmed by RendererTests T016 (same pattern).
    let ctx =
        match RenderContext.create 80 24 true "default" with
        | Ok c -> c
        | Error e -> failwithf "test context construction failed: %A" e
    let badMarkupComp = Composition.Leaf (Primitive.Markup "[red][unclosed")
    match RenderBridge.toDrawOp ctx badMarkupComp with
    | Error (RenderError.InvalidMarkup (input, detail)) ->
        Assert.NotEmpty input
        Assert.NotEmpty detail
    | Ok op ->
        failwithf "Expected Error (InvalidMarkup), got Ok %A" op
    | Error other ->
        failwithf "Expected Error (InvalidMarkup), got Error %A" other
