module Fugue.Tests.ReadFnTests

open System
open System.IO
open System.Collections.Generic
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj | null) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonEl (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

[<Fact>]
let ``ReadFn schema declares path required`` () =
    let fn = ReadFn.create "/tmp" Fugue.Core.Hooks.defaultConfig "test"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "path"

[<Fact>]
let ``ReadFn invocation reads tmp file`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "x.txt")
    File.WriteAllText(path, "alpha\nbeta\ngamma\n")
    let fn = ReadFn.create dir Fugue.Core.Hooks.defaultConfig "test"
    let args = mkArgs [ "path", box (jsonEl (JsonSerializer.Serialize "x.txt")) ]
    let result = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    result |> should haveSubstring "alpha"
    result |> should haveSubstring "beta"
    Directory.Delete(dir, true)

[<Fact>]
let ``ReadFn missing path raises ArgumentException`` () =
    let fn = ReadFn.create "/tmp" Fugue.Core.Hooks.defaultConfig "test"
    let args = mkArgs []
    let task = fn.InvokeAsync(args, CancellationToken.None).AsTask()
    (fun () -> task.Wait())
    |> should throw typeof<AggregateException>
