module Fugue.Tests.BashFnTests

open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj | null) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj | null =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``BashFn runs echo and returns stdout`` () =
    let fn = BashFn.create "/tmp"
    let args = mkArgs [ "command", jsonStr "echo hello-fugue" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "hello-fugue"

[<Fact>]
let ``BashFn schema requires command`` () =
    let fn = BashFn.create "/tmp"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "command"
