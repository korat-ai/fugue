module Fugue.Tests.WriteToolTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

[<Fact>]
let ``Write creates file and reports byte count`` () =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt")
    let result = Fugue.Tools.WriteTool.write (Path.GetTempPath()) path "hello"
    File.Exists path |> should equal true
    File.ReadAllText path |> should equal "hello"
    result |> should haveSubstring "5 bytes"

[<Fact>]
let ``Write overwrites existing file`` () =
    let path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt")
    File.WriteAllText(path, "old")
    Fugue.Tools.WriteTool.write (Path.GetTempPath()) path "new" |> ignore
    File.ReadAllText path |> should equal "new"
