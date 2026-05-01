module Fugue.Tests.GrepFnTests

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
let ``GrepFn finds matches in files`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\nfoo bar\nbeta\n")
    let fn = GrepFn.create dir Fugue.Core.Hooks.defaultConfig "test"
    let args = mkArgs [ "pattern", jsonStr "foo" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "foo"
    Directory.Delete(dir, true)
