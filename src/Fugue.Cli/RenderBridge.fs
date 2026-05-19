module Fugue.Cli.RenderBridge

open Fugue.Adapters.Console
open Fugue.Surface

let toDrawOp (ctx: RenderContext) (composition: Composition) : Result<DrawOp, RenderError> =
    Renderer.toRawAnsi ctx composition
    |> Result.map DrawOp.RawAnsi
