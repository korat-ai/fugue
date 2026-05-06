module Fugue.Tests.GrepToolTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

let private mkTree () =
    let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, "a.fs"), "let x = 1\nlet target = 42\nlet z = 3\n")
    File.WriteAllText(Path.Combine(root, "b.fs"), "no match here\n")
    root

[<Fact>]
let ``Grep finds matching lines with file:line prefix`` () =
    let root = mkTree ()
    let result = Fugue.Tools.GrepTool.grep root "target" None None None
    result |> should haveSubstring "a.fs:2"
    result |> should haveSubstring "let target = 42"

[<Fact>]
let ``Grep with glob filter only searches matching files`` () =
    let root = mkTree ()
    let result = Fugue.Tools.GrepTool.grep root "match" None (Some "b.fs") None
    result |> should haveSubstring "b.fs"
    result |> should not' (haveSubstring "a.fs")
