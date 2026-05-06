module Fugue.Tests.DelegatedFnTests

open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

[<Fact>]
let ``DelegatedAIFunction surfaces name + description + schema`` () =
    let schema = DelegatedFn.parseSchema """{"type":"object","properties":{}}"""
    let fn =
        DelegatedFn.DelegatedAIFunction(
            name = "Echo",
            description = "echoes input",
            schema = schema,
            hooksConfig = Fugue.Core.Hooks.defaultConfig,
            sessionId = "test",
            invoke = fun _ _ -> Task.FromResult("ok"))
    fn.Name |> should equal "Echo"
    fn.Description |> should equal "echoes input"
    fn.JsonSchema.GetProperty("type").GetString() |> should equal "object"

[<Fact>]
let ``DelegatedAIFunction.InvokeAsync calls invoke and boxes result`` () =
    let schema = DelegatedFn.parseSchema """{"type":"object"}"""
    let fn =
        DelegatedFn.DelegatedAIFunction(
            name = "Echo",
            description = "",
            schema = schema,
            hooksConfig = Fugue.Core.Hooks.defaultConfig,
            sessionId = "test",
            invoke = fun args _ ->
                Task.FromResult(sprintf "got %d keys" args.Count))
    let args = AIFunctionArguments(dict [ "a", box "1"; "b", box "2" ])
    let result = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result
    result |> string |> should equal "got 2 keys"

[<Fact>]
let ``parseSchema returns clone-safe JsonElement`` () =
    let el1 = DelegatedFn.parseSchema """{"type":"object"}"""
    let el2 = DelegatedFn.parseSchema """{"type":"array"}"""
    el1.GetProperty("type").GetString() |> should equal "object"
    el2.GetProperty("type").GetString() |> should equal "array"
