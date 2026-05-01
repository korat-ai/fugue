module Fugue.Tests.GlobFnTests

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
let ``GlobFn finds files matching pattern`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.fs"), "")
    File.WriteAllText(Path.Combine(dir, "b.fs"), "")
    File.WriteAllText(Path.Combine(dir, "c.txt"), "")
    let fn = GlobFn.create dir Fugue.Core.Hooks.defaultConfig "test"
    let args = mkArgs [ "pattern", jsonStr "*.fs" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "a.fs"
    out |> should haveSubstring "b.fs"
    out |> should not' (haveSubstring "c.txt")
    Directory.Delete(dir, true)
