module Fugue.Tests.EditFnTests

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

let private jsonBool (b: bool) : obj | null =
    use doc = JsonDocument.Parse(if b then "true" else "false")
    box (doc.RootElement.Clone())

[<Fact>]
let ``EditFn replaces single occurrence`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "f.txt")
    File.WriteAllText(path, "old text here")
    let fn = EditFn.create dir
    let args = mkArgs [
        "path",       jsonStr "f.txt"
        "old_string", jsonStr "old"
        "new_string", jsonStr "new"
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText path |> should equal "new text here"
    Directory.Delete(dir, true)

[<Fact>]
let ``EditFn replace_all replaces every occurrence`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "f.txt")
    File.WriteAllText(path, "x x x")
    let fn = EditFn.create dir
    let args = mkArgs [
        "path",        jsonStr "f.txt"
        "old_string",  jsonStr "x"
        "new_string",  jsonStr "y"
        "replace_all", jsonBool true
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText path |> should equal "y y y"
    Directory.Delete(dir, true)
