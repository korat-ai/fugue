module Fugue.Tests.WriteFnTests

open System
open System.IO
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
let ``WriteFn schema requires path and content`` () =
    let fn = WriteFn.create "/tmp"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "path"
    req |> should contain "content"

[<Fact>]
let ``WriteFn writes file content`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let fn = WriteFn.create dir
    let args = mkArgs [
        "path", jsonStr "out.txt"
        "content", jsonStr "hello world"
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText(Path.Combine(dir, "out.txt")) |> should equal "hello world"
    Directory.Delete(dir, true)
