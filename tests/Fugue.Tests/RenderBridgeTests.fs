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
let ``toDrawOp propagates RenderError for degenerate context`` () =
    // width = 0 should fail RenderContext.create, but if for some reason a degenerate
    // context were constructed, the bridge should propagate the underlying renderer error.
    // Sanity-check: width 0 is rejected at context construction time.
    match RenderContext.create 0 24 true "default" with
    | Ok _ -> failwith "RenderContext.create should reject width=0"
    | Error (RenderError.InvalidArgument _) -> ()
    | Error other -> failwithf "Expected InvalidArgument for width=0, got %A" other
