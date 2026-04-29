module Fugue.Tests.GlobToolTests

open System
open System.IO
open Xunit
open FsUnit.Xunit

let private mkSandbox () =
    let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(root) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "sub")) |> ignore
    File.WriteAllText(Path.Combine(root, "a.fs"), "")
    File.WriteAllText(Path.Combine(root, "b.txt"), "")
    File.WriteAllText(Path.Combine(root, "sub", "c.fs"), "")
    root

[<Fact>]
let ``Glob finds files by pattern`` () =
    let root = mkSandbox ()
    let result = Fugue.Tools.GlobTool.glob root "**/*.fs" None
    result |> should haveSubstring "a.fs"
    result |> should haveSubstring "c.fs"
    result |> should not' (haveSubstring "b.txt")

[<Fact>]
let ``Glob with explicit cwd works`` () =
    let root = mkSandbox ()
    let result = Fugue.Tools.GlobTool.glob (Path.GetTempPath()) "**/*.fs" (Some root)
    result |> should haveSubstring "a.fs"
